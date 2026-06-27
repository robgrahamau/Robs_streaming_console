# Hype Train

## Goal

Add a cross-platform "Hype Train" system that:

- uses the existing event + alert pipeline for one-shot moments
- supports Twitch and Kick from day one
- tracks names, contribution amounts, levels, and timeout windows
- shows persistent live progress in OBS without blocking or replacing normal alerts
- is fully transparent / invisible when no train is active

## What Already Works

These parts already exist and should be reused, not replaced:

- `StreamEvent` already carries the incoming platform events.
- `OverlayDispatcher` already turns event moments into `RenderAlertV2` alerts.
- `EventConfig` + `AlertLayout` already let the user customize alert text, sound, duration, and layout.
- Labels/goals already prove the app can drive persistent OBS-side state separately from transient alerts.

That means the Hype Train should plug into the current event system, not invent a second alert stack.

## High-Level Design

The feature should have two outputs:

1. Transient hype alerts
- "Hype Train Started"
- "Level 2 Reached"
- "Train Completed"
- "Train Expired"
- optionally "Big contribution"

These should go through the existing `OverlayDispatcher` / `RenderAlertV2` path so they behave like the rest of the alert system.

2. Persistent hype widget
- current level
- current points / target
- time remaining
- last contributor name
- optional recent top contributors

This should use its own OBS source so it does not interfere with follow/sub/raid alerts. When inactive, it should render nothing at all.

## Why It Needs Its Own OBS Source

A Hype Train is not just one event. It is a timed state machine:

- starts
- accumulates progress
- levels up
- expires or completes

Trying to show that through only transient alerts would be wrong because:

- progress would disappear between contributions
- the bar/timer would fight with unrelated alerts
- overlapping alerts would make the train feel broken

So the correct split is:

- existing alert system for moments
- dedicated OBS source for live train state

The source must be transparent when inactive:

- no background
- no placeholder text
- no idle animation
- zero visible output

## User Experience

### When inactive

- no widget visible in OBS
- no CPU-heavy animation loop doing unnecessary work
- no interaction with other alerts

### When started

- a normal alert fires: "Hype Train Started"
- the Hype Train source becomes visible
- it shows bar, level, timer, and contributor text

### During progress

- new qualifying events add points
- the persistent source updates immediately
- optional mini-alerts may fire for major contributions

### On level up

- the persistent source updates to the next level target
- a one-shot alert fires through the normal alert system

### On complete

- completion alert fires
- source can either:
  - hold for a short configured victory period, then hide
  - or hide immediately if the user prefers

### On timeout

- expiry alert may fire
- source fades/hides and becomes fully transparent again

## Data Model

### Config

Add a new `HypeTrainConfig` to settings.

Suggested fields:

```csharp
public sealed class HypeTrainConfig
{
    public bool Enabled { get; set; } = false;

    // Per-platform enablement
    public bool TwitchEnabled { get; set; } = true;
    public bool KickEnabled { get; set; } = true;

    // A separate train per platform avoids mixing Twitch + Kick into one fake pool.
    public bool RunSeparatePerPlatform { get; set; } = true;

    // Lifetime
    public int StartWindowSeconds { get; set; } = 300;
    public int ExtendWindowSeconds { get; set; } = 120;
    public int MaxWindowSeconds { get; set; } = 900;
    public int VictoryHoldSeconds { get; set; } = 8;

    // Scoring
    public int TwitchBitsPerPoint { get; set; } = 100;
    public int TwitchSubPoints { get; set; } = 5;
    public int TwitchGiftSubPoints { get; set; } = 5;
    public int KickGiftedPointsPerUnit { get; set; } = 1;
    public int KickSubPoints { get; set; } = 5;
    public int KickGiftSubPoints { get; set; } = 5;
    public int RaidBonusPoints { get; set; } = 0;

    // Thresholds
    public List<int> LevelTargets { get; set; } = new() { 25, 50, 100, 150, 250 };

    // Alert behavior
    public bool AlertOnStart { get; set; } = true;
    public bool AlertOnLevelUp { get; set; } = true;
    public bool AlertOnComplete { get; set; } = true;
    public bool AlertOnExpire { get; set; } = false;
    public bool AlertOnBigContribution { get; set; } = false;
    public int BigContributionThreshold { get; set; } = 20;

    // Widget text
    public string TitleTemplate { get; set; } = "{platform} Hype Train";
    public string LastContributorTemplate { get; set; } = "{user} +{amount}";
    public string CompleteText { get; set; } = "Completed!";
}
```

### Runtime State

The train itself should be runtime state, not an alert config.

```csharp
public sealed class HypeTrainState
{
    public Platform Platform { get; set; }
    public bool IsActive { get; set; }
    public int Level { get; set; }
    public int CurrentPoints { get; set; }
    public int TargetPoints { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public string LastContributorName { get; set; } = "";
    public int LastContributionPoints { get; set; }
    public string LastContributionType { get; set; } = "";
    public List<HypeContributor> TopContributors { get; set; } = new();
}

public sealed class HypeContributor
{
    public string Name { get; set; } = "";
    public int Points { get; set; }
}
```

## Service Design

Add a dedicated `HypeTrainService`.

Responsibilities:

- subscribe to `EventBus`
- decide which incoming events count toward hype
- start a train when the first qualifying event lands
- maintain timer, level, progress, and contributors
- publish hype-related events for the alert path
- push live state to the dedicated OBS source

This should live alongside the existing application services, not in WinUI code-behind.

### Input Events

Suggested inputs:

- Twitch
  - `Bits`
  - `Subscribe`
  - `GiftSubscribe`
  - optionally `Raid`

- Kick
  - `KicksGifted`
  - `Subscribe`
  - `GiftSubscribe`
  - optionally `Raid`

Do not mix Twitch and Kick into one combined train by default. This app is dual-platform and should keep platform identity explicit.

## Event Flow

### Start

1. A qualifying event arrives.
2. `HypeTrainService` checks config and platform.
3. If no active train exists for that platform, create one.
4. Publish a hype-start event.
5. Push visible state to the Hype Train OBS source.
6. Fire a normal alert through the existing alert pipeline.

### Contribution

1. A qualifying event arrives during an active train.
2. Convert event to points.
3. Add points to current level progress.
4. Update contributor summary and last contributor fields.
5. Extend timeout if configured.
6. Push updated state to OBS source.
7. If threshold crossed, level up.

### Level Up

1. Increment level.
2. Set next target.
3. Publish a level-up hype event.
4. Fire existing alert-system alert.
5. Keep source visible.

### Completion

1. If final level target is reached, mark train complete.
2. Fire completion alert.
3. Hold visible state for configured victory period.
4. Then hide the source completely.

### Expiry

1. Timer reaches zero with no qualifying contribution.
2. Mark inactive.
3. Optionally fire expiry alert.
4. Clear OBS widget state so the source becomes transparent.

## Reusing The Existing Alert System

This should not invent a second alert editor.

Add these event-config keys:

- `HypeTrainStart`
- `HypeTrainLevelUp`
- `HypeTrainComplete`
- `HypeTrainExpire`
- optional `HypeTrainBigContribution`

Each is a normal `EventConfig`:

- enabled
- text template
- duration
- sound
- layout

That keeps editing/testing identical to Follow/Raid/Bits.

Suggested tokens:

- `{platform}`
- `{level}`
- `{amount}`
- `{target}`
- `{user}`
- `{timeleft}`

Examples:

- Start: `{platform} Hype Train started by {user}!`
- Level up: `{platform} Hype Train reached level {level}!`
- Complete: `{platform} Hype Train completed!`
- Big contribution: `{user} pushed the train with {amount}!`

## OBS Source Design

Add a dedicated source, conceptually:

- `steaming_hypetrain`

Behavior:

- fully transparent when inactive
- renders only while active or during the short completion hold
- no dependency on transient alert queue

Suggested visual fields:

- title
- progress bar
- countdown bar
- level badge
- current / target text
- countdown timer
- last contributor text

Suggested payload:

```text
[1] active
[1] platform
[2+N] title
[2+N] lastContributor
[2+N] statusText
[4] level
[4] currentPoints
[4] targetPoints
[4] secondsRemaining
[1] completed
```

If style needs to be user-editable like alerts, use an `AlertLayout`-style serialized layout for the source too. But the runtime progress values should be a separate live-state message.

## Bar + Countdown Mechanics

This is the part that should be explicit in the design.

The Hype Train source should render **two independent bars**:

1. Progress bar
- fills up as contribution points move from `0` to `TargetPoints`
- resets/retargets when the train levels up

2. Countdown bar
- shrinks as time moves from the active window down to `0`
- restores/extends when a valid contribution extends the train

Those two bars express different things:

- progress bar = "how close are we to the next level?"
- countdown bar = "how long until the train dies?"

Both should be optional style elements in the source layout, but the runtime state must support both.

## Timer Ownership

The timer should be owned by `HypeTrainService`, not by the transient alert system.

Authoritative runtime fields:

- `StartedAt`
- `EndsAt`
- `CurrentPoints`
- `TargetPoints`
- `Level`
- `IsActive`

That means:

- C# decides when the train starts
- C# decides when contributions extend the timeout
- C# decides when the train expires or completes
- C++ only renders the latest known state

Do **not** try to run the hype timer purely inside a one-shot alert payload. That would make the bar/timer drift, disappear, or fight with unrelated alerts.

## Runtime Countdown Model

Recommended rules:

- on train start:
  - `StartedAt = now`
  - `EndsAt = now + StartWindowSeconds`

- on each valid contribution:
  - add points
  - extend `EndsAt` by `ExtendWindowSeconds`
  - clamp to `now + MaxWindowSeconds` if needed

- on each level up:
  - set the new target
  - keep the countdown running
  - optionally add a level-up bonus extension if desired later

- on expiry:
  - if `now >= EndsAt` and the train is not complete, expire it

- on completion:
  - mark complete
  - keep visible for `VictoryHoldSeconds`
  - then clear back to transparent/inactive

## Update Strategy

Do not send pipe traffic every render frame from C#.

Recommended split:

### Full state push

Send a full source-state update immediately on:

- train start
- contribution
- level up
- completion
- expiry

### Tick updates

While active, send a lightweight timer update at a low fixed rate:

- 4-5 times per second is enough

That is smooth enough for a shrinking countdown bar and timer text without turning the pipe into spam.

## Better Option: Let C++ Count Down Locally

The better design is to send an absolute end time, not just a remaining-seconds value.

Recommended payload fields:

```text
[1] active
[1] platform
[1] completed
[4] level
[4] currentPoints
[4] targetPoints
[8] endsAtUtcTicks
[2+N] title
[2+N] lastContributor
[2+N] statusText
```

Why this is better:

- C# only sends state when something changes
- C++ computes the remaining time every frame locally
- the countdown bar is always smooth
- less pipe chatter

Then the source computes:

- `remaining = max(0, endsAtUtcTicks - nowUtcTicks)`
- `countdownFraction = remaining / activeWindowLength`
- `progressFraction = currentPoints / targetPoints`

If you want exact source-side countdown behavior, also send:

- `windowStartUtcTicks`
- or `windowDurationSeconds`

so the source can derive the countdown fraction without guessing.

## Suggested Source State

The source should keep a cached state object something like:

```cpp
struct hype_train_state {
    bool active;
    bool completed;
    uint8_t platform;
    int32_t level;
    int32_t current_points;
    int32_t target_points;
    int64_t started_at_utc_ticks;
    int64_t ends_at_utc_ticks;
    std::string title;
    std::string last_contributor;
    std::string status_text;
};
```

Rendering logic:

- if `active == false` and not in victory hold:
  - draw nothing
- else:
  - draw progress bar from `current_points / target_points`
  - draw countdown bar from `ends_at_utc_ticks - now`
  - draw title / level / time left / contributor text

## Transparency Rules For The Source

When the train is off, the source must be truly invisible.

That means:

- no frame
- no empty bar outline
- no title shell
- no clock text
- no background alpha

The source should simply early-return from rendering when inactive.

## Required New Functionality

To support the countdown/bar behavior, the Hype Train feature needs functionality that does **not** exist in the current alert-only path:

### In C#

- `HypeTrainService`
- active-train timer/tick loop
- state transitions for start/progress/level-up/complete/expire
- new pipe messages for source-state updates

### In C++

- new `steaming_hypetrain` source
- cached live state
- render logic for:
  - progress bar
  - countdown bar
  - timer text
  - fully transparent inactive state

That is the actual answer to "where is the bar/timer/countdown stuff?":

- it belongs in the dedicated Hype Train source design
- not in the existing one-shot alert renderer

## Non-Locking Requirements

This feature must be designed so it cannot freeze the WinUI app, the OBS plugin, or the render path.

### C# timer rules

- `HypeTrainService` must own the timer on a background loop using `CancellationToken` + `Task.Delay`.
- Do **not** use a WinUI `DispatcherQueueTimer` for train state.
- Do **not** use `.Result`, `.Wait()`, or `GetAwaiter().GetResult()` anywhere in the hype train path.
- Do **not** do file I/O, network I/O, WebView work, or DB writes inside the active timer tick path.

### Pipe/update rules

- C# must never send per-frame updates.
- Full source-state updates are only sent on:
  - start
  - contribution
  - level up
  - completion
  - expiry
- If timer text needs periodic refresh from C#, use a lightweight tick at low fixed rate only.
- If the pipe is disconnected, the train state continues locally and output updates are skipped rather than retried synchronously.

### Preferred countdown design

- C# sends absolute timing state such as `StartedAtUtcTicks` and `EndsAtUtcTicks`.
- C++ computes remaining time locally during rendering.
- This avoids high-frequency C# timer spam and keeps the countdown smooth even if pipe messages arrive irregularly.

### OBS/C++ rules

- The Hype Train source renders only from cached state already received from C#.
- The source must never block waiting for pipe input during render.
- The source must never perform network I/O, file I/O, or expensive dynamic layout work every frame.
- When inactive, the source should early-return and draw nothing.
- Pipe writes must still obey the existing architecture rule: never write from OBS main/render thread.

### UI-thread rules

- WinUI only observes state changes that have already been computed by the background service.
- Any ViewModel/UI updates must be marshalled onto the UI dispatcher.
- No timer math or countdown ownership should live in code-behind.

### Coalescing / stale updates

- If multiple hype updates are queued rapidly, newer state should replace older stale timer/progress state.
- The system should prefer "latest known train state" over replaying every intermediate tick.

### Failure behavior

- If the Hype Train source fails to receive updates, it should keep rendering its last cached state until timeout/clear arrives.
- If C# fails mid-train, the feature should fail quiet:
  - no modal dialogs
  - no debugger breaks in production behavior
  - no blocking retries on the UI thread

These are mandatory implementation constraints, not optional polish.

## Transparency / Inactive State

The source must render nothing when off.

That means:

- no panel
- no bar shell
- no text
- no idle background alpha

OBS users should be able to leave the source in scenes permanently and never think about it until a train starts.

## Names / Contributors

Support names in three places:

- train starter
- last contributor
- top contributors

Minimum useful behavior:

- store `LastContributorName`
- store cumulative per-user points in a dictionary
- show top 1-3 if desired

That avoids the train feeling anonymous.

## Timeout Rules

The train needs a clear timeout model.

Recommended:

- `StartWindowSeconds`
  - initial duration when the train starts

- `ExtendWindowSeconds`
  - amount added on each valid contribution

- `MaxWindowSeconds`
  - cap so the train cannot grow forever

The live source should show the remaining time so the urgency is visible.

## Platform Rules

Recommended default behavior:

- separate train state for Twitch
- separate train state for Kick

Reason:

- the app is explicitly dual-platform
- viewers on one platform should not silently fund the train shown as if it came from the other

If a combined mode is ever wanted later, add it explicitly as a config option.

## Persistence

Persist config, not live train state.

Persist:

- enable/disable
- thresholds
- point weights
- timeout values
- alert templates/layouts
- source style settings

Do not persist the live train by default. If the app restarts mid-train, the clean behavior is usually:

- clear train
- become transparent

If restart recovery is ever needed later, add it intentionally.

## Suggested Implementation Order

### Phase 1

- add `HypeTrainConfig`
- add `HypeTrainService`
- score existing event types into train points
- track level/progress/timeout per platform

### Phase 2

- add alert config keys:
  - `HypeTrainStart`
  - `HypeTrainLevelUp`
  - `HypeTrainComplete`
  - `HypeTrainExpire`
- wire those through `OverlayDispatcher`

### Phase 3

- add dedicated OBS Hype Train source
- add runtime payload updates
- make inactive state fully transparent

### Phase 4

- add UI editor/settings:
  - thresholds
  - points per event
  - timeout lengths
  - per-platform enablement
  - source appearance

## Recommendation

The correct architecture for this repo is:

- use the existing alert system for hype moments
- add a dedicated stateful service for the train
- add a dedicated transparent OBS source for persistent progress

That gives:

- no interference with follow/sub/raid alerts
- proper levels and timeout behavior
- clear names/contributors
- identical customization flow for the alert moments
- correct idle behavior when no train is active
