<#
.SYNOPSIS
  One-command release packaging for Steaming: stages the app + OBS plugin, bootstraps
  NSIS, fetches the WebView2 bootstrapper, and compiles the Windows installer.

.DESCRIPTION
  SAFETY: This script is COPY-ONLY from build outputs into a clean staging tree under
  artifacts\. It never builds the plugin with auto-deploy on, and never writes into any
  OBS installation. Re-runnable; cleans its own staging folders each run.

  Output: artifacts\release\installer\Steaming-Setup-<version>.exe (+ .sha256)

.PARAMETER PluginConfig
  C++ plugin build config to stage from (default RelWithDebInfo).

.PARAMETER SkipWebView2
  Skip bundling the WebView2 bootstrapper (installer will then not handle WebView2).

.PARAMETER SignCert
  Optional path to a .pfx code-signing certificate. If given, the produced installer
  is signed with signtool. Omit to ship unsigned (default).

.PARAMETER SignPassword
  Password for the .pfx given in -SignCert.
#>
[CmdletBinding()]
param(
    [string] $PluginConfig = 'RelWithDebInfo',
    [switch] $SkipWebView2,
    [string] $SignCert,
    [string] $SignPassword
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Invoke-WebRequest in Windows PowerShell 5.1 is 10-50x slower while it renders its per-byte progress
# bar — on the ~58 MB .NET runtime that looks like a hang. Suppress it so downloads run at full speed.
$ProgressPreference = 'SilentlyContinue'

# Pinned tool versions / download URLs (edit here to bump).
$NsisVersion       = '3.12'
# Use a direct SourceForge mirror host; the generic downloads.sourceforge.net returns
# an HTML mirror-picker page instead of the file.
$NsisZipUrl        = "https://master.dl.sourceforge.net/project/nsis/NSIS%203/$NsisVersion/nsis-$NsisVersion.zip?viasf=1"
$WebView2Url       = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703'  # Evergreen Bootstrapper (~2 MB)
$DotNetRuntimeUrl  = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe'              # .NET 10 Desktop Runtime
$VcRedistUrl       = 'https://aka.ms/vs/17/release/vc_redist.x64.exe'                             # VC++ x64 redistributable
$WinAppSdkUrl      = 'https://aka.ms/windowsappsdk/1.8/latest/windowsappruntimeinstall-x64.exe'   # Windows App SDK 1.8 runtime

# ---------------------------------------------------------------------------
#  Paths
# ---------------------------------------------------------------------------
$RepoRoot   = Split-Path -Parent $PSScriptRoot
$WinUiProj  = Join-Path $RepoRoot 'Steaming.WinUI\Steaming.WinUI.csproj'
$CoreProj   = Join-Path $RepoRoot 'Steaming.Core\Steaming.Core.csproj'
$NsiScript  = Join-Path $RepoRoot 'installer\steaming.nsi'

$ArtifactsRoot = Join-Path $RepoRoot 'artifacts'
$StageApp      = Join-Path $ArtifactsRoot 'release\app'
$StagePlugin   = Join-Path $ArtifactsRoot 'release\plugin'
$StagePrereqs  = Join-Path $ArtifactsRoot 'release\prereqs'
$StageInstall  = Join-Path $ArtifactsRoot 'release\installer'
$ToolsNsis     = Join-Path $ArtifactsRoot 'tools\nsis'

function Write-Step([string] $msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }

function Get-AppVersion {
    [xml] $xml = Get-Content -Path $CoreProj
    $v = ($xml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
    if (-not $v) { throw "Could not read <Version> from $CoreProj" }
    return [string]$v
}

function Reset-Dir([string] $path) {
    if (Test-Path $path) { Remove-Item -Path $path -Recurse -Force }
    New-Item -ItemType Directory -Path $path -Force | Out-Null
}

# ---------------------------------------------------------------------------
$Version = Get-AppVersion
Write-Step "Steaming release packaging - v$Version"

# 1) Clean staging (copy-only; safe to wipe)
Write-Step 'Preparing staging folders'
Reset-Dir $StageApp
Reset-Dir $StagePlugin
Reset-Dir $StagePrereqs
Reset-Dir $StageInstall
if (-not (Test-Path $ToolsNsis)) { New-Item -ItemType Directory -Path $ToolsNsis -Force | Out-Null }

# 2) Publish the WinUI app framework-dependent (the installer installs the runtimes as prereqs,
#    so we do NOT dump the .NET / Windows App SDK runtime into the app folder)
Write-Step 'Publishing WinUI app (framework-dependent)'
& dotnet publish $WinUiProj `
    -c Release -r win-x64 --self-contained false `
    -p:WindowsAppSDKSelfContained=false `
    -p:WindowsPackageType=None `
    -o $StageApp
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$AppExe = Join-Path $StageApp 'Steaming.WinUI.exe'
if (-not (Test-Path $AppExe)) { throw "Publish did not produce Steaming.WinUI.exe in $StageApp" }
Write-Host "  App payload: $StageApp"

# CRITICAL: dotnet publish does NOT copy the WinUI resource index (Steaming.WinUI.pri) into the publish
# output. Without it WinUI can resolve no XAML/control styles, so every window throws XamlParseException
# at startup and the app dies on the splash. Copy it from the build output next to the published exe.
$PriName = 'Steaming.WinUI.pri'
$PriSrc  = Join-Path $RepoRoot "Steaming.WinUI\bin\Release\net10.0-windows10.0.19041.0\win-x64\$PriName"
if (-not (Test-Path $PriSrc)) {
    throw "WinUI resource index not found: $PriSrc`nThe publish build must produce it; the app cannot run without it."
}
Copy-Item $PriSrc (Join-Path $StageApp $PriName) -Force
Write-Host "  Staged WinUI resource index: $PriName"

# 3) Stage the OBS plugin by COPYING the already-built artifact (never rebuild/deploy)
Write-Step 'Staging OBS plugin'
$PluginDllSrc    = Join-Path $RepoRoot "obs\obs-plugintemplate\build_x64\$PluginConfig\steaming-plugin.dll"
$PluginLocaleSrc = Join-Path $RepoRoot 'obs\obs-plugintemplate\data\locale\en-US.ini'
if (-not (Test-Path $PluginDllSrc)) {
    throw "Plugin DLL not found: $PluginDllSrc`nBuild the plugin first: cmake --build obs/obs-plugintemplate/build_x64 --config $PluginConfig"
}
if (-not (Test-Path $PluginLocaleSrc)) { throw "Plugin locale not found: $PluginLocaleSrc" }

$PluginDll    = Join-Path $StagePlugin 'steaming-plugin.dll'
$PluginLocale = Join-Path $StagePlugin 'en-US.ini'
Copy-Item $PluginDllSrc    $PluginDll    -Force
Copy-Item $PluginLocaleSrc $PluginLocale -Force
Write-Host "  Plugin DLL:    $PluginDll"
Write-Host "  Plugin locale: $PluginLocale"

# 4) WebView2 bootstrapper (optional)
$WebView2Define = $null
if (-not $SkipWebView2) {
    Write-Step 'Fetching WebView2 bootstrapper'
    $Wv2Exe = Join-Path $StagePrereqs 'MicrosoftEdgeWebview2Setup.exe'
    Invoke-WebRequest -Uri $WebView2Url -OutFile $Wv2Exe -UseBasicParsing
    if (-not (Test-Path $Wv2Exe) -or (Get-Item $Wv2Exe).Length -lt 100000) {
        throw "WebView2 bootstrapper download looks wrong (too small): $Wv2Exe"
    }
    $WebView2Define = $Wv2Exe
    Write-Host "  WebView2: $Wv2Exe"
} else {
    Write-Host '  Skipping WebView2 bundling (-SkipWebView2).'
}

# 4b) Fetch the runtime prerequisites the framework-dependent app needs. The installer detects each
#     and installs only what is missing, so end users never hunt for a runtime.
Write-Step 'Fetching runtime prerequisites (.NET 10 Desktop, VC++, Windows App SDK)'
$DotNetExe   = Join-Path $StagePrereqs 'windowsdesktop-runtime.exe'
$VcRedistExe = Join-Path $StagePrereqs 'vc_redist.x64.exe'
$WinAppExe   = Join-Path $StagePrereqs 'windowsappruntimeinstall.exe'
Invoke-WebRequest -Uri $DotNetRuntimeUrl -OutFile $DotNetExe   -UseBasicParsing
Invoke-WebRequest -Uri $VcRedistUrl      -OutFile $VcRedistExe -UseBasicParsing
Invoke-WebRequest -Uri $WinAppSdkUrl     -OutFile $WinAppExe   -UseBasicParsing
foreach ($f in @($DotNetExe, $VcRedistExe, $WinAppExe)) {
    if (-not (Test-Path $f) -or (Get-Item $f).Length -lt 100000) { throw "Prerequisite download looks wrong (too small): $f" }
}
Write-Host "  .NET 10 Desktop: $DotNetExe"
Write-Host "  VC++ redist:     $VcRedistExe"
Write-Host "  Windows App SDK: $WinAppExe"

# 5) Bootstrap NSIS (download + extract the pinned zip if makensis is missing)
Write-Step "Ensuring NSIS $NsisVersion"
$MakeNsis = Get-ChildItem -Path $ToolsNsis -Filter 'makensis.exe' -Recurse -ErrorAction SilentlyContinue |
            Select-Object -First 1
if (-not $MakeNsis) {
    $zip = Join-Path $ToolsNsis "nsis-$NsisVersion.zip"
    Write-Host "  Downloading NSIS from $NsisZipUrl"
    Invoke-WebRequest -Uri $NsisZipUrl -OutFile $zip -UseBasicParsing
    if ((Get-Item $zip).Length -lt 500000) { throw "NSIS zip download looks wrong (too small): $zip" }
    # Verify ZIP magic bytes (PK\x03\x04) so an HTML interstitial can't masquerade as the zip.
    $magic = [System.IO.File]::ReadAllBytes($zip)[0..1]
    if ($magic[0] -ne 0x50 -or $magic[1] -ne 0x4B) { throw "NSIS download is not a ZIP (got non-PK header): $zip" }
    Write-Host '  Extracting NSIS'
    Expand-Archive -Path $zip -DestinationPath $ToolsNsis -Force
    $MakeNsis = Get-ChildItem -Path $ToolsNsis -Filter 'makensis.exe' -Recurse -ErrorAction SilentlyContinue |
                Select-Object -First 1
}
if (-not $MakeNsis) { throw "makensis.exe not found under $ToolsNsis after bootstrap" }
Write-Host "  makensis: $($MakeNsis.FullName)"

# 6) Compile the installer
Write-Step 'Compiling installer'
$OutInstaller = Join-Path $StageInstall "Steaming-Setup-$Version.exe"

$nsisArgs = @(
    "/DAPP_VERSION=$Version",
    "/DAPP_PAYLOAD=$StageApp",
    "/DPLUGIN_DLL=$PluginDll",
    "/DPLUGIN_LOCALE=$PluginLocale",
    "/DOUTFILE=$OutInstaller"
)
if ($WebView2Define) { $nsisArgs += "/DWEBVIEW2_BOOTSTRAPPER=$WebView2Define" }
$nsisArgs += "/DDOTNET_RUNTIME=$DotNetExe"
$nsisArgs += "/DVCREDIST=$VcRedistExe"
$nsisArgs += "/DWINAPPSDK_RUNTIME=$WinAppExe"
$nsisArgs += $NsiScript

& $MakeNsis.FullName @nsisArgs
if ($LASTEXITCODE -ne 0) { throw "makensis failed ($LASTEXITCODE)" }
if (-not (Test-Path $OutInstaller)) { throw "Installer was not produced: $OutInstaller" }

# 7) Optional code signing
if ($SignCert) {
    Write-Step 'Signing installer'
    $signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe' -ErrorAction SilentlyContinue |
                Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw 'signtool.exe not found (install the Windows 10/11 SDK).' }
    $signArgs = @('sign','/fd','SHA256','/f',$SignCert)
    if ($SignPassword) { $signArgs += @('/p',$SignPassword) }
    $signArgs += @('/tr','http://timestamp.digicert.com','/td','SHA256',$OutInstaller)
    & $signtool.FullName @signArgs
    if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)" }
}

# 8) Checksum
Write-Step 'Writing checksum'
$hash = (Get-FileHash -Algorithm SHA256 -Path $OutInstaller).Hash
"$hash *$(Split-Path -Leaf $OutInstaller)" | Set-Content -Path "$OutInstaller.sha256" -Encoding ascii

Write-Step 'Done'
Write-Host "Installer : $OutInstaller"
Write-Host "SHA-256   : $hash"
