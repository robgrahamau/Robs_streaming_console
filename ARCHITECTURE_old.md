# Steaming — Architecture Reference

Read this file when working on any component. Understand the full system before touching any part of it.

---

## Components

| Component | Language | Role |
|---|---|---|
| `Steaming.WinUI` | C# WinUI 3 .NET 10 | The UI (only shipping app; WPF removed 2026-06-18) |
| `Steaming.Application` | C# .NET 8 | ViewModels, Services, EventBus |
| `Steaming.Core` | C# .NET 8 | Models, IPC client, VersionInfo |
| `Steaming.Data` | C# .NET 8 | SQLite repositories |
| `obs-plugintemplate` | C++ | OBS plugin: ALL rendering, pipe client |

---

## IPC — Named Pipe

- Name: `\\.\pipe\steaming`
- Direction: C# writes → C++ reads
- Frame format: `[1]type [4]payloadLen LE [N]payload`
- Types defined in `PipeMessageType.cs` (C#) and `pipe_client.h` (C++) — **must stay in sync**
- **Never write to the pipe from the OBS main/render thread** — causes startup deadlock

### Message types

| Hex | Name | Description |
|---|---|---|
| 0x11 | RenderChat | Chat message payload (v3 format) |
| 0x14 | RenderAlertV2 | Alert layout payload (ALT3 format) |
| 0x15 | RefreshChat | Emote/badge image downloaded — redraw all chat sources |

---

## Binary Formats

### Alert layout (ALT3)
Magic: `0x33544C41`. See `AlertLayout.cs` `Serialize()` and `layout_renderer.cpp` `Parse()`.  
Text elements use rich text spans — each span has its own font/size/color/bold/italic.  
Supports outline (8-direction stroke) and drop shadow per element.

### Chat payload (v3)
See `ChatPayload.cs` `Serialize()` and `chat_source.cpp` `parse_chat()`.
```
[2+N] platform   UTF-8
[2+N] username   UTF-8
[2+N] message    UTF-8
[2+N] color      UTF-8 "#RRGGBB"
[1]   flags      bit0=broadcaster bit1=mod bit2=sub bit3=vip bit4=highlighted bit5=hasBits
[4]   bitsAmount int32 LE
[2]   subMonths  uint16 LE
[1]   badgeCount
each badge: [2+N] cachedFilePath  (empty = coloured pill fallback)
[1]   emoteCount
each emote: [2]startChar [2]endChar [2+N] cachedFilePath  (empty = text fallback)
```

---

## OBS Plugin Rules

- Sources use `OBS_SOURCE_VIDEO` — no `OBS_SOURCE_CUSTOM_DRAW`
- In `video_render`: always use passed `effect` parameter — `gs_effect_set_texture` + `gs_draw_sprite`
- All pixels uploaded to OBS must be **premultiplied BGRA** (`GS_BGRA` format)
- Alert sources: per-instance queues + global `s_instances` broadcast
- Chat sources: global `s_lines` ring (all instances show same messages) + `s_instances` for dirty marking

---

## Analytics Database

Location: `%APPDATA%\Steaming\analytics.db`

### Tables

**stream_sessions** — one row per streaming session
```
id, platform, started_at, ended_at, peak_viewers, avg_viewers,
total_follows, total_subs, sample_count, title, category, unique_chatters,
twitch_peak_viewers, kick_peak_viewers, twitch_avg_viewers, kick_avg_viewers,
twitch_sample_count, kick_sample_count
```

**viewer_snapshots** — one row per 30-second poll during a session
```
id, session_id, timestamp, twitch_viewers, kick_viewers, chat_count
```

### Platform values
- `"Twitch"` — Twitch only
- `"Kick"` — Kick only
- `"Both"` — streaming to both simultaneously

### Filter rule
When filtering by "Twitch" or "Kick", include "Both" sessions — they streamed to that platform.  
SQL pattern: `WHERE (platform = 'Twitch' OR platform = 'Both')`

### DB safety rules
- **Never `SET peak_viewers = X` on an existing row.** Use `MAX(peak_viewers, $new)`.
- **Always git commit before running any DB patch script.** The commit is the backup.
- New columns default to 0. Existing rows will have 0 — do not display those zeros as real data.

---

## Emote/Badge Cache

- Emotes: `%APPDATA%\Steaming\emote_cache\{emoteId}.{ext}` — extension from HTTP Content-Type (.gif/.webp/.png)
- Badges: `%APPDATA%\Steaming\badge_cache\{setId}_{version}.png`
- `EmoteCache.Instance.GetOrDownloadAsync(id, url)` — single Task per ID, no concurrent downloads
- `EmoteCache.OnDownloadComplete` fires on completion → `OverlayDispatcher` sends `RefreshChat` → C++ marks all chat instances dirty
- C++ `LoadCachedEmote()` in `renderer.cpp` loads all GIF frames; uses elapsed time to pick current frame

---

## Platform APIs

### Twitch
- Uses TwitchLib v4 for chat and EventSub
- `ChatMessage` has NO `IsSubscriber`/`IsModerator`/`IsVip` properties — derive from `msg.Badges`
- EventSub for follows, subs, raids

### Kick
- Only `api.kick.com/public/v1/*` — no internal or undocumented endpoints
- Channel live check: `GET https://api.kick.com/public/v1/channels?broadcaster_user_id={id}` → `stream.is_live`
- Credentials stored encrypted via DPAPI in `%APPDATA%\Steaming\credentials.json`

---

## Version

- Single source of truth: `Steaming.Core/Steaming.Core.csproj`
- Fields: `Version`, `AssemblyVersion`, `FileVersion`
- `VersionInfo.cs` reads assembly version at runtime → shown in the WinUI title bar
- **Bump patch version before every commit that ships changes.**

---

## Build Commands

```powershell
# C# WinUI (the only shipping app)
dotnet build Steaming.WinUI/Steaming.WinUI.csproj -c Release

# C++ OBS plugin (auto-deploys to OBS plugin folder)
cd obs/obs-plugintemplate
cmake --build build_x64 --config RelWithDebInfo
```

After C++ build: restart OBS to load the new DLL.
