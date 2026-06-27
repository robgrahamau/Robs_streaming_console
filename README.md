# Steaming

Steaming is a Windows desktop streaming control app with a companion OBS plugin.

It is built around simultaneous Twitch + Kick streaming and provides:

- live chat ingestion and chat overlays
- alert rendering through the OBS plugin
- labels, goals, emoji rain, music overlays, and analytics
- Twitch + Kick auth/session management
- WinUI desktop control surface

## Architecture

Main components:

- `Steaming.WinUI`
  - the shipping desktop app
- `Steaming.Application`
  - ViewModels and app services
- `Steaming.Core`
  - models, auth/config, IPC client logic
- `Steaming.Data`
  - SQLite repositories
- `obs/obs-plugintemplate`
  - the OBS plugin that does all rendering

Rendering stays in the OBS plugin. The desktop app sends state and event payloads over the named pipe described in [ARCHITECTURE.md](ARCHITECTURE.md).

## Build

WinUI app:

```powershell
dotnet build Steaming.WinUI/Steaming.WinUI.csproj -c Release
```

OBS plugin:

```powershell
cd obs/obs-plugintemplate
cmake --build build_x64 --config RelWithDebInfo
```

After building the plugin, restart OBS to load the updated DLL.

## Auth And Secrets

There are two different auth-related storage locations:

### 1. User login tokens and refreshed session credentials

These are **not stored in the repo**.

They are saved locally in:

- `%APPDATA%\Steaming\credentials.json`

They are DPAPI-encrypted in [Steaming.Core/Auth/TokenStore.cs](Steaming.Core/Auth/TokenStore.cs).

This file can contain:

- Twitch access token
- Kick access token
- Kick refresh token
- bot account tokens
- stored usernames / channel IDs
- saved Kick client ID / secret copied into the credential store after login setup

### 2. App OAuth client configuration

These are currently **hardcoded in the repo** in:

- [Steaming.Core/Configuration/PlatformAuthConfig.cs](Steaming.Core/Configuration/PlatformAuthConfig.cs)

That file currently contains:

- `TwitchClientId`
- `KickClientId`
- `KickClientSecret`

If you want a public GitHub branch, this is the file that must be treated as public-safe configuration rather than populated secrets.

## Public Branch Notes

For a public branch:

- keep `%APPDATA%\Steaming\credentials.json` out of Git entirely
- do not publish populated client secrets in `PlatformAuthConfig.cs`
- replace repo-populated OAuth values with environment/local override loading before publishing, or maintain a public-safe branch with placeholders only

## Repo Docs

- [ARCHITECTURE.md](ARCHITECTURE.md)
  - system architecture, pipe protocol, DB notes, build commands
- [MISTAKES.md](MISTAKES.md)
  - known historical failures and rules to avoid repeating them
- [HANDOFF.md](HANDOFF.md)
  - current session handoff/state
- [hypetrain.md](hypetrain.md)
  - Hype Train feature design sketch

## Current State

- WinUI is the only shipping desktop app
- OBS plugin does all overlay rendering
- named pipe is the only desktop-to-plugin IPC path
