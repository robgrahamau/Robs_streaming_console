# Steaming - Architecture Reference

Read this file before changing any subsystem. It is intended to describe the code that actually exists now, not an aspirational design.

## Current Product Shape

Steaming is a WinUI 3 desktop application that drives a native OBS plugin over a named pipe.

- Shipping desktop app: `Steaming.WinUI`
- Shared application logic: `Steaming.Application`
- Shared models, platform adapters, IPC, settings, auth storage: `Steaming.Core`
- SQLite repositories: `Steaming.Data`
- Native OBS renderer: `obs/obs-plugintemplate`
- Web project: `Steaming.Web` exists but is currently a minimal placeholder, not part of the shipping runtime path

There is no browser-source overlay architecture in the current product. The OBS plugin does all rendering.

## Project Map

| Project | Role | Notes |
|---|---|---|
| `Steaming.WinUI` | Shell, windows, pages, WinUI wiring | The only shipping desktop UI |
| `Steaming.Application` | ViewModels and non-UI services | Must stay free of WPF/WinUI types in app logic |
| `Steaming.Core` | Models, platform integrations, IPC, settings, auth | Holds the named pipe server and adapters |
| `Steaming.Data` | SQLite persistence | Analytics and activity repositories |
| `Steaming.Web` | Placeholder web app | Not used for overlay rendering |
| `obs/obs-plugintemplate` | OBS module | **THE plugin.** The shipped `steaming-plugin.dll` is built from HERE and nowhere else |
| ~~`Steaming.Plugin`~~ | Removed 2026-06-27 | Was a dead/abandoned plugin tree (never built, never deployed); deleted to prevent confusion. Recover from git history if ever needed |

## Startup and Composition

`Steaming.WinUI/App.xaml.cs` is the real composition root.

### What startup does

1. Sets `WEBVIEW2_USER_DATA_FOLDER` to `%LOCALAPPDATA%\Steaming\WebView2`.
2. Builds the DI host and registers the app services.
3. Shows a splash window while services start.
4. Loads saved settings and encrypted credentials.
5. Starts long-lived services:
   - named pipe server
   - overlay dispatcher
   - sound dispatcher
   - music services
   - chat TTS
   - analytics collector
   - chatbot
   - stream data polling
6. Reconnects saved platform sessions:
   - Twitch main account
   - Twitch bot account
   - Kick bot token state
   - YouTube state
   - remote Kick bridge session
7. Optionally auto-connects OBS WebSocket.
8. Creates `MainWindow` and injects a hidden WebView2 host used by auth and Kick channel resolution helpers.
9. On shutdown, persists avatar state, disposes TTS, and stops the host.

### Important registrations

The WinUI app currently registers these major services:

- Event and IPC:
  - `EventBus`
  - `PluginPipeServer`
  - `OverlayDispatcher`
  - `SoundDispatcher`
- Platforms and auth:
  - `TwitchAdapter`
  - `KickAdapter`
  - `YouTubeLiveChatService`
  - `RemoteKickBridgeClient`
  - `KickRaidListener`
  - `TwitchEventSubClient`
  - `PlatformAuthConfig`
  - `TokenStore`
  - `PlatformCredentialService`
  - `PlatformSessionFlowService`
- Stream/runtime:
  - `StreamDataService`
  - `StreamManagementService`
  - `ObsWebSocketService`
  - `ModerationService`
  - `ViewerListService`
  - `ChatbotService`
  - `IntegrationConfigService`
- Data:
  - `ActivityRepository`
  - `AnalyticsRepository`
  - `AnalyticsCollectorService`
- TTS and audio:
  - `ChatTtsService`
  - `WinRtTtsBackend`
  - `KokoroTtsBackend`
  - `KokoroAssetService`
  - `EspeakNgPhonemizer`
- Music:
  - `MusicPlayerService`
  - `MusicLibraryService`
  - `MusicOverlayDispatcher`
- Avatar and tracking:
  - `MicCaptureService`
  - `CameraCaptureService`
  - `FaceTrackingService`
  - `FaceRetargetService`
  - `FaceTrackingPersistenceService`
  - `FaceTrackingDiagnosticsService`
  - `ReplayFrameService`
  - `AvatarRenderService`
  - `NdiSendService`
  - `AvatarViewModel`
- Shell:
  - `MainViewModel`

## UI Surface

`Steaming.WinUI/MainWindow.xaml` is the shell.

### Navigation sections

- STREAM
  - Dashboard
  - Stream Management
  - Viewers
  - Activity
- CONFIGURE
  - Overlays
  - Chat
  - Chatbot
  - Emoji Rain
  - Music
  - Analytics
  - Avatar
- SYSTEM
  - Runtime Status
  - Connections
  - OBS Config
  - Settings
  - About

### Window-level responsibilities

- Hosts the `NavigationView`
- Shows the live status bar, health pill, Twitch/Kick/pipe state, and version
- Owns `HiddenWebHost` for off-screen WebView2 usage
- Prompts for stream metadata mismatch and reconnect flows

ViewModels live in `Steaming.Application/ViewModels`. UI code should stay view-only.

## Configuration and Credentials

### Settings file

`Steaming.Core/Services/AppSettings.cs`

- Path: `%APPDATA%\Steaming\settings.json`
- Stores:
  - alert configs and custom alerts
  - rewards
  - labels and goals
  - emoji rain
  - music settings
  - chat overlay profiles
  - Kick bridge config
  - debug log config
  - OBS WebSocket settings
  - last-applied title/category flags
  - per-platform active toggles
  - per-destination stream warning toggles
  - TTS settings including Kokoro

Notable behavior:

- `EnsureDefaultEvents()` patches in missing alert keys for persisted settings.
- `NormalizeChatOverlayProfiles()` guarantees a default/current overlay profile exists.

### Credential store

`Steaming.Core/Auth/TokenStore.cs`

- Path: `%APPDATA%\Steaming\credentials.json`
- Encryption: DPAPI
- Stores:
  - Twitch main and bot tokens
  - Kick main and bot tokens
  - YouTube main and bot tokens
  - Twitch/Kick/YouTube client credentials where used
  - Kick broadcaster identity values
  - YouTube token expiry metadata

### Platform auth config

`Steaming.Core/Configuration/PlatformAuthConfig.cs`

- Holds the current redirect URI and platform client configuration
- `KickDirectLoginEnabled` is currently `true`

## Auth and Session Flows

### Login flow builder

`Steaming.Application/Services/PlatformSessionFlowService.cs`

- Twitch: implicit-flow URL with broad scopes
- Kick: PKCE auth-code flow
- YouTube: PKCE auth-code flow with offline access
- Bot-account login flows are also built here

### Credential and token operations

`Steaming.Application/Services/PlatformCredentialService.cs`

- Saves and clears platform logins
- Resolves Twitch username via Helix
- Exchanges and refreshes Kick tokens
- Exchanges and refreshes YouTube tokens
- Resolves YouTube channel identity
- Resolves Kick identity and channel metadata

Important detail:

- The current Kick identity helper uses public user and channel endpoints.
- `KickChatroomId` in the saved config is actually the broadcaster user ID used by the current app logic.

### Login window

`Steaming.WinUI/LoginWindow.xaml.cs`

- Uses WebView2 for auth
- Supports both fragment and code redirects
- Can clear Twitch/Kick/YouTube cookies using a temporary hidden WebView2 instance

## Event Flow

`Steaming.Core/EventBus.cs` is the central event spine used by the desktop app.

Typical flow:

1. A platform adapter receives chat or an event.
2. The adapter publishes normalized app events into `EventBus`.
3. Subscribers react:
   - `OverlayDispatcher` sends named-pipe updates to OBS
   - `ActivityRepository` records activity
   - `AnalyticsCollectorService` updates stream session state
   - `MainViewModel` updates UI-facing collections and counters
   - `ChatbotService` and moderation services react where relevant

`App.xaml.cs` wires the event bus into the shell ViewModel, analytics, activity logging, and the dispatcher services during startup.

## Platform Integrations

### Twitch

Primary files:

- `Steaming.Core/Platforms/TwitchAdapter.cs`
- `Steaming.Core/Platforms/TwitchEventSubClient.cs`
- `Steaming.Core/Services/TwitchBadgeService.cs`
- `Steaming.Core/Services/ThirdPartyEmoteService.cs`

Current behavior:

- Uses TwitchLib v4 for chat
- Uses EventSub for follows, subs, raids, and related events
- Moderator/subscriber/VIP state is derived from `msg.Badges`, not nonexistent TwitchLib convenience properties
- Downloads Twitch badges plus BTTV/FFZ/7TV assets
- Supports broadcaster and optional bot account connections

### Kick direct adapter

Primary file: `Steaming.Core/Platforms/KickAdapter.cs`

Current behavior:

- Uses Kick's Pusher-based realtime connection for chat/events
- Parses chat, follows, subscriptions, gifted subs, and host/raid style events
- Sends outbound chat through `https://api.kick.com/public/v1/chat`
- Uses main or bot token depending on configuration

### Remote Kick bridge

Primary file: `Steaming.Core/Platforms/RemoteKickBridgeClient.cs`

Current behavior:

- Connects to a configured remote bridge over WebSocket
- Sends:
  - `kick.send_chat`
  - `kick.bootstrap_session`
- Publishes bridge-originated events into `EventBus`
- Reconnects automatically with backoff
- Emits auth rejection status when the bridge rejects credentials

This is distinct from the direct Kick adapter. Both exist in the codebase because the app supports both direct Kick runtime behavior and a remote bridge path.

### Kick raid listener

Primary file: `Steaming.Core/Platforms/KickRaidListener.cs`

Current behavior:

- Separate opt-in listener used for unsupported Kick raid and follower-count cases
- Prefers a hidden-WebView2-assisted channel resolver to get past Cloudflare
- Falls back to public HTTP lookup paths if needed
- Publishes raid events and follower updates back into app state

### YouTube

Primary file: `Steaming.Core/Platforms/YouTubeLiveChatService.cs`

Current behavior:

- Uses gRPC streaming for live chat
- Uses REST to resolve active broadcasts and send messages
- Normalizes YouTube text messages, memberships, super chat/super stickers, and gifted events into the app event model
- Maintains live chat and active-broadcast state
- Refreshes tokens when required

## Stream Data and Runtime Status

### Stream polling

`Steaming.Core/Services/StreamDataService.cs`

Current behavior:

- Polls Twitch, Kick, and YouTube every 30 seconds
- Tracks:
  - per-platform viewer counts
  - per-platform follower counts
  - per-platform subscriber counts
  - combined totals
  - live state
  - uptime
  - title/category
  - recent follower/subscriber/gift/donation labels

Notable rules:

- Twitch metadata is authoritative when Twitch is live.
- Kick metadata is used as fallback when Twitch is not live.
- Follower totals only include Kick when a real Kick follower count is known.
- Kick auth refresh failure is tied to the exact token that failed, not latched forever.

### Stream metadata editing

`Steaming.Core/Services/StreamManagementService.cs`

- Updates Twitch/Kick title and category
- Searches categories/games
- Reads current metadata
- Uses Kick metadata cache helpers where needed

### OBS WebSocket

`Steaming.Core/Services/ObsWebSocketService.cs`

- Handles obs-websocket 5.x auth and reconnect
- Exposes:
  - stream state
  - scene list
  - current scene
  - input list
  - input setting updates

## Chat, Moderation, Viewers, and Bot

### Chat overlays and message handling

`Steaming.Core/Services/OverlayDispatcher.cs`

This is the service that translates app events into OBS plugin messages.

It currently handles:

- chat messages
- alerts
- labels
- goals
- emoji rain
- music now playing
- music lyrics
- chat settings refresh
- plugin bootstrap payloads on pipe connect

Important current behavior:

- Dedupes pending outgoing chat echoes
- Sends merged display-platform icons for chat entries
- Re-sends chat refreshes when emote/badge downloads complete
- Sends all alert settings, chat settings, goal names, label layouts, goal layouts, emoji rain settings, and music overlay state when the plugin connects

Label indices currently sent by the dispatcher:

- `0` recent follower
- `1` recent subscriber
- `2` subscriber count
- `3` viewer count
- `4` follower count
- `5` stream uptime
- `6` recent donation
- `7` top donation
- `8` donation total
- `9` recent gift sub

### Chatbot

`Steaming.Core/Services/ChatbotService.cs`

The current chatbot service owns:

- command definitions
- timers
- shoutout behavior
- auto-mod settings
- message send callbacks
- moderation callbacks
- custom alert trigger hooks
- sound trigger hooks

### Moderation

`Steaming.Core/Services/ModerationService.cs`

- Performs platform moderation actions used by the shell and bot features

### Viewer list

`Steaming.Core/Services/ViewerListService.cs`

- Maintains viewer/chatter state used by the UI

### Emote and badge caching

- Emotes: `%APPDATA%\Steaming\emote_cache`
- Badges: `%APPDATA%\Steaming\badge_cache`
- `EmoteCache` coordinates single-download-per-ID behavior
- `OverlayDispatcher` triggers chat redraws when assets finish downloading

## OBS Plugin and IPC

### Active plugin

The active (and now ONLY) OBS plugin source is `obs/obs-plugintemplate`. **All C++ edits go here.**
The former second tree `Steaming.Plugin/` was deleted 2026-06-27 (it was never built or deployed).

**Verified empirically 2026-06-27 before deleting the dead tree** (do not take this on faith — re-run the check if in doubt):
- The deployed DLL `C:\ProgramData\obs-studio\plugins\steaming-plugin\bin\64bit\steaming-plugin.dll`
  was **byte-for-byte identical** to `obs/obs-plugintemplate/build_x64/RelWithDebInfo/steaming-plugin.dll`
  (matching SHA-256), proving the shipped DLL is built from `obs/obs-plugintemplate`.
- The old `Steaming.Plugin/` had **no build directory and produced no DLL** anywhere on disk, and its
  `plugin-main.cpp` registered only 2 sources (alert, chat) vs. the active tree's 7 — it could not have
  produced the goal/label/music/lyrics/emoji-rain sources visible in OBS.
- Re-verify the active build any time with:
  `sha256sum "C:/ProgramData/obs-studio/plugins/steaming-plugin/bin/64bit/steaming-plugin.dll" obs/obs-plugintemplate/build_x64/RelWithDebInfo/steaming-plugin.dll`

`obs/obs-plugintemplate/src/plugin-main.cpp` registers:

- `alert_source`
- `chat_source`
- `label_source`
- `goal_source`
- `emoji_rain_source`
- `music_source`
- `lyrics_source`

### Named pipe

`Steaming.Core/Ipc/PluginPipeServer.cs`

- Pipe name: `\\.\pipe\steaming`
- Frame format: `[1]type [4]payloadLen LE [N]payload`
- The app sends a hello JSON payload immediately after connect
- Ping/pong and plugin hello are supported
- Plugin capability/version metadata is tracked by the app

### Message types

These must stay synchronized between C# and C++.

| Hex | Name | Purpose |
|---|---|---|
| `0x01` | `Ping` | Keepalive |
| `0x02` | `Pong` | Keepalive response |
| `0x03` | `Hello` | Handshake metadata |
| `0x10` | `RenderAlert` | Legacy alert payload |
| `0x11` | `RenderChat` | Current chat payload |
| `0x12` | `UpdateGoal` | Legacy goal update path |
| `0x13` | `Clear` | Clear/reset message |
| `0x14` | `RenderAlertV2` | Current alert layout payload |
| `0x15` | `RefreshChat` | Force chat source redraw |
| `0x16` | `UpdateChatSettings` | Chat overlay settings |
| `0x17` | `ChatSourceList` | Plugin -> app source size report |
| `0x18` | `SetLabelLayout` | Push one label layout |
| `0x19` | `SetGoalLayout` | Push one goal layout |
| `0x1A` | `TriggerEmojiRain` | Fire emoji rain |
| `0x1B` | `EmojiRainSettings` | Push emoji rain settings |
| `0x1C` | `SetGoalNames` | Push goal display names |
| `0x1D` | `MusicNowPlaying` | Push current track info |
| `0x1E` | `MusicPosition` | Push track position |
| `0x1F` | `MusicLyrics` | Push lyrics payload |
| `0x20` | `MusicNowPlayingSettings` | Push now-playing settings |
| `0x21` | `MusicLyricsSettings` | Push lyrics settings |

### Hello payload

`Steaming.Core/Ipc/PipeHelloPayload.cs`

- Protocol version: `1`
- App capabilities currently include:
  - `render_alert_v2`
  - `render_chat`
  - `label_layouts`
  - `goal_layouts`
  - `emoji_rain`
  - `chat_source_list`

### Chat payload format

`Steaming.Core/Ipc/ChatPayload.cs`

The current chat payload is version 5, not the older version documented in previous architecture notes.

```
[2+N] platform         UTF-8 primary platform
[2+N] platformIcons    UTF-8 joined display platforms, e.g. "Twitch|Kick"
[2+N] username         UTF-8
[2+N] message          UTF-8
[2+N] color            UTF-8 "#RRGGBB"
[2+N] timestamp        UTF-8
[1]   flags            bit0=broadcaster bit1=moderator bit2=subscriber bit3=vip bit4=highlighted bit5=hasBits
[4]   bitsAmount       int32 LE
[2]   subMonths        uint16 LE
[1]   badgeCount
each badge: [2+N] cachedFilePath
[1]   emoteCount
each emote: [2]start [2]end [2+N]cachedFilePath
```

### Chat settings payload

`Steaming.Core/Ipc/ChatOverlaySettingsPayload.cs`

- The current chat settings payload does not send width/height
- OBS source properties are the source of canvas dimensions

### Source list payload

`App.xaml.cs` currently parses `ChatSourceList` as:

```
[2]   count
repeat count times:
  [2+N] sourceName
  [4]   width
  [4]   height
```

### Wire-format rule

If a payload changes, the C# serializer and the C++ parser must be updated in the same edit set.

## Alert Layout System

### Shared model

`Steaming.Core/Models/AlertLayout.cs`

Current element types:

- `Rect`
- `Text`
- `Image`
- `Gif`
- `Audio`
- `GoalBar`
- `Video`

Current text transition types:

- `Cut`
- `TypeOn`
- `SlideLeft`
- `SlideRight`
- `Fade`
- `Morph`

Current video end behaviors:

- `Loop`
- `Hold`
- `EndHide`
- `EndFade`
- `HoldFirst`

The alert layout model also supports:

- rich text spans
- keyframes
- outline and shadow
- audio envelopes
- goal bars
- image/gif/video elements
- text span transitions

### Editor and preview surfaces

Primary files:

- `Steaming.Application/ViewModels/AlertEditorViewModel.cs`
- `Steaming.WinUI/AlertEditorWindow.xaml.cs`
- `Steaming.WinUI/Pages/OverlaysPage.xaml.cs`

Current state:

- Video elements are implemented in the editor and preview flow
- Text transitions are implemented in the editor and plugin
- Overlays page includes integrated playback preview for gif/video/audio

### Plugin renderer

Primary files:

- `obs/obs-plugintemplate/src/layout_renderer.cpp`
- `obs/obs-plugintemplate/src/layout_types.h`

The native plugin renderer is the final rendering authority for alert, label, and goal layouts inside OBS.

## Analytics and Activity Persistence

### Activity feed

`Steaming.Data/ActivityRepository.cs`

- Stores historical activity used by the app UI

### Analytics database

`Steaming.Data/AnalyticsRepository.cs`

- Path: `%APPDATA%\Steaming\analytics.db`

Current tables:

#### `stream_sessions`

- base fields for session timing and totals
- per-platform peak/average/sample columns for Twitch, Kick, and YouTube
- merge metadata such as `KickSessionId` and `MergedSessionIds`

#### `viewer_snapshots`

- snapshot rows taken every 30 seconds
- stores `twitch_viewers`, `kick_viewers`, `youtube_viewers`, and `chat_count`

Important current behavior:

- Missing columns are added via migration checks against `PRAGMA table_info`
- The code stores separate single-platform sessions
- Combined or dual-platform views are query-time merge concepts, not a separate persisted platform row
- Dual filters supported:
  - `Twitch+Kick`
  - `Twitch+YouTube`
  - `Kick+YouTube`
- Legacy `"Both"` is treated as Twitch + Kick

### Analytics collection

`Steaming.Application/Services/AnalyticsCollectorService.cs`

- Uses a 20-minute resume window
- Ends sessions after two consecutive offline polls
- Writes snapshots every 30 seconds
- Treats chatter count as a floor for viewer count
- Tracks separate Twitch/Kick/YouTube sessions and merges later in repository queries

## Avatar, Face Tracking, and NDI

### Avatar ViewModel

`Steaming.Application/ViewModels/AvatarViewModel.cs`

Current responsibilities:

- avatar model path and run state
- NDI toggle and availability
- mic and camera device selection
- tracking model selection
- mouth-mode selection
- calibration values
- diagnostics strings
- saved poses
- bone control and IK state

Persistence:

- `%APPDATA%\Steaming\avatar.json`
- `%APPDATA%\Steaming\poses.json`

### Renderer

`Steaming.Application/Services/AvatarRenderService.cs`

Current renderer shape:

- Off-screen Direct3D 11 renderer
- Render size: `540 x 960`
- CPU morph blending
- CPU skinning
- D3D11 rasterization
- BGRA readback for preview and NDI
- Bone rotation overrides and two-bone IK

This is not a Skia-based avatar renderer.

### Tracking and retargeting

Primary files:

- `MicCaptureService.cs`
- `CameraCaptureService.cs`
- `FaceTrackingService.cs`
- `FaceRetargetService.cs`
- `FaceTrackingPersistenceService.cs`
- `FaceTrackingDiagnosticsService.cs`
- `ReplayFrameService.cs`
- `MediaPipeFaceProvider.cs`

Current behavior:

- Supports `OpenSeeFace` and `MediaPipe`
- Uses DirectML when available, CPU fallback otherwise
- Face tracking runs on a background thread
- Mic capture provides amplitude and coarse vowel hints
- Retargeting blends face landmarks with mic-driven mouth behavior
- The WinUI Avatar page exposes calibration, diagnostics, preview, saved poses, camera controls, and NDI state

### NDI

`Steaming.Application/Services/NdiSendService.cs`

- Dynamically loads the NDI Runtime 6 DLL
- Publishes the avatar output as `Steaming Avatar`

## TTS and Music

### Chat TTS

`Steaming.Application/Services/ChatTtsService.cs`

- Queue-based TTS pipeline
- Supports:
  - `WinRtTtsBackend`
  - `KokoroTtsBackend`
- Falls back to WinRT if Kokoro is unavailable or fails

Related files:

- `KokoroAssetService.cs`
- `KokoroTokenizer.cs`
- `KokoroVoices.cs`
- `EspeakNgPhonemizer.cs`

Kokoro support is implemented and exposed in the WinUI settings UI.

### Music overlay

Primary pieces:

- `MusicPlayerService`
- `MusicLibraryService`
- `MusicOverlayDispatcher`

Named-pipe message types exist for:

- now playing
- track position
- lyrics
- now-playing settings
- lyrics settings

The OBS plugin has matching source types for now-playing and lyrics rendering.

## Installer and Release Packaging

### Current release script

`build/release.ps1`

This is implemented, not just planned.

Current behavior:

- stages app and plugin artifacts under `artifacts/release`
- publishes the WinUI app
- stages the OBS plugin DLL and locale files
- downloads runtime prerequisites as needed
- bootstraps NSIS from a zip
- runs `makensis`
- can optionally sign artifacts
- emits SHA-256 output

### Installer assets

- `installer/steaming.nsi`
- `installer/README.md`

Important current state:

- The installer path is no longer design-only
- The current installer docs describe a fixed ProgramData plugin install path
- The NSIS installer already detects an existing install and upgrades in place by running the previous uninstaller while preserving `%APPDATA%\Steaming`
- There is no separate standalone updater subsystem in the repository yet (no dedicated updater executable, release manifest flow, or background self-update service)

## OBS Plugin Build and Deployment

`obs/obs-plugintemplate/CMakeLists.txt`

Important current behavior:

- `STEAMING_AUTO_DEPLOY_TO_OBS` defaults to `ON`
- Dev builds auto-copy the plugin into `C:/ProgramData/obs-studio/plugins/steaming-plugin/...`
- Release packaging stages artifacts instead of using the live OBS install

Build command from the plugin root:

```powershell
cmake --build build_x64 --config RelWithDebInfo
```

After a plugin build, OBS must be restarted to load the new DLL.

## Version Source

The current version source of truth is `Steaming.Core/Steaming.Core.csproj`.

Fields to keep synchronized there:

- `Version`
- `AssemblyVersion`
- `FileVersion`

Current code version at the time of this document update: `0.10.85`.

## Build Commands

Use these before finishing work:

```powershell
dotnet build Steaming.WinUI/Steaming.WinUI.csproj -c Release
cmake --build obs/obs-plugintemplate/build_x64 --config RelWithDebInfo
```

Zero build errors is the bar.

## Documentation Status

The repository contains several historical plan docs. Based on the current codebase:

### Docs that are now completed/stale and can be removed or archived

- `docs/CODE_REVIEW.md`
  - Empty file, no current value
- `docs/PLAN_video_alerts.md`
  - Video alert elements are implemented
- `docs/PLAN_text_transitions.md`
  - Text transitions are implemented
- `docs/PLAN_kokoro_tts.md`
  - Kokoro TTS is implemented and wired into settings/runtime
- `docs/PLAN_face_tracking_completed.md`
  - Face tracking is implemented far beyond the original plan
- `docs/PLAN_avatar_completed.md`
  - Avatar system is implemented; the old plan no longer matches the renderer/runtime design

### Docs that should not be treated as completed

- `docs/PLAN_engagement_meter.md`
  - No matching implementation was found in the current code
- `docs/PLAN_installer_updater.md`
  - Installer work is implemented, including in-place upgrade detection; the standalone updater portion is still not present
- `docs/REFACTOR.md`
  - Historical refactor audit, not current architecture
- `docs/steaming.md`
  - Original product plan; no longer an accurate architecture reference
- `docs/ui_update.md`
  - Migration history, not current architecture
- `docs/editor_update.md`
  - Historical planning doc, not current behavior reference

## Non-Negotiable Rules

- Read the full call chain before changing behavior.
- Keep MVVM boundaries intact: application logic in ViewModels/services, not UI code-behind.
- The OBS plugin is the renderer. Do not introduce alternate overlay architectures.
- Named-pipe message enums and binary payloads must stay synchronized on both sides.
- Never claim runtime behavior works unless it has been verified.
