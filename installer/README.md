# Steaming installer

Produces one Windows installer EXE that installs the Steaming app **and** the OBS plugin,
handles the WebView2 runtime, and uninstalls cleanly while preserving user data.

## Build it

```powershell
# 1) Build the C++ plugin first (the release script copies the artifact, it does not build it)
cmake --build obs/obs-plugintemplate/build_x64 --config RelWithDebInfo

# 2) Produce the installer (publishes the app, bootstraps NSIS, compiles)
build\release.ps1
```

Output: `artifacts\release\installer\Steaming-Setup-<version>.exe` (+ `.sha256`).

Options:
- `-SkipWebView2` — don't bundle the WebView2 bootstrapper.
- `-SignCert <path.pfx> -SignPassword <pw>` — sign the installer (optional; unsigned by default).
- `-PluginConfig <cfg>` — which plugin build config to stage (default `RelWithDebInfo`).

## What it installs / where

| Thing | Location | Removed on uninstall? |
|---|---|---|
| App (self-contained) | `C:\Program Files\Steaming\` | Yes |
| OBS plugin | `C:\ProgramData\obs-studio\plugins\steaming-plugin\` | Yes |
| Start menu shortcut | All-users Start Menu → `Steaming` | Yes |
| Registry (install metadata + ARP) | `HKLM\Software\Steaming`, ARP `Uninstall\Steaming` | Yes |
| **User data** | `%APPDATA%\Steaming\` (settings, credentials, analytics) | **No — always preserved** |

The plugin always goes to the fixed ProgramData folder — OBS loads it from there regardless of
where OBS is installed. The installer **never** touches the OBS install directory, so it cannot
corrupt an OBS installation.

## Safety guarantees in the script

- Every `RMDir /r` is guarded: it only runs when the path is non-empty **and** a sentinel file
  proves the folder is ours (`Steaming.WinUI.exe` for the app, `steaming-plugin.dll` for the plugin).
- `%APPDATA%\Steaming` is never deleted — reinstalling keeps your logins and analytics.
- Upgrades run the previous uninstaller in place first, then install fresh; user data survives.
- If the app is running, it's closed first; if OBS is running, the user is asked to close it
  (the plugin step is skipped rather than forcing OBS to quit).

## How to test SAFELY before sharing

Do the uninstall test in a **Windows Sandbox or a throwaway VM**, not your dev machine:

1. Run the installer → confirm app launches, plugin loads in OBS, Start menu shortcut works.
2. Create some settings/logins in the app.
3. Run the installer again (same or higher version) → confirm it upgrades and your settings remain.
4. Uninstall from Add/Remove Programs → confirm:
   - `C:\Program Files\Steaming\` is gone,
   - `C:\ProgramData\obs-studio\plugins\steaming-plugin\` is gone,
   - `%APPDATA%\Steaming\` **still exists** with your data,
   - no other OBS plugin files were touched.
