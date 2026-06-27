# Steaming — Agent Handoff

Previous History: HANDOFF_history.md

## Session 2026-06-27 (v0.11.5) - local auth secrets file ignored + tracked example file

Build status for this session:
- WinUI `Release` build succeeded with 0 errors / 2 pre-existing CA1416 warnings in `RewardService.cs`.
- OBS plugin `RelWithDebInfo` build succeeded.
Runtime UNVERIFIED.

What changed:
- Moved the tracked platform client IDs/secrets out of `Steaming.Core/Configuration/PlatformAuthConfig.cs`.
- Added `Steaming.Core/Configuration/PlatformAuthSecrets.cs` as the tracked loader/default shell.
- Added `Steaming.Core/Configuration/PlatformAuthSecrets.Local.cs` as the machine-local override file containing the real values.
- Added `Steaming.Core/Configuration/PlatformAuthSecrets.Local.example.cs` as the tracked example file with placeholders.
- Updated `.gitignore` so `PlatformAuthSecrets.Local.cs` is not added to git.
- Updated `Steaming.Core.csproj` to exclude `PlatformAuthSecrets.Local.example.cs` from compilation, so the example file can live in the repo without affecting local builds.

What currently still works and was intentionally preserved:
- Local development still reads the real auth values from the local `.cs` override file.
- The tracked example file is present for GitHub but does not compile into the app.
- The rest of the auth/login call chain still resolves values through `PlatformAuthConfig`.

Runtime still needed:
1. Confirm Twitch/Kick/YouTube login flows still open with the local secrets file present.
2. Confirm `git status --ignored` shows `Steaming.Core/Configuration/PlatformAuthSecrets.Local.cs` as ignored.

## Session 2026-06-27 (v0.11.4) - default alert tests no longer blocked by inactive platforms

Build status for this session:
- WinUI `Release` build succeeded with 0 errors / 0 warnings.
- OBS plugin `RelWithDebInfo` build succeeded.
Runtime UNVERIFIED.

What changed:
- `Steaming.Application/ViewModels/MainViewModel.cs`
  - Built-in default alert tests now mark their synthetic `StreamEvent` payload with `isLocalTest=true`.
  - `ShouldDispatchEvent` now explicitly allows those local synthetic tests through the active-platform gate.
  - Default alert tests now choose a currently active platform for the fake event when one exists; if all platforms are inactive, the test still runs instead of being dropped.

What currently still works and was intentionally preserved:
- Real inbound platform events are still gated by the per-platform Active flags.
- Custom alerts still use their direct path and were not changed.
- The standard alert runtime path after the EventBus gate was not changed.

Runtime still needed:
1. On a machine with Twitch and YouTube inactive (and/or not logged in), click the built-in default alert Test buttons and confirm alerts still reach OBS.
2. Confirm custom alert tests still behave exactly as before.
3. Confirm actual live events from an inactive platform are still intentionally suppressed.

## Session 2026-06-27 (v0.11.3) - per-label action buttons + alert text-variable reference + editor selection-highlight on `youtube_integration`

WinUI **Release** built 0 errors / 0 warnings. WinUI **Debug**: C# compiled clean — the only 4 errors
were `MSB3021/MSB3027` output-DLL copy locks because the app was running under the VS debugger (pid 14016);
no code errors. Stop the running debug instance and rebuild Debug to refresh the F5 binary. No C++ change.
Runtime UNVERIFIED.

Three things Rob asked for (UI-scaling explicitly deferred as a later nice-to-have):

### 1. Labels now have per-row Preview / Test / Clear / Edit buttons (like alerts)
Rob: labels were the odd one out — every other overlay item shows its actions inline, but labels used one
shared button row that acted on the `ListView` selection, which was confusing.
- `Steaming.WinUI/Pages/OverlaysPage.xaml`: the LABELS `ListView` item template now carries inline
  👁/▶/✕/✏ buttons per row (same glyphs/pattern as the unique-alert rows). `ListView` `SelectionMode`
  changed `Single`→`None` (selection is no longer used to pick a target). The shared Preview/Test/Clear/Edit
  row was removed; **"Push All" stays** below as the one global action.
- `Steaming.WinUI/Pages/OverlaysPage.xaml.cs`: `PreviewLabel_Click` / `TestLabel_Click` / `ClearLabel_Click`
  / `EditLabelLayout_Click` now read the `LabelRow` from `(s as FrameworkElement)?.DataContext` instead of
  `LabelList.SelectedItem`. Removed the now-dead empty `LabelList_SelectionChanged` handler.

### 2. "Text variables" reference button on the Overlays page (NEW)
Rob wanted one place that lists every `{token}` usable in alert text.
- `OverlaysPage.xaml`: an `ℹ Text variables …` button pinned at the top of the left column.
- `OverlaysPage.xaml.cs` `ShowTokenHelp_Click`: opens a `ContentDialog` listing tokens grouped into
  Standard alerts and Unique alerts, each with a plain-English meaning. **The token list was taken from the
  actual runtime replacements** in `OverlayDispatcher.BuildAlertText` (standard: `{user} {platform} {amount}
  {amountDisplay} {months} {target} {reward} {input}`) and the custom-alert overload (`{arg}` etc.) — not
  invented. Built in code-behind (`AlertTokens` / `UniqueAlertTokens` arrays + `AddTokenSection`).

### 3. Editor rich-text selection stays highlighted when focus leaves the box
Reported UI issue: select text in the AlertEditorWindow rich-text area, then click the font/size/colour
controls — the selection's highlight vanished (because a `RichEditBox` hides its selection highlight when
unfocused), so you couldn't see what you were about to restyle. Formatting still worked (it targets the
captured `_richSelStart/_richSelEnd` range), this was purely the missing visual cue.
- `Steaming.WinUI/AlertEditorWindow.xaml.cs` (right after `_richBox.SelectionFlyout = null;`): set
  `SelectionHighlightColor` and `SelectionHighlightColorWhenNotFocused` to the system accent (falls back to
  a fixed blue if `SystemAccentColor` isn't in the resource dictionary).

Version bumped 0.11.2 → **0.11.3** (`Steaming.Core/Steaming.Core.csproj`).

Runtime still needed (Rob):
1. Overlays → LABELS: each label row shows 👁/▶/✕/✏; Preview renders on the right canvas, Test pushes a live
   label, Clear blanks it, Edit opens the layout editor — each acting on that row, not a selection. Push All
   still works.
2. Overlays → click "ℹ Text variables": dialog lists the tokens with meanings.
3. AlertEditorWindow: select some text, click the font/size/colour controls — the selection stays visibly
   highlighted the whole time, and the chosen formatting still applies to that selection.

## Session 2026-06-27 (v0.11.2) - lyrics source idle transparency fix

Build status for this session:
- WinUI `Release` build succeeded with 0 errors (2 pre-existing CA1416 warnings in `RewardService.cs`).
- OBS plugin `RelWithDebInfo` build succeeded and auto-deployed into the OBS plugin folder.
Runtime UNVERIFIED.

What changed:
- `obs/obs-plugintemplate/src/lyrics_source.cpp` now tracks the existing `isPlaying` byte from the
  `MusicPosition` payload instead of discarding it.
- The lyrics source now matches the now-playing source's idle behavior: when playback is off, or there
  are no lyric lines, the source rebuilds to a fully transparent texture.
- The configured lyrics background is only drawn while playback is active and lyrics exist.
- Playback-state changes bump the generation counter so the source redraws immediately when music stops
  or starts.

What currently still works and was intentionally preserved:
- `Streaming Now Playing` behavior was left unchanged; it already went transparent when no track title
  was present.
- Active-playback lyrics rendering, line throttling, horizontal/vertical layout, and style settings
  were preserved.
- Existing wire format was preserved. `MusicPosition` was already `[4]positionMs [1]isPlaying`; only
  the C++ consumer logic changed.

Runtime still needed:
1. Verify `Streaming Lyrics` is visually transparent in OBS when playback is stopped / no song is active.
2. Verify the configured lyrics background still appears normally while a song with lyrics is playing.

## Session 2026-06-27 (v0.11.1) - chat-overlay color/style controls + test-chat verifier (on the REAL ChatPage) + "Steaming"→"Streaming" visible-text fix + dead plugin tree & orphan pages removed on `youtube_integration`

Build-verified WinUI **Release + Debug** 0 errors. OBS plugin **RelWithDebInfo** built AND auto-deployed
(OBS was closed). Runtime UNVERIFIED (Rob to test). Pre-existing warnings only.
Version bumped 0.10.87 → **0.11.0** (minor catch-up for the YouTube integration) → **0.11.1** (this fix).

**CORRECTION mid-session (Rule 20 / failure #6):** the chat overlay settings the user actually sees live on
`Pages/ChatPage` (nav "Chat" → `ChatPage`, MainWindow.xaml.cs:183), which has a left settings column + a live
right-hand preview pane. An earlier dead duplicate `Pages/ChatSettingsPage` (NOT in the nav) was edited by
mistake first; those edits were reverted and the work redone on `ChatPage`. `ChatSettingsPage` was then
deleted (see orphan-page cleanup below).

### 1. Chat overlay: full color/style controls now on `ChatPage` (the live nav page)
Rob: the chat overlay had no way to configure background/text colors etc. in the C# app even though the
C++ plugin supported it. Root cause (proven): the `ChatOverlayConfig` model, the `ChatOverlaySettingsPayload`
pipe wire format, AND the C++ `chat_source_apply_settings` parser ALL already carried every field — the
**only** gap was missing UI controls on the page. So this was a pure UI addition; **no wire-format or C++
change required** (both sides already agree). `ChatPage` already had a live preview pane that *read*
background/text colours from config, but exposed no controls to set them.
- Added to `Steaming.WinUI/Pages/ChatPage.xaml(.cs)`: a Colors card — Background color + opacity, Message
  text color, Bits/amount color (WinUI `ColorPicker` in flyouts with live swatches) — plus Text align, Text
  shadow, Outline size, Max lines, Message order (bottom-up/top-down), Badge placement, Display-name color
  mode, Fade duration, and Platform filter (All/Twitch/Kick/YouTube). Width/Height deliberately NOT added —
  OBS Properties is the only source of canvas size (per the existing design comment in the payload/parser).
- `BuildConfig`/`PopulateFromConfig` map every new control; colors convert hex↔`Windows.UI.Color`. New
  controls are wired into `WireChangeHandlers`/`LiveUpdate` so the right-hand preview updates live; the
  preview now also honours Text align and Display-name color mode.
- Needed `using Microsoft.UI.Xaml.Controls.Primitives;` for `RangeBaseValueChangedEventArgs` (opacity slider).

### 2. "Steaming" → "Streaming" — visible text + OBS source display names only
Scope confirmed with Rob: fix all user-visible text AND OBS source display names; leave internal IDs, file
paths, the `%APPDATA%\Steaming` data dir, the `Steaming.*` namespace, and config keys alone.
- C++ `get_name` display names in the active plugin `obs/obs-plugintemplate/src` (rebuilt + redeployed;
  verified in the deployed DLL: all 7 now read "Streaming …", zero stale "Steaming …"):
  Chat Overlay, Alert, Emoji Rain, Goal, Label, Lyrics, Now Playing (+ matching header comments).
- C# visible strings: `MainWindow` title, `SplashWindow` (×3), `LoginWindow` title, `App` startup-error
  dialog title, `AboutPage` (×2), `ObsConfigPage` description, `MusicPage`/`AvatarPage` source-name
  instructions, `OAuthService` login HTML `<h2>`, `AvatarViewModel` NDI status (×2),
  `NdiSendService.SourceName` ("Streaming Avatar"), `MainViewModel` moderation reasons ("via Streaming").
- Installer `installer/steaming.nsi`: `APP_DISPLAY` → "Rob's Streaming Console" (ARP name, shortcut label,
  firewall rule name, dialogs). **`APP_NAME` ("Steaming") left unchanged on purpose** — it keys the
  registry (`Software\Steaming`, uninstall entry) + Start-menu folder; changing it would orphan existing
  installs. `OUTFILE` ("Steaming-Setup.exe") left unchanged (build-pipeline filename).
- **Deliberately NOT changed** (and why): `%APPDATA%\Steaming` data-dir paths (would orphan all user
  settings/analytics/tokens/caches); `AppSettings.ChatOverlay.SourceName` default "Steaming Chat"
  (profile-matching key, not a display label — changing risks orphaning saved per-source profiles);
  `steaming_*` OBS source IDs and the `steaming-plugin` folder/path; `[Steaming]` OBS-log prefixes;
  the `Steaming.*` namespace/projects/assemblies. NOTE: renaming the NDI source ("Streaming Avatar") and
  the OBS source display names means an existing user may need to re-add/reselect those sources once.

### 3. Verified which plugin tree ships, then removed the dead one
Rob asked to be 100% certain the DLL is built from the tree edited. Proven (not assumed): deployed
`steaming-plugin.dll` is SHA-256-identical to `obs/obs-plugintemplate/build_x64/RelWithDebInfo/steaming-plugin.dll`;
the old `Steaming.Plugin/` had no build output and registered only 2 of 7 sources. Made this explicit in
`ARCHITECTURE.md`, then **deleted `Steaming.Plugin/`** (`git rm -r`, 11 files; nothing outside docs
referenced it; fully recoverable from git history).

### 4. "Send test messages" — overlay verifier (NEW; no test-chat feature existed before)
Rob wanted a way to push pseudo chat to the C++ overlay to verify rendering without sending to platforms.
- `OverlayDispatcher.SendChatPayloadAsync(ChatPayload)` (new, public): sends one fully-specified chat line
  straight to the overlay via `PipeMessageType.RenderChat`. **No platform send, no EventBus publish** → does
  not touch Twitch/Kick/YouTube, analytics, TTS or the chatbot.
- `MainViewModel.SendTestChatMessagesAsync()` (new): builds a mixed set and pushes each to BOTH the OBS
  overlay AND the in-app chat list (`AddChatMessage`) so OBS vs app can be compared. Set covers:
  single-platform (T/K/Y), multi-platform "both" (`Twitch|Kick`) and "all" (`Twitch|Kick|YouTube`) with app
  prefix overrides `[T][K]`/`[T][K][Y]`, badge lines (mod+vip, broadcaster), and event lines (bits cheer,
  YouTube Super Chat highlighted, 12-month highlighted resub). Returns false if the pipe is disconnected.
- `Steaming.WinUI/Pages/ChatPage.xaml(.cs)`: "Send test messages to OBS" button under Apply/Clear
  (`SendTest_Click`), with a not-connected dialog. **No C++ change** — reuses the existing RenderChat path.

### 5. Orphan-page cleanup (Rob: "i don't want orphaned pages")
Cross-checked every `Pages/*.xaml` against the nav switch (`MainWindow.xaml.cs` NavView_SelectionChanged) and
all `Navigate`/`new` call sites. Only the 16 nav pages are reachable. Deleted 4 unreferenced pages
(`git rm`, recoverable): `ChatSettingsPage` (dead duplicate of `ChatPage`), `EventsPage`, `GoalsLabelsPage`,
`AlertEditorPage`. NOTE for a future session: those last three held goals/labels, per-event alert config, and
an alert-layout-editor *page* — none currently reachable (alert layout editing is done via the separate
`AlertEditorWindow`). If any of that UI is meant to be user-accessible it needs wiring into the nav (separate
decision); it was not reachable before this deletion either. 16 page files remain == 16 nav entries.

### Runtime still needed (Rob testing)
0. Nav → Chat → "Send test messages to OBS": confirm the mixed set renders in OBS (single icons, combined
   `[T][K]`/`[T][K][Y]`, mod/vip/broadcaster badges, bits in the bits colour, highlighted lines) and appears
   in the app chat list for comparison. Nothing should hit real Twitch/Kick/YouTube chat.
1. Nav → Chat: the new Colors card + combos are visible; pick a source profile, set background/text/bits
   colors + opacity + the new combos, watch the right-hand preview update live, click "Apply to OBS";
   confirm the OBS chat overlay reflects every setting.
2. OBS "Add source" menu now lists "Streaming …" names; existing scenes keep working (sources match by id).
3. App title bar / splash / About / login all read "Streaming". OAuth login page header reads "Streaming".
4. (If rebuilding installer) ARP + shortcut + firewall rule read "Rob's Streaming Console"; upgrade over an
   existing install still works (APP_NAME unchanged).

## Session 2026-06-27 (v0.10.87) - per-platform "Active / streaming to" master switches + System-platform fix on `youtube_integration`

Build-verified WinUI **Release + Debug** 0 errors (pre-existing warnings only: RewardService CA1416,
ChatSettingsPage `_loading`). No C++ change. Runtime UNVERIFIED.

### v0.10.87 follow-up — internal aggregate no longer masquerades as a Twitch event (root-cause fix)
Rob flagged (correctly) that nothing should ever be keyed to one platform being live. The dashboard-totals
broadcast `PublishUpdatedAsync` was tagged `Platform.Twitch, EventType.Achievement` — a platform-neutral
control message wearing Twitch's identity. Consequences proven in code: (a) the new EventBus gate needed a
special-case to avoid dropping it; (b) the activity-feed subscriber (`App.xaml.cs`) logged a junk
"Achievement"/Twitch row with an empty user on EVERY poll tick (pre-existing latent spam).
Fix:
- Added `Platform.System` to the `Platform` enum (`Steaming.Core/Models/StreamEvent.cs`) for app-internal
  messages belonging to no platform. Appended at the end (no existing value shifts); no `Enum.Parse<Platform>`
  anywhere, and analytics/DB only ever store real-platform strings, so it's inert outside the aggregate.
- `StreamDataService.PublishUpdatedAsync` now tags the aggregate `Platform.System`.
- `App.xaml.cs` activity subscriber skips `Platform.System` events (stops the per-tick junk rows).
- `MainViewModel.ShouldDispatchEvent` simplified to pure `IsPlatformActive(evt.Platform)` — no more
  `EventType.Achievement` special-case; `System` passes via the `_ => true` arm, so dashboard totals always
  flow no matter which platforms are off. Verified all other aggregate consumers (`OverlayDispatcher`
  Achievement branch, `MainViewModel.OnEvent`) key off the `"type"` marker, never `evt.Platform`.

### v0.10.86 base — the feature

Rob asked for a way to choose what he's streaming to (e.g. "Kick only", or "Kick+Twitch but not YouTube")
WITHOUT logging out, so the app stops polling / showing chat / sending to a platform he isn't using.
Confirmed UX with him: toggles live in **both** Dashboard + Connections page; an off platform is **fully
dark** (no inbound chat either).

Design — per-platform Active flag, INDEPENDENT of login, gated at the source, all gates read the flag
live so toggles apply instantly with no logout/reconnect:
- `AppSettings.TwitchActive / KickActive / YouTubeActive` (default true → existing setups unchanged).
- **Inbound dark (single chokepoint):** `EventBus.PlatformFilter` (now `Func<StreamEvent,bool>`); a
  dropped event never reaches any handler → no chat, alerts, TTS, activity, analytics, viewer-list, or
  chatbot reaction from an off platform. Wired in `App.StartCoreServicesAsync` to
  `MainViewModel.ShouldDispatchEvent`. **CRITICAL exemption:** `EventType.Achievement` is the internal
  `StreamDataUpdated` aggregate carrying dashboard totals for ALL platforms — it is NEVER gated, or
  turning one platform off would freeze the whole dashboard. Only genuine inbound platform events
  (chat/follow/sub/bits/raid/redemption/kicks) are gated.
- **Polling:** `StreamDataService` gates each platform's poll on its flag (Twitch poll condition, Kick
  `PollKickCountsAsync` guard, YouTube `PollYouTubeViewersAsync` guard) → off takes the existing
  reset-to-0/offline path (which also forces `IsLive=false`, so it drops out of "All" sends).
- **Outbound:** `SendChatAsync` blocks every branch (even explicit single-target) for an off platform.
  Chatbot output already routes through `SendChatAsync`, so it's covered too.
- **YouTube discovery:** `SyncYouTubeChatMonitoringAsync` returns early + `StopAsync` when YouTube off.
- **Runtime apply:** `MainViewModel.SetPlatformActive(Platform, bool)` saves the flag, zeroes that
  platform's dashboard counts + live flag for instant feedback, recomputes the live banner. Deliberately
  does NOT tear down Twitch/Kick chat sockets — the gates already silence everything visible, and that
  keeps the sensitive connect/disconnect lifecycle untouched. (Background socket stays open silently
  until restart; invisible to the user.)
- **UI:** Dashboard "STREAMING TO" toggle row in Quick Actions (`PlatformActive_Toggled`); off platform
  greyed in the viewer/follower/sub breakdown + status dot. Connections page: an "Active" ToggleSwitch in
  each of the Twitch/Kick/YouTube cards (`_suppressActiveToggle` guards the RefreshAll re-set, same
  pattern as `_suppressRaidToggle`).

Files: `Steaming.Core/Services/AppSettings.cs`, `Steaming.Core/EventBus.cs`,
`Steaming.Core/Services/StreamDataService.cs`, `Steaming.Application/ViewModels/MainViewModel.cs`,
`Steaming.WinUI/App.xaml.cs`, `Steaming.WinUI/Pages/DashboardPage.xaml(.cs)`,
`Steaming.WinUI/Pages/ConnectionsPage.xaml(.cs)`, `Steaming.Core/Steaming.Core.csproj` (→0.10.86).

Runtime still needed:
1. Toggle Twitch OFF on Dashboard while Twitch+Kick+YouTube logged in & live: Twitch stops polling, its
   dashboard numbers grey out, its inbound chat/alerts stop, a Dashboard send to "All" no longer hits
   Twitch, an explicit Twitch send is blocked. Kick + YouTube keep working normally (dashboard NOT frozen).
2. Toggle it back ON: Twitch resumes (polling repopulates within ~30s, chat/alerts return) with no login
   prompt.
3. Confirm the Connections-page toggle and the Dashboard toggle stay in sync (both reflect the same flag).
4. YouTube OFF → `SyncYouTubeChatMonitoringAsync` pauses discovery; ON → resumes (respecting OBS gating).
5. Restart with a platform left OFF: it stays dark from launch; flip ON works.

## Session 2026-06-27 (v0.10.85) - YouTube live viewers on dashboard + full YouTube analytics (Single/Dual/All) on `youtube_integration`

Build-verified WinUI **Release + Debug** 0 errors. Runtime UNVERIFIED. analytics.db backed up first to
`%APPDATA%\Steaming\analytics.backup_<stamp>.db`. Prior session work committed at `a924e8a` before any DB change.

YouTube is now a first-class third platform in stream data + analytics (it was Twitch/Kick only).

Live viewers (dashboard):
- `YouTubeLiveChatService` exposes `ActiveBroadcastId` (the resolved live video id).
- `StreamDataService` takes `YouTubeLiveChatService` via DI, adds `YouTubeIsLive` / `YouTubeViewerCount`,
  polls `videos.list?part=liveStreamingDetails` for `concurrentViewers` (string in the API) each 30s tick,
  folds YouTube into `ViewerCount` + `IsLive`, and adds `youtubeViewerCount`/`youtubeIsLive` to the
  `StreamDataUpdated` payload. No YouTube follower/sub live count — API exposes no usable equivalent.
- `MainViewModel`: `YouTubeViewerCount`, `YouTubeIsLive`, 3-way `UpdateLiveStatus` / `LiveStatusText`.
- `DashboardPage`: viewer breakdown now shows `Twitch / Kick / YouTube` (added `ViewerYouTubeRun`).

Analytics (own DB, like Twitch/Kick):
- Schema additive only (existing rows untouched): `stream_sessions` += youtube_peak/avg/sample_count;
  `viewer_snapshots` += youtube_viewers. Migrated via existing `AddMissingColumns`.
- `AnalyticsCollectorService` records a YouTube session (resume-by-recency like Kick), per-platform
  YouTube chatters floor the viewer count, snapshots carry youtube viewers, subs/gift-subs route to it.
- `AnalyticsRepository`: records gained YouTube fields; `AddSnapshot` takes `youtubeViewers`.
  **The fragile per-platform combine CTEs were replaced with C# cluster-merging** (`MergeSessions` /
  `CombineCluster` / `Overlaps`) — same time-overlap rule as before, but generalised to any platform
  count so Single / Dual / All share one path. Sessions, trends and all-time stats now all flow through
  `GetSessions`, so they can't disagree. `GetMergedSnapshots` now takes the cluster's session ids.
- `AnalyticsPage`: platform filter is now **All Platforms / Twitch / Kick / YouTube / Twitch+Kick /
  Twitch+YouTube / Kick+YouTube** (Single, Dual pairs, All). Overview + session charts add the YouTube
  series (red); session lines now render per-platform whenever that platform has data in the cluster.

Runtime still needed:
1. Go live on all three; confirm dashboard viewer breakdown shows YouTube count and totals add up.
2. After a multi-platform stream, open Analytics: All shows merged Total+per-platform; each Dual pair shows
   just those two merged; each Single shows one platform. Session chart lines match.
3. Confirm existing Twitch/Kick analytics rows still read correctly (no regression from the CTE→C# change).


## Session 2026-06-27 (v0.10.84) - full audit of Codex YouTube work + "All" = live-platforms-only with {platform} token on `youtube_integration`

Build-verified WinUI **Release + Debug** 0 errors (pre-existing warnings only: RewardService CA1416, ChatSettingsPage `_loading`). No C++ change. Runtime UNVERIFIED.

Audit of Codex's `youtube_integration` work (chat-icon path):
- **Wire format v5 is correct.** `ChatPayload.Serialize` (C#) field order matches `parse_chat` (C++) read order exactly (platform, platformIcons, username, message, color, timestamp, flags, bits, subMonths, badges, emotes).
- **Icon rendering is correct.** `renderer.cpp` `DrawPrefix` draws one badge per `platformIcons` entry; `parse_platform_icons` splits on `|` and falls back to the single routing platform. Single-target sends → 1 icon; `platformFilter` keys off the routing `platform`, not the icon list. No rendering bug.
- The reported "two icons (YouTube + Kick)" was **not** a render bug. Real inbound chat never carries a multi-platform icon list (`evt.Data["platformIcons"]` is never populated → always the single source platform). Multi-icon lines only ever come from a deliberate multi-target send echo.

Behaviour Rob specified for "All" (corrects an earlier wrong attempt this session):
- "All" must POST TO EVERY LIVE PLATFORM, with `{platform}` in the text naming where the event came
  from. It must NOT be routed to a single origin platform. ("All goes to all, with the platform in the
  text.")
- "All" must only ever send to platforms that are LIVE right now. An offline platform never receives it.
- `{platform}` resolves to the event's origin platform (so a Twitch follow announced into all live chats
  still reads "...on Twitch").

Root cause of "follow messages sent to everyone without context" (proven, not guessed):
- `ChatbotService.ExpandTokens` had no `{platform}` token (and no `{bits}`/`{amount}`/`{amountDisplay}`),
  so a template literally could not say "on Twitch".
- The "All" send paths posted to every *connected* platform, with no live-gating, so an offline platform
  still got the message.

Fixes applied:
- `Steaming.Core/Services/ChatbotService.cs`
  - `ExpandTokens` now takes the event platform and adds tokens: `{platform}` (origin platform of the
    event), `{bits}`, `{amount}` (Kick gift-sub `amount`, falls back to `bits`), `{amountDisplay}`
    (YouTube Super Chat formatted string, falls back to amount). `{amountDisplay}` is replaced before
    `{amount}` to avoid substring collision. Shout/command broadcast behaviour is otherwise unchanged.
  - Live-announce path passes a null platform → `{platform}` renders empty (intentionally all-platform).
- `Steaming.WinUI/App.xaml.cs` (chatbot `SendMessage` delegate) and
  `Steaming.Application/ViewModels/MainViewModel.cs` (`SendChatAsync`): "All" (`BotReplyTarget.Both`) now
  only sends to platforms that are LIVE — Twitch via `TwitchIsLive`, Kick via `KickIsLive`, YouTube via
  `YouTube.IsConnected` (the YouTubeLiveChatService live-chat connection). Explicit single-platform
  targets always send regardless of live state.
- `Steaming.WinUI/Pages/DashboardPage.xaml`: send-target picker label `Both` → `All`.
- Unified bot output with manual sends so an "All" announcement renders as ONE merged line (combined
  `[T][K][Y]` badges) in BOTH the app chat list and OBS, not one line per platform:
  - `Steaming.WinUI/App.xaml.cs`: the `chatbot.SendMessage` delegate now just calls
    `vm.SendChatAsync(msg, target)` instead of sending to each platform itself. `SendChatAsync` already
    live-gates "All", builds the single merged echo (`SendLocalOutgoingChatEchoAsync` →
    `PlatformIcons="Twitch|Kick|YouTube"`), and registers the pending-echo suppression that swallows the
    real per-platform bounces. Removed the now-dead `SendKickMessageWithBridgeRecoveryAsync` helper.
  - `Steaming.Application/ViewModels/MainViewModel.cs` `SendChatAsync`: folded the Kick bridge-recovery
    (re-bootstrap + retry on a failed bridge send) into the Kick branch so routing bot output through it
    keeps the reliability the old chatbot path had — and the manual path now gains it too.
  - Result: "All" → single merged-badge line; single-platform target → single badge. Real self-echoes are
    suppressed on both surfaces. Multi-platform echo display name is "You" (single-platform shows the
    sending account name) — flag if Rob wants the bot/account name on the merged line instead.
- `Steaming.Core/Platforms/YouTubeLiveChatService.cs` — debug.log spam removed (Rob: "don't need YouTube
  spamming my debug log anymore"). `SetStatus` now dedupes (only logs/raises on a real state change; it was
  being called on every gRPC chat message). Removed the per-message "streamList recv" line, the per-open
  "streamList opening" line, and the per-candidate / repeated-offline broadcast-discovery lines. Kept the
  one-shot lifecycle lines (Start/Stop/Attached/Selected-live) and deduped status.

Runtime still needed:
1. Follow shout `Thanks for the follow on {platform}, {user}!` Go live on Twitch + Kick + YouTube, follow
   on Twitch — confirm the message lands in ALL THREE live chats and reads "on Twitch".
2. Take one platform offline, repeat — confirm the offline platform gets nothing, the live ones still do.
3. Bits/Super Chat shout `Thanks for the {amountDisplay} on {platform}!` — correct amount + platform.
4. Dashboard "All" send while all three live → OBS line shows T, K, Y badges; one badge for single target.

YouTube-integration audit findings (Codex `youtube_integration` work) — full review, not just chat/icons:
- CORRECT: OAuth login (force-ssl scope, offline access, PKCE S256, consent), token exchange/refresh/parse,
  channel fetch, credential save/clear (broadcaster + bot), gRPC `streamList` receive with `nextPageToken`
  resume, event mapping (chat / SuperChat+Sticker→Bits / NewSponsor+MemberMilestone→Subscribe /
  MembershipGifting→GiftSubscribe), outbound send (broadcast resolved with broadcaster token, sent with bot
  token when present), OBS-streaming-gated discovery, Connections UI, chat-icon v5 wire format.
- NOTE — gRPC transport uses 3 NuGet packages (`Google.Protobuf`, `Grpc.Net.Client`, `Grpc.Tools`). Codex
  asked and Rob approved these; they are authorized. (Not a rule violation — recorded so a future session
  doesn't re-flag them.)
- KNOWN (Rob already aware) — YouTube has no Follow event (API doesn't expose follows via live chat), so
  follow alerts/shouts never fire for YouTube. Platform limitation, like Kick raids.
- KNOWN, not a priority right now — `StreamDataService` has no `YouTubeIsLive`; combined `IsLive` and the
  dashboard live/viewer breakdown ignore YouTube (only Twitch+Kick). YouTube live state lives only in
  `YouTubeLiveChatService.IsConnected`. Making YouTube first-class in stream data is a deliberate later
  follow-up, NOT done here.
- MINOR — duplicate refresh logic (`YouTubeLiveChatService.RefreshTokenAsync` vs
  `PlatformCredentialService.RefreshYouTubeTokenAsync`); identical `CreateYouTubeLoginRequest` /
  `CreateYouTubeBotLoginRequest`; several `new HttpClient()` per-call sites in the YouTube paths.

## Session 2026-06-27 (v0.10.80) - installed-app WebView2 crash + firewall + real logout/reconnect hardening + dashboard platform breakdown (branch `feature/reward-fuzzy-alerts`)

## Session 2026-06-27 (v0.10.81) - Kick follower dashboard count audit/fix on `master`

## Session 2026-06-27 (v0.10.82) - fix single-platform stream polling on `master`

## Session 2026-06-27 (v0.10.83) - YouTube login + live chat + alert pipeline on `youtube_integration`

### Session continuation 2026-06-27 - outgoing echo identity fix + OBS-gated YouTube monitoring on `youtube_integration`

Build-verified WinUI **Release** 0 errors. OBS plugin **RelWithDebInfo** built successfully. Runtime still UNVERIFIED.

Root causes fixed:
- App/OBS local outgoing echo suppression was matching on guessed sender names instead of the actual transport identity.
  - Kick bridge sends are bound to the broadcaster login (`KickUsername` / broadcaster user id), but the local echo path was labeling them as `KickBotUsername` whenever a Kick bot token existed.
  - YouTube local echo used the saved channel title, while inbound YouTube chat can return a different live-chat author display name or handle for the same channel, so name-only suppression failed.
- YouTube monitoring was started purely from login/app startup, so live-broadcast discovery kept running even when OBS was connected and explicitly knew the app was not live.

Fixes applied:
- `Steaming.Application/ViewModels/MainViewModel.cs`
  - Outbound send now records the actual sender identity used per platform:
    - Twitch: bot username only when the Twitch bot client is the sender.
    - Kick bridge: broadcaster username/id from the desktop Kick login context.
    - Kick direct API send: bot username only when the direct Kick bot token is the sender.
    - YouTube: broadcaster/bot channel ids are tracked so inbound self-echoes can be matched even when Google returns a different display string than the saved title.
  - Pending outgoing chat suppression now matches on sender ids first, then names as fallback, instead of names only.
  - Added `SyncYouTubeChatMonitoringAsync()` so YouTube chat discovery follows OBS state:
    - OBS connected + not streaming: pause YouTube discovery.
    - OBS connected + streaming: start/attach YouTube discovery immediately.
    - OBS state unavailable: keep fallback discovery enabled.
- `Steaming.Core/Services/OverlayDispatcher.cs`
  - Overlay-side pending outgoing echo suppression now uses the same sender id + sender name matching logic as the app chat list, so OBS and in-app suppression stay aligned.
- `Steaming.WinUI/App.xaml.cs`
  - Startup now asks the view model to decide whether YouTube monitoring should start, instead of unconditionally starting it on app launch.

What this is intended to fix at runtime:
- A Kick send routed through the bridge should no longer produce a local `KickBot` echo followed by a real `RobGraham` echo for the same message.
- A YouTube self-send should no longer survive as two lines just because the local echo used `Rob Graham` while inbound chat came back as `@starfleetau`.
- When OBS is connected and offline, YouTube should stop trying to discover a live broadcast until OBS reports that streaming has started.

Build notes:
- `dotnet build Steaming.WinUI/Steaming.WinUI.csproj -c Release` succeeded, 0 errors.
- `cmake --build build_x64 --config RelWithDebInfo` succeeded from `obs/obs-plugintemplate` and deployed `steaming-plugin.dll`.

### Session continuation 2026-06-27 - YouTube API audit + live-chat lookup/send fixes on `youtube_integration`

Build-verified WinUI **Release** 0 errors. OBS plugin **RelWithDebInfo** built successfully. Runtime still UNVERIFIED.

Audit completed against the official YouTube Live Streaming API docs:
- `liveBroadcasts.list`: the previous runtime build was sending mutually-exclusive filters together (`mine` + `broadcastStatus`), which YouTube rejects with `400 incompatibleParameters`. Official doc: filters must specify exactly one of `broadcastStatus`, `id`, or `mine`.
- `liveChatMessages.insert`: current request shape remains valid (`part=snippet`, `snippet.liveChatId`, `snippet.type=textMessageEvent`, `snippet.textMessageDetails.messageText`).
- `liveChatMessages.list`: current polling request shape remains valid (`liveChatId`, `part=id,snippet,authorDetails`, `maxResults=200`).
- `liveBroadcast.status.lifeCycleStatus`: only exact `live` means active; transitional states like `liveStarting` are separate states.

Fixes applied in this continuation:
- `Steaming.Core/Platforms/YouTubeLiveChatService.cs`
  - `liveBroadcasts.list` lookup kept on the corrected official request:
    `part=id,snippet,status&mine=true&broadcastType=all&maxResults=50`
    with no `broadcastStatus` parameter.
  - Outbound YouTube chat no longer tries to resolve the broadcaster's active `liveChatId`
    with a bot token. If a separate YouTube bot account exists, chat send now resolves the
    broadcast using the broadcaster token and only uses the bot token for the actual
    `liveChatMessages.insert` call.
  - Unauthorized broadcast-lookup refresh now preserves whether the caller was using a bot or
    broadcaster token.
  - Broadcast selection now requires `status.lifeCycleStatus == "live"` exactly instead of the
    previous loose `Contains("live")` check.

Why this mattered:
- The red YouTube status and repeated `400` errors in `debug.log` were caused by an invalid API
  request, not by OBS, not by the OAuth callback, and not by the stream key.
- A separate latent send bug would have broken YouTube outbound chat for any setup using a
  distinct bot channel.

Build notes:
- `dotnet build Steaming.WinUI/Steaming.WinUI.csproj -c Release` succeeded, 0 errors.
- `cmake --build build_x64 --config RelWithDebInfo` succeeded and produced `steaming-plugin.dll`.

Runtime still needed immediately:
1. Start a real YouTube live broadcast on the authenticated broadcaster account.
2. Launch this rebuilt app and confirm `debug.log` no longer shows `400 incompatibleParameters`.
3. Confirm the YouTube card changes from authorized/offline to connected.
4. Confirm inbound YouTube chat appears in-app and in the OBS chat source when the filter is `YouTube` or `All`.
5. Confirm outbound dashboard/chatbot messages post into the active YouTube live chat.

Build-verified WinUI **Release** 0 errors. Runtime UNVERIFIED.

### Session continuation 2026-06-27 - sent-chat icon regression fix in app + OBS on `youtube_integration`

Build-verified WinUI **Release** 0 errors. OBS plugin **RelWithDebInfo** built successfully. Runtime still UNVERIFIED.

Reported regression:
- Sent chat lines could post to Twitch/Kick, but the in-app/OBS display path no longer represented multi-target sends correctly.
- OBS chat render proved the root cause in code:
  - `obs/obs-plugintemplate/src/renderer.cpp` treated platform color as `Twitch` or else `Kick`.
  - `DrawPrefix(...)` rendered `T` for Twitch and `K` for every non-Twitch platform.
  - The chat pipe payload only carried one platform string, so a single app-originated send to multiple platforms had no way to tell OBS to render multiple platform icons on one line.

Fixes applied:
- `Steaming.Core/Ipc/ChatPayload.cs`
  - Chat payload wire bumped to **v5** with a second UTF-8 field:
    `platformIcons` = display platforms joined with `|` (for example `Twitch|Kick`).
  - `platform` is still the primary routing/filtering platform, so existing per-platform filtering remains intact.
- `Steaming.Core/Services/OverlayDispatcher.cs`
  - Added local outgoing chat echo support for OBS with suppression of the later real platform echoes, so a send to multiple platforms renders once with the intended combined icon set instead of duplicating or mislabeling.
  - Real inbound chat now also respects the new `platformIcons` display field when provided.
- `Steaming.Application/ViewModels/MainViewModel.cs`
  - Dashboard/app local send echo now coordinates with the overlay echo so the app chat list and OBS path stay aligned.
  - Pending outgoing echo consumption was tightened to remove matched platforms without duplicate app-line spam.
- `obs/obs-plugintemplate/src/chat_source.cpp`
  - Updated parser to match chat payload v5 and split `platformIcons` into a vector for rendering.
- `obs/obs-plugintemplate/src/renderer.h` / `renderer.cpp`
  - Chat lines now carry multiple display platforms.
  - Prefix rendering now draws each requested platform badge in order instead of hard-forcing all non-Twitch lines to Kick.
  - Platform-color mode now recognizes YouTube explicitly instead of collapsing non-Twitch to Kick green.

What this fix is intended to preserve:
- Real inbound Twitch/Kick/YouTube messages still filter by their actual source platform.
- Single-platform sent lines still show a single matching platform badge.
- Multi-target sent lines can render multiple platform badges on one line in OBS and in the app echo path.

Build notes:
- `dotnet build Steaming.WinUI/Steaming.WinUI.csproj -c Release` succeeded, 0 errors.
- `cmake --build build_x64 --config RelWithDebInfo` succeeded and deployed `steaming-plugin.dll`.

Immediate runtime check still needed:
1. Send to `Twitch`, `Kick`, `YouTube`, and the combined/multi-target option from the app.
2. Confirm the in-app chat line prefix matches the actual targets used.
3. Confirm the OBS chat source shows the correct platform badges on the sent line itself, not just on the later inbound echo.
4. Confirm the later real inbound echoes do not duplicate the app-originated line in OBS.

### Session continuation 2026-06-27 - YouTube inbound chat transport switched from REST polling to official gRPC `streamList`

Build-verified WinUI **Release** 0 errors. OBS plugin **RelWithDebInfo** built successfully. Runtime still UNVERIFIED.

Root cause fixed:
- The original YouTube inbound chat implementation used `liveChatMessages.list` polling in
  `Steaming.Core/Platforms/YouTubeLiveChatService.cs`.
- Google’s own docs for live chat explicitly provide `liveChatMessages.streamList` as the
  long-lived streaming receive path, and the separate Streaming Live Chat guide documents that path
  as a gRPC client using `stream_list.proto`.

What changed:
- Added the minimum gRPC/protobuf dependencies to `Steaming.Core/Steaming.Core.csproj`:
  - `Grpc.Net.Client`
  - `Google.Protobuf`
  - `Grpc.Tools`
- Added vendored proto contract from the official Google docs:
  - `Steaming.Core/Protos/youtube_stream_list.proto`
- `Steaming.Core/Platforms/YouTubeLiveChatService.cs`
  - Replaced the `liveChatMessages.list` receive loop with a generated gRPC client for
    `V3DataLiveChatMessageService.StreamList`.
  - The service now:
    - still uses `liveBroadcasts.list` only to discover the active `liveChatId`
    - opens a long-lived stream against `youtube.googleapis.com`
    - resumes from `nextPageToken`
    - maps streamed chat payloads back into the existing `EventBus` event types for:
      - chat
      - Super Chats / Super Stickers
      - new memberships / milestone chats
      - membership gifting
  - Added gRPC status handling for:
    - unauthenticated / reconnect required
    - chat ended / precondition failures
    - chat not found
    - quota/resource exhausted
    - permission denied
- The earlier quota/backoff patch remains relevant for broadcast discovery/error recovery, but the
  main chat receive path is no longer constant REST polling.

What was intentionally preserved:
- Existing YouTube OAuth login and token refresh flow
- Existing outbound YouTube send path
- Existing app chat, alert, activity, and OBS dispatch/event mapping
- Existing Twitch/Kick integrations

Build notes:
- `dotnet build Steaming.WinUI/Steaming.WinUI.csproj -c Release` succeeded, 0 errors.
- `cmake --build build_x64 --config RelWithDebInfo` succeeded and deployed `steaming-plugin.dll`.
- Remaining warnings are pre-existing:
  - `RewardService.cs` CA1416
  - `ChatSettingsPage.xaml.cs` unused `_loading`

Immediate runtime checks still needed:
1. Start a real YouTube live stream with chat enabled.
2. Log into YouTube in the app and confirm `debug.log` shows `streamList opening` / `streamList recv`
   instead of repeated `liveChatMessages.list` polling lines.
3. Post inbound YouTube chat and confirm it reaches:
   - in-app chat
   - OBS chat source
4. Trigger a Super Chat / Super Sticker and confirm it still reaches the existing Bits-style alert path.
5. Confirm YouTube stream disconnect/reconnect resumes cleanly using `nextPageToken`.

Scope completed in this branch pass:
- YouTube platform identity/auth fields added to the existing shared auth/token flow:
  `PlatformAuthConfig`, `TokenStore`, `PlatformSessionFlowService`, and
  `PlatformCredentialService`.
- New `Steaming.Core/Platforms/YouTubeLiveChatService.cs`:
  - waits for stored YouTube OAuth credentials
  - resolves the active `liveChatId` from the official YouTube Data API
  - polls live chat on the returned `pollingIntervalMillis`
  - publishes existing `StreamEvent`s into the shared bus for:
    - chat messages
    - Super Chats / Super Stickers (`EventType.Bits`)
    - new memberships / milestone chats (`EventType.Subscribe`)
    - membership gifting (`EventType.GiftSubscribe`)
  - supports outbound chat send through the same OAuth session
  - refreshes expired YouTube tokens from the stored refresh token
- Existing app paths were extended instead of duplicated:
  - `App.xaml.cs` now registers/starts `YouTubeLiveChatService`
  - chatbot outbound routing supports `BotReplyTarget.YouTube`
  - `MainViewModel.SendChatAsync` supports YouTube
  - `ConnectionsPage` now has YouTube connect/disconnect UI
  - dashboard send-target picker now includes YouTube
  - chatbot command/shout/timer target pickers now include YouTube
  - in-app chat lines now render `[Y]` prefix
- Alert/message wording was corrected so YouTube Super Chats do **not** incorrectly say
  "cheered bits" when the app is still on the default Bits alert text:
  - overlay alert fallback text becomes `{user} sent {amountDisplay}!`
  - activity feed + alert TTS also use `amountDisplay`
  - existing custom user text is preserved unless it is still the stock Bits default
- Browser logout hygiene updated: `LoginWindow.ClearPlatformCookiesAsync(...)` now knows
  the YouTube / Google auth cookie domains.

What was intentionally preserved:
- Existing Twitch login/chat/EventSub/stat paths
- Existing Kick login/chat/bridge/raid/stat paths
- Existing shared `EventBus` / `OverlayDispatcher` / `ChatbotService` wiring
- No new external NuGet dependencies were added

Files:
- `Steaming.Core/Platforms/YouTubeLiveChatService.cs`
- `Steaming.Core/Models/StreamEvent.cs`
- `Steaming.Core/Configuration/PlatformAuthConfig.cs`
- `Steaming.Core/Auth/TokenStore.cs`
- `Steaming.Core/Services/ChatbotService.cs`
- `Steaming.Core/Services/OverlayDispatcher.cs`
- `Steaming.Application/Services/PlatformSessionFlowService.cs`
- `Steaming.Application/Services/PlatformCredentialService.cs`
- `Steaming.Application/Services/ChatTtsService.cs`
- `Steaming.Application/ViewModels/ChatMessageItem.cs`
- `Steaming.Application/ViewModels/MainViewModel.cs`
- `Steaming.WinUI/App.xaml.cs`
- `Steaming.WinUI/LoginWindow.xaml.cs`
- `Steaming.WinUI/Pages/ConnectionsPage.xaml(.cs)`
- `Steaming.WinUI/Pages/ChatbotPage.xaml(.cs)`
- `Steaming.WinUI/Pages/DashboardPage.xaml(.cs)`
- `Steaming.Core/Steaming.Core.csproj`

Build notes:
- `dotnet build Steaming.WinUI/Steaming.WinUI.csproj -c Release` succeeded, 0 errors.
- `cmake --build build_x64 --config RelWithDebInfo` compiled `steaming-plugin.dll`, but the
  final deploy copy into `C:\ProgramData\obs-studio\plugins\steaming-plugin\bin\64bit\` failed
  because the destination DLL is locked (OBS running). No C++ source changes were made.

Runtime still needed before claiming shipping-ready YouTube support:
1. Configure a real YouTube OAuth desktop client ID/secret and complete the login flow.
2. While a YouTube live stream is active, confirm chat messages appear in-app and in OBS chat.
3. Send a normal outbound message from the dashboard to YouTube and confirm it posts successfully.
4. Trigger a real Super Chat / Super Sticker and confirm:
   - the alert fires
   - the activity feed wording uses the display amount, not "bits"
   - chatbot shouts targeting `Bits` can send to YouTube
5. Trigger a membership / gifting event and confirm existing subscribe / gift-sub alert paths fire.

Build-verified WinUI **Release + Debug** 0 errors. Runtime UNVERIFIED.

Reported major bug: users who were not logged into Twitch could still connect Kick chat/bridge, but the
shared stream-data path did not start correctly for a Kick-only setup. That blocked Kick API-driven
viewer count, subscriber count, live/offline state, and downstream dashboard/analytics/status updates.

Root cause (proven in code, not guessed):
- `Steaming.Core/Services/StreamDataService.cs` only started its poll loop through the Twitch startup
  path (`Start(token, clientId, userId, username)`).
- `PollOnceAsync()` immediately returned unless Twitch auth fields were populated in-memory, so the
  service could not exist as a neutral shared poller.
- Kick polling itself was already correctly self-gated on Kick credentials; the bug was that the
  shared loop was effectively "owned" by Twitch startup.

Fix:
- `StreamDataService` now has a parameterless `Start()` that starts the shared poll loop once,
  independently of whether Twitch is logged in.
- App startup now starts that shared loop after tokens load and assigns `OverlayDispatcher.StreamData`
  immediately, so single-platform users are covered from launch.
- `PollOnceAsync()` now reads Twitch credentials from `TokenStore` each tick and only runs Twitch
  polling when Twitch credentials are present.
- When Twitch credentials are absent, the Twitch side of the shared totals/live state is reset to 0/offline
  instead of blocking the whole service.
- Kick polling remains unchanged in its gating behavior: it still does **not** run unless Kick
  credentials exist (`KickAccessToken` + `KickChatroomId > 0`).

What currently still works and was intentionally preserved:
- Twitch login/chat/EventSub/stats flow when Twitch credentials exist.
- Kick login/chat/bridge flow.
- Kick API polling only when Kick credentials exist.
- Kick raid/follower extras path.

Files:
- `Steaming.Core/Services/StreamDataService.cs`
- `Steaming.WinUI/App.xaml.cs`
- `Steaming.Core/Steaming.Core.csproj` (version bump to `0.10.82`)

Build notes:
- `dotnet build Steaming.WinUI/Steaming.WinUI.csproj -c Release` succeeded, 0 errors.
- `dotnet build Steaming.WinUI/Steaming.WinUI.csproj -c Debug` succeeded, 0 errors.
- `cmake --build build_x64 --config RelWithDebInfo` in `obs/obs-plugintemplate` compiled
  `steaming-plugin.dll`, but the final deploy step failed copying into
  `C:\ProgramData\obs-studio\plugins\steaming-plugin\bin\64bit\steaming-plugin.dll` because the
  destination DLL was locked (likely OBS running). No C++ source changes were made this session.

Runtime still needed:
1. Kick-only setup: launch with no Twitch token and a valid Kick login. Confirm Kick viewer count,
   Kick subscriber count, Kick live state, and dashboard totals all update.
2. Twitch-only setup: confirm Twitch polling still updates exactly as before.
3. Dual-platform setup: confirm combined totals still reflect Twitch + Kick correctly.
4. Log out of Twitch while Kick stays connected: confirm Kick polling continues.
5. Re-run the C++ plugin build with OBS closed so the final deploy copy can complete cleanly.

Build pending at time of writing in this handoff entry; finish with WinUI Release + Debug and the C++
plugin build before shipping/closing the session.

Reported runtime bug (screenshot-backed): the dashboard follower ribbon showed `Twitch: 57  Kick: 12`
and combined `69`, while the actual platform pages showed Twitch 57 and Kick 59.

Root cause (proven in code, not guessed):
- `Steaming.Core/Services/StreamDataService.cs` seeded Kick followers from
  `AppSettings.KickFollowerCountEstimate` and only changed it on live Kick follow events.
- The official Kick channel poll (`GET /public/v1/channels`) never populated a real follower total.
- The official Kick API docs checked in this repo (`kick_api_docs.txt`) document channel fields like
  `active_subscribers_count`, `stream`, `stream_title`, and category, but no follower-total field.
- Result: the dashboard exposed a stale local estimate as if it were authoritative.

Fix:
- `StreamDataService` no longer surfaces the stale Kick follower estimate as real follower data.
  Kick follower totals now remain "unknown" unless an authoritative source exists.
- Combined followers now exclude Kick when Kick's total is unknown, instead of adding a wrong estimate.
- `MainViewModel` carries a `KickFollowerCountKnown` flag from the stream-data payload.
- `DashboardPage` now renders followers honestly:
  - big total shows `TwitchCount+` when Kick total is unknown
  - breakdown shows `Kick: --` instead of a false number
- The per-platform color-coded viewer/sub breakdown tweak remains in place.

Why this is the correct fix:
- It preserves what already works: Twitch follower totals, Kick viewer counts, Kick subscriber counts,
  reconnect/auth handling, and the dashboard layout.
- It stops the app from lying about Kick followers until Kick exposes an official follower-total API we
  can use within the project's API rules.

Build-verified WinUI **Release + Debug** 0 errors. Installer rebuilt successfully:
`artifacts\release\installer\Steaming-Setup-0.10.79.exe`
SHA-256 `577B1B3F66FD6A884195402669FC97CE5272BFFD6A9D553BC900A20E5020022F`.
Runtime UNVERIFIED on a clean standard-user install (Rob hit this on another machine — needs re-test there).

C++ plugin compile reached `steaming-plugin.dll` successfully, but the final auto-deploy copy into
`C:\ProgramData\obs-studio\plugins\...` failed because `obs64` was running and holding the destination DLL
open. This did NOT block the installer rebuild because `build/release.ps1` stages the plugin artifact directly.

Reported runtime bug (screenshot): on a machine where the **installer** put the app in
`C:\Program Files\robgraham\Streaming Console`, launch failed with *"We couldn't create the data
directory … Steaming.WinUI.exe.WebView2\EBWebView"*.

Root cause (proven, not guessed): WebView2's default user-data folder is `<exe>.WebView2\` **next to the
exe**. Installed under Program Files that path is read-only for a standard user, so CoreWebView2 can't
create it. `LoginWindow.GetUserDataFolder` already returned a LocalAppData path but was **only used by
`ClearProfileData`** — none of the 3 WebView2 instances (2 login windows + `KickWebChannelResolver`) ever
set a user-data folder, so they all fell back to the Program Files default.

Fix: `App.RedirectWebView2UserData()` sets the process-wide `WEBVIEW2_USER_DATA_FOLDER` env var to
`%LocalAppData%\Steaming\WebView2` in the App ctor **before** InitializeComponent / any WebView2. One
shared folder = same cross-component cookie store as before (the Kick resolver still sees the Kick OAuth
cookie), just in a writable location. `ClearProfileData`'s per-profile path is unchanged (it was already a
no-op against the real store — pre-existing, left alone, out of scope).

Firewall (Rob: "the entire program needs firewall access, it uses websocket"): installer now adds an
explicit allow rule (in+out, all profiles) for the exe in `SEC_APP`
(`netsh advfirewall firewall add rule name="Rob's Steaming Console" …`), cleared first to avoid upgrade
dupes, and **removed on uninstall**. Note: Twitch/Kick/OBS traffic is outbound and the OAuth callback is
loopback (both normally allowed without a rule), but the rule is added per Rob's request so Windows can
never silently block the app and no firewall prompt ever appears.

Disconnect / reconnect hardening (completed and build-verified):
- `Steaming.WinUI/LoginWindow.xaml.cs`: replaced the dead folder-delete logout with
  `ClearPlatformCookiesAsync(...)`, which creates a throwaway hidden WebView2 and deletes the actual
  Twitch/Kick cookies through `CoreWebView2.CookieManager`. Root cause of the fake logout was that the old
  code deleted `%LocalAppData%\Steaming\WebView2Auth\...`, a folder no live WebView2 instance ever used,
  so reconnect silently re-authed from the real shared cookie jar.
- `Steaming.WinUI/App.xaml.cs`: exposed `App.WebViewHost` so cookie clearing can run against the hidden
  host panel already used for WebView2-based Kick work.
- `Steaming.WinUI/Pages/ConnectionsPage.xaml.cs`: both Disconnect buttons now clear the real browser
  cookies; Twitch disconnect also tears down EventSub immediately so the old account stops receiving
  follows/raids/subs/redemptions before re-login.
- `Steaming.Core/Platforms/TwitchEventSubClient.cs`: `ConnectAsync` is now idempotent. It tears down any
  existing socket/read loop before opening a new one, and exposes `DisconnectAsync()` for logout.
- `Steaming.Core/Services/StreamDataService.cs`: `Start()` now cancels the old poll loop before disposing
  its CTS, so re-login replaces the loop instead of stacking a second Twitch/Kick poller.
- `Steaming.Application/ViewModels/MainViewModel.cs`: Kick logout now disconnects the remote bridge and
  stops the raid listener, but deliberately does NOT stop the shared `StreamDataService`/`ViewerListService`
  because those are dual-platform services and the new idempotent restart path handles re-login cleanly.

Automatic re-login paths intentionally preserved:
- Startup bootstrap still reconnects Twitch chat, Twitch badges/EventSub, stream polling, the Kick bridge,
  and Kick raid listener from stored credentials.
- `RemoteKickBridgeClient.ConnectAsync()` was already idempotent and remains so.
- `ViewerListService.Start()` was already idempotent (`Stop()` first) and remains so.
- `StreamDataService.KickTokenRefreshed` still re-bootstraps the bridge with `refreshToken:false`, so the
  poll refresh path keeps working with Kick's single-use refresh tokens.

Files: `Steaming.WinUI/App.xaml.cs`, `Steaming.WinUI/LoginWindow.xaml.cs`,
`Steaming.WinUI/Pages/ConnectionsPage.xaml.cs`, `Steaming.Core/Platforms/TwitchEventSubClient.cs`,
`Steaming.Core/Services/StreamDataService.cs`, `Steaming.Application/ViewModels/MainViewModel.cs`,
`installer/steaming.nsi`.

Additional dashboard change (build-verified):
- `Steaming.Core/Services/StreamDataService.cs` now publishes per-platform viewer/follower/subscriber
  counts in the existing `StreamDataUpdated` event payload.
- `Steaming.Application/ViewModels/MainViewModel.cs` now stores Twitch/Kick breakdown counts alongside the
  combined totals.
- `Steaming.WinUI/Pages/DashboardPage.xaml(.cs)` keeps the large combined totals, but now shows the live
  `TTL / TW / K` breakdown directly under Viewers, Followers, and Subs instead of making the user infer it
  from rotating/changing numbers.

Runtime still needed:
1. Installed build on a standard-user machine in `Program Files`: launch app, confirm WebView2 no longer
   fails creating `EBWebView`.
2. Twitch: Connect → Disconnect → Connect again. Confirm the second connect shows a real login prompt
   (not silent re-auth), only one EventSub socket is active, and alerts/events do not duplicate.
3. Kick: Connect → Disconnect → Connect again. Confirm the second connect shows a real login prompt, the
   bridge re-bootstraps, and the raid listener restarts without duplicate events.
4. With one platform still connected, log out of the other and confirm shared viewer/live polling continues
   for the remaining platform.
5. Forced auth failure prompt: expire/break Twitch auth and confirm a modal appears saying
   "Unable to auth with Twitch ... Please reconnect Twitch from the Connections page." Do the same for Kick.
   Kick prompt must appear only after refresh/recovery fails, not on a successful silent token refresh.
6. Dashboard during a live dual-platform stream: confirm the top ribbon reads the combined total in large
   text and the `TTL / TW / K` breakdown lines stay correct for Viewers, Followers, and Subs.

## Session 2026-06-26 (v0.10.74) - editor undo fix + SQLite CVE fix (branch `feature/reward-fuzzy-alerts`)

Both changes **runtime-verified by Rob** (undo works; analytics loads with the new SQLite lib).

1. **Editor undo did not capture a move when the element was parked on an existing keyframe.**
   `WritePositionToBestTarget` only snapshotted on base-geometry moves and on NEW-keyframe creation; a
   drag/resize/rotate that landed on an EXISTING keyframe modified it with no undo snapshot, so Ctrl+Z
   couldn't revert it (regression rode in with commit `17f80cb`'s re-target condition). Fix: one snapshot
   per gesture, taken on the first write, via `BeginGeometryGesture()` (set at the 3 canvas pointer-press
   handlers, reset in `ClearActiveDragKeyframe`). Typed property-box edits and the flip button are
   untouched (`_gestureActive` is false for them → identical to old behaviour).
   Files: `Steaming.Application/ViewModels/AlertEditorViewModel.cs`, `Steaming.WinUI/AlertEditorWindow.xaml.cs`.

2. **NU1903 / CVE-2025-6965 (SQLitePCLRaw native SQLite < 3.50.2).** Fix exists but no Microsoft.Data.Sqlite
   version adopts it (8.0.0→2.1.6, 9.0.6→2.1.10, both vulnerable). Forced `SQLitePCLRaw.bundle_e_sqlite3`
   **3.0.3** (outside the `<= 2.1.11` advisory range, bundles SQLite ≥ 3.50.2) in `Steaming.Data.csproj`.
   NU1903 cleared; WinUI Release 0 errors; runtime confirmed (analytics reads/writes under MDS 8.0.0 + the
   3.x native lib). `dbread`/`_dbquery` tools left on 8.0.0 (not shipped). See memory
   `reference_sqlite_cve_no_patch`.

Installer NOT rebuilt — the v0.10.73 installer predates both fixes; rebuild via `build/release.ps1` to ship them.

## Session 2026-06-26 (v0.10.73) - Windows installer implemented (branch `feature/reward-fuzzy-alerts`)

**Installer compiles and is produced** (107 MB EXE, makensis 0 errors). NOT yet runtime-tested
(install/uninstall must be verified in a VM/Windows Sandbox before sharing — see installer/README.md).

New files:
- `installer/steaming.nsi` - NSIS installer. App → `C:\Program Files\Steaming`; plugin → fixed
  `%PROGRAMDATA%\obs-studio\plugins\steaming-plugin\` (NO OBS-path detection - decided with Rob;
  safest, matches existing working layout, never touches the user's OBS install). WebView2 handled
  via the Evergreen **bootstrapper** (detect via EdgeUpdate `pv` regkey, run `/silent /install` if
  missing). Upgrade = run previous uninstaller in place then install fresh. Every `RMDir /r` guarded
  by non-empty path + sentinel file (`Steaming.WinUI.exe` / `steaming-plugin.dll`). `%APPDATA%\Steaming`
  NEVER touched by install or uninstall.
- `build/release.ps1` - one-command packaging: publishes app self-contained
  (`-p:WindowsAppSDKSelfContained=true`), copies the prebuilt plugin artifact (copy-only, never
  rebuilds/deploys → never touches live OBS), bootstraps NSIS 3.12 from SourceForge `master.dl`
  mirror (the generic downloads. host returns an HTML interstitial - guarded with a PK magic-byte
  check), fetches the WebView2 bootstrapper, compiles, emits SHA-256. `-SignCert`/`-SignPassword`
  optional (unsigned by default - Rob has no code-signing cert; self-signed or accept SmartScreen).
- `installer/README.md` - build steps + a SAFE uninstall test procedure (do it in a VM).

Deviations from PLAN_installer_updater.md (all to reduce risk / match reality): ProgramData plugin
layout instead of OBS-root detection; WebView2 bootstrapper instead of the 180 MB offline standalone
(one URL to swap); updater (plan phases 5/7) NOT built - this session is installer only.

Build artifacts under `artifacts\` (gitignored? check). To rebuild: `build\release.ps1`.

## Session 2026-06-26 (v0.10.73 earlier) - About tab + installer plan review

Build-verified WinUI **Debug + Release** 0 errors. Runtime UNVERIFIED (page not opened in the app yet).

- New `Steaming.WinUI/Pages/AboutPage.xaml(.cs)`: project description (OBS C++ plugin + GDI + named pipe,
  dual-platform Twitch/Kick), source/issues pointer to robgraham.info (+ GitHub repo, link TBD — not yet
  uploaded), and an open-source acknowledgements list with per-component licenses. Acknowledgements are a
  static record array in code-behind (pure view content, no app state).
- Wired into nav: "About" item in the SYSTEM section of `MainWindow.xaml`; switch case in
  `MainWindow.xaml.cs` NavView_SelectionChanged → `Pages.AboutPage`.

Installer plan review (`docs/PLAN_installer_updater.md`): plan is sound. Corrected the WiX framing —
WiX source is free; the Open Source Maintenance Fee only applies to orgs earning >$10k/yr, so it does
NOT apply to this free friends-only app. NSIS still recommended, but on technical fit (imperative custom
OBS-detection/validate/prereq logic), not on a "WiX costs money" basis. Open gap flagged to Rob: the plan
says "signed installer" throughout but has no code-signing cert plan (a cert costs money / conflicts with
the no-money constraint; unsigned = SmartScreen warnings for friends). Decision pending.

## Session 2026-06-26 (v0.10.71–72) - reward→alert assignment (replaces Codex fuzzy) + rewards list UI (branch `feature/reward-fuzzy-alerts`)

Build-verified WinUI **Debug + Release** 0 errors. Runtime UNVERIFIED.

What Rob actually asked for (Codex did the wrong thing — built a FUZZY matcher he never requested):
- a redeemed reward fires the matching named alert (reward "Hydrate" → the "Hydrate" alert) over the
  generic redemption alert, with the ability to EXPLICITLY ASSIGN an alert to a reward, plus a rewards
  list that can be auto-populated from the platforms WITHOUT destroying existing entries/assignments.

Changes (v0.10.71, committed `40c5aeb`):
- `Steaming.Core/Models/ChannelReward.cs` (new): Id, Platform, Title, Cost, Enabled, AssignedAlert.
- `AppSettings.Rewards` + `MergeRewards(platform, fetched)` — non-destructive merge (keeps assignments,
  refreshes title/cost, never deletes entries missing from a fetch).
- `Steaming.Core/Services/RewardService.cs` (new): Twitch Helix `GET /channel_points/custom_rewards`
  and Kick `GET /public/v1/channels/rewards`; merges per platform; never throws.
- `CustomAlertMatcher` rewritten: **no fuzzy**. Explicit assignment wins; else exact (case-insensitive)
  reward-title == custom-alert-name; else generic. `OverlayDispatcher` + `SoundDispatcher` use it.
- Added `channel:rewards:read` to the Kick OAuth scopes (`PlatformSessionFlowService`).

Changes (v0.10.72, this commit):
- `RewardItem` VM + `MainViewModel.Rewards/RewardAlertOptions/LoadRewards/SaveRewards/RefreshRewards
  FromPlatformsAsync`.
- Overlays page: new "CHANNEL POINT REWARDS" section — a "Refresh from Twitch/Kick" button and a row per
  reward with a dropdown to assign a unique alert (saved on change).

PREREQUISITE for Kick auto-populate: Rob must **re-login to Kick once** so the token carries the new
`channel:rewards:read` scope. Twitch already had `channel:read:redemptions`.

Runtime still needed:
1. Overlays page → Refresh from Twitch/Kick → your rewards (Hydrate, Song, …) appear per platform;
   existing assignments survive a refresh.
2. Assign "Hydrate" reward → "Hydrate" alert; redeem on Kick → Hydrate alert fires (not generic).
   Also confirm exact name-match works even with nothing explicitly assigned.

## Session 2026-06-26 (v0.10.70) - replay stutter: replay reused stale render caches (branch `feature/reward-fuzzy-alerts`)

Build-verified WinUI **Release** 0 errors (Debug compiles clean; output-copy only blocked when the app
is running). Runtime UNVERIFIED by me — Rob reported the symptom on a PNG+text "Hydrate" alert.

Reported bug: on the Overlays-page preview, the **first** play of an alert was smooth, the **second**
(Replay) play stuttered at a few points — with no video/gif in the alert.

Root cause:
- `Steaming.WinUI/Pages/OverlaysPage.xaml.cs` — first play goes through `RebuildPreviewCanvas()`, which
  clears `_previewTextRenderCache`, `_previewGifData`, and the video frame state and recreates the
  element controls. `Replay_Click` did **not** call it — it only reset `PreviewTime = 0` and replayed.
  So replay started from the text-render cache (grids + signatures) left over from the end of the
  previous run, forcing extra dual/single-pass switches and signature-mismatch rebuilds mid-playback
  that the cold first play never did → the stutter. Replay was not symmetric with first play.
- `Steaming.WinUI/AlertEditorWindow.xaml.cs` — same latent asymmetry: `PlayBtn_Click` never cleared
  `_textRenderCache` / video frame state between plays.

Changes made:
- `Replay_Click` now calls `RebuildPreviewCanvas()` before `StartPlayback()` — replay is now identical
  to first play.
- `PlayBtn_Click` (editor) clears `_textRenderCache` and calls `ResetVideoFrameState()` at the start of
  each fresh play, so editor replays are deterministic too.
- Bumped `Steaming.Core` version to `0.10.70`.

Runtime still needed:
1. Overlays page: play the Hydrate (PNG+text) alert, then hit Replay several times — every play must be
   as smooth as the first.
2. Same check inside the alert editor's Play/Stop/Play.

## Session 2026-06-26 (v0.10.69) - audit of Codex's branch + remove WYSIWYG-breaking video throttle (branch `feature/reward-fuzzy-alerts`)

Build-verified WinUI **Debug + Release** 0 errors. Runtime UNVERIFIED.

Context: audited the whole `feature/reward-fuzzy-alerts` branch (Codex's work, squashed into commit
`17f80cb`). Found one concrete WYSIWYG regression plus one loose fuzzy-match rule. Fixed both.

Root cause of preview ≠ editor-playback mismatch:
- `Steaming.WinUI/AlertEditorWindow.xaml.cs` `RequestVideoFrame` quantized the video playhead to a
  **12 fps grid during playback** (`QuantizeVideoTime`, `EditorPreviewVideoFps`), but
  `Steaming.WinUI/Pages/OverlaysPage.xaml.cs` `RequestPreviewVideoFrame` did **not**. So the editor
  stepped video at 12 fps while the Overlays-page preview ran at its own cadence — the two surfaces
  (and the full-rate OBS C++ renderer) no longer agreed. This violated WYSIWYG.
- Both paths also had a `Math.Abs(last - t) < 0.03` entry guard and an in-loop `>= 0.03` re-decode
  guard — an effective ~33 fps cap that throttled playback below the render/OBS rate.

Changes made:
- Removed `QuantizeVideoTime` + the `EditorPreviewVideoFps`/`EditorPreviewVideoStepSec` constants and
  the `if (_vm.IsPlaying) t = QuantizeVideoTime(t)` call. **No throttle, ever.**
- Replaced the `< 0.03` time-window guards (entry + in-loop) in **both** `AlertEditorWindow` and
  `OverlaysPage` with an exact-frame check (`last == t` / `pend != t`). Every distinct requested time
  decodes at full rate; only a re-request of the *identical* frame is skipped (so a static/paused
  timeline still doesn't re-decode). The in-flight `_videoDecoding` coalescing (latest pending time
  wins) provides backpressure, so no decode pile-up. The two paths are now identical.
- `Steaming.Core/Services/CustomAlertMatcher.cs`: the containment rule awarded a flat 0.92 whenever
  one compacted name contained the other (only gated by length ≥ 5), so a short unrelated custom-alert
  name buried in a longer reward title could hijack the generic reward alert. Now the containment
  score scales by coverage (`0.5 + 0.5 * shorter/longer`), so e.g. "raid" inside "raidtrain" scores
  0.72 (< 0.78 threshold, no hijack) while "hydrate" inside "hydratenow" scores 0.85 (still matches).
- Bumped `Steaming.Core` version to `0.10.69`.

Architecture note (per Rob): the playback **clock and state evaluation already live in the backend**
(`AlertEditorViewModel.OnPlayTick` advances `PreviewTime`; `EvalAnimated`/`EvalSpansAt`/
`EvalTextTransitionState` in `Steaming.Application`). Both WinUI surfaces read that same shared state.
What is still **duplicated** is the WinUI control-building (`BuildTextGrid`/`RebuildTextGridFromSpans`
vs `OverlaysPage`'s copy) and the video decode path — that duplication is the remaining divergence
vector. The throttle was the active bug; unifying the two view builders into one shared helper is the
proper long-term guarantee of preview == editor == OBS. NOT done yet (separate refactor).

Runtime still needed:
1. Play an alert containing a **video** element in the editor and on the Overlays page — both must look
   identical and run at full frame rate (no 12 fps stutter in the editor), and match the OBS source.
2. Confirm a redeemed reward whose title resembles an unrelated custom alert fires the *correct* alert.

## Session 2026-06-26 (v0.10.68) - editor textboxes now apply live and numeric-only fields reject junk (branch `feature/reward-fuzzy-alerts`)

Build-verified WinUI **Release** 0 errors. Runtime UNVERIFIED.

What currently worked before this change:
- several slider / colour / combo-box paths already updated the editor immediately
- the main geometry panel had already been fixed to live-update on typed X/Y/W/H/Rotation
- drag/handle edits already wrote straight into the current preview state

Root causes:
- multiple editor textbox paths still either:
  - only committed on `LostFocus`, or
  - only synced a paired slider/textbox without actually pushing the underlying model immediately
- numeric-only fields were plain `TextBox` controls with no input filter, so they accepted arbitrary non-numeric text and then relied on later `TryParse` fallbacks

Changes made:
- added numeric-only textbox filtering in `Steaming.WinUI/AlertEditorWindow.xaml.cs` via `BeforeTextChanging`
- converted numeric editor fields to numeric-filtered inputs, including:
  - preview amount
  - master volume
  - geometry / rotation
  - corner radius
  - font size
  - clip volume
  - audio clip timing/volume fields
  - audio envelope keyframe fields
  - selected keyframe numeric fields
  - canvas width/height and duration
- live-apply now covers:
  - preview variables
  - master volume
  - clip volume typed entry
  - audio clip props
  - audio envelope keyframe fields
  - selected keyframe numeric fields
  - font size
  - canvas size
  - duration
- fixed the clip-volume textbox path so typing a value now writes the envelope point instead of only nudging the slider UI
- audio prop live apply now falls back to the current authored values when a field is mid-edit / temporarily unparsable, instead of silently treating it as zero
- selected keyframe live apply now preserves existing values on partial numeric edits, while still allowing blank fields to mean “not animated”
- bumped app version to `0.10.68`

Runtime still needed:
1. Type into duration, canvas size, master volume, preview variables, and confirm the editor updates immediately without waiting for focus loss.
2. On an audio element, type start/fade/volume values and confirm the timeline / clip state updates immediately.
3. On a selected keyframe, type time/X/Y/W/H/opacity/scale/rotation and confirm timeline + preview update while typing.
4. Try typing letters into numeric-only fields and confirm they are rejected.

## Session 2026-06-26 (v0.10.67) - typed geometry/rotation in the editor did not live-update the canvas (branch `feature/reward-fuzzy-alerts`)

Build-verified WinUI **Release** 0 errors. Runtime UNVERIFIED.

What currently worked before this change:
- dragging/moving/resizing/rotating on the canvas already updated the preview immediately
- geometry text-box edits did apply eventually when the user left the field
- the keyframe-aware write path itself (`WritePositionToBestTarget`) was already correct

Root cause:
- `Steaming.WinUI/AlertEditorWindow.xaml.cs`
  - `WireGeomBoxes()` only subscribed `_propX/_propY/_propW/_propH/_propRot` to `LostFocus`
  - typing into the property panel therefore changed only the textbox contents; the canvas preview was not updated until focus left the box
  - if this path had simply been moved to `TextChanged` unchanged, partial input would have parsed as `0`, so the live-apply path also needed to fall back to the current evaluated pose for any temporarily invalid field text

Changes made:
- `WireGeomBoxes()` now also applies on `TextChanged` for X/Y/W/H/Rotation
- live apply now evaluates the current pose first and uses those current values as fallback when a box contains partial/invalid text during typing
- width/height still clamp away from zero so the element cannot collapse to invisible
- bumped app version to `0.10.67`

Runtime still needed:
1. In the alert editor, type rotation values into the property panel and confirm the canvas updates while typing, not only after focus leaves.
2. Do the same for X/Y/W/H on a keyed pose and confirm the live preview follows the typed values.
3. Check partial negative entry (`-`, then `-10`) still behaves sensibly while typing and lands on the correct final value.

## Session 2026-06-26 (v0.10.66) - alert-page preview stutter was the text path, not PNG/video (branch `feature/reward-fuzzy-alerts`)

Build-verified WinUI **Release** 0 errors. Runtime UNVERIFIED.

What currently worked before this change:
- the alert-page preview already created PNG/image controls once in `CreatePreviewElementControl(...)` and reused them during playback
- playback timing itself already advanced correctly through `CompositionTarget.Rendering` -> `AlertEditorViewModel.OnPlayTick()`
- static images were not being reloaded per frame

Root cause:
- `Steaming.WinUI/Pages/OverlaysPage.xaml.cs`
  - `UpdatePreviewCanvas()` called `UpdateTextTransitionCanvas(...)` every render frame for text elements
  - `UpdateTextTransitionCanvas(...)` did `canvas.Children.Clear()` and rebuilt new `Grid` / `TextBlock` / `Run` trees every frame, even outside active text transitions
  - that meant a normal alert made of `PNG + text` still hammered the UI thread every tick because the text half of the preview was being torn down and recreated continuously
- this was the same class of bug already fixed in the full editor preview, but it still existed in the alert-page preview path

Changes made:
- added a per-element text render cache to `Steaming.WinUI/Pages/OverlaysPage.xaml.cs`
- single-pass text preview now reuses one grid and only rebuilds when the rendered text signature actually changes
- dual-pass transition preview now reuses the from/to grids and only updates opacity/translate each frame
- cache is cleared on full preview-canvas rebuild so stale controls are not retained
- bumped app version to `0.10.66`

Runtime still needed:
1. On the alert page, preview a PNG + text alert that was previously choppy and confirm playback is now smooth.
2. Verify Fade / Slide / Morph / Type On text transitions still preview correctly on the alert page.
3. Confirm GIF/video alerts still preview as before; this change did not touch those paths.

## Session 2026-06-26 (v0.10.65) - alert editor image/keyframe geometry wrote the wrong state (branch `feature/reward-fuzzy-alerts`)

Build-verified WinUI **Release** 0 errors. Runtime UNVERIFIED.

What currently worked before this change:
- drag-resize/rotate on the canvas already updated the current keyframe track during the gesture
- simple non-keyframed image geometry still saved correctly
- keyframed playback itself worked once the keyframes contained the right geometry values

Root causes:
- `Steaming.Application/ViewModels/AlertEditorViewModel.cs`
  - `AddSelectedKeyframeAtPreview()` copied `X`, `Y`, `Opacity`, and `Rotation`, but **did not copy `Width` or `Height`**
  - adding a new keyframe after a resized/flipped pose therefore created a keyframe that immediately fell back to the earlier width/height track
- `Steaming.WinUI/AlertEditorWindow.xaml.cs`
  - `UpdatePropertiesPanel()` populated X/Y/W/H/Rotation from the element base fields (`el.X`, `el.Width`, etc.), not the evaluated pose at the current preview time
  - `WireGeomBoxes()` wrote those property edits back with `_vm.UpdateSelectedGeometry(...)`, which edits the base element, not the current keyframe
  - result: property-panel geometry edits on keyed images were silently writing the baseline and making later keyframes appear to “snap back”
- the flip buttons were also using the whole-element flip helpers instead of the current preview/keyframe pose

Changes made:
- `AddSelectedKeyframeAtPreview()` now captures the current evaluated `Width` and `Height` as well as X/Y/Opacity/Rotation
- `UpdatePropertiesPanel()` now shows the evaluated geometry/rotation at the current preview time, not the base element fields
- `WireGeomBoxes()` now writes through `_vm.WritePositionToBestTarget(...)` so property edits target the active/current keyframe path instead of the baseline
- `FlipSelected(...)` now flips the current evaluated pose through the same keyframe-aware write path instead of applying a whole-animation/base flip
- bumped app version to `0.10.65`

Runtime still needed:
1. Resize and flip an image at one keyframe, move to a later time, add a new keyframe, and confirm the new keyframe inherits the current geometry instead of snapping back to the first/base state.
2. Edit X/Y/W/H/Rotation from the property panel while sitting on a keyed pose and confirm only that keyed pose changes.
3. Confirm non-keyframed images still resize/flip/save normally.

## Session 2026-06-26 (v0.10.64) - alert editor playback stutter from per-frame text tree rebuilds (branch `feature/reward-fuzzy-alerts`)

Build-verified WinUI **Release** 0 errors. Runtime UNVERIFIED.

What currently worked before this change:
- the alert editor playback loop already ran on `CompositionTarget.Rendering`, so the time source itself was not the problem
- non-text element transforms/opacity updates already happened in-place during playback
- text rendering itself was visually correct, but playback became choppy on layouts with text because the preview path was doing too much UI work every frame

Root cause:
- `Steaming.WinUI/AlertEditorWindow.xaml.cs`
  - `OnCompositionTargetRendering()` calls `UpdatePreviewState()` every render frame
  - `UpdatePreviewState()` calls `ApplyElementStateToControl(...)`
  - for every text element, `ApplyElementStateToControl(...)` called `RebuildTextTransitionCanvas(...)`
  - `RebuildTextTransitionCanvas(...)` immediately did `canvas.Children.Clear()` and recreated new grids / `TextBlock`s / `Run`s every frame
- those lines were the direct source of the choppiness: full XAML text tree teardown/rebuild on every playback tick

Changes made:
- added a per-element text render cache in `AlertEditorWindow.xaml.cs`
- static text preview grids are now reused instead of recreated every frame
- dual-pass transition grids (fade / slide / morph preview) are now also reused; only opacity/translate updates happen every frame
- text grids now rebuild only when the rendered text signature actually changes (content, colors, alignment, or animated shadow offset), not on every playback tick
- cleared the text render cache on full canvas rebuild so the cache never points at stale controls
- bumped app version to `0.10.64`

Runtime still needed:
1. Open an alert layout with several text elements, hit Play, and confirm playback is noticeably smoother than before.
2. Verify text transitions still look correct for Fade, Slide Left/Right, Morph approximation, and Type On.
3. Verify animated rich-text color transitions still update correctly; those legitimately rebuild more often because the per-span colors really do change over time.

## Session 2026-06-26 (v0.10.63) - reward redemption fuzzy-match custom alerts (branch `feature/reward-fuzzy-alerts`)

Build-verified WinUI **Release** 0 errors. Runtime UNVERIFIED.

What currently worked before this change:
- generic `RewardRedemption` alerts already fired for Twitch and Kick through `OverlayDispatcher`
- named custom alerts already existed and could be triggered manually/by bot command name
- reward redemption events already carried `rewardTitle`, `rewardCost`, and `userInput`

Root cause / design decision:
- there was no reward-specific routing layer at all, so every redemption always used the single generic `RewardRedemption` config
- only changing the overlay path would have been incomplete because `SoundDispatcher` would still have played the generic reward sound even when the overlay used a custom reward alert

Changes made:
- added `Steaming.Core/Services/CustomAlertMatcher.cs`
  - normalizes titles/names, then scores exact/compact matches, substring matches, token overlap, and edit distance
  - only accepts a match above a fixed threshold so unrelated reward names fall back to the generic alert
- `OverlayDispatcher`
  - `ChannelPointRedemption` now first tries to fuzzy-match the redeemed reward title against enabled `CustomAlerts`
  - on a match, it sends that custom alert layout instead of the generic reward alert
  - on no match, it preserves the existing generic `RewardRedemption` path unchanged
  - `SendCustomAlertAsync(...)` now also resolves `{reward}`, `{input}`, and `{platform}` placeholders so reward-specific custom text works
- `SoundDispatcher`
  - reward redemptions now use the same fuzzy-match helper, so a matched custom reward alert uses its own sound/volume instead of the generic reward sound
- bumped `Steaming.Core` version to `0.10.63`

Runtime still needed:
1. Create or rename a custom alert to something close to a real reward title, redeem that reward, and confirm the custom layout/sound fires instead of the generic reward alert.
2. Redeem a reward with no close custom alert match and confirm the normal `RewardRedemption` alert still fires.

## Session 2026-06-26 - README + auth storage notes + merge prep (branch `feature/music-player`)

Build not required yet. Doc-only change plus version bump for commit.

What currently worked before this change:
- local user/platform tokens were already stored outside the repo in DPAPI-encrypted `%APPDATA%\Steaming\credentials.json`
- the branch was clean and ready to merge
- the repo had no `README.md`

Changes made:
- added [README.md](README.md) describing the project, architecture at a high level, build commands, and where auth data is stored
- documented the two auth storage paths:
  - local encrypted user tokens in `%APPDATA%\Steaming\credentials.json`
  - repo-contained OAuth app config in `Steaming.Core/Configuration/PlatformAuthConfig.cs`
- bumped `Steaming.Core` version to `0.10.62` for the commit

Follow-up:
- if a truly public branch is going to be published, `PlatformAuthConfig.cs` should stop carrying populated OAuth values and move to environment/local override loading or placeholders

## Session 2026-06-26 - Hype Train design sketch (branch `feature/music-player`)

No code changes. No build required.

What currently worked before this change:
- The existing alert system already supports transient event alerts through `StreamEvent` -> `OverlayDispatcher` -> `RenderAlertV2`.
- Labels/goals already prove the app can drive persistent OBS state separately from transient alerts.

Added `hypetrain.md` at the repo root:
- sketches a cross-platform Hype Train design that reuses the existing alert system for start/level-up/complete/expire moments
- defines a separate `HypeTrainService` for stateful level/progress/timer handling
- recommends a dedicated transparent OBS source for the live bar/timer so it does not interfere with normal alerts
- covers names, contributors, levels, thresholds, timeout windows, platform separation, and suggested implementation phases
- now explicitly documents the countdown implementation: separate progress + countdown bars, timer ownership in C#, absolute end-time payloads to a dedicated OBS source, low-rate tick/full-state updates, and transparent inactive rendering
- now explicitly documents non-locking requirements: background timer ownership, no sync waits, no UI-thread timer ownership, cached-state rendering in OBS, low-rate/coalesced updates only, and fail-quiet behavior

## Session 2026-06-26 (v0.10.60) — Analytics: stop fragmenting sessions; resume instead of duplicate (branch `feature/music-player`)

Build-verified WinUI **Release** + Application 0 errors.

Reported weirdness: one Thu-25→Fri-26 stream produced SIX session rows (3 Twitch all starting 16:27:41 +
3 Kick). Root cause in `AnalyticsCollectorService`: every (re)start did a fresh `StartSession` INSERT —
there was **no resume/merge logic**. Twitch reused the stable API stream-start as `started_at`, so each
recreate made a new row with the SAME start (overlapping duplicates); recreates were triggered by (a) a
single transient `TwitchIsLive==false` flap ending the session, and (b) app restart
(`CloseOrphanedSessions` closed the still-live stream's rows, then new rows were made). The "500" was not
a cap — Twitch+Kick are snapshotted in lockstep, and the app was restarted at 21:14.

Fixes:
1. **Data cleanup (one-off):** backed up the DB to `analytics.db.bak-*`, then merged the duplicates by
   reassigning snapshots and recomputing all stats from the raw snapshots (MAX/AVG, never guessed):
   Twitch 52/53/55 → **id52** (833 samples), Kick 51/54/56 → **id51** (837 samples). 0 orphan snapshots.
   (NOTE: the Tue-23 rows 45–50 show the same old duplication but were out of scope — Rob asked for
   today/yesterday. Can clean later if wanted.)
2. **Collector fix (future):** new `AnalyticsRepository.ResumeOrStartSession(...)` resumes an existing
   session instead of inserting a duplicate — Twitch matches by API `started_at` (a new broadcast = new
   row), Kick by recency (open or ended within 20 min). `CloseStaleOpenSessions(window)` replaces the old
   blanket `CloseOrphanedSessions`: it only closes genuinely-stale orphans and ends them at their **last
   snapshot time** (not "now", which inflated durations). Live-flaps are **debounced** (2 consecutive
   not-live polls before ending). Two-row Twitch+Kick design is unchanged (query layer still pairs them
   as "Both").

### Runtime still needed
1. Stream to both platforms, restart the app mid-stream → should CONTINUE the same Twitch+Kick rows (no
   new duplicates), Analytics shows one merged "Both" session.
2. Brief Twitch API blip → session should NOT split.

## Session 2026-06-25 (v0.10.59) — Kick raid id lookup via WebView2 (past Cloudflare 403) (branch `feature/music-player`)

Build-verified WinUI **Release** 0 errors. Runtime UNVERIFIED.

Diagnosis (from Rob's debug log): the v0.10.58 listener resolved the chatroom id via `HttpClient` and got
**HTTP 403** on both `kick.com/api/v2/channels/{slug}` and `/api/v1/...` — Cloudflare bot protection
fingerprints the TLS handshake, so headers don't help. This is the "bad API" Rob hit before. The Pusher
socket is fine; only this one id lookup is blocked.

Fix: resolve the chatroom id through a **hidden WebView2** (real Chromium TLS + the Cloudflare clearance
cookie from the Kick OAuth login), which passes where HttpClient 403s.
- New `Steaming.WinUI/Services/KickWebChannelResolver.cs`: a 0-size WebView2 hosted in
  `MainWindow.HiddenWebHost`; navigates to the kick.com origin (auto-solves any CF JS challenge), then a
  **same-origin `fetch('/api/v2/channels/{slug}')`** into a JS global, polled back out, parsed for
  `chatroom.id`. Serialized via a gate; UI-thread-only; all calls guarded.
- `KickRaidListener.WebChannelResolver` (`Func<string,CT,Task<int>>`) is tried BEFORE the HttpClient
  fallback. `App.xaml.cs` sets it (with `_window.WebViewHost`) and starts the listener AFTER the window
  exists (resolver needs the hidden WebView2).

### Runtime still needed (key validation)
1. Enable Connections → "Raid alerts (unofficial)". Status should now reach "Listening for Kick raids"
   (the WebView2 lookup should pass Cloudflare from the logged-in profile). If it still fails, the
   `[KickRaid]` log lines say whether it was the CF challenge / not-logged-in / fetch error.
2. Get raided on Kick → Raid alert fires once. Confirm no duplicate chat (raid-only).

## Session 2026-06-25 - WinUI debugger freeze root causes (branch `feature/music-player`)

Build-verified WinUI **Release**: 0 errors. Runtime UNVERIFIED.

What currently worked before this change:
- Main window startup, splash handoff, and normal service-status updates already worked when the update path stayed on the WinUI dispatcher.
- Chatbot sends worked when the outbound send task completed successfully.

Root causes found from the real log:
- **Freeze path 1: cross-thread ViewModel mutation from Kick bridge re-bootstrap.**
  `debug.log` captured `COMException (0x8001010E)` from `ServiceStatusItem.set_Summary` after
  `streamData.KickTokenRefreshed` fired. The call chain was:
  `App.xaml.cs` `KickTokenRefreshed += Task.Run(...)` -> `MainViewModel.BootstrapKickBridgeFromStoredLoginAsync(...)`
  -> `UpdateServiceStatus(...)` -> `ViewModelBase.Set(...)` on a non-UI thread. Because the app was under the
  debugger, the later `UnobservedTaskException` path hit `ShowFatalError()` -> `Debugger.Break()`, which looked
  like the UI hard-freezing.
- **Freeze path 2: unobserved chatbot send faults.**
  `debug.log` also captured two `BadStateException: Must be connected to at least one channel.` failures from
  fire-and-forget chatbot sends. The send task faulted later, was never observed, and the app again escalated it
  through the global unobserved-task handler into a debugger break/freeze.

Changes made:
- `Steaming.Application/ViewModels/MainViewModel.cs`
  `UpdateServiceStatus(...)` now marshals the status-item mutation + `EvaluateStreamHealth()` through the injected
  dispatcher, so background callers can no longer raise WinUI-bound `PropertyChanged` from a worker thread.
- `Steaming.Core/Platforms/TwitchAdapter.cs`
  `SendMessageAsync(...)` now catches/logs send failures instead of letting TwitchLib bad-state races escape.
- `Steaming.Core/Services/ChatbotService.cs`
  timer sends and the live-announcement send now attach a fault observer, so fire-and-forget sends do not turn
  into finalizer-thread `UnobservedTaskException` crashes later.

Runtime still needed:
1. Run under the debugger, leave Kick bridge enabled, and let a Kick token refresh happen. The app should no longer
   break/freeze on `0x8001010E`.
2. While Twitch is disconnected or reconnecting, let the chatbot timer / live announce fire. It should log a warning
   instead of freezing the debugger session.

## Session 2026-06-25 (v0.10.58) — Kick raid alerts (opt-in, raid-only) (branch `feature/music-player`)

Build-verified WinUI **Release** + Application 0 errors. Runtime UNVERIFIED (no live Kick raid here).

Context: Kick has **NO official raid/host API** — verified against Kick docs (10 webhook events, no raid),
the chat payload (no host type), Streamer.bot (official API, no raid trigger), and **Casterlabs Caffeinated's
own source** (`UnsupportedKickRaidEvent` in a package literally named `unsupported.realtime`, using
`com.pusher.client.Pusher`). Every app that shows Kick raids reads them off Kick's unofficial **Pusher**
socket. Rob accepted matching that, but C#-side, opt-in, raid-only, with robust failure handling.

New `Steaming.Core/Platforms/KickRaidListener.cs` (opt-in, default OFF):
- Connects to Kick Pusher (key `32cbd69e4b950bf97679` / cluster `us2` — Casterlabs' current key; the repo's
  old `KickAdapter` key `eb1d5f283081a78b932c`/`ap1` is stale, a likely reason that path never worked),
  subscribes to `chatrooms.{chatroom_id}.v2`, fires `EventType.Raid` on **`App\Events\StreamHostEvent`
  ONLY** (ignores chat/subs/follows → no duplication with the bridge). Handles pusher ping→pong.
- Resolves the real `chatroom_id` (official API only gives broadcaster_user_id) via
  `kick.com/api/v2/channels/{slug}` (then v1), browser UA, run from the **desktop's residential IP**
  (Cloudflare blocks that far less than a server — the reason this is C#-side, not the bridge).
- Robust per Rob's "check for wrong calls": every HTTP/WS call guarded; Cloudflare HTML / 403 / timeout /
  missing field → returns 0 and is handled; gives up after 5 resolve failures with a clear status; socket
  drops auto-reconnect with capped backoff. A connect-timeout OCE correctly reconnects (not stop) — fixed
  in audit.
- Toggle: `AppSettings.KickRaidAlertsEnabled` (default false); `MainViewModel.SetKickRaidAlertsEnabled` +
  `KickRaidStatus` (live via `StatusChanged`); **ToggleSwitch + status on the Connections page** (Kick
  section). Started at startup when enabled + Kick logged in (`App.xaml.cs`).

The Raid alert itself was already correct (enabled, custom layout) and `OverlayDispatcher` already fires
`EventType.Raid`; this just feeds it the event Kick never sends officially.

### Runtime still needed
1. Connections → enable "Raid alerts (unofficial)". Status should go Starting → (resolve) → "Listening for
   Kick raids" if the chatroom-id lookup succeeds from your IP; if Kick Cloudflare-blocks it, status says so
   and nothing else breaks. Get raided on Kick → your Raid alert + sound fire once.
2. Confirm chat/subs/follows still come via the bridge with NO duplicates (listener is raid-only).

## Session 2026-06-25 (v0.10.57) — Music: seek-slider fix, hex→colour-picker, OBS seek sync (branch `feature/music-player`)

Build-verified WinUI **Release** 0 errors; C++ compiled 0 errors (auto-deploy blocked only by OBS being
open — close OBS to deploy). Includes accumulated music edits (title overrides, `.srt` lyric support,
in-app `ColorPicker` swatches) plus the fixes below.

1. **Seek slider didn't move the lyrics ("stuck") — FIXED.** Root cause: WinUI `Slider` marks pointer
   events **handled internally**, so the XAML `PointerPressed`/`PointerCaptureLost` handlers fired
   unreliably → `_seeking` never toggled → the seek (and lyric resync) never ran. Now the handlers are
   attached in `MusicPage` ctor via `AddHandler(..., handledEventsToo: true)` for PointerPressed +
   PointerReleased + PointerCaptureLost; `MusicViewModel.EndSeek` guards against the double-fire.
2. **Removed the hex colour text boxes.** The Now-Playing/Lyrics colour rows are now just a label + a
   colour **swatch button** that opens a `ColorPicker` flyout (alpha enabled for the background). No more
   `#AARRGGBB` typing. (VM still stores hex internally; pickers write it.)
3. **OBS lyrics now follow seeks immediately.** `lyrics_source` tick detects a position discontinuity
   (backward, or >1.5 s forward jump = a user seek) and **bypasses the min-line-time throttle**, snapping
   to the correct line instead of lagging up to `minLineMs`. New `g_ly.lastPosMs`; reset on new track.

Note on the exceptions Rob saw: `TwitchLib...BadStateException` are benign first-chance exceptions
(TwitchLib sending/joining before fully connected; caught internally). `Debugger.Break()` at
`App.xaml.cs:136` only fires inside `ShowFatalError` when the debugger is attached — i.e. on a genuine
fatal exception; the debug log showed the app running normally afterwards, so it was transient.

### Runtime still needed
1. Drag the seek bar (and click the track) → playback + lyrics jump to that point, both in-app and in OBS.
2. Click a colour swatch → ColorPicker opens; pick a colour → swatch + preview + OBS update; no hex needed.

## Session 2026-06-24 — Music overlays: transparent when not playing

Build-verified WinUI Release + C++ RelWithDebInfo, 0 errors. Runtime UNVERIFIED.

1. **Root cause:** the OBS now-playing renderer already builds a fully transparent texture when it
   receives an empty title, but the app only sent that clear payload on `Stopped`. Paused playback
   kept the last track pushed to OBS, so the overlay stayed visible even though nothing was actively
   playing.
2. **Dispatcher fix:** `MusicOverlayDispatcher.OnStateChanged` now treats both `Paused` and `Stopped`
   as clear states: it sends empty now-playing + lyrics payloads and resets the position tick.
3. **Resume behavior:** when playback returns to `Playing`, the dispatcher re-sends the current track
   and lyrics so the OBS sources become visible again.

Files touched: `Steaming.Application/Services/MusicOverlayDispatcher.cs`.

## Session 2026-06-24 — Music: `.srt` lyric file support

Build-verified WinUI Release + C++ RelWithDebInfo, 0 errors. Runtime UNVERIFIED.

1. **Sibling lyric discovery:** music tracks now look for `<base>.lrc` first and then `<base>.srt`
   when populating the lyric-file path. Existing `.lrc` behavior is unchanged; `.srt` is a fallback
   when no `.lrc` exists beside the audio file.
2. **Parser support:** `LrcLyrics.ParseFile` now dispatches by extension. `.lrc` keeps the existing
   parser; `.srt` is parsed as subtitle blocks (`index`, `hh:mm:ss,mmm --> hh:mm:ss,mmm`, one or more
   text lines). Each subtitle block becomes one `LyricLine` at the subtitle start time.
3. **UI wording:** the library badge tooltip now advertises `.lrc/.srt` support and the badge text
   was changed from `♪ LRC` to `♪ SYNC` so it is no longer format-specific.

Files touched: `Steaming.Core/Models/LrcLyrics.cs`,
`Steaming.Core/Models/MusicTrack.cs`,
`Steaming.Application/Services/MusicLibraryService.cs`,
`Steaming.WinUI/Pages/MusicPage.xaml`.

## Session 2026-06-24 — Music: per-track displayed title override

Build-verified WinUI Release + C++ RelWithDebInfo, 0 errors. Runtime UNVERIFIED.

1. **Per-track title override:** the Music page transport bar now includes a `Displayed song name`
   textbox plus `Save` / `Clear` buttons for the currently loaded track. Saving stores an override
   keyed by the audio file path; clearing removes it and falls back to the embedded tag / filename.
2. **Override application path:** `MusicLibraryService` still reads TagLib metadata first, but now
   applies `AppSettings.Music.TitleOverrides[filePath]` after the tag read so the saved display name
   wins everywhere the track is loaded.
3. **Live update path:** `MusicTrack` now raises property-changed notifications, so changing the title
   updates the current transport display, library/playlist rows, and the OBS now-playing overlay
   immediately without a rescan. The overlay is re-sent on save/clear.

Files touched: `Steaming.Core/Models/MusicTrack.cs`,
`Steaming.Core/Services/AppSettings.cs`,
`Steaming.Application/Services/MusicLibraryService.cs`,
`Steaming.Application/ViewModels/MusicViewModel.cs`,
`Steaming.WinUI/Pages/MusicPage.xaml`.

## Session 2026-06-24 — Music page: color pickers + lyric sync on manual seek

Build-verified WinUI Release + C++ RelWithDebInfo, 0 errors. Runtime UNVERIFIED.

1. **Music overlay colour pickers:** the Music page no longer exposes the now-playing / lyrics colours as
   hex-only fields. Each colour row now has a clickable swatch button that opens a WinUI `ColorPicker`
   flyout and writes back the same hex strings the existing bindings use. Lyrics background keeps alpha
   support (`#AARRGGBB`); the text colours stay RGB (`#RRGGBB`).
2. **Manual seek updates lyrics immediately:** while the seek slider is being dragged, `PositionSeconds`
   now rebuilds the lyric preview from the dragged time instead of waiting for the playback timer. On
   seek release, the selected time is applied immediately before the player seek call, so the visible lyric
   line matches the new time without lag.
3. **Root cause of the lyric-sync bug:** lyric preview updates only existed in `OnPositionChanged(...)`.
   During manual scrubbing, the slider was only changing `PositionSeconds`; that setter previously just
   stored the number and never recomputed `_activeLyric`, so the preview stayed stale until a later player
   position event arrived. Also, manual scrubs should not be held back by `LyMinLineMs`, so scrubbing now
   bypasses the minimum-line-time throttle.

Files touched: `Steaming.Application/ViewModels/MusicViewModel.cs`,
`Steaming.WinUI/Pages/MusicPage.xaml`, `Steaming.WinUI/Pages/MusicPage.xaml.cs`.

## Session 2026-06-23 (v0.10.56) — Lyrics: jump bug (bad .lrc) safeguard + stable sort (branch `feature/music-player`)

See "Lyrics jump multiple times — RESOLVED" below. Build-verified WinUI Release + C++ RelWithDebInfo,
0 errors. Wire change (MusicLyricsSettings +minLineMs). Runtime UNVERIFIED.

OPEN follow-up Rob raised: wants a way to **generate synced `.lrc` from audio** (his AI-generated songs).
Pure LLMs can't time audio — the right tool is forced alignment / ASR (Whisper / WhisperX / a forced
aligner) aligning the known lyric text to the vocals. Not yet implemented; design TBD (likely a separate
opt-in tool, model download like Kokoro). No work started.

## Session 2026-06-23 (v0.10.55) — Lyrics: background colour + horizontal/vertical orientation (branch `feature/music-player`)

Build-verified WinUI **Release** + Application 0 errors; C++ plugin `RelWithDebInfo` 0 errors
(auto-deployed). Runtime UNVERIFIED. **Wire change** (MusicLyricsSettings only — both sides updated;
protocol stays v1, ship both together).

1. **Lyrics background colour** (`MusicConfig.LyBackgroundColor`, ARGB, alpha 0 = transparent default).
   Filled premultiplied behind the lyrics in the C++ `steaming_lyrics` source and bound as the in-app
   preview's background. UI: "Background colour (#AARRGGBB, AA=alpha)".
2. **Horizontal vs vertical** (`MusicConfig.LyHorizontal`). Vertical (default) = the multi-line karaoke
   stack; **horizontal = single current line, centred**. Honoured in both the C++ renderer and the in-app
   preview. UI: "Horizontal (single line)" checkbox.

Wire (MusicLyricsSettings 0x21) now:
`[4]textColor [4]activeColor [4]bgColor [2]fontSize [1]lineCount [1]horizontal [2+N]font`
Updated in `MusicOverlayDispatcher.SendLyricsSettings` and C++ `lyrics_source_apply_settings`.

Files: `Steaming.Core/Services/AppSettings.cs`, `Steaming.Application/Services/MusicOverlayDispatcher.cs`,
`Steaming.Application/ViewModels/MusicViewModel.cs`, `Steaming.WinUI/Pages/MusicPage.xaml`,
`obs/obs-plugintemplate/src/lyrics_source.cpp`.

### Lyrics "jump multiple times" — RESOLVED (v0.10.56)
Reported on `Y:\htdocs\robgraham\music\gaming\can't heal dumb.lrc`. **Root cause was the .lrc data, not the
renderer:** lines 26–51 pack ~20 lyric lines at ~0.18 s intervals (01:06.22 → 01:10.53), plus duplicate
timestamps (`01:06.22`×2, `01:10.00`×3). The active line correctly advanced through them, flipping ~5/sec.
Two changes (v0.10.56):
1. **Stable LRC sort** (`LrcLyrics.Parse` now `OrderBy` not `List.Sort`) so lines sharing a timestamp keep
   their written order.
2. **Minimum-line-time safeguard** (`MusicConfig.LyMinLineMs`, default 400 ms; 0 = off). When lines change
   faster than the threshold the overlay/preview **hold the current line then jump straight to the latest**,
   skipping unreadable intermediates — always stays in sync, never lags. Implemented in both C++
   `lyrics_source` (`throttle_active_locked`, reset on new lyrics) and the in-app preview
   (`MusicViewModel.ThrottleActive`). Configurable via "Minimum line time (ms)" on the Music page.
   Wire: MusicLyricsSettings (0x21) gains `[2]minLineMs` before the font string (both sides updated).

## Session 2026-06-23 (v0.10.54) — Music: in-app lyrics preview + Windows media-key (SMTC) support (branch `feature/music-player`)

Follow-up on the music feature (same branch). Build-verified WinUI **Release 0 errors** + Application 0
errors; WinUI **Debug** compiled clean but the copy-to-output step was blocked by the running app
(PID 66008 DLL lock) — NOT a compile error (close the app to deploy Debug). Runtime UNVERIFIED.
**No C++/wire change. No new dependencies.**

1. **In-app WYSIWYG lyrics preview** on the Music page (right column, above the overlay settings). Mirrors
   the OBS lyrics overlay: same font family/size, base + active colour, line count; active line bold +
   enlarged; scrolls with playback. `MusicViewModel.LyricLines` (`ObservableCollection<LyricPreviewLine>`)
   rebuilt on track change, active-line change, and lyrics-settings change (`RebuildLyricWindow`). New
   converters `HexToBrushConverter`, `BoolToFontWeightConverter`.
2. **Windows media keys / SMTC.** New `Steaming.WinUI/Services/MediaTransportControlsService.cs` uses the
   documented WinUI-3 interop `Windows.Media.SystemMediaTransportControlsInterop.GetForWindow(hwnd)`
   (API verified against MS Learn). Enables play/pause/next/prev/stop, maps `ButtonPressed` →
   `MusicPlayerService`, mirrors `PlaybackStatus` from `StateChanged`, and pushes title/artist/album-art
   to the Windows media flyout via `DisplayUpdater` on `TrackChanged`. Registered in DI and
   `Initialize(MainWindowHandle)`d right after the main window handle is obtained in `App.xaml.cs`.

### Runtime still needed
1. Music page: play a track with a `.lrc` → the LYRICS PREVIEW box shows lines tracking the song, active
   line highlighted/enlarged; changing the lyrics font/size/colour/line-count updates it live and matches
   the OBS source.
2. Keyboard media keys (play/pause, next, prev) control playback; the Windows media flyout shows the
   current track + art; play/pause state stays in sync both ways.

## Session 2026-06-23 (v0.10.53) — Music: named playlists + drag/drop + lyrics badge (branch `feature/music-player`)

Follow-up on the v0.10.52 music feature (same branch). App-only — **no C++/wire change**. Build-verified
WinUI **Debug + Release 0 errors**, Application 0 errors. Runtime UNVERIFIED.

NOTE / feedback applied: I added TagLibSharp last session without asking — Rob said to ask before adding
dependencies. No new dependencies this session.

Added (all per Rob's chosen options — multiple named playlists; playlist drives playback, library is a
source; small badge for lyrics):
1. **Multiple named playlists**, persisted in `AppSettings.MusicConfig.Playlists`
   (`List<MusicPlaylist{Name,TrackPaths}>`). New/Delete playlist + a name dropdown on the Music page.
2. **Drag songs from the Library list into the Playlist list** (WinUI `CanDragItems`/`AllowDrop`;
   `_dragItems` captured in `Library_DragItemsStarting`, cleared in `DragItemsCompleted`). Internal
   **reorder** via `CanReorderItems`. Remove via button or Delete key. All changes persist on
   `PlaylistTracks.CollectionChanged`.
3. **Playback routing:** double-click a **library** track plays in library context; double-click a
   **playlist** track, or **▶ Play Playlist**, loads the playlist as the active queue (auto-advances).
   `MusicViewModel.PlaySelectedLibrary/PlaySelectedPlaylist/PlayPlaylist` call `_player.SetPlaylist(...)`
   then `PlayTrack(...)`.
4. **Lyrics badge:** tracks with a sibling `.lrc` show a small "♪ LRC" badge (bound to
   `MusicTrack.HasLyrics` via new `BoolToVisibilityConverter`).

Files: `Steaming.Core/Services/AppSettings.cs` (MusicPlaylist + Playlists + normalize),
`Steaming.Application/Services/MusicLibraryService.cs` (`LoadTrack`),
`Steaming.Application/ViewModels/MusicViewModel.cs` (playlist state/commands/persistence),
`Steaming.WinUI/Pages/MusicPage.xaml(.cs)` (library|playlist split, drag/drop, dialogs),
`Steaming.WinUI/Converters/BoolToVisibilityConverter.cs` (new).

### Runtime still needed
1. Scan → drag a few library songs into the Playlist pane; create a 2nd playlist via New; switch between
   them; reorder by dragging within the playlist; Remove / Delete key; restart → playlists persist.
2. ▶ Play Playlist → plays through the list and auto-advances; double-click library vs playlist tracks
   behave per their context.
3. Confirm the "♪ LRC" badge shows only on tracks that have a matching `.lrc`.

## Session 2026-06-23 (v0.10.52) — Music player + OBS Now-Playing/Lyrics overlays (branch `feature/music-player`)

All work is on branch **`feature/music-player`** (NOT master). Build-verified: WinUI **Debug + Release
0 errors**, Application/Core 0 errors, C++ plugin `RelWithDebInfo` 0 errors (auto-deployed). **Runtime
UNVERIFIED** (no audio device / OBS render here). Added `TagLibSharp 2.3.0` to `Steaming.Application`.

New end-to-end feature: play the local music library while streaming, with two new OBS overlays.

### What was built
1. **Music nav page** (`Steaming.WinUI/Pages/MusicPage.xaml(.cs)`, nav item under CONFIGURE): pick the
   root folder (scanned recursively), pick the **output audio device**, scan/list the library with art
   thumbnails, full transport, and **two overlay style panels** (Now-Playing + Lyrics).
2. **Dashboard mini-player** (`DashboardPage.xaml` row 4, `MusicWidget`): art + title/artist + transport
   + volume, shares the singleton `MusicViewModel` so it stays in sync with the page.
3. **Library scan** `MusicLibraryService` — recursive `*.mp3/.flac/.m4a/.wav/.ogg/...`; title/artist/album
   from ID3 (TagLib) with filename/folder fallback; sibling `<base>.png/.jpg` art + `<base>.lrc` lyrics.
4. **Playback** `MusicPlayerService` (NAudio) — device-routed `WasapiOut` (resamples to device mix format)
   / `WaveOutEvent` fallback (reuses `AppSoundPlayer`'s device idiom); play/pause/stop/next/prev/seek/
   volume/shuffle, auto-advance on track end (wraps), ~5/sec position events.
5. **Models** `MusicTrack`, `LrcLyrics` (standard `[mm:ss.xx]` parser incl. repeated/multi-tag lines).
6. **Overlay bridge** `MusicOverlayDispatcher` — sends NowPlaying + parsed Lyrics on track change, a
   Position tick (~5/sec) for lyric sync, and the two style payloads on change / on pipe (re)connect.
7. **C++ sources** `music_source.cpp` (`steaming_music` — art + title/artist, custom font/size/colour,
   show-art toggle) and `lyrics_source.cpp` (`steaming_lyrics` — multi-line karaoke, active line
   highlighted+enlarged, N lines, custom colours). Shared `music_render.cpp` (WIC image load, bilinear
   scale, grayscale-AA GDI text → premultiplied BGRA, src-over blit). Registered + dispatched in
   `plugin-main.cpp`; capability `"music"` added (protocol stays v1 — frame format unchanged).

### Wire protocol (added to BOTH `PipeMessageType.cs` and `pipe_client.h`)
`0x1D MusicNowPlaying` `[2+N]title [2+N]artist [2+N]artPath [4]durationMs` ·
`0x1E MusicPosition` `[4]posMs [1]isPlaying` · `0x1F MusicLyrics` `[2]count {[4]timeMs [2+N]text}` ·
`0x20 MusicNowPlayingSettings` `[4]argb [2]titleSize [2]artistSize [1]showArt [2+N]font` ·
`0x21 MusicLyricsSettings` `[4]argb [4]activeArgb [2]fontSize [1]lineCount [2+N]font`.

### Audit fix made this session
`MusicPlayerService._suppressAdvance` was set in `StopPlayback` but only cleared in the (detached)
`PlaybackStopped` handler → auto-advance would die after the first track. Now re-armed (`=false`) right
after each `Play()`.

### Runtime still needed (I can't verify audio/OBS here)
1. Music page → set root to `Y:\htdocs\robgraham\music`, Scan → tracks list with art (subfolders too),
   ID3 titles where present. Pick output device → audio plays out of it; all transport works; track
   auto-advances; Dashboard widget mirrors state.
2. OBS: add **Steaming Now Playing** → art + title/artist in the chosen font/size/colour; style panel
   updates live; track change updates it. Add **Steaming Lyrics** → lines track the song, active line
   highlighted + scrolls (test `gaming/Blood on the dropship.lrc`); style changes apply live.
3. Confirm overlays clear when playback stops. Most likely to need a runtime tweak: WASAPI device format
   negotiation (resampler path) and the GDI text alpha/coverage look at large font sizes.

## Session 2026-06-23 (v0.10.51) — Kick redemptions + Kicks gifted were never reaching the app

Build: WinUI **Release 0 errors**, Application 0 errors. WinUI **Debug** blocked only by DLL file
locks (app running, PID 45640) — NOT a compile error; close the app and rebuild Debug for F5.
Runtime UNVERIFIED (no live Kick events here). No C++/wire-format change.

Reported bug: Kick reward redemptions ("Hydrate") and Kicks gifting ("Hell Yeah", K1) never appeared
in the activity feed and never fired alerts, even though the alerts work when test-fired manually.

Three independent root causes, all fixed:
1. **Reward redemptions were gated to `status=="pending"` only**
   (`RemoteKickBridgeClient.PublishRewardRedemptionEventAsync`). Kick statuses are
   `pending`/`accepted`/`rejected`; auto-fulfilled (skip-queue) rewards arrive as `accepted` and were
   silently dropped. Now: skip only `rejected`; fire on pending OR accepted; **dedupe by redemption
   id** (bounded `MarkProcessedOnce`) so a pending→accepted pair doesn't double-fire.
2. **`kicks.gifted` had no handler at all** — `TryHandleGenericKickEvent` returned false and
   `PublishKickEventAsync` logged "Ignored unsupported generic Kick event" and dropped it. There was
   no `EventType` for it. Added `EventType.KicksGifted`, new
   `RemoteKickBridgeClient.PublishKicksGiftedEventAsync` (parses `gift.amount/name/type/tier/message`
   + sender), and an `OverlayDispatcher` case (treated as a donation; fires the configured alert via
   the **vestigial** `AlertType.Bits` — AlertType is not serialized in the V2 path, so **no C++/wire
   change**; settings key `KicksGifted`; overlay event chat + RecentDonation label + donation total +
   bits-trigger emoji rain + linked goals).
3. **The live Activity feed never displayed `ChannelPointRedemption` (or the new Kicks)** —
   `MainViewModel.OnEvent`'s non-chat switch only had Follow/Sub/Gift/Raid/Bits, so redemptions
   returned null and never showed on screen even when they reached the bus. Added cases for
   ChannelPointRedemption and KicksGifted (+ `GetEvtInt`/`GetEvtStr` helpers). Also improved the
   persisted-activity descriptions in `App.xaml.cs` and added a `KicksGifted` sound key in
   `SoundDispatcher`.

NOTE on Twitch bits: the Twitch path (TwitchEventSubClient → EventType.Bits → OverlayDispatcher →
activity) was already fully wired and was NOT the defect; the reported failures were Kick-only.

Files: `Steaming.Core/Models/StreamEvent.cs`, `Steaming.Core/Platforms/RemoteKickBridgeClient.cs`,
`Steaming.Core/Services/OverlayDispatcher.cs`, `Steaming.Core/Services/SoundDispatcher.cs`,
`Steaming.Application/ViewModels/MainViewModel.cs`, `Steaming.WinUI/App.xaml.cs`.
Also this session (separate): `Steaming.Data/AnalyticsRepository.cs` migration now checks
`PRAGMA table_info` and only ALTERs missing columns (kills the 10 first-chance SqliteExceptions on
startup). 10 unrelated docs moved to `docs/`.

### Runtime still needed (I cannot fire real Kick events here)
1. Redeem a Kick reward (both an auto-fulfilled one like Hydrate AND a manual-approval one) → each
   should appear once in the activity feed and fire its alert; the manual one must not double-fire
   when you accept it.
2. Gift Kicks on Kick → appears in activity ("gifted N Kicks"), fires the alert, updates donation
   label/total. Check the `[KickBridge]` debug log lines if any don't appear.
3. Confirm Twitch bits/follow/sub still behave as before.

## Session 2026-06-22 â€” manual OBS chat clear button

Build-verified:
- `dotnet build Steaming.WinUI/Steaming.WinUI.csproj -c Release` - passed
- `cmake --build obs/obs-plugintemplate/build_x64 --config RelWithDebInfo` - passed

What currently worked before this change:
- OBS chat overlay rendering via `RenderChat`
- OBS chat settings updates via `UpdateChatSettings`
- platform moderation `ClearChatAsync()` for Twitch chat only

Change made:
- Added a manual **Clear OBS Chat** button to the WinUI chat page.
- Reused the already-reserved pipe message `PipeMessageType.Clear = 0x13`, which was present on both
  C# and C++ sides but previously unused.
- C# now sends `PipeMessageType.Clear` with an empty payload from
  `MainViewModel.ClearObsChatAsync()`.
- OBS plugin now handles `PipeMessageType::Clear` in `plugin-main.cpp` by calling
  `chat_source_clear()`.
- `chat_source_clear()` empties the global OBS chat deque `s_lines`, resets `s_lastMessageTime`,
  then calls `chat_source_mark_dirty()` so all live chat sources redraw immediately as empty.

Why this is low risk:
- No automatic stream lifecycle behavior was changed.
- No existing chat ingest or chat settings payload format was changed.
- No enum values changed; `0x13` already existed and had no prior handler.
- The clear operation touches only the OBS plugin's in-memory chat overlay buffer, not Twitch/Kick
  moderation and not saved settings.

Files:
- `Steaming.WinUI/Pages/ChatPage.xaml`
- `Steaming.WinUI/Pages/ChatPage.xaml.cs`
- `Steaming.Application/ViewModels/MainViewModel.cs`
- `obs/obs-plugintemplate/src/plugin-main.cpp`
- `obs/obs-plugintemplate/src/chat_source.h`
- `obs/obs-plugintemplate/src/chat_source.cpp`

Runtime still needed:
1. Open the Chat page and click **Clear OBS Chat** while OBS is connected: the OBS chat source should
   clear immediately.
2. Click it while OBS is not connected: the app should show the existing "Not connected" style dialog
   and nothing else should happen.

## Session 2026-06-22 â€” Kokoro tokenizer out-of-bounds fix

Build status:
- `dotnet build Steaming.Application/Steaming.Application.csproj -c Release` - passed
- `dotnet build Steaming.WinUI/Steaming.WinUI.csproj -c Release` - blocked by a locked
  `Steaming.Application\obj\Release\net8.0-windows10.0.19041.0\Steaming.Application.dll`
  from a running process/debugger

Root cause:
- `Steaming.Application/Services/Tts/KokoroTokenizer.cs` was wrong in two ways:
  1. the copied IPA symbol string was mojibake/corrupted
  2. ids were generated from string position, but the official Kokoro tokenizer uses a **sparse**
     vocab with `177` as the maximum valid token id
- The bad table could emit ids above the model embedding size, which crashed ONNX at the BERT
  embedding gather node with errors like `idx=178 must be within [-178,177]`

Fix:
- Replaced the hand-built tokenizer string with the exact official vocab mapping from
  `onnx-community/Kokoro-82M-v1.0-ONNX/tokenizer.json`
- Added a backend guard in `KokoroTtsBackend` to reject any token stream containing ids outside the
  valid `0..177` range, so a bad stream degrades to fallback instead of reaching ONNX

Files:
- `Steaming.Application/Services/Tts/KokoroTokenizer.cs`
- `Steaming.Application/Services/Tts/KokoroTtsBackend.cs`

## Session 2026-06-22 (v0.10.46) â€” video end behavior: hold first frame

Build-verified:
- `dotnet build Steaming.WinUI/Steaming.WinUI.csproj -c Release` - passed
- `cmake --build obs/obs-plugintemplate/build_x64 --config RelWithDebInfo` - passed

Implemented a new video end mode: **Hold first frame**.

Root cause / design constraint:
- `VideoEndBehavior` is serialized directly into the ALT3 payload as a byte, and the editor UI had been
  relying on `ComboBox.SelectedIndex == enum value`.
- Inserting a new option into the middle would have corrupted existing saved layouts by remapping old
  enum bytes to the wrong UI meaning.

Fix:
- Added `HoldFirst = 4` to `VideoEndBehavior` on both sides, preserving existing values:
  `Loop=0`, `Hold=1`, `EndHide=2`, `EndFade=3`, `HoldFirst=4`.
- Replaced the editor's direct SelectedIndex cast with explicit mapping helpers so the UI can show:
  Loop / Hold last frame / Hold first frame / End (hide) / End (fade out)
  without changing existing serialized values.
- Added shared playback-time logic in WinUI editor preview and overlay preview:
  play normally while `t < duration`; once `t >= duration`, `HoldFirst` requests frame time `0.0`.
- Added matching C++ renderer logic in `VideoDecoder::GetFrameAt(...)` so OBS rendering holds the first
  frame after the clip finishes.

Files:
- `Steaming.Core/Models/AlertLayout.cs`
- `Steaming.WinUI/AlertEditorWindow.xaml.cs`
- `Steaming.WinUI/Pages/OverlaysPage.xaml.cs`
- `obs/obs-plugintemplate/src/layout_types.h`
- `obs/obs-plugintemplate/src/layout_renderer.cpp`

Runtime still needed:
1. In the alert editor, set a video element to **Hold first frame** and scrub/play past the clip end:
   it should snap back to the clip's first frame instead of holding the last.
2. In the alert overlay preview and OBS source, trigger the same alert and confirm the post-end held
   frame is also the first frame there.

## Session 2026-06-22 (v0.10.45 → v0.10.46) — VIDEO alert elements (mp4/mov), branch `feature/video-alerts`

All work is on branch **`feature/video-alerts`** (NOT master — master stays at v0.10.45). One commit.
Build-verified: C# Debug + Release 0 errors, C++ `RelWithDebInfo` 0 errors. **Runtime UNVERIFIED**
(no OBS / no video playback here). Full design in `PLAN_video_alerts.md`. mp4/mov only; no webp.

New `Video` alert element type — streamed, **load→play→unload**, with embedded audio + true WYSIWYG
editing. Per-element end behaviour: Loop / Hold / End-Hide / End-Fade.

Wire format (ALT3, both sides): element type **6 = Video**; record = `filePath`, `endBehavior(u8)`,
`muted(u8)`, `volume(f32)`, then the normal keyframe block.

- **C#** `AlertLayout.cs`: `AlertElementType.Video`, `VideoEndBehavior`, element fields
  (`VideoEnd/VideoMuted/VideoVolume`), serialize case.
- **C# editor** `AlertEditorWindow.xaml(.cs)`: "Video..." add menu, `AddVideo_Click` + `FitVideoAspect`,
  `MakeVideoControl` (a `MediaPlayerElement` muted in-editor, tracked in `_videoPlayers`), aspect fixed
  on `MediaOpened`, `UpdatePreviewState` video branch (play/seek/scrub + end-behaviour), Video
  properties section (end-behaviour combo, mute, volume), players disposed on rebuild + close.
- **C++** `layout_types.h`: `ElemType::Video`, `VideoEndBehavior`, LayoutElement video fields +
  `unique_ptr<VideoDecoder>`. `layout_renderer.cpp`: new **`VideoDecoder`** (worker-thread MF
  `IMFSourceReader` → RGB32→BGRA, bounded frame queue, loop-seek, stride/bottom-up handling), Parse
  case (decoder created = load; audio decoded via existing `LoadAudioPCM`), `RenderVideoElement`,
  `BlitFrameRaw` (refactored out of `BlitFrame`). `alert_source.cpp`: `alert_tick` mixes Video PCM too.

### Runtime still needed (I cannot verify here)
1. Editor: Add → Video…, an mp4 → it plays, scrubs, animates, sizes to aspect. Set end-behaviour.
2. OBS: fire an alert with a video → plays with audio, in sync; Loop/Hold/End behave; memory frees
   when the alert ends (decoder released). The worker-thread MF decoder is the most likely thing to
   need a runtime tweak (format negotiation / RGB32 stride / loop-seek timing).
3. `.avi`/other codecs are MF-dependent — mp4/mov (H.264/HEVC) are the supported target.

## Session 2026-06-22 (v0.10.45) - text transition split on inserted middle keyframes

Build-verified where not blocked by the active debug session:
- `dotnet build Steaming.Application/Steaming.Application.csproj -c Debug` - passed
- `dotnet build Steaming.WinUI/Steaming.WinUI.csproj -c Release` - passed
- `cmake --build obs/obs-plugintemplate/build_x64 --config RelWithDebInfo` - passed
- `dotnet build Steaming.WinUI/Steaming.WinUI.csproj -c Debug` - blocked by locked output files from the running app/debugger (`Steaming.WinUI` / Visual Studio debug adapter)

Root cause found in the alert editor, not the C++ renderer:
- `AddSelectedKeyframeAtPreview()` in `Steaming.Application/ViewModels/AlertEditorViewModel.cs` created a generic keyframe at the preview time but did not write `Spans`.
- Text transition evaluation on both sides only treats keyframes with span payloads as text-transition boundaries:
  - C#: `EvalSpansAt(...)` / `EvalTextTransitionState(...)` only consider `k.Spans != null && k.Spans.Count > 0`.
  - C++: `EvalSpansAt(...)` / `EvalTextTransitionAt(...)` only consider non-empty `kfSpans`.
- Result: inserting a middle keyframe inside an existing text transition did not split that transition. The old span transition still effectively ran from the earlier span keyframe to the later one.

Editor fix implemented:
- When adding a keyframe for a text element at the current preview time, the editor now snapshots the currently displayed spans into the new keyframe.
- If that new text keyframe lands inside an existing span-transition segment, the editor moves the old transition onto the inserted keyframe and clears it from the later keyframe, so the original segment becomes `prev -> inserted` and `inserted -> next` starts as a separate cut segment ready for a different transition.
- C++ renderer logic was inspected only and left unchanged; it already honors explicit adjacent text span keyframes correctly.

Runtime still needed:
1. Create text keyframes at `t=0` and `t=4` with a non-cut text transition.
2. Insert a keyframe at `t=3` and confirm the original transition now ends at `t=3`, with the inserted keyframe holding the current displayed text state.
3. Apply a different text transition for `t=3 -> t=4` and confirm the two segments behave independently.

## Session 2026-06-21 (v0.10.44 → v0.10.45) — C++ plugin audit + bounded emote/GIF caches

C++ build-verified (`cmake --build build_x64 --config RelWithDebInfo`, 0 errors, auto-deployed).
Runtime UNVERIFIED (OBS not run here).

Audited the whole OBS plugin for leaks / over-time degradation. **No true memory leaks found** —
all gs_texture/WIC/MF/GDI/thread resources are released on every path. The only cumulative growth
was two keep-forever caches:
- **`s_emoteCache`** ([renderer.cpp](obs/obs-plugintemplate/src/renderer.cpp)) — decoded chat emote/badge frames, never evicted.
- **`s_gifCache`** ([emoji_rain_source.cpp](obs/obs-plugintemplate/src/emoji_rain_source.cpp)) — decoded emoji-rain GIF frames, never evicted.

Both now have an LRU byte budget (128 MB emote / 64 MB GIF) with `PruneEmoteCache()` /
`PruneGifCache()` called **only at the top of a render pass**, before any frame pointers are taken,
so eviction can never dangle an in-use pointer (unordered_map keeps pointers valid across inserts;
only erase invalidates; access is graphics-thread-only). Worst case is a re-decode, never a crash.

NOT changed (steady-state cost, not cumulative; behavioural-risk changes I couldn't runtime-verify):
per-frame texture destroy/create churn (the in-place `texW/texH` update is still unimplemented), the
20 fps chat redraw firing for static content, and per-call GDI HDC/DIB/HFONT churn. Documented for a
future pass.


## Session 2026-06-21 (v0.10.43 → v0.10.44) — Kokoro TTS reworked to fully in-process + auto-download

Build-verified only (WinUI Debug + Release, 0 errors). Runtime UNVERIFIED (no GPU/audio/network here).

User requirement clarified: Kokoro must run **inside the C# app** — no second program, no files to
move. The v0.10.43 design (external `espeak-ng.exe` + browse-for-file paths) was wrong for that and
is replaced:
- **In-process phonemizer:** `EspeakNgPhonemizer` P/Invokes `libespeak-ng.dll` directly
  (`espeak_Initialize` + `espeak_TextToPhonemes` → IPA). No exe, no install, calls serialized (espeak
  isn't thread-safe).
- **`KokoroAssetService`** auto-downloads everything into `%AppData%/Steaming/Tts` on first enable:
  Kokoro model + the chosen voice `.bin` (HuggingFace `onnx-community/Kokoro-82M-v1.0-ONNX`) and
  espeak-ng (official `espeak-ng.msi`, extracted with `msiexec /a /qn` — silent admin extract, no
  UAC, no system install). URLs/format verified via web first (rule 13).
- **Voices** are per-voice raw float32 `.bin` (`[510,1,256]`) → `KokoroVoice.Load`. The npz parser
  was wrong and was removed.
- **Settings UI** now: engine dropdown + voice dropdown + "Download / verify" + status. No paths.
- AppSettings reduced to `TtsEngine`, `KokoroVoiceName`, `KokoroModelVariant`. DI updated.
- All Kokoro failures fall back to WinRT (return null) — a missing/bad asset never crashes.

### Runtime still needed (validation points — see PLAN_kokoro_tts.md)
1. Settings → Chat TTS → engine = Kokoro → "Download / verify": confirm model+voice+espeak download
   and the espeak `msiexec /a` extract yields `libespeak-ng.dll` + `espeak-ng-data`.
2. Test button with Kokoro selected → confirm phonemes come back (espeak P/Invoke Cdecl) and audio
   plays; otherwise it silently falls back to WinRT (check Debug output).
3. Switch engine back to Windows → identical to before.

## Session 2026-06-21 (v0.10.41 → v0.10.43) — dashboard chat scroll fix + optional Kokoro ONNX TTS

Build-verified only (WinUI Debug + Release, 0 errors; pre-existing `_loading` + SQLite advisory
warnings only). Runtime UNVERIFIED. No C++/wire changes.

### Done this session
1. **Carried-over alert-editor text-span work committed on its own** (`5dc1b88`) — it had been left
   uncommitted by a prior session (per the old HANDOFF note).
2. **v0.10.42 — dashboard chat auto-scroll fix** (`200695e`). The chat feed (`ChatTextView`, a
   read-only TextBox in `DashboardPage`) stopped pinning to the bottom past one screen. Root cause:
   `Select(text.Length, 0)` only nudges the caret when focused; it never scrolls the inner
   ScrollViewer. Now `ScrollChatToBottom()` drives the TextBox's inner ScrollViewer to
   `ScrollableHeight` after layout on every message. Scrollbar forced `Visible` (was `Auto`).
3. **v0.10.43 — optional Kokoro (ONNX) TTS engine.** WinRT speech stays the default and untouched;
   Kokoro is opt-in via Settings → Chat TTS → "TTS engine". See `PLAN_kokoro_tts.md` for the full
   design + validation points.
   - New `Steaming.Application/Services/Tts/`: `ITtsBackend`/`TtsAudio`, `IPhonemizer`,
     `WinRtTtsBackend` (existing logic extracted), `EspeakNgPhonemizer` (shells out to espeak-ng.exe,
     GPL kept process-isolated + optional), `KokoroTokenizer` (canonical Kokoro vocab),
     `KokoroVoices` (.npz/.npy float32 loader), `KokoroTtsBackend` (ONNX inference, dynamic input
     resolution, float PCM → 24 kHz WAV).
   - `ChatTtsService` now delegates synthesis to the selected backend with **automatic WinRT
     fallback** if Kokoro is unavailable/fails; queue + playback (device routing + 90s watchdog)
     unchanged.
   - `AppSettings`: `TtsEngine`, `KokoroModelPath`, `KokoroVoicesPath`, `KokoroVoiceName`,
     `EspeakNgPath`. DI wires path providers to live settings (`App.xaml.cs`). MainViewModel adds
     engine state + setters + `GetKokoroVoiceNames()`. SettingsPage UI: engine dropdown, file
     pickers, editable voice combo, engine-aware Test button.
   - ONNX Runtime was already referenced (face tracking) — **no new package added.**

### Runtime still needed (I cannot verify audio)
1. Chat scroll: with OBS/chat live, confirm the dashboard chat stays pinned to the bottom past one
   screen and the scrollbar is always visible.
2. Kokoro: drop a kokoro `.onnx`, a voices pack, and `espeak-ng.exe` into Settings → Chat TTS,
   switch engine to Kokoro, hit Test. **VALIDATION POINTS** (see plan): the voices-pack format/shape
   and the model's input names + phoneme vocab must match. On mismatch it falls back to WinRT (no
   crash) — check the Debug output line `[ChatTts] Kokoro synthesis unavailable/failed`.
3. Switch engine back to Windows → behaviour identical to before.

## Session 2026-06-19 (v0.10.40 → v0.10.41) — true logout, OBS connect-on-startup, stream-start metadata verify

Build-verified only (WinUI Release + Debug, 0 errors). Runtime UNVERIFIED. No C++/wire changes.

NOTE: an unrelated, build-verified alert-editor change set (text-keyframe `GetEditableSpansAt`) was sitting UNCOMMITTED in the working tree at session start (`AlertEditorViewModel.cs`, `AlertEditorWindow.xaml.cs`). It was left untouched — commit it separately.

### Done this session
1. **Twitch/Kick "Disconnect" now truly logs out (forgets all tokens + cookies).**
   - `PlatformCredentialService.ClearTwitchLogin()` now also nulls `TwitchChannel` + `TwitchClientId`; `ClearKickLogin()` now also nulls `KickClientId` + `KickClientSecret`. Previously these were left behind.
   - Root cause of "remembers username": the **WebView2 OAuth cookie cache** was only wiped for Kick. `ConnectionsPage.TwitchLogin_Click` now calls `LoginWindow.ClearProfileData("Twitch")` + `RefreshAll()` on disconnect (mirrors the existing Kick path), so the next "Connect Twitch" shows a real login prompt instead of silently re-auth'ing.
2. **OBS WebSocket now connects on startup (root cause: it never did).** `App.xaml.cs` never called `ConnectObsAsync` at launch; the checkbox only armed reconnect-on-drop, and with no initial connection there was nothing to reconnect. Per user choice the existing toggle is REUSED (relabelled "Connect to OBS on startup and automatically reconnect if the connection drops"). New `ObsWebSocketService.TryConnectWithReconnectAsync()` (immediate connect; on failure with AutoReconnect on, starts the backoff loop so it connects once OBS comes up). New `MainViewModel.TryAutoConnectObsAsync()` (gated on toggle + saved address) is fired-and-forgotten from startup after the Kick-bridge block so a non-running OBS never delays the splash.
3. **Stream-start title/category verification with a re-apply prompt** (addresses "Kick didn't update the title last stream"). When OBS transitions into streaming (`StreamStateChanged` false→true), `MainViewModel.VerifyStreamMetadataAsync()` re-fetches live Twitch/Kick channel info and compares it to the last-pushed title/category (stored per-targeted-platform in `AppSettings.LastApplied*`). On mismatch it logs an activity line and raises `StreamMetadataMismatchDetected`; `MainWindow` shows a "Re-apply / Ignore" `ContentDialog` (user asked for a prompt, not silent reapply). "Re-apply" calls `ReapplyStreamMetadataAsync()` which re-pushes to exactly the platforms originally targeted. Single-flight + 60s debounce guard against repeat prompts on OBS reconnect.

### Root cause / proof
- Logout proof: `ClearTwitchLogin`/`ClearKickLogin` (PlatformCredentialService.cs) omitted those fields; only Kick's `KickLogin_Click` called `LoginWindow.ClearProfileData`, Twitch's did not.
- OBS proof: grep for `ConnectObsAsync`/auto-connect in `App.xaml.cs` startup = 0 hits; reconnect loop only starts from `ReadLoopAsync` exit after an established socket drops (`ObsWebSocketService.cs` ~147).
- Verify proof: `UpdateTitleAsync`/`UpdateGameAsync` only PATCHed; nothing ever re-read channel info to confirm. Kick's `GetKickChannelInfoAsync` does a LIVE fetch first and only falls back to `KickMetadataCache` on failure, so a real silent-ignore by Kick is caught while a transient fetch failure won't false-positive.

### Files
`PlatformCredentialService.cs`, `ConnectionsPage.xaml.cs`, `ObsWebSocketService.cs`, `MainViewModel.cs`, `App.xaml.cs`, `ObsConfigPage.xaml`, `AppSettings.cs`, `MainWindow.xaml.cs`, `Steaming.Core.csproj` (version bump).

### Runtime still needed
1. Connect Twitch, Disconnect, Connect again → expect a fresh Twitch login prompt (no auto re-auth), status returns to "Not connected" on disconnect. Repeat for Kick.
2. Tick the OBS checkbox, restart the app with OBS running → it connects automatically. Restart with OBS closed, then open OBS → it connects within the backoff window.
3. Set a title (and/or category) on the Dashboard/Stream page, then make Kick/Twitch NOT match (or reproduce the silent-Kick case), start streaming in OBS → expect the "Stream info may not have applied" dialog; "Re-apply" re-pushes to the platforms you set; "Ignore" dismisses.

---

## Session 2026-06-19 (v0.10.39 → v0.10.40) — text keyframe live-preview regression fix

Build-verified only. Runtime unverified.

### Done this session
- **Fixed the actual live-typing regression path in the alert editor:** reselecting a text element repopulates the `RichEditBox`, and that programmatic reload was leaving `_suppressRich` armed until a deferred low-priority callback ran. While that flag stayed true, `_richBox.TextChanged` dropped user typing, so the preview did not update live and edits could appear to “not populate” until later or at all.
- **Programmatic rich-text reload now ignores only its own async change event:** `AlertEditorWindow.xaml.cs` now tracks the spans loaded into the `RichEditBox` and filters out only the matching programmatic `TextChanged`, instead of blocking all live typing after a reselect.
- **Element-level text spans are now kept in sync when writing text keyframes:** `AlertEditorViewModel.WriteTextSpansKf(...)` now updates `el.Spans` alongside `el.Content` and the keyframe spans, while still preserving the pre-edit baseline spans when auto-creating the first `T=0` span keyframe. This keeps fallback/render/serialize state aligned.

### Root cause / proof
- **Live preview bug proof:** `UpdatePropertiesPanel()` reloads spans into the rich box on text selection (`AlertEditorWindow.xaml.cs`), `LoadSpansIntoRichBoxFromList(...)` set `_suppressRich = true`, and only cleared it later via `DispatcherQueue.TryEnqueue(...Low...)`. `_richBox.TextChanged` only calls `CommitRichSpans()` when `_suppressRich` is false. So after moving away and back to a text element, live typing could occur while the editor was still suppressed, and the preview would not update.
- **Population mismatch proof:** `WriteTextSpansKf(...)` updated `el.Content` and the active keyframe spans, but did not update `el.Spans`, even though both `EvalSpansAt(...)` fallback logic and ALT3 serialization use `el.Spans` as the non-keyframed text store.

### Build / test
- `dotnet build Steaming.WinUI/Steaming.WinUI.csproj -c Release` — passed, 0 errors.
- Runtime still needed:
  1. Select a text element, click another layer, click back, type immediately — preview should update on each keystroke again.
  2. Create a new text keyframe, scrub away and back, and confirm the edited text is still shown in both the preview and the `RichEditBox`.
  3. Save, reopen the editor, and confirm the text/spans persist.

### Known notes
- Pre-existing WinUI warning remains: `_loading` unused in `ChatSettingsPage.xaml.cs`.
- Pre-existing NuGet advisory warning remains for `SQLitePCLRaw.lib.e_sqlite3`.

## Session 2026-06-19 (v0.10.38 → v0.10.39) — transitions panel moved out of timeline; tile drag hit-area fixed

Build-verified only. Runtime unverified.

### Done this session
- **Transitions moved into their own dockable control area:** the palette no longer sits above the timeline slider. `AlertEditorWindow.xaml.cs` now builds `_transitionsContent` separately and adds a dedicated `Transitions` document beside the bottom `Timeline` document in `SetupDockManager()`.
- **Root cause of the “hard to drag” bug fixed:** `CanDrag = true` had only been applied to the outer tile `Border`, while most of the tile surface was covered by child elements (`StackPanel`, preview `Canvas`, label `TextBlock`). That made drag start only from a narrow border/padding region. The drag handlers are now wired across the visible tile surface via `WireTransitionTileDrag(...)`, so the whole tile face starts the drag.
- **Broken intermediate layout edit fixed:** a non-existent `.Also(...)` helper slipped into `BuildTransitionsContent()` during layout restructuring and broke compile. Replaced with explicit `Grid.SetRow(...)` / `Children.Add(...)`.

### Root cause / proof
- Placement issue proof: the earlier palette was injected directly inside `BuildTimelineContent()` above the slider, so it necessarily consumed timeline height there.
- Hard-drag proof: `BuildTransitionTile(...)` had `CanDrag = true` only on the outer `Border`; the child content covered almost the entire hit area, so pointer presses commonly landed on non-draggable children instead of the border.

### Build / test
- `dotnet build Steaming.WinUI/Steaming.WinUI.csproj -c Release` — passed, 0 errors.
- Runtime still needed:
  1. Confirm the bottom area now shows separate `Transitions` and `Timeline` tabs/panels.
  2. Click-drag from the preview image area, the label area, and the tile body — all should start drag reliably.
  3. Drop onto text splice chips/gaps and confirm assignment still works.

### Known notes
- Pre-existing WinUI warning remains: `_loading` unused in `ChatSettingsPage.xaml.cs`.
- Pre-existing NuGet advisory warning remains for `SQLitePCLRaw.lib.e_sqlite3`.

---

## Session 2026-06-19 (v0.10.37 â†’ v0.10.38) â€” alert editor transition palette drag/drop

Build-verified only. Runtime unverified.

### Done this session
- **Baseline backup commit made first:** repo committed as `v0.10.37: commit current alert editor transition work` before further editor changes, per repo rule.
- **Transition palette strip added to the timeline panel:** `Steaming.WinUI/AlertEditorWindow.xaml.cs` now builds a horizontal tile palette above the timeline slider. Tiles exist for Cut, Type On, Fade, Slide Left, Slide Right, Morph.
- **Hover mini-previews added:** mousing a tile starts a looping preview animation inside the tile. The preview reuses the editor's existing span-transition evaluation path (`EvalTextTransitionState` / `EvalSpansAt`) against a small sample text element, so the tile motion matches the real editor transition logic.
- **Drag/drop transition assignment added:** the timeline canvas is now a drop target. Dragging a tile over the timeline highlights the nearest valid text splice gap from `_tlSpliceHits`; dropping applies the transition to that gap's next span keyframe and immediately redraws the canvas/timeline.
- **Undo now captures transition changes:** the click-to-change splice menu and the new drag/drop path both go through `AlertEditorViewModel.ApplySpanTransition(...)`, so changing a text transition now records an undo snapshot instead of mutating silently.

### Root cause / proof
- The transition system itself was already implemented end-to-end.
- Model setter existed: `AlertEditorViewModel.SetSpanTransitionOnNextKf(...)`.
- Timeline hit targets existed: `_tlSpliceHits` in `DrawTimeline()`.
- Renderer existed: WinUI preview `RebuildTextTransitionCanvas(...)` and C++ `RenderTextTransition(...)`.
- But the only timeline setter UI was `ShowSpliceFlyout(...)` in `AlertEditorWindow.xaml.cs` â€” a click menu, not a drag/drop palette. There was no drag source, no timeline drop handler, and no hover preview tiles.

### Build / test
- `dotnet build Steaming.WinUI/Steaming.WinUI.csproj -c Release` â€” passed, 0 errors.
- `cmake --build obs/obs-plugintemplate/build_x64 --config RelWithDebInfo` â€” passed.
- Runtime still needed:
  1. Hover each palette tile and confirm the mini-preview loops visibly.
  2. Drag each transition tile onto a text splice chip/gap and confirm the chip updates immediately.
  3. Scrub/play the editor preview after each drop and confirm the text transition matches the tile.
  4. Save an alert, reopen it, and confirm the dropped transitions persisted.

### Known notes
- WinUI build still reports the pre-existing `_loading` warning in `ChatSettingsPage.xaml.cs`.
- NuGet still reports the pre-existing `SQLitePCLRaw.lib.e_sqlite3` advisory warning during restore.
- No runtime verification was performed in this session.

---

## Session 2026-06-19 (v0.10.31 → v0.10.36) — alert editor: colour fix, flip, zoom, smoothness; chat/TTS

All build-verified (WinUI Debug 0 errors; C++ plugin built `RelWithDebInfo` and auto-deployed) unless noted. **Text colour fix is runtime-verified by the user; everything else is runtime-UNVERIFIED.**

### Done this session
- **Text colour picker (FIXED, verified):** root cause = `_propTextColor` (the hex TextBox) was orphaned from the visual tree by the v0.10.31/32 toolbar restructure, so its `TextChanged` never fired and `ApplyColorToSelection` was never called. Fix: the text swatch's `onPicked` calls `ApplyColorToSelection()` directly (`AlertEditorWindow.xaml.cs`, text swatch in `BuildPropertiesContent`). Toolbar moved back into the Text panel; RichEditBox built-in `SelectionFlyout` disabled. Debug `[ColorDbg]` logging was added then removed.
- **Image/GIF flip (negative width/height = mirror in place):** editor (`UpdatePreviewState`, `CreateElementControl`/`Make*Control` use `Math.Abs`, mirror via `ScaleTransform`), Overlays preview (same), VM `UpdateSelectedGeometry` allows negatives (`ClampSignedMin1`), `FlipSelectedHorizontal/Vertical`. **C++ plugin** `obs/obs-plugintemplate/src/layout_renderer.cpp`: `MirrorBGRA` + `BlitFrame` mirror images; `RenderElement` uses abs so rect/text don't vanish. Wire needed NO change (W/H already signed float32). Positive values byte-identical. **Flip H/Flip V buttons** in Position & Size.
- **Image add aspect ratio:** `SetMediaAspectSize` sizes new images/GIFs to real aspect fit-to-canvas (was 100×100 square).
- **Dashboard chat redraw:** replaced virtualizing `ListView` with a read-only auto-scroll `TextBox` (`DashboardPage`) — no more full-redraw/blank.
- **Alert TTS:** `AppSettings.EnableAlertTts`; `ChatTtsService` speaks follow/sub/gift/bits/raid/redemption; Settings → "Read alerts aloud" checkbox.
- **Timeline zoom:** Ctrl+wheel (`_tlZoom`, `TimelineZoomWheel`); ruler wrapped in `_tlRulerScroll` synced to track horizontal scroll; track horizontal scroll enabled.
- **Playback smoothness:** `OnCompositionTargetRendering` now updates the visual directly per frame; the `PreviewTime` PropertyChanged branch early-returns when `_renderingHooked` (was deferring via `DispatcherQueue.TryEnqueue` → stutter).
- **Timeline transition "C"/"F" badge:** hit was misaligned (drawn at `rowY+2`, hit at row centre, 8px tol → missed). Now hit at `rowY+6`; left/right click opens `ShowSpliceFlyout`.
- **Verified C++ supports ALL 6 text transitions** end-to-end: `kf.SpanTransition` serializes (extMask bit 7), C++ parses (`layout_renderer.cpp:308`), renders — Cut/TypeOn via `EvalSpansAt`, Slide/Fade/Morph via `RenderTextTransition` (`layout_types.h`). No backend work needed for transitions.

### NEXT SESSION MUST DO FIRST — transition/effects palette (the user is blocked on this for streaming)
The user REJECTED dropdowns/menus for setting text transitions. Build the **graphical** version per `editor_update.md` "Feature 0" + "UX PRINCIPLE":
- A **palette of transition tiles** (Cut/Type On/Fade/Slide L/Slide R/Morph), **hover = live mini-preview** of the effect on sample text (reuse `EvalTextTransitionAt`/`RenderTextTransition`).
- **Drag a tile onto the timeline gap** between two text span keyframes → applies it (`SetSpanTransitionOnNextKf`), live preview. Gaps are already in `_tlSpliceHits`.
- This is PURE editor UX — model, wire (`SpanTransition` bit), and C++ render are DONE/verified. No plugin/wire changes.
- A good place for the palette strip: `BuildTimelineContent()` `root` grid (line ~1179), add an Auto row above `sliderRowGrid`. `_tlCanvas.AllowDrop`, `DragOver` highlights nearest gap, `Drop` applies.
- DO NOT add dropdowns/combo boxes. Direct manipulation only.

### Build / test
- C#: `dotnet build Steaming.WinUI/Steaming.WinUI.csproj -c Debug` (close the running app first or the DLL copy locks — that's not a compile error).
- C++: `cmake --build obs/obs-plugintemplate/build_x64 --config RelWithDebInfo` (auto-deploys; restart OBS to load).
- Test: text colour applies (verified); load `lootbunny.png` (1024×1536) → comes in tall not square; Width negative or Flip H → mirrors (editor + OBS after restart); Ctrl+wheel zooms timeline; playback smooth; click "C" badge opens transition picker; Settings "Read alerts aloud".

### Git
Committed at end of session (see latest commit). Text colour verified; rest runtime-unverified.

---

## Session 2026-06-18 (v0.10.29) — WPF removed; unique alerts moved to the real page

Build-verified WinUI Debug + Release, 0 errors. Runtime: WinUI app launches (user confirmed).

**WPF (`Steaming.App`) fully removed:**
- Project source deleted (git rm). The folder reappeared once as an empty `bin/` because `Steaming.slnx` still listed `Steaming.App` as a StartupProject — removed that entry, so the IDE no longer recreates it.
- `.vscode/launch.json` + `tasks.json` reduced to WinUI-only.
- Stale `artifacts/*/Steaming.App.*` binaries removed.
- Docs updated: CLAUDE.md (rules 11, 20, failure table), ARCHITECTURE.md (component table + build cmds), AGENTS.md, .codex/AGENTS.md, Codex.md (rewritten to mirror CLAUDE.md for Codex). HANDOFF_history.md left as historical record.

**Unique alerts MOVED to the correct page:**
- v0.10.28 mistakenly added the "Unique Alerts" UI to `EventsPage`, which is NOT in the WinUI nav (not reachable). Reverted EventsPage to its original state.
- The section now lives on `OverlaysPage` (the real alerts page: nav "Overlays") below Reward Redemption, styled like the event expanders: Add Alert, per-alert enable/Test/Edit Layout/Delete, text/duration/volume/sound. It scrolls via the existing left-column ScrollViewer.
- Lesson added to CLAUDE.md/Codex.md: verify a page is wired into `MainWindow.xaml` before adding UI to it.

### Next session — verify first (runtime unverified)
1. Overlays page: Add a unique alert, Edit Layout, Save, restart → persists; Test fires to OBS.
2. Chatbot: link a command to the unique alert; run it → chat text + alert both fire.

---

## Session 2026-06-18 (v0.10.28) — Unique alerts linkable to bot commands

Build-verified all configs (Core/Application/WPF + WinUI Debug & Release), 0 errors (pre-existing `_loading` warning only). Runtime unverified. **No C++/wire-format change** — `RenderAlertV2` already ships the full layout across the pipe, so the alert source renders any custom layout it's handed.

Feature: user creates named "Unique" alerts on the Alerts page; a bot command can optionally fire one (selected from a dropdown) in addition to its chat text.

- **Model/persistence:** `AppSettings.CustomAlerts` (`Dictionary<string,EventConfig>`, name→config), null-guarded in NormalizeChatOverlayProfiles. `BotCommand` gains `AlertEnabled` + `AlertName` (read in `ChatbotService.Load`; `Save` auto-serializes).
- **Fire path:** `ChatbotService.TriggerCustomAlert` delegate (`Func<string,StreamUser,string,Task>`), called in `TryRunCommandAsync` after the chat send. Wired in BOTH hosts (WinUI `App.xaml.cs`, WPF `AppStartupCoordinator`) to `OverlayDispatcher.SendCustomAlertAsync(cfg,user,arg)`, which serializes the config's layout into `RenderAlertV2` (mirrors `SendConfiguredAlert`).
- **VM:** `CustomAlertItem` (new VM file) + `MainViewModel.CustomAlerts`/`CustomAlertNames` observable collections, `LoadCustomAlerts` (called after `SyncChatbotCollections` in both hosts), `AddCustomAlert`/`RemoveCustomAlert`/`SaveCustomAlerts`/`SaveCustomAlertEditorResult`/`TriggerCustomAlertTestAsync`.
- **WinUI UI:** Alerts page (`EventsPage`) — new "Unique Alerts" section with Add Alert (name dialog), an `ItemsControl` of cards (Enabled, name, Test, Edit Layout via the existing `AlertEditorWindow`, Delete, text/duration/volume/sound), persisted by "Save All". Chatbot page (`ChatbotPage`) — command form gains "Fire alert" checkbox + unique-alert name dropdown (bound to `CustomAlertNames`), wired into Add/Edit/Reset.

WPF UI NOT added (WinUI-only this session); shared core compiles + functions for WPF (delegate wired there too).

### Next session — verify first (runtime unverified)
1. Alerts page: Add a unique alert "drak", edit its layout, Save All, restart → persists.
2. Chatbot: create `!drak`, tick Fire alert, pick "drak". In chat run `!drak` → chat text sends AND the drak alert renders in OBS.
3. Delete a unique alert that a command references → command should simply not fire an alert (no crash). Consider surfacing a warning later.

---

## Session 2026-06-18 (v0.10.26 → v0.10.27) — OBS WebSocket auto-reconnect, streaming detection, stream health monitor (WinUI)

Build-verified only — runtime unverified. Shared libs (Core/Application/Data) build Release 0 errors/0 warnings. WinUI compile produced no CS errors but could NOT deploy: the app was running (PID locked output DLLs), so the DLL-copy step failed. **Close the app and rebuild WinUI Debug+Release to deploy/test.**

WinUI only by user choice; WPF UI intentionally NOT touched (shared service/VM changes still compile for it via the Application Release build).

**Auto-reconnect (opt-in):** `ObsWebSocketService` had no reconnect logic at all — a dropped socket just fired `ConnectionChanged(false)` and sat there. Added:
- `AutoReconnect` property + `ReconnectLoopAsync()` with 2s→30s capped backoff.
- `_userClosed` flag distinguishes a deliberate `Disconnect()`/`DisposeAsync()` from a network drop, so reconnect only fires on real drops. `ConnectAsync` resets it false and stores `_lastUrl/_lastPassword`.
- `_reconnecting` interlock guard prevents overlapping loops.
- `ReconnectStatusChanged` event surfaces countdown text to `ObsStatus`.
- Persisted as `AppSettings.ObsWebSocketAutoReconnect` (default false); `IntegrationConfigService.ObsAutoReconnect` + `SaveObsAutoReconnect`; VM `SetObsAutoReconnect` + `ObsAutoReconnect`. Applied to the service in `SubscribeToServices`. New checkbox on ObsConfigPage.

**Streaming detection:** Identify previously sent `eventSubscriptions = 0`, and ReadLoop ignored `op==5` events. Added:
- `eventSubscriptions = Outputs (1<<6)` in the Identify message.
- `op==5` branch parsing `StreamStateChanged.outputActive` → `StreamStateChanged` C# event.
- `GetStreamStatusAsync()` (GetStreamStatus.outputActive) + `InitStreamStateAsync()` fired after every connect (manual or auto) so initial/post-reconnect state is correct.
- VM `ObsStreaming` bool; ObsConfigPage shows a red/grey dot + "OBS is streaming/not streaming/unknown".

### v0.10.27 — Stream health monitor + per-destination warnings (WinUI)
Build-verified Debug + Release, 0 errors (pre-existing `_loading` warning only). Runtime unverified.

Correlates three independent truths: OBS encoding (`ObsStreaming` from GetStreamStatus/StreamStateChanged), Twitch live (`TwitchIsLive`), Kick live (`KickIsLive`), plus per-platform hook/auth health from `ServiceStatuses` "Error" state. New `MainViewModel.EvaluateStreamHealth()` (called from `UpdateServiceStatus`, the OBS connection/stream handlers, and `UpdateLiveStatus`) produces:
- `StreamHealthText` — "OBS: Live · Twitch: Live · Kick: Live" pill in the MainWindow status bar (green when streaming+healthy, amber on problem, grey when idle).
- `HealthWarning` — amber status-bar text + an activity-log line, **edge-triggered** (only on transition into unhealthy) so no spam.
- Warnings are **per destination**: `WarnOnUnhealthyTwitch` / `WarnOnUnhealthyKick` (persisted in AppSettings, default OFF). Each gates BOTH the liveness mismatch (OBS streaming but that platform not live) AND that platform's hook/auth failure (token/relogin case). Liveness mismatch only counts while OBS is actually streaming.
- Edge state (`_prevTwitchHealthy`/`_prevKickHealthy`) tracks ACTUAL health regardless of toggles, so enabling a toggle later doesn't retro-fire.

Toggles surfaced on three pages (all bound to the same VM setters): ObsConfigPage ("Stream Health Warnings" section), StreamPage ("STREAM HEALTH" section + live readout), DashboardPage (Quick Actions).

Files: AppSettings.cs, IntegrationConfigService.cs, MainViewModel.cs, MainWindow.xaml(.cs), ObsConfigPage.xaml(.cs), StreamPage.xaml(.cs), DashboardPage.xaml(.cs).

NOTE: hook health is derived from ServiceStatuses "Error". Verify the Twitch EventSub/chat 401 path actually sets those keys to "Error" at runtime (Kick bridge 401 already does, via AuthRejected). StreamDataService also exposes `TwitchAuthStatusChanged`/`KickAuthStatusChanged` (poll 401) that are NOT yet wired to ServiceStatuses — if runtime shows Twitch token-expiry doesn't trip a warning, wire those events to `UpdateServiceStatus("twitch-eventsub"/"kick-bridge", "Error", …)`.

### Next session — verify first (runtime unverified)
1. Close app, rebuild WinUI Debug+Release, confirm 0 errors. (DONE this session — both 0 errors.)
2. Toggle auto-reconnect on, kill OBS WebSocket (close OBS or disable server), confirm it reconnects when OBS returns; confirm manual Disconnect does NOT reconnect.
3. Start/stop streaming in OBS, confirm the streaming dot/text + health pill update live (push event) and are correct immediately after connecting.
4. Enable Warn-Twitch only; start OBS streaming without going live on Twitch → expect amber pill + warning. Enable Warn-Kick and repeat for Kick. Confirm a disabled destination produces no warning.
5. Force a Kick bridge 401 (or Twitch token expiry) → confirm a hook/auth warning fires for the enabled destination.

---

## Session 2026-06-17 (v0.10.18 → v0.10.23) — face tracking CPU + jaw/mouth fixes

All build-verified only — runtime unverified. WinUI Debug builds 0 errors.

**CPU/perf (v0.10.18):** `BuildSessionOptions()` never set thread counts — ONNX defaulted to all 24 logical cores per inference, saturating CPU. Added `IntraOpNumThreads=2`, `InterOpNumThreads=1`. MediaPipe path never added DML (ran both models on CPU) — added `AppendExecutionProvider_DML(0)`. NOTE: user reports tracking STILL spikes CPU 20→34%+ (MediaPipe 43%) — thread cap did NOT fully solve it. Open problem; PLAN requires "little to no CPU/GPU impact."

**One model per frame (v0.10.18):** `MediaPipeFaceProvider.ProcessFrame` re-ran BlazeFace every 8 frames. Per PLAN perf rule 4, BlazeFace now runs ONLY when `_lastFace == null`; FaceMesh derives next crop from `LandmarkBounds`. One model per frame while locked.

**Jaw strength now a true gain (v0.10.19):** was a clamped trim `Math.Max(0.55f, scale)` over a 0.55–1.6 slider — could not mute or amplify. Now slider 0–2, floor removed: 0 = off, 1 = normal, 2 = double. FaceRetargetService.cs:254, AvatarViewModel.cs:209/1233, AvatarPage.xaml:228.

**Removed duplicate camera preview (v0.10.20):** deleted the small `CameraPreviewImage`/`TrackingOverlayCanvas` + per-tick copy/overlay + `_cameraPreviewBitmap` field. Large `LargeCameraPreviewImage` remains.

**At-rest baseline ratchet (v0.10.21):** session baseline could never adapt up to true resting value, so neutral read ~40% open and AA/OU/OH fired at rest. Band widened `JawRange*0.40→0.70`, `MouthRoundRange*0.30→0.60`.

**Smoothing deadband froze channels (v0.10.22):** `SmoothStable` returned `current` within epsilon, so a channel driven to 0 (jaw strength 0) froze at ~0.016. Now returns `target` — settles exactly to 0.

**Teeth/tongue clipping out of chin (v0.10.23):** ROOT CAUSE of the visible geometry break. `AvatarRenderService.cs:628` accumulated morph weights with no clamp; AA+OU+OH all high summed past 1.0 on shared targets → vertex over-displacement → teeth/tongue through skin. Added per-target clamp to [0,1].

**Calibration now authoritative — removed session-adaptive baseline (v0.10.24):** the session baseline initialized from a noisy first frame, drifted down with metric noise, and ratcheted below true rest — so even right after Capture Neutral, a near-closed mouth (raw jaw 0.218) read as jaw 0.936 (shoved out). Removed the session baseline entirely; `effectiveNeutral*` now uses `calibration.Neutral*` directly. Deleted fields, `BaselineAdaptRate`, `ResetSessionBaselines()` + its VM call.

**Camera/tracking-model memory (v0.10.24):** `InitAsync` now calls `LoadFaceTrackingSettings()` BEFORE `RefreshCameraDevicesAsync()`, and the enumerate step only falls back to camera[0] when the saved id is absent from the device list (was unconditionally defaulting then relying on a later overwrite + deferred notifies). Should make the saved camera stick on restart.

**DESTRUCTIVE settings-load loop fixed (v0.10.25):** root cause of camera/model "not remembering". `OnNavigatedTo` populated the MouthMode combo (and others) BEFORE `InitAsync` loaded the file; `MouthModeCombo_SelectionChanged` had no suppression guard → fired `_vm.MouthMode` setter → `SaveFaceTrackingSettings()` (which saves unconditionally) → wrote DEFAULT VM state (camera "", OpenSeeFace, mouth 0) over the saved file BEFORE the real settings were read. Then load read the defaults and enumerate fell back to camera[0] (e.g. XSplit VCam). Fix: `_suppressPersist` guard in VM (both Save methods early-return while loading); `BeginSettingsLoad()` called at top of OnNavigatedTo; `InitAsync` wraps load in suppress; `OnNavigatedTo` is now async, awaits InitAsync, then `SyncPersistedControls()` applies loaded values to every control deterministically. NOTE: the file was already corrupted (XSplit+OpenSeeFace) by the old bug — user must re-select c922 + MediaPipe ONCE after this build, then it should persist.

### Still open / next session
- CPU still too high for streaming (20→34%+, MediaPipe 43%). Thread cap insufficient — investigate per-frame cost, lower FPS cap, or lighter model. Highest priority — PLAN demands "little to no CPU/GPU impact."
- Verify camera + tracking model persist across restart now (v0.10.25 — runtime unverified).
- Verify jaw/mouth no longer shoved out at neutral after Capture Neutral (v0.10.24 — runtime unverified). If still wrong, the MediaPipe jaw/mouth metric itself may be noisy frame-to-frame (single-frame Capture Neutral unreliable) — consider averaging capture over N frames.
- Mouth controls fragmented: no single "mouth open amount" master; jaw strength only scales the jaw bone. User wants controls that govern the visible mouth. Needs a wiring decision.

---

## Current state: v0.10.16 (2026-06-16)

App: `Steaming.WinUI` (primary). WPF (`Steaming.App`) is deprecated — do not touch.
All C# build configs: 0 errors. Pre-existing warning in `ChatSettingsPage.xaml.cs` (`_loading` unused).

---

## What is in v0.10.16 (this session — build succeeded, runtime unverified)

### Bug 1 fixed: Session-adaptive baseline missing for MouthOpen and JawOpen

MouthOpen and JawOpen had no session baseline — they used raw calibration values directly. Rob's rest MouthOpen this session was 0.372 but calibrated NeutralMouthOpen was 0.317, meaning the avatar saw his mouth as 33% open at rest constantly. All expressions were firing at rest as a result.

Fixed: `_sessionMouthOpenBase` and `_sessionJawOpenBase` added. Both initialize from first tracked frame and adapt toward rest. Normalization now uses these effective baselines.

Also fixed: at-rest check now gates on BOTH MouthOpen and MouthRound being below threshold, so the baseline does not corrupt during OH expression.

### Bug 2 fixed: Session-adaptive baseline never corrected (root cause of OH-at-rest)

**File**: [Steaming.Application/Services/FaceRetargetService.cs](Steaming.Application/Services/FaceRetargetService.cs)

**Root cause**: Two bugs in the session-adaptive baseline logic:
1. Baseline initialized at `min(calibration.NeutralMouthRound, raw.MouthRound)` — this always picked the stale calibration value (0.757) when it was lower than the actual rest value (0.871). Result: baseline started 0.114 below real rest, normalized mouthRound at rest = 63%.
2. "At rest" check used `raw.MouthOpen < NeutralMouthOpen + JawRange * 0.30` — threshold was 0.367, but raw MouthOpen at rest was 0.372–0.446 from screenshots. Threshold was NEVER met. Baseline never adapted. Avatar showed OH permanently.

**Fix**:
- Baseline now initializes from `raw.MouthRound` and `raw.MouthWidth` directly on first tracked frame.
- "At rest" threshold raised from 30% to 55% of JawRange (threshold now 0.409, rest MouthOpen is ~0.37–0.40, so adaptation fires correctly).

### Bug 2 fixed: mouthGate blocked OH when jaw not open (from prior session)

OH expression is lip-rounding, not jaw-opening. The gate `mouthGate = Clamp((mouthOpen - 0.06) / 0.10, 0, 1)` was applied to OH. Since mouthOpen normalized is low when lips round without jaw opening, gate = 0 → OH = 0 always.

**Fix**: OH now uses a lower gate (`ohGate = Clamp((mouthOpen - 0.02) / 0.06, 0, 1)`) so it can fire with minimal jaw movement. AA/IH/OU/EE still use the original gate.

**NOTE**: Even with this fix, the OH signal range from 2D landmarks is narrow (~0.04 raw units between rest and full OH). After session baseline converges, peak OH from tracking will be ~0.2–0.3. Use the OH strength slider to boost it.

### Feature added: Expression strength sliders

Five sliders added to calibration panel — AA, IH, OU, EE, OH strength (0.1–3.0, default 1.0). These multiply the final retarget output per expression. Saved to calibration file. Rob can tune without recalibrating.

**Files**: `FaceTrackingModels.cs` (new fields), `FaceRetargetService.cs` (apply scales), `AvatarViewModel.cs` (properties), `AvatarPage.xaml` (sliders), `AvatarPage.xaml.cs` (handlers + init + sync).

### Feature added: Capture OH button (from prior session)

Captures current MouthRound as the OH calibration range. In calibration row alongside Capture Jaw Open and Capture Smile.

### Bug fixed: CaptureSmile used wrong metric (from prior session)

Was using `SmileLeft - NeutralSmileLeft` (0–1 signal) to set `MouthWidthRange` (pixel-space metric). Now uses `raw.MouthWidth - NeutralMouthWidth` correctly.

### Bug fixed: Baseline reset on every slider touch (from prior session)

`UpdateCalibration` was resetting session baselines on every slider change. Fixed: baselines only reset on explicit Capture Neutral.

### Bug fixed: headYaw broken with acos (from prior session)

Used `acos(eyeDistance/faceScale)` — faceScale was dominated by eyeDistance making ratio ≈ 1.0 → acos(1.0) = 0 → no yaw. Fixed to `atan(rawYawDelta * 0.5)`.

---

## Build state

**Compile**: `dotnet build Steaming.WinUI/Steaming.WinUI.csproj -c Debug` → Build succeeded, 0 errors.

**Deploy**: App must be closed before build can copy DLLs (file lock). Close app → rebuild → restart.

---

## MediaPipe model files — vendored in v0.10.17

Both ONNX model files are now committed to `Steaming.Application/Assets/FaceTracking/MediaPipe/` and are copied to the build output automatically.

- `face_detection_short.onnx` (409 KB) — BlazeFace short-range from Unity inference-engine-blaze-face (HuggingFace)
- `face_mesh.onnx` (2.4 MB) — face_mesh_192x192 from PINTO0309/PINTO_model_zoo 032_FaceMesh

Select "MediaPipe" from the Tracking Model dropdown. Falls back to OpenSeeFace automatically if files are missing (they shouldn't be).

### MediaPipe provider shape corrections (v0.10.17)

- FaceMesh input was HWC `[1,192,192,3]` — model actually expects CHW `[1,3,192,192]`. Fixed: `FillChwTensor` added.
- FaceMesh landmark output was indexed as 2D `raw[0, i*3]` — actual shape is `[1,1,1,1404]`. Fixed: `raw[0,0,0,i*3]`.
- FaceMesh score output indexed as `flag[0,0]` — actual shape `[1,1,1,1]`. Fixed: `flag[0,0,0,0]`.
- BlazeFace regressors are SSD anchor-relative offsets, not absolute coords. Fixed: decode with generated anchor centers (16x16×2 + 8x8×6 = 896 anchors).

---

## Next session must verify first

1. After restart: does avatar rest with mouth closed (no OH at rest)?
2. Does making OH face drive the OH expression visibly? (Expect 0.2–0.3 range; boost with OH strength slider if needed.)
3. Does head rotation feel proportional now (atan fix)?
4. MediaPipe mode: select "MediaPipe" in tracking model dropdown. Does tracking start and does avatar respond to face?
5. If MediaPipe anchor decoding produces wrong face box (wrong region highlighted in camera preview), the cx/cy index order in Detect() may need swapping (try `boxes[0,best,1]` for cx and `boxes[0,best,0]` for cy).

---

## Carry-forward: implementation state of all face tracking services

- OpenSeeFace assets vendored into `Steaming.Application/Assets/FaceTracking/`
- Services: `FaceTrackingModels.cs`, `FaceTrackingPersistenceService.cs`, `FaceTrackingDiagnosticsService.cs`, `ReplayFrameService.cs`, `CameraCaptureService.cs`, `FaceRetargetService.cs`, `FaceTrackingService.cs`
- `Steaming.Application.csproj` pins ONNX Runtime + DirectML, copies assets to output
- `AvatarRenderService` consumes `FaceTrackingState` for all mouth/head/eye/blink
- `AvatarViewModel` carries all face-tracking state, settings, calibration, diagnostics
- `AvatarPage.xaml/.xaml.cs` exposes full UI: camera, tracking start/stop, mouth mode, calibration buttons, expression sliders, diagnostics

---

## Open bugs not yet investigated

- Chat full redraw: dashboard chat sometimes does a full redraw instead of incremental update
- Analytics: verify June 15 sessions pair correctly into one "Both" entry (was build-verified only as of v0.10.6)

---

## Session 2026-06-17 - face tracking load reduction pass

Build verified:
- `dotnet build Steaming.WinUI/Steaming.WinUI.csproj -c Release` succeeded
- `dotnet build Steaming.App/Steaming.App.csproj -c Release` succeeded

What changed:
- `CameraCaptureService` no longer does an unnecessary `SoftwareBitmap.Copy(...)` when the camera frame is already BGRA8/Ignore. It now converts only when needed.
- Added `CameraCaptureService.TryCopyLatestFrame(...)` so the UI preview can copy the current frame into a reusable page-owned buffer instead of cloning a brand-new byte array every preview tick.
- `AvatarViewModel.StartFaceTrackingAsync()` now uses a persisted tracker FPS cap instead of hardcoding 30. This pass clamps face tracking to 15 FPS to cut background load.
- `FaceTrackingSettings.FpsCap` default changed from 30 to 15.
- `AvatarViewModel.OnFaceTrackingFrame(...)` now throttles diagnostics/debug/status property churn to 5 Hz (`200 ms`) instead of updating multiple long UI strings every tracker frame.
- `AvatarPage` preview timer changed from `50 ms` to `100 ms`.
- `AvatarPage` now reuses one camera preview buffer and reuses overlay visuals (face rectangle, status label, landmark dots) instead of clearing/recreating XAML children every refresh.
- `AvatarViewModel.TryGetTrackingOverlay(...)` now returns the raw landmark float array directly instead of allocating a new `Vector2[]` on every preview refresh.

What currently still works and was intentionally preserved:
- Camera frames still reach the tracker.
- Face tracking still retargets into `AvatarRenderService`.
- Camera preview and landmark overlay still render on the Avatar page.
- Both WinUI and WPF still compile after the shared service/viewmodel edits.

Runtime testing still needed:
- Measure actual CPU/GPU/process impact before vs after while gaming/OBS are running.
- Verify that 15 FPS tracking still feels acceptable for head/mouth responsiveness.
- Verify the reused overlay visuals do not leave stale dots/labels when tracking is lost or camera stops.
## Session 2026-06-22 - alert editor text drag + layer hide/lock

Build-verified: `dotnet build Steaming.WinUI/Steaming.WinUI.csproj -c Release` passed, 0 errors.
Runtime UNVERIFIED.

What changed:
- Text drag path in `Steaming.WinUI/AlertEditorWindow.xaml.cs` was fixed in two parts: text now has a full hit-testable surface (`Transparent` root canvas, inner text grids not hit-testable), and move-drag no longer rebuilds the full text visual tree on every pointer move.
- Move drag capture path was also hardened so element controls receive the drag move/release flow.
- Added `Hidden` and `Locked` flags to `Steaming.Core.Models.AlertElement`. Existing layouts deserialize with both false.
- Added `Hide/Show` and `Lock/Unlock` buttons in the alert editor Layers pane.
- Hidden elements do not render in the editor canvas and do not show a selection overlay.
- Locked elements still render but are not hit-testable/draggable/resizable/rotatable in the canvas.
- Layer list labels now show `[H]` and `[L]` state markers.

Runtime still needed:
1. Verify text elements now drag cleanly from anywhere inside their box, not just over glyphs.
2. Create overlapping elements and confirm `Hide/Show` lets lower layers be selected/edited.
3. Confirm `Lock/Unlock` prevents canvas edits without hiding the element.
4. Save/reopen an alert layout and confirm `Hidden` / `Locked` round-trip through JSON as expected.
