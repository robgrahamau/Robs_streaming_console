# PLAN - Proper Windows installer + updater for Steaming

Status: design only - not built.
Scope: one proper Windows installer for the WinUI app + OBS plugin, plus an updater path.
Constraint: end users download and run one installer. No manual runtime installs. No manual file copying. No "open installer tool X and do steps" for Rob.
Decision status: installer tool choice, prerequisite strategy, release-build integration strategy, and OBS-path flow are locked below unless a verified blocker is found.

---

## 1. What this plan is solving

Steaming currently has dev-build behavior, not a real product install flow:

- The shipping app is an unpackaged WinUI desktop app in [Steaming.WinUI/Steaming.WinUI.csproj](../Steaming.WinUI/Steaming.WinUI.csproj).
- The OBS plugin build currently auto-copies into a hardcoded OBS plugin path on the local machine in [obs/obs-plugintemplate/CMakeLists.txt](../obs/obs-plugintemplate/CMakeLists.txt).
- The WinUI OBS page also displays a hardcoded plugin path in [Steaming.WinUI/Pages/ObsConfigPage.xaml](../Steaming.WinUI/Pages/ObsConfigPage.xaml).

That is fine for local development. It is NOT acceptable for:

- random end-user machines
- custom OBS install locations
- portable OBS installs
- clean upgrades
- a release pipeline that must not touch Rob's live OBS folders

This plan replaces that with a real installer + updater design.

---

## 2. Non-negotiable requirements

These are locked by the user's request and must not be diluted:

- End user downloads one installer and runs it.
- The installer must work on machines other than the dev machine.
- The installer must not assume OBS is in one fixed path.
- If OBS cannot be found automatically, the installer must ask where OBS is.
- The installer must validate the chosen OBS path before copying anything.
- The installer must carry required prerequisites itself. No "go download X first".
- The release packaging process must not copy into or move files into Rob's live OBS install.
- Release packaging must use staged copies only.
- The repo's release build must produce the installer as an output artifact.
- User data under `%APPDATA%\Steaming` must survive upgrades and normal uninstall.
- Updates must be installable without manual zip extraction or DLL copying.

---

## 3. What currently works and must not break

### App install/runtime shape

- The app is a WinUI `WinExe` with `WindowsPackageType=None` and `RuntimeIdentifier=win-x64` in
  [Steaming.WinUI/Steaming.WinUI.csproj](../Steaming.WinUI/Steaming.WinUI.csproj).
- It uses `Microsoft.WindowsAppSDK` and `Microsoft.Web.WebView2`, so runtime dependencies matter.

### Plugin install/runtime shape

- The plugin currently deploys to:
  - `C:/ProgramData/obs-studio/plugins/steaming-plugin/bin/64bit`
  - `C:/ProgramData/obs-studio/plugins/steaming-plugin/data/locale`
  from [obs/obs-plugintemplate/CMakeLists.txt](../obs/obs-plugintemplate/CMakeLists.txt).
- The OBS page currently tells the user the plugin path is
  `C:\ProgramData\obs-studio\plugins\steaming-plugin\`
  in [Steaming.WinUI/Pages/ObsConfigPage.xaml](../Steaming.WinUI/Pages/ObsConfigPage.xaml).

### User data that must be preserved

- Tokens: `%APPDATA%\Steaming\credentials.json`
  in [Steaming.Core/Auth/TokenStore.cs](../Steaming.Core/Auth/TokenStore.cs).
- Settings: `%APPDATA%\Steaming\settings.json`
  in [Steaming.Core/Services/AppSettings.cs](../Steaming.Core/Services/AppSettings.cs).
- Analytics DB: `%APPDATA%\Steaming\analytics.db`
  in [Steaming.Data/AnalyticsRepository.cs](../Steaming.Data/AnalyticsRepository.cs).

These files must never be deleted during normal upgrades. Uninstall should leave them alone by default.

---

## 4. Verified tool choice

### Installer technology

Use **NSIS** to build the Windows installer.

This choice is based on verified constraints, not memory:

- NSIS is "completely free for any use" and open source.
- NSIS has a command-line compiler: `makensis`.
- NSIS supports standard installer pages, including a directory page.
- NSIS supports custom pages via `nsDialogs`.
- NSIS supports registry reads and process execution in script.
- NSIS is available as a downloadable zip, so the repo can bootstrap it for release builds without requiring a machine-wide installer-tool install.

Important clarification:

- Rob should not be opening NSIS and doing manual setup work.
- End users definitely should not be touching NSIS.
- NSIS exists only as a repo/build dependency inside the release pipeline.

### Why NSIS fits this project

- proper Windows installer semantics
- silent install support
- custom detection logic for OBS
- a browse/validate flow for custom OBS paths
- process execution for prerequisite installers
- uninstall support
- command-line compilation so the repo release build can produce the installer artifact automatically
- a single generated installer executable for end users

### Other viable tool paths

- **WiX**: viable in principle. The important distinction is:
  - WiX source code license terms are separate from the current maintenance-fee policy around official project usage/downloads.
  - A self-built WiX toolchain is not the same claim as "you must pay to use WiX".
  - Therefore WiX is **not** disqualified on a proven universal-payment basis and remains a real alternative.
- **Inno Setup**: not chosen because its current official site asks commercial users to purchase licenses. Even if that is phrased as a request rather than a hard technical lock, it is not as clean a fit for the "no paying" constraint as NSIS.
- **MSIX**: wrong fit for this plugin deployment model because the OBS plugin must be copied into the chosen OBS install/plugin tree, not only into an app container or app-local package layout.

---

### Why NSIS is still the current plan choice

Even with WiX back on the table as a viable alternative, NSIS is still the current planned choice because it already satisfies, from verified docs:

- free for any use
- command-line compiler
- custom installer UI/pages
- registry/process scripting
- easy repo-local bootstrap from zip during release build

WiX can still be reconsidered, but it should now be compared against NSIS on implementation cost and release-pipeline complexity, not on the bad "WiX always means payment" claim.

---

## 5. Final product shape

The released product should be:

- one signed Windows installer executable
- one installed app under `Program Files\Steaming`
- one installed OBS plugin under the validated OBS plugin path
- one update flow that downloads and applies the next signed installer executable

The end user experience should be:

1. Download installer.
2. Run installer.
3. Installer finds OBS or asks where it is.
4. Installer validates the path.
5. Installer installs app + plugin + prerequisites.
6. Done.

No separate runtime hunt. No manual plugin copying.

The developer/release experience should be:

1. Run one repo-controlled release command.
2. It stages app output.
3. It stages plugin output.
4. It bootstraps the NSIS tool if missing.
5. It builds the installer EXE.
6. It leaves a finished installer artifact in a release output folder.

No manual installer-tool install. No manual packaging checklist.

---

## 6. Installer structure

Use one **NSIS-built installer EXE** as the shipped installer.

The installer contains:

- published self-contained WinUI app payload
- OBS plugin payload
- bundled prerequisite payload(s) needed for offline install

The installer UI is split into two parts:

- normal app install path selection
- OBS detection/selection page

### App payload

Installs:

- published self-contained WinUI app files
- shortcuts
- uninstall entry
- registry values for install location and version

Target:

- `C:\Program Files\Steaming`

Registry written:

- app install path
- installed app version
- uninstall command
- stored OBS root selected during install
- stored OBS plugin target path

### Plugin payload

Installs:

- `steaming-plugin.dll`
- locale/data files

Target:

- computed from detected or user-supplied OBS install root

The installer determines the OBS root during its detection/custom-page flow, validates it, and then copies the plugin payload to the computed destination.

Installed plugin layout must remain compatible with the current runtime expectation:

- `...\plugins\steaming-plugin\bin\64bit\steaming-plugin.dll`
- `...\plugins\steaming-plugin\data\locale\en-US.ini`

That preserves compatibility with the current code and UI assumptions until the UI is updated to display the detected path dynamically.

---

## 7. Prerequisites - no manual downloads

The installer must carry prerequisites itself.

### App runtime strategy

Publish the WinUI app as:

- self-contained
- unpackaged
- release payload staged into a clean release directory

This avoids asking the user to install the matching .NET runtime / Windows App SDK runtime separately.

### WebView2

Since the app references WebView2, the installer must bundle a WebView2 runtime installation path as part of the release.

Requirement:

- The installer handles WebView2 itself.
- The user is never told to go download WebView2 manually.

Locked choice:

- bundle the **Evergreen standalone installer** in the release payload so the installer works offline
- run it from the NSIS installer only when WebView2 runtime detection says it is missing

Reason:

- Microsoft documents that the WebView2 runtime must be present on client machines
- Microsoft documents the standalone installer as the offline distribution path
- that matches the "no manual download" and "offline-capable installer" constraints better than the bootstrapper

---

## 8. OBS detection and path validation

This is the key machine-agnostic part.

### Detection order

1. Check known uninstall/registry entries for OBS Studio.
2. Check common install roots such as `C:\Program Files\obs-studio`.
3. If exactly one valid OBS root is found, use it.
4. If multiple are found, ask the user which one to use.
5. If none are found, ask the user to browse to OBS.

Detection sources to use:

- uninstall registry entries for OBS Studio
- known install roots
- stored previous OBS root for upgrades/repairs

### Validation rules

A selected OBS root is valid only if:

- it contains a real OBS executable layout
- it is structurally compatible with the plugin destination layout we need

At minimum, validation should confirm expected OBS executable presence before any copy/install step is allowed.

Locked validation rule:

- the chosen OBS root must contain `bin\64bit\obs64.exe`

If that file is not present, the path is rejected.

Computed plugin target from OBS root:

- `<OBSROOT>\obs-plugins\64bit\steaming-plugin.dll`

If the installer must instead target an OBS portable plugin tree variant discovered during implementation, that change must be applied consistently in:

- installer copy logic
- stored install metadata
- app UI path display
- update/uninstall logic

### Stored install metadata

After install, store:

- resolved OBS root
- resolved plugin target path
- installed app version
- installed plugin version

This is used by repair and updater flows.

Store it in:

- installer registry entries under the app's install metadata

Do not rely on `%APPDATA%` for install-location metadata. `%APPDATA%` is user data and must survive uninstall; install metadata is installer-owned state.

---

## 9. Release build versus dev build

This is mandatory. The current repo behavior proves why.

Root problem:

- `STEAMING_AUTO_DEPLOY_TO_OBS` defaults `ON` in
  [obs/obs-plugintemplate/CMakeLists.txt](../obs/obs-plugintemplate/CMakeLists.txt).
- That means a normal plugin build can write directly into a live OBS install on the build machine.

That is acceptable for dev convenience only.

### Required separation

#### Dev build

Can keep optional local auto-deploy convenience.

#### Release packaging build

Must:

- be one repo-controlled release command/script, not a manual multi-step checklist
- force `STEAMING_AUTO_DEPLOY_TO_OBS=OFF`
- build the plugin into a staging output directory only
- publish the app into a staging output directory only
- copy from staging into installer payload
- build the installer as part of that same release flow
- never write into Rob's live OBS install
- never move files out of an existing release folder

Locked repo expectation:

- the repo gets one release script/entry point dedicated to packaging

Recommended shape:

- `build/release.ps1`

Responsibilities of that script:

1. read version
2. clean/create staging folders
3. publish WinUI self-contained output into staging
4. build plugin with auto-deploy disabled into staging
5. fetch/extract pinned NSIS zip into repo-local tool cache if missing
6. fetch/copy pinned WebView2 standalone installer into release prerequisites cache if missing
7. compile the NSIS installer
8. place final installer EXE into release output
9. emit checksum file for updater/release manifest use

### Packaging rule

Release packaging is copy-only from staged artifacts.

Never:

- move
- reuse live install folders as staging
- build directly into final installer payload
- touch the local OBS installation as part of packaging

Recommended staging layout:

- `artifacts/release/app/`
- `artifacts/release/plugin/`
- `artifacts/release/prereqs/`
- `artifacts/release/installer/`
- `artifacts/tools/nsis/`

---

## 10. Updater design

The updater should be installer-driven, not "replace running files in place".

### Components

- `Steaming.Updater.exe` - small helper process
- hosted release manifest
- hosted signed installer executable

Manifest fields:

- version
- installer URL
- installer SHA-256
- minimum supported upgrade version
- release notes URL or text
- mandatory flag

### App behavior

The app:

- checks for updates
- shows update availability
- launches updater when the user accepts

The app does NOT overwrite itself.

### Updater behavior

The updater:

1. Reads installed metadata.
2. Downloads and validates the signed release manifest.
3. Compares versions.
4. Downloads the new installer executable.
5. Verifies checksum/signature.
6. Closes Steaming if running.
7. If plugin update is included, closes OBS before proceeding.
8. Runs the installer in upgrade mode.

Locked updater behavior:

- updater never patches binaries directly
- updater always hands off to the same installer product
- if OBS is running and plugin files are going to change, updater blocks and asks for OBS closure before continuing

### OBS path handling during updates

The updater reuses the stored OBS path.

If that path is missing or invalid:

- updater must prompt again
- updater must not silently install to a guessed fallback path

### Installer/uninstaller handoff for upgrades

Locked strategy:

- the new installer handles upgrade in-place
- if an older install is present, the installer reads the uninstall command and install metadata, removes/replaces the old app files, then installs the new payload
- user data in `%APPDATA%\Steaming` is not removed as part of this upgrade flow

---

## 11. Installed state and uninstall rules

### Preserve by default

Normal uninstall must leave intact:

- `%APPDATA%\Steaming\settings.json`
- `%APPDATA%\Steaming\credentials.json`
- `%APPDATA%\Steaming\analytics.db`
- caches and other user-state files under `%APPDATA%\Steaming`

### Remove on uninstall

Normal uninstall removes:

- app binaries under `Program Files\Steaming`
- installed plugin files from the selected OBS plugin target
- shortcuts
- installer registry entries

### Optional full cleanup

If a full data wipe option is ever added, it must be explicit and opt-in.

---

## 12. Versioning requirement before updater work

Updater correctness depends on version discipline.

Current state:

- app version is in [Steaming.Core/Steaming.Core.csproj](../Steaming.Core/Steaming.Core.csproj)
- plugin version is separately in [obs/obs-plugintemplate/buildspec.json](../obs/obs-plugintemplate/buildspec.json)

That split is a release risk.

Before building the updater, versioning must be unified or at least generated from one authoritative release version so:

- app version
- plugin version
- installer version
- manifest version

all agree.

Locked version source requirement for implementation:

- one repo-controlled release version value must drive:
  - app version
  - plugin version
  - installer filename
  - updater manifest version

If the current project structure cannot do that cleanly, fix that first before implementing updater logic.

---

## 13. Required implementation phases

### Phase 1 - release artifact separation

- add proper release staging directories
- disable plugin auto-deploy for release builds
- produce self-contained app publish output
- produce plugin build output into staging
- add one repo release entry point that runs the whole release packaging flow
- add NSIS tool bootstrap to the repo release flow:
  - if pinned NSIS tool files are absent from the repo tool cache, download the pinned official NSIS zip
  - extract into a repo-local tools/cache directory
  - call `makensis` from that repo-local tool path
  - no manual installer-tool installation on the build machine
- pin the WebView2 standalone installer acquisition step for offline bundling

### Phase 2 - installer foundation

- create the NSIS script(s)
- app payload packaging
- plugin payload packaging
- uninstall/upgrade handling
- installer UI pages
- add silent install and silent upgrade command-line support
- write install metadata to registry

### Phase 3 - OBS detection UI + validation

- detect OBS automatically
- browse/select fallback
- validate
- persist selected path
- use NSIS built-in directory/custom-page capabilities for this flow

### Phase 4 - prerequisite bundling

- bundle required runtimes/installers
- fully offline-capable installer flow
- detect WebView2 before launching prerequisite installer
- skip prerequisite install when already present

### Phase 5 - updater

- manifest format
- updater helper
- app-side update check
- silent/in-place upgrade path

### Phase 6 - app UI cleanup

- remove hardcoded plugin path text from the OBS page
- replace with detected/installed path from configuration/install metadata

### Phase 7 - updater implementation

- add updater project
- add installer handoff logic
- add release manifest production
- add checksum generation
- add release-hosting publish step

---

## 14. Runtime verification checklist

This plan is not done until these are verified on real machines:

1. Clean machine with no Steaming installed:
   - one installer run succeeds
   - app launches
   - plugin files land in the correct OBS path

2. Machine with OBS in default location:
   - detection works without user input

3. Machine with OBS in custom location:
   - detection or browse flow works
   - plugin installs correctly

4. Machine with OBS absent:
   - installer gives a real choice
   - can install app without silently breaking plugin expectations, or explicitly requires OBS based on chosen UX

5. Upgrade from older Steaming build:
   - settings/tokens/analytics preserved
   - plugin upgraded cleanly

6. Uninstall:
   - binaries removed
   - user data preserved by default

7. Updater:
   - detects update
   - downloads installer
   - upgrades app + plugin
   - reuses stored OBS path

8. Repo release build:
   - one release script produces the final installer EXE
   - no manual installer-tool installation was required first
   - no files were copied into Rob's live OBS install during packaging

---

## 15. Explicitly rejected outcomes

These do NOT satisfy the request:

- "download this runtime first"
- "open installer tooling and configure this manually"
- "copy this DLL into OBS yourself"
- "zip release and a README"
- "works on my machine because OBS is in the default path"
- release packaging that touches Rob's local OBS install
- "the installer tool must be manually installed first"

---

## 16. Bottom line

The correct plan is:

- NSIS is the installer tool because it is verified free for any use and scriptable from the command line.
- The shipped output is one proper Windows installer.
- The installer is machine-agnostic.
- The installer detects OBS or asks where it is.
- The installer validates before copying.
- The installer bundles prerequisites.
- Release packaging uses staged copies only and never touches Rob's live OBS install.
- Running the repo release build produces the installer.
- Updates reuse the same install metadata and installer model.

Anything less is not the feature that was asked for.

---

## 17. Verified source notes

Verified external facts used by this plan:

- NSIS is free for any use and open source, and has a command-line compiler:
  - https://nsis.sourceforge.io/Docs/Chapter1.html
  - https://nsis.sourceforge.io/Docs/Chapter3.html
  - https://nsis.sourceforge.io/Docs/
- NSIS standard pages, custom pages, registry reads, and scripting support:
  - https://nsis.sourceforge.io/Docs/Chapter4.html
  - https://nsis.sourceforge.io/Docs/nsDialogs/Readme.html
  - https://nsis.sourceforge.io/Docs/Modern%20UI%202/Readme.html
- NSIS official download/release availability including zip distribution:
  - https://nsis.sourceforge.io/Download
  - https://sourceforge.net/projects/nsis/files/NSIS%203/3.12/nsis-3.12.zip/download
- WiX maintenance-fee wording / source-build reference:
  - https://wixtoolset.org/docs/development/
- Windows App SDK self-contained deployment:
  - https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps
  - https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/deploy-overview
- WebView2 runtime distribution requirement and offline installer path:
  - https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution
  - https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/evergreen-vs-fixed-version
  - https://developer.microsoft.com/en-us/microsoft-edge/webview2/
