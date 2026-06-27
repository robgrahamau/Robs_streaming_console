# YouTube Live integration — implementation plan

Status: **PLAN ONLY. No code written.** Drafted 2026-06-26 on branch `feature/music-player`.

Goal: add **YouTube Live** as a third streaming platform alongside Twitch and Kick, using
**only Google's official APIs** (YouTube Data API v3 / YouTube Live Streaming API + Google OAuth
2.0). Reach feature parity with what Twitch/Kick already do, wherever YouTube's official API allows
it.

This plan is grounded in the existing code (file/line references below) and in the official Google
docs fetched 2026-06-26. **Every API/OAuth fact in this document was taken from the official docs,
not from memory** (per CLAUDE.md rule 13). Where a fact could not be pinned down in the official
docs (notably exact live-chat quota costs), it is explicitly flagged as **MUST VERIFY** rather than
asserted.

---

## 0. The one fact that shapes the whole design

**YouTube has no EventSub/webhook equivalent for chat-time events.** Twitch gives us a push socket
(`TwitchEventSubClient`) with separate `channel.follow` / `channel.cheer` / `channel.subscribe` /
`channel.raid` notifications. YouTube does **not**. Instead:

- **Almost every live event arrives through ONE feed:** `liveChatMessages`. Chat text, Super Chats,
  Super Stickers, new members, member milestones, and gifted memberships are all items in that feed,
  distinguished by `snippet.type`. We demultiplex them ourselves.
- **Stats (concurrent viewers, subscriber count, live status, title/category) are polled** from
  `liveBroadcasts` / `videos` / `channels` — exactly like `StreamDataService` already polls Twitch
  Helix and Kick.

Consequence: the YouTube integration is **two services**, mirroring the existing split:

| Existing (Twitch)                 | New (YouTube)                       | Role |
|-----------------------------------|-------------------------------------|------|
| `TwitchAdapter` (chat in/out)     | `YouTubeLiveChatService`            | Poll/stream the live-chat feed, send messages, demux events onto the `EventBus` |
| `TwitchEventSubClient` (events)   | *(folded into the chat service)*    | YouTube events ARE chat-feed items — no separate client |
| `StreamDataService` (Helix poll)  | extend `StreamDataService`          | Concurrent viewers, sub count, live status, title/category |

---

## 1. Verified API facts (official docs, 2026-06-26)

### 1.1 OAuth 2.0 — desktop/installed app flow
Source: `developers.google.com/identity/protocols/oauth2/native-app`

- **Client type:** "Desktop app" (installed application). Client secret is **optional/not secret**
  for installed apps — PKCE is what secures the flow.
- **PKCE is mandatory.** Generate a `code_verifier` + S256 `code_challenge` per authorization.
  (We already do exactly this for Kick — see `PlatformSessionFlowService.GeneratePkceVerifier/Challenge`.)
- **Redirect URI:** loopback — `http://127.0.0.1:{port}` or `http://localhost:{port}` on a free port.
  This matches our existing `OAuthService` + `LoginWindow` (WebView2) approach. The Twitch path uses
  `http://localhost`; YouTube can use the same `PlatformAuthConfig.RedirectUri`.
- **Authorization endpoint:** `https://accounts.google.com/o/oauth2/v2/auth`
- **Token endpoint:** `https://oauth2.googleapis.com/token`
- **Refresh tokens:** returned for installed apps. To *guarantee* one, send `access_type=offline`
  and `prompt=consent`. Google access tokens expire in ~1 hour, so a **refresh-token flow is
  mandatory** (see §5). This is more important than Twitch, whose implicit token we currently just
  let expire.

### 1.2 Getting the live broadcast + chat id
Source: `liveBroadcasts/list`

- `GET liveBroadcasts?part=snippet,contentDetails&broadcastStatus=active&mine=true`
- `broadcastStatus` ∈ `active | upcoming | completed | all`. `mine=true` = the authed channel's own
  broadcasts (no broadcast id needed).
- `snippet.liveChatId` → the id we feed to every `liveChatMessages` call.
- Scopes accepted: `youtube.readonly`, `youtube`, or `youtube.force-ssl`.
- **Fallback path:** if `liveBroadcasts` returns nothing (e.g. the user streams via OBS/RTMP without
  a "broadcast" resource in some edge cases), resolve via
  `videos.list?part=liveStreamingDetails&id={videoId}` → `liveStreamingDetails.activeLiveChatId` and
  `liveStreamingDetails.concurrentViewers`. Keep both paths.

### 1.3 Reading chat + events
Source: `liveChatMessages/list` and `liveChatMessages/streamList`

- `GET liveChatMessages?liveChatId={id}&part=id,snippet,authorDetails`
- Response gives `pollingIntervalMillis` (**honour it** — do not poll faster) and `nextPageToken`
  (pass back as `pageToken`). First call returns recent history; subsequent calls page forward.
- **`liveChatMessages.streamList` is the newer, recommended method** — it pushes new messages and
  "reduces the need for constant polling and helps avoid exceeding your quota." Prefer it; keep
  `.list` polling as the compatibility fallback.
- `authorDetails` gives `displayName`, `channelId`, `profileImageUrl`,
  `isChatOwner/isChatModerator/isChatSponsor/isVerified` → maps cleanly to our `StreamUser`
  (`IsBroadcaster`/`IsMod`/`IsSubscriber`...). **This is how we get YouTube badges/roles.**

**`snippet.type` values we demux** (the YouTube → `EventType` mapping, §3):
`textMessageEvent`, `superChatEvent`, `superStickerEvent`, `newSponsorEvent` (new member),
`memberMilestoneChatEvent`, `membershipGiftingEvent`, `giftMembershipReceivedEvent`,
`messageDeletedEvent`, `userBannedEvent`, `pollEvent`. **MUST VERIFY** the exact set + field names
against `liveChatMessages` resource docs during implementation (these are the documented types as of
the fetch, but field shapes must be confirmed before parsing).

### 1.4 Sending chat
Source: `liveChatMessages/insert`

- `POST liveChatMessages?part=snippet` with body
  `{ snippet: { liveChatId, type: "textMessageEvent", textMessageDetails: { messageText } } }`
- Scope: `youtube` or `youtube.force-ssl`.

### 1.5 Donations (Super Chat / Super Stickers)
- Arrive in the chat feed as `superChatEvent` / `superStickerEvent` (preferred — single feed).
- Also available historically via `superChatEvents.list`. We will read them off the chat feed to
  avoid a second polling loop and extra quota.
- Fields include amount (micros), currency, and a display string → maps to our donation/`Bits` path.

### 1.6 Stats
- **Concurrent viewers:** `liveBroadcasts`/`videos` `liveStreamingDetails.concurrentViewers`.
- **Subscriber count:** `channels.list?part=statistics&mine=true` → `statistics.subscriberCount`.
  **Caveat:** YouTube rounds/abbreviates this and channels can hide it. Treat as approximate; if
  `hiddenSubscriberCount` is true, show "—" (do NOT show a fake 0 — same rule as CLAUDE.md #18).
- **Title/category:** `liveBroadcasts.snippet.title` + `videos.snippet.categoryId`.

### 1.7 Moderation
- Ban/timeout: `liveChatBans.insert` (`type: "permanent"` or `"temporary"` + `banDurationSeconds`),
  unban: `liveChatBans.delete`.
- Delete a message: `liveChatMessages.delete?id={messageId}`.
- Scope: `youtube.force-ssl`.
- Maps onto `ModerationService` as a third platform branch (§6).

### 1.8 Quota — THE limiting constraint (read this before building)
Source: `determine_quota_cost` + `getting-started`

- Default project quota: **10,000 units/day combined** for all endpoints (plus 100 `search.list`
  and 100 `videos.insert` separately).
- The public quota calculator lists `list` reads at 1 unit and writes (`insert`) much higher, but
  **does NOT enumerate the live-chat methods' costs**. Historically `liveChatMessages.list` has cost
  more than a plain list read. **MUST VERIFY exact cost in the Google Cloud Console quota dashboard
  during implementation — do not hard-code an assumption.**
- **Design implications (regardless of the exact number):**
  - Use **`streamList`** (push) over `.list` polling wherever possible.
  - **Always honour `pollingIntervalMillis`** — never poll on a fixed fast timer.
  - **Only poll while live.** Stop all YouTube polling the moment the broadcast ends (mirror
    `StreamDataService`'s live-gating and `ChatbotService.IsLive`).
  - Poll stats (`channels`/`liveBroadcasts`) on the existing 30s `StreamDataService` cadence, not
    faster.
  - Sending chat (chatbot timers/commands/announcements) consumes write quota — the existing
    cooldown/interval machinery in `ChatbotService` already limits this; keep YouTube on the same
    `BotReplyTarget` gating.
  - Surface a **quota-exceeded (HTTP 403 `quotaExceeded`)** state to the user as a service-status
    warning, exactly like the Twitch/Kick 401 handling in `StreamDataService` — degrade, don't crash.
  - For a serious multi-platform streamer, the user may need to **request a YouTube API quota
    increase** from Google. Document this in the UI/help text; it is out of our code's control.

---

## 2. Feature parity matrix (what we have → what YouTube can do)

| Feature (Twitch/Kick today) | YouTube official-API support | Plan |
|---|---|---|
| Live chat read | ✅ `liveChatMessages.streamList`/`.list` | `YouTubeLiveChatService` |
| Send chat (bot/commands/timers) | ✅ `liveChatMessages.insert` | wire into `ChatbotService` send delegate + add `BotReplyTarget.YouTube` |
| Chat overlay (OBS) | ✅ already platform-agnostic | publish `EventType.Chat` with `Platform.YouTube`; renderer needs a YT badge colour |
| Emotes in chat | ⚠️ YouTube custom/member emojis are text shortcodes; no per-message emote image map like Twitch | Phase 2 — render text only first; YT emoji image resolution is a stretch goal |
| Viewer count | ✅ `liveStreamingDetails.concurrentViewers` | extend `StreamDataService` |
| Subscriber count | ✅ `channels.statistics.subscriberCount` (rounded; may be hidden) | extend `StreamDataService`; respect hidden flag |
| New follower events | ❌ **No realtime per-event subscriber/follow notification in the API** | Not possible. Show count deltas only; no per-subscriber alert (documented limitation) |
| New member / "sub" events | ✅ `newSponsorEvent` in chat feed | map → `EventType.Subscribe` |
| Member milestone | ✅ `memberMilestoneChatEvent` | map → `EventType.Subscribe` (with months) |
| Gifted memberships | ✅ `membershipGiftingEvent` / `giftMembershipReceivedEvent` | map → `EventType.GiftSubscribe` |
| Bits/donations | ✅ Super Chat / Super Sticker | map → `EventType.Bits` (donation path) |
| Raids (incoming) | ❌ **No public incoming-raid API** (same gap as Kick) | Not supported. Document it. |
| Polls | ✅ `pollEvent` (read) / poll via `insert` | Phase 3, optional |
| Moderation (ban/timeout/delete) | ✅ `liveChatBans` + `liveChatMessages.delete` | extend `ModerationService` |
| Stream title/category | ✅ read via `liveBroadcasts`/`videos`; update via `liveBroadcasts.update` | read in Phase 1; update in `StreamManagementService` Phase 3 |
| Analytics per-platform | ✅ we own this | add YouTube columns to analytics DB (§7) |
| Going-live announce | ✅ uses chat send | works once chat send is wired |

**Two hard "no"s to set expectations now (and tell Rob):** YouTube's official API exposes **no
realtime new-subscriber event** and **no incoming-raid event**. Everything else has parity.

---

## 3. Event mapping (YouTube chat-feed `snippet.type` → our `EventType`)

`YouTubeLiveChatService` parses each `liveChatMessages` item and publishes a `StreamEvent` with
`Platform.YouTube`. The existing `OverlayDispatcher`, `SoundDispatcher`, `ChatbotService`,
`AnalyticsCollectorService`, and `MainViewModel.OnEvent` are already platform-agnostic on
`EventType`, so they light up for YouTube automatically once events flow.

| YouTube `snippet.type` | `EventType` | Data dict keys (match existing consumers) |
|---|---|---|
| `textMessageEvent` | `Chat` | `message`, `color`, `messageId`, `isBroadcaster`, `isModerator`, `isSubscriber` (=isChatSponsor), `emotes` (empty Phase 1), `badgePaths` (empty/colour pill Phase 1) |
| `superChatEvent` | `Bits` | `bits` (or donation amount), `amount`, `currency`, `message` — reuse the Kick `KicksGifted`→donation idiom in `OverlayDispatcher` |
| `superStickerEvent` | `Bits` | same as Super Chat (+ sticker id, ignored for now) |
| `newSponsorEvent` | `Subscribe` | `months` = "1" |
| `memberMilestoneChatEvent` | `Subscribe` | `months` = milestone months |
| `membershipGiftingEvent` | `GiftSubscribe` | `count` = gift count |
| `giftMembershipReceivedEvent` | *(suppressed)* | dedupe — the gifting event already fired the alert; do not double-count (same lesson as the Kick pending→accepted dedupe) |
| `messageDeletedEvent` | *(internal)* | remove from chat overlay (reuse `PipeMessageType.Clear`-style per-message delete if available; else ignore) |
| `userBannedEvent` | *(internal)* | optional activity-feed line |
| `pollEvent` | `Poll` | Phase 3 |

`StreamUser` fields come from `authorDetails`: `Id`=channelId, `Username`/`DisplayName`=displayName,
`AvatarUrl`=profileImageUrl, `IsBroadcaster`=isChatOwner, `IsMod`=isChatModerator,
`IsSubscriber`=isChatSponsor.

---

## 4. Concrete code changes

### 4.1 Models — `Steaming.Core/Models/StreamEvent.cs`
- Add `YouTube` to `enum Platform { Twitch, Kick, Steam, YouTube }`.
  ⚠️ **Audit every `switch`/`if` on `Platform`** before adding — there are 13 files matching
  `Platform.Twitch|Kick|Steam` (grep result). Each platform switch that currently throws/ignores on
  unknown must get a YouTube branch or a safe default. Key ones: `ChatMessageItem` (chat colour/icon),
  `MainViewModel.OnEvent`, `OverlayDispatcher`, `ModerationService`, `StreamDataService`,
  `AnalyticsCollectorService`, `ChatbotService`.
- No new `EventType` values needed for Phase 1 (reuse Chat/Bits/Subscribe/GiftSubscribe). Add `Poll`
  is already present.

### 4.2 Credentials — `Steaming.Core/Auth/TokenStore.cs`
Add to `StoredCredentials`:
```
YouTubeAccessToken, YouTubeRefreshToken, YouTubeTokenExpiry (DateTimeOffset?),
YouTubeChannelId, YouTubeChannelTitle, YouTubeClientId, YouTubeClientSecret,
BotYouTubeAccessToken, BotYouTubeRefreshToken, BotYouTubeUsername
```
(DPAPI-encrypted store already handles arbitrary fields — just add properties.)

### 4.3 Auth config — `Steaming.Core/Configuration/PlatformAuthConfig.cs`
Add `YouTubeClientId` + `YouTubeClientSecret` (from a Google Cloud OAuth "Desktop app" client the
user/Rob creates). Reuse existing `RedirectUri`. **Do not hard-code a client secret that must stay
secret** — installed-app secrets are non-confidential, but still keep it here alongside the existing
Kick secret for consistency.

### 4.4 OAuth flow — `Steaming.Application/Services/PlatformSessionFlowService.cs`
Add `CreateYouTubeLoginRequest()` + `CreateYouTubeBotLoginRequest()`:
- Auth URL: `https://accounts.google.com/o/oauth2/v2/auth?client_id=...&redirect_uri=...&response_type=code&scope=...&access_type=offline&prompt=consent&code_challenge=...&code_challenge_method=S256&state=...`
- Scopes (space-joined): `https://www.googleapis.com/auth/youtube.force-ssl`
  (covers chat read+send+moderation) and `https://www.googleapis.com/auth/youtube.readonly` if a
  read-only split is wanted. PKCE verifier/challenge reuse the existing helpers.
- **Auth-code flow (not implicit)** — like Kick, `isFragment: false` in `LoginWindow`.

### 4.5 Token exchange/refresh — `Steaming.Application/Services/PlatformCredentialService.cs`
Add, mirroring the Kick methods:
- `ExchangeYouTubeCodeAsync(code, verifier, clientId, clientSecret, redirectUri)` →
  `POST https://oauth2.googleapis.com/token` (grant_type=authorization_code). Store access+refresh+expiry.
- `RefreshYouTubeTokenAsync()` → `POST .../token` (grant_type=refresh_token). **Call proactively when
  `YouTubeTokenExpiry` is near** AND reactively on 401 (Google tokens last ~1h — refresh is not
  optional). Fire a `YouTubeTokenRefreshed` event like the existing `KickTokenRefreshed` so dependent
  callers re-bootstrap.
- `FetchYouTubeChannelInfoAsync(token)` → `channels.list?part=snippet&mine=true` → channelId + title.
- `SaveYouTubeLogin / ClearYouTubeLogin / SaveYouTubeBotLogin / ClearYouTubeBotLogin` +
  extend `SavedPlatformAuthState` / `BotAuthState` records with YouTube fields.

### 4.6 NEW — `Steaming.Core/Platforms/YouTubeLiveChatService.cs`
The core component. Responsibilities:
1. Resolve `liveChatId` via `liveBroadcasts.list?broadcastStatus=active&mine=true` (fallback to
   `videos.list` activeLiveChatId).
2. Open the chat feed: prefer `liveChatMessages.streamList`; fall back to `.list` honouring
   `pollingIntervalMillis` + `nextPageToken`.
3. Demux each item per §3 → `EventBus.PublishAsync(new StreamEvent(Platform.YouTube, ...))`.
4. `SendMessageAsync(message)` via `liveChatMessages.insert` (broadcaster account; bot account if a
   separate bot token is connected — mirror `TwitchAdapter`'s bot/broadcaster fallback).
5. Robust failure handling (the Kick lesson): every HTTP call guarded; 401 → trigger token refresh
   and retry once; 403 `quotaExceeded` → stop polling, raise a status event, back off; 404/no active
   broadcast → idle and re-resolve periodically; cancellation-safe; auto-reconnect with capped
   backoff. **Never let a fire-and-forget send fault escape** (observe the task — see the v0.10.59
   `ChatbotService` fix and `TwitchAdapter.SendMessageAsync`).
6. Expose `IsConnected`, `ChannelTitle`, `StatusChanged` for the Connections page.

Threading: publish onto the `EventBus` only; never touch the UI thread directly (the COMException
freeze lesson). All `StreamDataService`/VM mutations already marshal through the dispatcher.

### 4.7 Stats — extend `Steaming.Core/Services/StreamDataService.cs`
- Add `_youtubeViewerCount`, `_youtubeSubscriberCount`, `YouTubeIsLive`, `YouTubeStreamTitle/Category`,
  `YouTubeAuthStatusChanged`, `YouTubeTokenRefreshed` (mirror the Kick members exactly).
- Add `PollYouTubeCountsAsync()` called from `PollOnceAsync()` alongside `PollKickCountsAsync()`:
  - `liveBroadcasts.list?part=snippet,contentDetails&broadcastStatus=active&mine=true` → live flag,
    title, concurrentViewers (via contentDetails/videos), liveChatId (hand to the chat service).
  - `channels.list?part=statistics&mine=true` → subscriberCount (respect `hiddenSubscriberCount`).
- Fold YouTube into `IsLive`, `RecalculateTotals()` (viewer/sub totals), and `PublishUpdatedAsync()`
  (add `youtubeIsLive`, etc. to the data dict). Apply the **2-consecutive-empty-polls debounce**
  already used for Twitch so a single failed poll doesn't flip YouTube offline.
- **Token-failure latch pattern:** copy the `_kickAuthFailedToken` idiom (pause polling on a token
  that 401'd and could not be refreshed; auto-resume when a new token is stored). Do NOT use a
  permanent bool latch (the documented Kick regression).

### 4.8 App wiring — `Steaming.WinUI/App.xaml.cs`
- Register `services.AddSingleton<YouTubeLiveChatService>()`.
- Add a `// ── YouTube auto-connect ──` block mirroring the Twitch block (~line 355): if a stored
  YouTube token exists, refresh if needed, connect the chat service, start polling, configure
  moderation, set `vm.SetYouTubeLoggedIn(title)`, update service-status tiles.
- Extend the unified chatbot **send delegate** (~line 305) so `BotReplyTarget.Both/YouTube` routes to
  `YouTubeLiveChatService.SendMessageAsync`.
- Extend the persisted-activity description switch (~line 212) and `MainViewModel.OnEvent` for any
  YouTube-specific labels (Super Chat wording).
- Wire `YouTubeTokenRefreshed` → re-bootstrap the chat service (analogous to the Kick handler).

### 4.9 Chatbot — `Steaming.Core/Services/ChatbotService.cs`
- Add `YouTube` to `enum BotReplyTarget { Both, Twitch, Kick, YouTube }`.
- Add a `evt.Platform == Platform.YouTube` chat-command branch (copy the Kick branch at line 312;
  YouTube commands route replies to `BotReplyTarget.YouTube`).
- Shout matching already keys off `EventType` + a per-target check — add the YouTube target case in
  the `matching` predicate (line 337).
- Auto-mod is currently Twitch-only (line 278). Decide in Phase 2 whether to extend auto-mod to
  YouTube (needs `liveChatMessages.delete` + `liveChatBans.insert`, both already planned in §6).

### 4.10 Moderation — `Steaming.Core/Services/ModerationService.cs`
- Add `ConfigureYouTube(token, liveChatId)` + `ClearYouTube()` + a `Platform.YouTube` case in
  `CanModerate`.
- Add YouTube branches to `TimeoutAsync` (`liveChatBans.insert` temporary +
  `banDurationSeconds`), `BanAsync` (`liveChatBans.insert` permanent), `DeleteMessageAsync`
  (`liveChatMessages.delete?id=`). Use the per-request `HttpRequestMessage` pattern already there.
- Note: YouTube ban/delete are keyed by **liveChatId + the message/author resource id from the chat
  feed**, not a global user id — store the needed ids on the chat event so the VM can pass them back.

### 4.11 UI — `Steaming.WinUI/Pages/ConnectionsPage.xaml(.cs)`
- Add a **"YouTube" card** between Kick and Kick Bridge, copying the Twitch card markup (status
  label, dot, Connect button). Add `YouTubeLogin_Click` mirroring `TwitchLogin_Click` (line 117):
  build the request via `_flows.CreateYouTubeLoginRequest()`, open `LoginWindow("Login with YouTube",
  ..., isFragment: false, profileName: "YouTube")`, exchange the code, run post-connect startup.
- Add a YouTube bot card if/where the Twitch/Kick bot login lives.
- `MainViewModel`: add `IsYouTubeLoggedIn`, `YouTubeUsername`, `SetYouTubeLoggedIn`,
  `LogoutYouTubeAsync`, `CompleteYouTubeLoginAsync`, `IsYouTubeBotConnected`, etc., mirroring the
  Twitch members.
- Dashboard/health tiles: add YouTube viewer/sub/live indicators wherever Twitch+Kick are shown
  (rule 17 — per-platform from day one; never combined-only).

### 4.12 Chat rendering — `Steaming.Application/ViewModels/ChatMessageItem.cs` + OBS
- Add a YouTube platform colour/icon in `ChatMessageItem` (currently switches Twitch/Kick).
- The OBS chat payload (v3) is platform-string-driven (`[2+N] platform`); confirm the C++
  `chat_source.cpp` handles an arbitrary/"YouTube" platform string (likely just a colour pill).
  **If the C++ side hard-codes only "Twitch"/"Kick"**, that is a wire-format change touching both
  sides (CLAUDE.md rule 8) — scope it explicitly. Phase 1 can ship with YouTube chat showing the
  default pill colour and refine the badge later.

---

## 5. OAuth token lifecycle (do not skip — YouTube tokens expire hourly)

1. Login (Connections page) → auth-code + PKCE → `ExchangeYouTubeCodeAsync` → store
   access + **refresh** + expiry.
2. Before any YouTube API call, if `now >= YouTubeTokenExpiry - 60s` → `RefreshYouTubeTokenAsync`
   first.
3. On any `401` → refresh once, retry once; if refresh fails, latch (the `_kickAuthFailedToken`
   pattern) and surface "YouTube login expired — reconnect".
4. Persist the rotated refresh token if Google returns a new one.
5. `YouTubeTokenRefreshed` event re-bootstraps the chat service + moderation config with the new
   token (mirror `KickTokenRefreshed` at App.xaml.cs ~line 349).

---

## 6. Analytics (DB) — `Steaming.Data/AnalyticsRepository.cs` + `ARCHITECTURE.md` schema

The schema is currently **hard-coded to two platforms** (`twitch_*` / `kick_*` columns on
`stream_sessions`; `twitch_viewers`/`kick_viewers` on `viewer_snapshots`). Adding YouTube means:

- New columns: `youtube_peak_viewers`, `youtube_avg_viewers`, `youtube_sample_count` on
  `stream_sessions`; `youtube_viewers` on `viewer_snapshots`. Use the existing additive
  `PRAGMA table_info` guard (v0.10.51) so startup doesn't throw on old DBs.
- `AnalyticsCollectorService`: add a **third independent session** (`_youtubeSessionId` +
  `_youtubeSessionStart` + `_youtubeSessionChatters` + `_youtubeOffStreak`) mirroring the Twitch/Kick
  pair (the v0.10.60 resume-not-duplicate logic must be replicated for YouTube). "Both" becomes a
  query concept across up to three platforms; the platform-pairing/report layer needs review.
- **CLAUDE.md #18 / #1:** new columns default to 0 — never display `Y:0` as real data; back-fill or
  fall back. **Commit + back up the DB before any migration** (#3).
- This is the **largest non-API change** and is its own phase. Do not bolt it on hastily.

---

## 7. Phased implementation (each phase builds + is independently shippable)

**Phase 0 — prerequisites (Rob/setup, no app code):**
Create a Google Cloud project, enable **YouTube Data API v3**, create an **OAuth Desktop-app
client** (client id + secret), add the loopback redirect, put the channel's Google account on the
OAuth consent screen test users (or publish it). Note the 10,000-unit/day quota and the increase-
request process.

**Phase 1 — read + connect (core value):**
Platform enum + credentials + auth config + OAuth login (§4.1–4.5, 4.11), `YouTubeLiveChatService`
chat **read** + event demux (§4.6, §3), `StreamDataService` stats (§4.7), App wiring + Connections
card. Chat appears in-app and in OBS (default pill); Super Chats/new members/gifts fire existing
alerts; viewer/sub counts show. **No send, no moderation, no analytics columns yet.**

**Phase 2 — send + moderation + chatbot:**
`liveChatMessages.insert` send (§4.6), `BotReplyTarget.YouTube` (§4.9), `ModerationService` YouTube
(§4.10), going-live announce works. Optional: extend auto-mod to YouTube.

**Phase 3 — analytics + polish:**
Analytics 3rd-platform columns + session logic (§6), YouTube chat badges/emoji, stream
title/category update via `liveBroadcasts.update`, polls (`pollEvent`/poll insert), Super Sticker
art.

---

## 8. Build / verify gates (CLAUDE.md #11, #3)

- `dotnet build Steaming.WinUI/Steaming.WinUI.csproj -c Release` AND `-c Debug` — 0 errors each.
- No C++ change in Phase 1 unless the chat payload platform handling forces it (§4.12) — if so,
  change `ChatPayload.cs` + `chat_source.cpp` together (rule 8) and build the plugin
  (`cmake --build build_x64 --config RelWithDebInfo`).
- Bump `Steaming.Core/Steaming.Core.csproj` version before each commit (#B4).
- **Runtime is unverifiable without a live YouTube broadcast + a Google OAuth client** — any "done"
  claim is "build-verified only — runtime unverified" until Rob tests against a real live stream.
- Ask Rob before adding any NuGet package. **Recommendation: hand-roll the HTTP/JSON calls** (as the
  Twitch/Kick code already does with `HttpClient` + `System.Text.Json`) rather than pulling in
  `Google.Apis.YouTube.v3` — it keeps parity with the existing code style and avoids a large
  dependency, but confirm with Rob (the `Google.Apis` client does handle token refresh + quota
  retries for us, which is the one argument for it).

---

## 9. Open questions for Rob (decide before Phase 1)

1. **Google client library vs hand-rolled HTTP?** (§8) — affects effort and dependency footprint.
2. **One Google login or broadcaster + separate bot account?** (We support bot accounts for
   Twitch/Kick; YouTube bot = a second Google channel.)
3. **Quota:** is the default 10k/day acceptable to start, or should we request an increase up front?
   This caps how aggressively we can poll if `streamList` isn't usable.
4. **Set expectations:** confirm Rob accepts that **YouTube has no realtime new-subscriber event and
   no incoming-raid event** (official-API limitations, not our omission).

---

## 10. Risks / pitfalls (from past mistakes in MISTAKES.md / HANDOFF.md)

- **Quota exhaustion** mid-stream → chat dies silently. Mitigate: `streamList`, honour
  `pollingIntervalMillis`, poll-only-while-live, surface 403 as a status warning.
- **Token expiry** (1h) → must refresh proactively; don't copy Twitch's "let it expire" approach.
- **Cross-thread VM mutation** from the chat service → COMException freeze (v0.10.59). Publish to the
  bus only.
- **Fire-and-forget send faults** → finalizer crash (v0.10.59). Observe every send task.
- **Analytics duplicate/zero columns** (v0.10.60, rule #18) → replicate resume-not-duplicate and
  never display unpopulated zeros.
- **Platform switch fall-through** — adding `Platform.YouTube` without updating all 13 switch sites
  could silently drop YouTube events. Audit each.
```
