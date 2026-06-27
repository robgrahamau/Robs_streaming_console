# Steaming — Streaming Companion App Plan

A self-built C# / .NET alternative to Casterlabs Caffeinated, targeting full feature parity.

**Stack:**
- C# / .NET 8 — core app, event bus, platform adapters, bot, dashboard UI (WPF)
- C++ — native OBS plugin (renders overlays/widgets directly in OBS compositor, no browser/CEF)
- Named pipe — IPC bridge between C# app and C++ plugin
- ASP.NET Core (embedded, minimal) — OBS BrowserDocks only (docked panels inside OBS UI, not overlays)
- SignalR — live event relay to BrowserDock pages only

**Build environment (confirmed):**
- Visual Studio 2022 Community
- MSVC cl.exe v14.44.35207
- CMake 3.31.6
- Git 2.39.2

---

## Architecture Overview

```
Twitch EventSub / IRC ──┐
Kick WebSocket API ─────┼──► Platform Adapters ──► Normalised Event Bus
Steam API ──────────────┘              │
                                       ├──► Chat Bot
                                       ├──► Activity Feed (SQLite)
                                       ├──► Viewer List
                                       │
                          ┌────────────┼────────────────┐
                          ▼            ▼                 ▼
                   Named Pipe    SignalR Hub        OBS WebSocket
                      │          (docks only)       (scene control)
                      ▼
             C++ OBS Plugin
             (native source in
              OBS compositor)
             Renders: alerts,
             chat overlay,
             goals, widgets
```

---

## Core Technologies

| Concern | Library / Tool |
|---|---|
| Desktop UI | WPF (.NET 8) |
| OBS overlay rendering | Native C++ OBS plugin (obs-module) |
| C# ↔ Plugin IPC | Named pipe (binary protocol) |
| BrowserDocks only | ASP.NET Core + SignalR (docked OBS panels) |
| Twitch EventSub + IRC | TwitchLib (C#) |
| Kick API | Custom WebSocket client (Pusher protocol) |
| Steam API | SteamWebAPI + SteamKit2 |
| OBS control | OBS WebSocket v5 (obs-websocket-sharp or raw) |
| OAuth / HTTP | System.Net.Http |
| Storage | SQLite via EF Core |
| Plugin build | CMake + MSVC (cl.exe) |

---

## OBS Plugin Design

### Why Native Plugin
No CEF, no browser processes, no local HTTP server for overlays. The plugin is a native OBS source that renders directly into OBS's compositor using OBS's graphics API (D3D11 / OpenGL via `gs_*` calls). One `.dll` dropped into OBS's plugins folder.

### Plugin ↔ App Communication (Named Pipe)
- C# app opens a named pipe server: `\\.\pipe\steaming`
- C++ plugin connects as client on load
- Protocol: length-prefixed binary messages (msgpack or simple custom binary)
- Message types:
  - `RENDER_ALERT` — show an alert (type, username, amount, duration)
  - `RENDER_CHAT` — push a chat message to the overlay
  - `UPDATE_GOAL` — set goal current/max values
  - `CLEAR` — hide/reset a widget
  - `PING` / `PONG` — keepalive

### Plugin Source Types Registered with OBS
Each widget registers as a named OBS source type:

| Source ID | Widget |
|---|---|
| `steaming_alert` | Follow / Sub / Raid / Bits alerts |
| `steaming_chat` | Scrolling chat overlay |
| `steaming_goal` | Sub / bit / follower goal bar |
| `steaming_recentevents` | Rolling event ticker |
| `steaming_nowplaying` | Current game / song |

User adds these as sources in OBS exactly like any other source. Position and size set in OBS as normal.

### Rendering
- Text rendered via `obs_text_*` helpers or direct D3D11 texture writes
- Animations: frame-based within the plugin's `video_render` callback (delta time driven)
- Alert queue managed inside the plugin — C# fires events, plugin handles sequencing so alerts never overlap
- Sound playback: C# app side (plays audio via NAudio/Windows Audio API on alert trigger)

---

## Module Breakdown

### 1. Platform Connections

#### 1.1 Twitch
- OAuth2 PKCE flow
- TwitchLib: IRC chat client + EventSub WebSocket
- Events: follow, sub, gift sub, bits/cheer, raid, channel point redemptions, hype train, polls, predictions
- Token stored encrypted in SQLite

#### 1.2 Kick
- OAuth2 Authorization Code flow
- Chat via Pusher WebSocket (`wss://ws-us2.pusher.com`) — `chatroom.<channelId>`
- Events: `ChatMessageEvent`, `FollowersUpdated`, `SubscriptionEvent`, `GiftsLeaderboardUpdated`, `StreamHostEvent`

#### 1.3 Steam
- Steam Web API: currently playing game, achievement unlocks, player summary
- SteamKit2: Steam friend chat → unified chat panel
- Achievement unlocks triggerable as alerts

#### 1.4 Normalised Event Model
```csharp
public record StreamEvent
{
    public Platform Platform { get; init; }   // Twitch, Kick, Steam
    public EventType Type { get; init; }       // Chat, Follow, Sub, Bits, Raid, Gift, Achievement...
    public StreamUser User { get; init; }
    public Dictionary<string, object> Data { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
```

---

### 2. Chat System

#### 2.1 Unified Chat View (WPF)
- Merged chat from all connected platforms
- Per-platform colour coding and badge icons
- Emote rendering: BTTV, FFZ, 7TV, Twitch native, Kick native
- Clickable usernames → moderator actions

#### 2.2 Chatbot
**Commands**
- `!commands`, `!uptime`, `!game`, `!title`, `!so @user`, `!followage`
- Custom commands: trigger → response, cooldown, user level (everyone / sub / mod / broadcaster)

**Timers**
- Recurring messages on interval, supports `{uptime}`, `{viewers}`, `{game}` variables

**Auto-moderation**
- Link permit/block, caps filter, repeated character filter, banned word list
- Actions: delete, timeout (N seconds), ban

**Polls & Predictions**
- Create/manage Twitch polls and predictions via API from dashboard

---

### 3. Viewer List
- Twitch `/chatters` endpoint (mod OAuth scope)
- Kick viewer list from Kick API
- WPF panel grouped by: Broadcaster, Mods, VIPs, Subscribers, Viewers
- Click username → mod panel (timeout, ban, mod, unmod)
- Refresh interval configurable (default 60s)

---

### 4. Activity Feed
- Chronological cross-platform event log
- Filter by event type
- Stored in SQLite — reviewable after stream
- Double-click event → replay its alert in OBS via named pipe

---

### 5. OBS Integration

#### 5.1 OBS WebSocket Control
- Scene switching on triggers (raid → BRB scene, etc.)
- Toggle source visibility
- Trigger hotkeys
- Read current scene/source list into dashboard

#### 5.2 OBS BrowserDocks (ASP.NET Core)
Panels docked inside OBS window — these are UI/control surfaces, NOT overlays.
No rendering happens here, just dashboard panels.

| Dock URL | Content |
|---|---|
| `/docks/chat` | Chat + mod controls |
| `/docks/activity` | Activity feed |
| `/docks/viewers` | Viewer list |
| `/docks/bot` | Quick bot command triggers |
| `/docks/dashboard` | Stream title, game, live stats |

---

### 6. Widgets & Alerts

All rendering done by the C++ plugin in OBS compositor.

#### Alert Lifecycle (managed inside plugin)
1. C# fires `RENDER_ALERT` message over named pipe
2. Plugin queues the alert (no overlaps)
3. **Enter** — animated in (configurable: slide, pop, bounce, fade)
4. **Hold** — displays for N seconds
5. **Exit** — animated out
6. Plugin signals C# `ALERT_DONE` → C# plays exit sound / triggers any OBS scene action

#### Built-in Widgets

| Source ID | Trigger | Description |
|---|---|---|
| `steaming_alert` | Follow / Sub / Bits / Raid / Gift | Animated alert popup |
| `steaming_chat` | Chat messages | Scrolling chat feed overlay |
| `steaming_goal` | Sub / Bit / Follower events | Animated progress bar |
| `steaming_recentevents` | All events | Rolling ticker |
| `steaming_nowplaying` | Steam / manual | Current game or song |
| `steaming_hype` | Twitch Hype Train | Hype train progress |
| `steaming_prediction` | Twitch Prediction | Live odds display |

#### Customisation (configured in C# dashboard, sent to plugin)
- Animation style per event type
- Colours, fonts, sizes
- Sound file per event (played by C# app via NAudio)
- Duration
- Test/preview button

---

### 7. Config Dashboard (WPF)

| Tab | Content |
|---|---|
| Connections | Twitch / Kick / Steam auth status |
| Chat | Unified chat panel + mod controls |
| Bot | Command editor, timers, auto-mod |
| Viewers | Viewer list panel |
| Activity | Activity feed + post-stream replay |
| Widgets | Enable/disable widgets, animation/colour/sound config, preview |
| OBS | WebSocket connection, scene list, trigger rules, plugin status |
| Settings | Ports, startup, theme |

---

## File & Project Structure

```
Steaming/
├── Steaming.sln
├── Steaming.App/               # WPF desktop application
│   ├── Views/
│   ├── ViewModels/
│   └── Controls/
├── Steaming.Core/              # Business logic, platform-agnostic
│   ├── EventBus.cs
│   ├── Models/
│   ├── Platforms/
│   │   ├── TwitchAdapter.cs
│   │   ├── KickAdapter.cs
│   │   └── SteamAdapter.cs
│   ├── Bot/
│   │   ├── CommandProcessor.cs
│   │   ├── TimerService.cs
│   │   └── AutoMod.cs
│   ├── Services/
│   │   ├── ViewerListService.cs
│   │   └── ActivityFeedService.cs
│   └── Ipc/
│       └── PluginPipeServer.cs  # Named pipe server → C++ plugin
├── Steaming.Web/               # ASP.NET Core — BrowserDocks ONLY
│   ├── Hubs/SignalRHub.cs
│   └── wwwroot/docks/
├── Steaming.Data/              # EF Core + SQLite
│   ├── SteamingDbContext.cs
│   └── Migrations/
└── Steaming.Plugin/            # C++ OBS plugin (separate CMake project)
    ├── CMakeLists.txt
    ├── src/
    │   ├── plugin-main.cpp     # obs_module_load / unload
    │   ├── pipe_client.cpp     # Named pipe client (connects to C# server)
    │   ├── alert_source.cpp    # steaming_alert OBS source
    │   ├── chat_source.cpp     # steaming_chat OBS source
    │   ├── goal_source.cpp     # steaming_goal OBS source
    │   └── renderer.cpp        # Shared animation/draw helpers
    └── include/
```

---

## Build Order

1. **C++ plugin scaffold** — CMakeLists, `obs_module_load`, registers one dummy source, builds and loads in OBS
2. **Named pipe** — C++ client connects, C# server sends a test message, plugin logs it
3. **`steaming_alert` source** — renders a static text box in OBS, confirms the pipeline works
4. **C# solution scaffold** — projects, DI, event bus skeleton
5. **Twitch chat** via TwitchLib — first real event in C# console
6. **Alert pipeline end-to-end** — Twitch follow fires → C# → named pipe → plugin renders alert in OBS
7. **Kick chat + events**
8. **WPF main window** with chat tab
9. **Chatbot**
10. **OBS WebSocket control**
11. **BrowserDocks** (ASP.NET Core + SignalR — docked panels only)
12. **Viewer list**
13. **Steam integration**
14. **Remaining widget sources**
15. **Full settings/config UI**
16. **SQLite persistence**

---

## Key References

- OBS Plugin API: https://obsproject.com/docs/
- OBS Plugin Template (CMake): https://github.com/obsproject/obs-plugintemplate
- TwitchLib (C#): https://github.com/TwitchLib/TwitchLib
- Twitch EventSub WebSocket: https://dev.twitch.tv/docs/eventsub/handling-websocket-events/
- Kick API: https://docs.kick.com/
- OBS WebSocket v5 protocol: https://github.com/obsproject/obs-websocket/blob/master/docs/generated/protocol.md
- SteamKit2: https://github.com/SteamRE/SteamKit
- Steam Web API: https://developer.valvesoftware.com/wiki/Steam_Web_API
- NAudio (C# audio): https://github.com/naudio/NAudio
