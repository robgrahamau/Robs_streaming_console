; ============================================================================
;  Steaming - Windows installer (NSIS)
; ----------------------------------------------------------------------------
;  Built by build/release.ps1, which passes the staged payload paths + version
;  in as /D defines. Do NOT run makensis on this by hand without those defines.
;
;  SAFETY CONTRACT (this installer runs on other people's machines):
;    * App goes ONLY to $INSTDIR (default C:\Program Files\Steaming).
;    * Plugin goes ONLY to %PROGRAMDATA%\obs-studio\plugins\steaming-plugin\
;      - a fixed, self-owned folder. We never detect or write into the user's
;        OBS install directory, so we can never corrupt their OBS.
;    * User data in %APPDATA%\Steaming (settings/credentials/analytics) is NEVER
;      touched by install OR uninstall.
;    * Every recursive delete is guarded: non-empty path AND a sentinel file
;      that proves the folder is really ours before RMDir /r runs.
; ============================================================================

Unicode true
ManifestDPIAware true
SetCompressor /SOLID lzma

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "FileFunc.nsh"

; ---------------------------------------------------------------------------
;  Required defines (release.ps1 supplies these). Fail loudly if missing so a
;  hand-run never produces a broken/empty installer.
; ---------------------------------------------------------------------------
!ifndef APP_VERSION
  !error "APP_VERSION not defined (pass /DAPP_VERSION=x.y.z)"
!endif
!ifndef APP_PAYLOAD
  !error "APP_PAYLOAD not defined (staged self-contained app folder)"
!endif
!ifndef PLUGIN_DLL
  !error "PLUGIN_DLL not defined (staged steaming-plugin.dll)"
!endif
!ifndef PLUGIN_LOCALE
  !error "PLUGIN_LOCALE not defined (staged en-US.ini)"
!endif
!ifndef OUTFILE
  !define OUTFILE "Steaming-Setup.exe"
!endif
; WEBVIEW2_BOOTSTRAPPER is optional. If defined, it is bundled and run when the
; WebView2 runtime is missing. If not defined, the WebView2 step is skipped.

!define APP_NAME       "Steaming"
!define APP_DISPLAY    "Rob's Streaming Console"
!define APP_PUBLISHER  "Rob Graham"
!define APP_EXE        "Steaming.WinUI.exe"
!define APP_URL        "https://robgraham.info"

; Fixed, self-owned plugin subpath under %PROGRAMDATA%. Computed at runtime into
; $PluginRoot (see onInit) so it can never be empty or point at OBS itself.
!define PLUGIN_SUBPATH "obs-studio\plugins\steaming-plugin"

!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}"
!define APP_KEY    "Software\${APP_NAME}"

Var PluginRoot

Name "${APP_DISPLAY} ${APP_VERSION}"
OutFile "${OUTFILE}"
InstallDir "$PROGRAMFILES64\robgraham\Streaming Console"
InstallDirRegKey HKLM "${APP_KEY}" "InstallLocation"
RequestExecutionLevel admin

VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey  "ProductName"     "${APP_DISPLAY}"
VIAddVersionKey  "FileDescription" "${APP_DISPLAY} Setup"
VIAddVersionKey  "FileVersion"     "${APP_VERSION}.0"
VIAddVersionKey  "ProductVersion"  "${APP_VERSION}"
VIAddVersionKey  "CompanyName"     "${APP_PUBLISHER}"
VIAddVersionKey  "LegalCopyright"  "(c) Rob Graham"

; ---------------------------------------------------------------------------
;  UI
; ---------------------------------------------------------------------------
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Launch ${APP_DISPLAY}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

; ---------------------------------------------------------------------------
;  Compute the plugin root from the real PROGRAMDATA env var. Never trust a
;  blank result - fall back to the canonical literal, then assert non-empty.
; ---------------------------------------------------------------------------
!macro ResolvePluginRoot
  ReadEnvStr $0 "PROGRAMDATA"
  ${If} $0 == ""
    StrCpy $0 "C:\ProgramData"
  ${EndIf}
  StrCpy $PluginRoot "$0\${PLUGIN_SUBPATH}"
!macroend

; ---------------------------------------------------------------------------
;  Returns on stack: "1" if a process is running, "0" otherwise.
;  Uses tasklist + find; find sets errorlevel 0 only on a match.
; ---------------------------------------------------------------------------
!macro IsProcessRunning exeName
  nsExec::ExecToStack 'cmd /c tasklist /FI "IMAGENAME eq ${exeName}" /NH | find /I "${exeName}"'
  Pop $R9   ; exit code
  Pop $R8   ; (discard output)
  ${If} $R9 == 0
    Push "1"
  ${Else}
    Push "0"
  ${EndIf}
!macroend

; ===========================================================================
;  INSTALLER
; ===========================================================================
Function .onInit
  SetShellVarContext all          ; all-users: $SMPROGRAMS = common start menu
  SetRegView 64

  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "${APP_DISPLAY} requires 64-bit Windows."
    Abort
  ${EndIf}

  !insertmacro ResolvePluginRoot

  ; --- Don't install over a running copy of the app ---
  !insertmacro IsProcessRunning "${APP_EXE}"
  Pop $0
  ${If} $0 == "1"
    MessageBox MB_OKCANCEL|MB_ICONEXCLAMATION "${APP_DISPLAY} is currently running.$\n$\nClick OK to close it and continue, or Cancel to stop." IDOK +2
    Abort
    nsExec::Exec 'taskkill /IM "${APP_EXE}" /F'
    Sleep 800
  ${EndIf}

  ; --- Upgrade: run the previous uninstaller in place (keeps %APPDATA%) ---
  ReadRegStr $R1 HKLM "${UNINST_KEY}" "UninstallString"
  ${If} $R1 != ""
    MessageBox MB_YESNO|MB_ICONQUESTION "${APP_DISPLAY} is already installed.$\n$\nUpgrade now? Your settings, logins and analytics are kept." IDYES +2
    Abort
    ReadRegStr $R2 HKLM "${APP_KEY}" "InstallLocation"
    ${If} $R2 == ""
      StrCpy $R2 "$INSTDIR"
    ${EndIf}
    ; _?= runs the uninstaller in place and makes ExecWait actually wait.
    ; $R1 (UninstallString) is already quoted in the registry, so do NOT add quotes.
    ExecWait '$R1 /S _?=$R2'
    Delete "$R2\Uninstall.exe"
  ${EndIf}
FunctionEnd

Section "Steaming app" SEC_APP
  SectionIn RO
  SetOutPath "$INSTDIR"
  ; Wipe any stale loose files from a prior in-place copy, then lay down payload.
  File /r "${APP_PAYLOAD}\*"

  ; Start menu shortcuts (all-users)
  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortCut  "$SMPROGRAMS\${APP_NAME}\${APP_DISPLAY}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
  CreateShortCut  "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk" "$INSTDIR\Uninstall.exe"

  ; Uninstaller
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; --- Install metadata (installer-owned; NOT user data) ---
  WriteRegStr HKLM "${APP_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${APP_KEY}" "Version"         "${APP_VERSION}"
  WriteRegStr HKLM "${APP_KEY}" "PluginRoot"      "$PluginRoot"

  ; --- Add/Remove Programs entry ---
  WriteRegStr   HKLM "${UNINST_KEY}" "DisplayName"     "${APP_DISPLAY}"
  WriteRegStr   HKLM "${UNINST_KEY}" "DisplayVersion"  "${APP_VERSION}"
  WriteRegStr   HKLM "${UNINST_KEY}" "Publisher"       "${APP_PUBLISHER}"
  WriteRegStr   HKLM "${UNINST_KEY}" "DisplayIcon"     "$INSTDIR\${APP_EXE}"
  WriteRegStr   HKLM "${UNINST_KEY}" "URLInfoAbout"    "${APP_URL}"
  WriteRegStr   HKLM "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr   HKLM "${UNINST_KEY}" "UninstallString"      '"$INSTDIR\Uninstall.exe"'
  WriteRegStr   HKLM "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoRepair" 1

  ; EstimatedSize (KB) for Add/Remove Programs
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKLM "${UNINST_KEY}" "EstimatedSize" $0

  ; --- Windows Firewall: allow the app to communicate ---
  ; The app talks to Twitch/Kick/OBS over WebSockets and runs a loopback OAuth callback. We add an
  ; explicit allow rule (in + out, all profiles) for the exe so Windows never silently blocks it and
  ; the user is never hit with a firewall prompt. Installer is elevated, so this succeeds. Clear any
  ; stale rule of the same name first so upgrades don't pile up duplicates.
  nsExec::Exec 'netsh advfirewall firewall delete rule name="${APP_DISPLAY}"'
  nsExec::Exec 'netsh advfirewall firewall add rule name="${APP_DISPLAY}" dir=in  action=allow program="$INSTDIR\${APP_EXE}" enable=yes profile=any'
  nsExec::Exec 'netsh advfirewall firewall add rule name="${APP_DISPLAY}" dir=out action=allow program="$INSTDIR\${APP_EXE}" enable=yes profile=any'
SectionEnd

Section "OBS plugin" SEC_PLUGIN
  SectionIn RO

  ; If OBS is running the DLL is locked - ask to close it, re-check, skip if not.
  retry_obs:
  !insertmacro IsProcessRunning "obs64.exe"
  Pop $0
  ${If} $0 == "1"
    MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "OBS Studio is running, so the plugin file is locked.$\n$\nPlease close OBS completely, then click Retry. Click Cancel to skip installing/updating the plugin." IDRETRY retry_obs
    DetailPrint "Skipped OBS plugin (OBS was left running)."
    Goto plugin_done
  ${EndIf}

  ; $PluginRoot is the fixed self-owned folder resolved in onInit.
  SetOutPath "$PluginRoot\bin\64bit"
  File "/oname=steaming-plugin.dll" "${PLUGIN_DLL}"
  SetOutPath "$PluginRoot\data\locale"
  File "/oname=en-US.ini" "${PLUGIN_LOCALE}"
  DetailPrint "Installed OBS plugin to $PluginRoot"
  plugin_done:
SectionEnd

!ifdef DOTNET_RUNTIME
Section ".NET 10 Desktop Runtime" SEC_DOTNET
  SectionIn RO
  ; Present if any 10.x Microsoft.WindowsDesktop.App shared-framework folder exists.
  StrCpy $0 ""
  FindFirst $1 $2 "$PROGRAMFILES64\dotnet\shared\Microsoft.WindowsDesktop.App\10.*"
  ${If} $2 != ""
    StrCpy $0 "$2"
  ${EndIf}
  FindClose $1
  ${If} $0 == ""
    DetailPrint "Installing .NET 10 Desktop Runtime..."
    SetOutPath "$PLUGINSDIR"
    File "/oname=windowsdesktop-runtime.exe" "${DOTNET_RUNTIME}"
    ExecWait '"$PLUGINSDIR\windowsdesktop-runtime.exe" /install /quiet /norestart' $1
    DetailPrint ".NET runtime installer exit code: $1"
  ${Else}
    DetailPrint ".NET 10 Desktop Runtime already present ($0)."
  ${EndIf}
SectionEnd
!endif

!ifdef VCREDIST
Section "Visual C++ Runtime" SEC_VCREDIST
  SectionIn RO
  SetRegView 64
  ReadRegDWORD $0 HKLM "SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64" "Installed"
  ${If} $0 == 1
    DetailPrint "Visual C++ runtime already present."
  ${Else}
    DetailPrint "Installing Visual C++ runtime..."
    SetOutPath "$PLUGINSDIR"
    File "/oname=vc_redist.x64.exe" "${VCREDIST}"
    ExecWait '"$PLUGINSDIR\vc_redist.x64.exe" /install /quiet /norestart' $1
    DetailPrint "VC++ installer exit code: $1"
  ${EndIf}
SectionEnd
!endif

!ifdef WINAPPSDK_RUNTIME
Section "Windows App SDK Runtime" SEC_WINAPPSDK
  SectionIn RO
  ; No simple stable per-version regkey to probe, and the installer is idempotent (no-ops if a
  ; compatible runtime is already present), so run it silently every time.
  DetailPrint "Ensuring Windows App SDK runtime..."
  SetOutPath "$PLUGINSDIR"
  File "/oname=windowsappruntimeinstall.exe" "${WINAPPSDK_RUNTIME}"
  ExecWait '"$PLUGINSDIR\windowsappruntimeinstall.exe" --quiet' $1
  DetailPrint "Windows App SDK runtime installer exit code: $1"
SectionEnd
!endif

!ifdef WEBVIEW2_BOOTSTRAPPER
Section "WebView2 runtime" SEC_WV2
  SectionIn RO
  SetRegView 64
  ; pv > "" at either location means the Evergreen runtime is present.
  ReadRegStr $0 HKLM "SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
  ${If} $0 == ""
    ReadRegStr $0 HKCU "Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
  ${EndIf}
  ${If} $0 == ""
  ${OrIf} $0 == "0.0.0.0"
    DetailPrint "Installing Microsoft WebView2 runtime..."
    SetOutPath "$PLUGINSDIR"
    File "/oname=MicrosoftEdgeWebview2Setup.exe" "${WEBVIEW2_BOOTSTRAPPER}"
    ; Elevated (installer is admin) => per-machine. Bootstrapper no-ops if present.
    ExecWait '"$PLUGINSDIR\MicrosoftEdgeWebview2Setup.exe" /silent /install' $1
    DetailPrint "WebView2 installer exit code: $1"
  ${Else}
    DetailPrint "WebView2 runtime already present ($0)."
  ${EndIf}
SectionEnd
!endif

; ===========================================================================
;  UNINSTALLER
; ===========================================================================
Function un.onInit
  SetShellVarContext all
  SetRegView 64
  !insertmacro ResolvePluginRoot
FunctionEnd

Section "Uninstall"
  ; --- App files: only RMDir /r a path that is non-empty AND verifiably ours ---
  ${If} $INSTDIR != ""
  ${AndIf} ${FileExists} "$INSTDIR\${APP_EXE}"
    Delete "$INSTDIR\Uninstall.exe"
    RMDir /r "$INSTDIR"
  ${Else}
    DetailPrint "Skipped app folder (not a Steaming install: $INSTDIR)."
  ${EndIf}

  ; --- Plugin files: fixed self-owned folder, verified by our own DLL ---
  ${If} $PluginRoot != ""
  ${AndIf} ${FileExists} "$PluginRoot\bin\64bit\steaming-plugin.dll"
    RMDir /r "$PluginRoot"
  ${Else}
    DetailPrint "Skipped plugin folder (our DLL not found at $PluginRoot)."
  ${EndIf}

  ; --- Windows Firewall rule (added on install) ---
  nsExec::Exec 'netsh advfirewall firewall delete rule name="${APP_DISPLAY}"'

  ; --- Shortcuts ---
  Delete "$SMPROGRAMS\${APP_NAME}\${APP_DISPLAY}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk"
  RMDir  "$SMPROGRAMS\${APP_NAME}"

  ; --- Registry (only our own keys) ---
  DeleteRegKey HKLM "${UNINST_KEY}"
  DeleteRegKey HKLM "${APP_KEY}"

  ; NOTE: %APPDATA%\Steaming (settings.json, credentials.json, analytics.db) is
  ; intentionally left untouched so a reinstall keeps the user's data.
SectionEnd
