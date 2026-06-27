# PLAN — Chat Engagement Meter (on-stream gauge / graph overlay)

Status: **design only — not built.** Captured for a future build session.
Owner decisions captured from the 2026-06-23 discussion are in **"Decisions already made"** below.

---

## 1. What it is

A persistent on-stream overlay that visualises how engaged chat is in real time — a
"temperature gauge" that rises when chat is active and eases back down when it goes quiet.

- **Primary form:** a fill **bar** (horizontal by default, vertical optional) that fills and
  changes colour cool→hot with engagement. Reads like a hype/progress bar.
- **Secondary form (option):** a small scrolling **line/area graph** of the last ~1–2 minutes,
  for streamers who want history instead of a single level.
- **Editable** using the existing alert/overlay editor (position, size, colours, orientation).
- Viewers see it (it is an OBS overlay), so it must be rendered by the C++ plugin.

This is a viewer-facing feature, not a dashboard monitor. (A dashboard-only version is possible
and much cheaper — pure WinUI, no C++/pipe work — but was explicitly **not** what was asked for.)

---

## 2. Decisions already made (do not re-litigate)

- **Render target:** on-stream OBS overlay. (Not dashboard-only.)
- **Default visual:** horizontal fill bar, with a vertical toggle. Graph is an additional option.
- **Metric:** base signal is **messages per minute**. Unique chatters, follows, subs, bits, etc.
  act as **boosters / multipliers on top** — they never replace the base rate.
- **One person still counts.** A single active chatter is real engagement and must register on the
  bar. Unique-chatter count is only a *bonus* weight, never a gate. Do not build anything that
  reads "low/zero" just because few distinct people are talking.
- **Editable via the existing editor**, reusing the element/rendering machinery — not a new editor.

---

## 3. The engagement metric

Produce a single smoothed value `engagement ∈ [0, 100]` ("heat") on the C# side, pushed live to
the plugin. Suggested model (tunable — all constants belong in settings later):

```
base        = messagesPerMinute                      // core signal; >0 for even one chatter
boost       = 1
            + wUnique  * uniqueChattersInWindow       // breadth bonus (never a gate)
            + wFollow  * followsInWindow
            + wSub     * subsInWindow
            + wBits    * (bitsInWindow / 100)
            + wEmote   * emoteDensity                  // optional
raw         = base * boost
heat        = 100 * clamp(raw / fullScale, 0, 1)      // fullScale = "this is on fire" calibration
```

- **Windowing:** rolling window (e.g. last 60s) updated on a timer (e.g. 1 Hz). Counts decay as
  events age out of the window, so the bar naturally falls when chat slows.
- **Smoothing / decay:** exponential smoothing on `heat` so the bar rises quickly on a burst and
  eases back down (attack faster than release feels best for a "hype" meter). Separate
  attack/release constants.
- **Auto-scale option:** `fullScale` could adapt to the channel's own recent peak so the bar is
  meaningful for both small and large chats. Start with a fixed configurable `fullScale`; consider
  adaptive later.

All of this is **pure C#** and reuses event streams the app already has (chat ingest for alerts,
follow/sub/bits events for alerts + analytics, `chat_count` already persisted in
`viewer_snapshots`). This is the easy 80%.

---

## 4. Architecture — what to reuse, what is new

### Reuse (big savings)

| Reuse | Why it fits |
|---|---|
| **Alert editor** (`AlertEditorWindow`) | Already a WYSIWYG layout tool with position/size/colour/layers. Add gauge + graph as new **element types**, alongside Text/Image/GIF/Video. No new editor. |
| **ALT3 layout format + `layout_renderer.cpp`** | Already parses element records and draws rects/text/images/video. A gauge is a filled rect whose extent + colour track a value; a graph is a polyline over a ring buffer. Same element-parsing pattern. |
| **Persistent overlay source pattern** | The **chat source** is the model: an always-on source with a global state buffer, marked dirty on update. The meter is always-on like chat, NOT a transient alert. |

### New (the real work — the ~20%)

1. **A persistent "engagement meter" OBS source** (new source type in the plugin), modelled on the
   chat source, not the alert source. Alerts are fire-and-forget and time-boxed; the meter lives
   forever and updates continuously. **Do not implement the meter as an alert.**
2. **A new pipe message** that streams the live `engagement` value (and maybe component breakdown)
   from C# to C++ on a timer. Must be added to **both** `PipeMessageType.cs` (C#) and
   `pipe_client.h` (C++) in the same change set, with the byte layout documented in both (repo
   rule 8). The ALT3 layout defines *appearance*; this message carries the *live value*.
3. **Two new element types** (`Gauge`, `Graph`) in the ALT3 format + editor + renderer, whose draw
   code binds to "current engagement value" instead of a fixed keyframe value.

### Why alerts can't be the delivery mechanism

Alert layouts are serialized and sent **once** then animated to completion. There is no concept of
"a value that keeps arriving from outside after placement." The meter needs a continuous feed, hence
the separate live-value pipe message. The *layout/editor/rendering* code is reusable; the *trigger
model* is not.

---

## 5. Implementation outline

### 5.1 C# — metric service
- New service (e.g. `EngagementMeterService` in `Steaming.Application/Services/`) subscribing to the
  existing chat + follow/sub/bits event streams.
- Rolling-window counters; 1 Hz tick computing `heat` with smoothing/decay.
- Pushes the value via `OverlayDispatcher` over the new pipe message. Fire-and-forget, off the OBS
  threads (repo rule: never write the pipe from the OBS render thread — C# side is fine, just don't
  block).
- Settings (AppSettings) for weights, fullScale, attack/release, enabled platforms.

### 5.2 Wire format — new pipe message
- Add `PipeMessageType.EngagementUpdate = 0x16` (next free; 0x11/0x14/0x15 used per ARCHITECTURE.md —
  verify against `PipeMessageType.cs` at build time).
- Payload (draft): `[4] heat f32 LE (0..100)` plus optional component floats (msgRate, uniques,
  recent follows/subs/bits) for richer graph rendering. Keep it small; sent ~1 Hz.
- Mirror the exact layout in `pipe_client.h` with matching byte-offset comments.

### 5.3 ALT3 — new element types
- Add `AlertElementType.Gauge` and `AlertElementType.Graph` in `AlertLayout.cs` (next free element
  type ids — Video is 6 per HANDOFF; verify) and matching `ElemType` in C++ `layout_types.h`.
- Gauge element record: orientation (u8 horiz/vert), fill direction, background colour, the
  cool→hot colour ramp (2–3 stops), corner radius, optional value-label toggle, min/max mapping.
- Graph element record: time window seconds, line/area style, colour ramp, sample stride.
- Serialize on both sides in the same change set (ALT3 magic `0x33544C41`).

### 5.4 Editor (`AlertEditorWindow`)
- Add "Gauge…" and "Graph…" to the element add menu (same place Video was added).
- Properties panel: orientation toggle (horizontal/vertical), colour ramp stops, background,
  decay/smoothing exposure (or keep those server-side), value-label on/off.
- WYSIWYG preview: drive the editor preview from a **simulated** engagement value (e.g. a slider or
  an animated demo value) so the streamer can see fill + colour while editing, since there is no live
  chat in the editor. Reuse the same draw logic conceptually.

### 5.5 C++ — persistent source + renderer
- New source file pair (e.g. `engagement_source.{h,cpp}`) modelled on `chat_source.*`: global
  current value `s_engagement`, `s_instances` registry, dirty-mark on update, `OBS_SOURCE_VIDEO`,
  premultiplied BGRA, passed `effect` param (per OBS plugin rules).
- `plugin-main.cpp`: handle `EngagementUpdate` → store value → mark engagement instances dirty.
- Draw:
  - **Gauge:** background quad + fill quad scaled by `heat`; colour interpolated along the ramp by
    `heat`; orientation flips which axis the fill grows along (the only orientation-specific code).
  - **Graph:** ring buffer of recent values (push on each `EngagementUpdate`); draw as polyline /
    filled area; colour by current `heat`.
- Reuse `layout_renderer` element drawing where the meter is an element inside a layout; the source
  binds the element's value field to `s_engagement` at render time.

---

## 6. Phasing (ship in slices)

1. **Phase 1 — gauge, horizontal, fixed scale.** Metric service (msgs/min + basic boosters),
   pipe message, persistent source, horizontal fill bar with colour ramp, editor add + position +
   colour. This is the minimum viewer-facing feature.
2. **Phase 2 — vertical toggle + label + tuning.** Orientation flag, optional numeric/text label,
   expose weights/decay/fullScale in settings, simulated preview in editor.
3. **Phase 3 — graph option.** Ring buffer + line/area element type and renderer.
4. **Phase 4 — polish.** Adaptive fullScale, per-platform colours, glow/peak markers, presets.

Each phase is independently shippable and runtime-testable in OBS.

---

## 7. Open questions / to decide at build time

- **Boost weights & fullScale defaults** — need real numbers from Rob's actual chat volume to feel
  right. Start configurable, tune live.
- **Attack/release feel** — how fast it rises vs falls. Tune at runtime.
- **Adaptive vs fixed scale** — fixed first; adaptive (track recent peak) later.
- **Element-in-layout vs standalone source** — likely a standalone persistent source that *reuses*
  element rendering, so it can be a simple "drop the Engagement Meter source into your scene" without
  building a whole alert layout. Confirm against how chat source is added in OBS today.
- **Per-platform split** — Steaming is dual-platform (repo rule 17). Decide whether the meter is
  combined or shows Twitch/Kick separately (e.g. two-tone fill). At minimum the metric must track
  both platforms separately internally even if the default display is combined.

---

## 8. Build / test checklist (for when it's built)

- `dotnet build Steaming.WinUI/Steaming.WinUI.csproj -c Release` **and** `-c Debug` (rule 11).
- `cmake --build obs/obs-plugintemplate/build_x64 --config RelWithDebInfo`, restart OBS.
- Bump version in `Steaming.Core/Steaming.Core.csproj` before commit (rule B4).
- Runtime verify (the meter is inherently runtime-behavioural — "build-verified only" is not enough):
  1. Add the Engagement Meter source in OBS; with live chat, confirm the bar rises with message rate
     and eases down when chat slows.
  2. A single active chatter still moves the bar (one person counts).
  3. A burst (many messages / a sub / bits) spikes it; colour ramps cool→hot.
  4. Horizontal and vertical both fill correctly; editor position/size/colour round-trip on
     save/reopen.
  5. Memory/threading: continuous 1 Hz updates over a long session don't leak or stutter (the C++
     plugin audit rules apply).

---

## 9. Honest difficulty summary

- **Metric + data:** easy. Reuses existing event streams; it's windowed counting + smoothing.
- **Editor + appearance:** easy-ish. New element types in an editor that already does this.
- **On-stream rendering + live feed:** medium. A new persistent OBS source + a new pipe message is a
  proper feature-sized job, but it copies the well-trodden chat-source pattern.

No architectural obstacles. The gauge is the cheapest possible thing to render (a filled rect that
tracks one number); the graph is a modest add-on. Overall: a **medium** feature, because "on-stream"
always means a new source + pipe message — not a quick tweak, but nothing novel.
