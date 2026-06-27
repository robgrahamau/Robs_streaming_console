# Experimental Embedded Kick Bridge Plan

This document describes an additive implementation plan for an experimental in-process Kick bridge inside the WinUI app.

## Goals

- Fully optional.
- Zero behavior change when disabled.
- No duplicate Kick events or duplicate Kick outbound sends when enabled.
- C# only for the integrated runtime.
- Preserve the current remote-bridge path and current direct Kick path.

## What currently works and must not break

- The app already has a bridge abstraction: `Steaming.Core/Platforms/IKickBridgeClient.cs`.
- The current remote implementation is `Steaming.Core/Platforms/RemoteKickBridgeClient.cs`.
- The current bridge settings path is:
  - `Steaming.Core/Services/AppSettings.cs`
  - `Steaming.Application/Services/IntegrationConfigService.cs`
  - `Steaming.WinUI/Pages/ConnectionsPage.xaml.cs`
- Startup only connects the configured remote bridge in `Steaming.WinUI/App.xaml.cs`.
- `Steaming.Application/ViewModels/MainViewModel.cs` already routes Kick outbound chat through the bridge first, then falls back to direct Kick send.
- `Steaming.Core/Platforms/KickAdapter.cs` is the current direct Kick realtime path.

The experimental work must preserve all of that when the feature is off.

## Core design

Keep one app-facing bridge seam and swap implementations behind it.

- Keep `IKickBridgeClient` as the app-facing interface.
- Keep `RemoteKickBridgeClient` unchanged for current behavior.
- Add `EmbeddedKickBridgeClient` for the experimental in-process runtime.

When embedded mode is enabled, the app must not WebSocket to itself. `EmbeddedKickBridgeClient` should bind directly to internal bridge services and publish into `EventBus`.

## Runtime modes

Add one resolved Kick runtime mode:

- `Direct`
- `RemoteBridge`
- `EmbeddedExperimental`

Rules:

- Exactly one mode is authoritative at runtime.
- `EmbeddedExperimental` and `RemoteBridge` are mutually exclusive.
- Inbound Kick events must come from one source only.
- Outbound Kick sends must use one path only.

Suggested resolution order:

1. Embedded experimental enabled -> `EmbeddedExperimental`
2. Else remote bridge enabled -> `RemoteBridge`
3. Else -> `Direct`

## Configuration model

Do not overload the current remote bridge settings in a way that changes existing behavior.

Keep:

- `AppSettings.KickBridge` for remote bridge configuration

Add:

- `AppSettings.EmbeddedKickBridgeExperimental`

Suggested fields:

- `Enabled`
- `BindHost`
- `BindPort`
- `UseTls`
- `CertificatePath`
- `CertificateKeyPath`
- `CertificatePassword`
- `PublicBaseUrl`
- `WebhookPath`
- `AllowOutboundChat`
- `ClientToken`
- `KickAppClientId`
- `KickAppClientSecret`

Notes:

- `BindHost` and `PublicBaseUrl` are separate on purpose.
- `PublicBaseUrl` is the externally reachable URL Kick will call for webhooks.
- Keep the existing remote bridge config untouched so old installs behave exactly the same.

## Secret storage

Store experimental Kick bridge app credentials in DPAPI credentials, not plaintext settings.

Add to `Steaming.Core/Auth/TokenStore.cs`:

- `KickBridgeAppClientId`
- `KickBridgeAppClientSecret`

If certificate password support is added, store that there too.

## New services

Split the embedded runtime into small services.

### `EmbeddedKickBridgeHostService`

Responsibilities:

- Start/stop lifecycle
- Status snapshot
- Mode-aware startup failures
- Bridge session binding

### `KickWebhookServer`

Responsibilities:

- Listen for webhook POSTs
- Validate Kick webhook signatures
- Forward validated payloads to normalization

### `KickSubscriptionService`

Responsibilities:

- Acquire Kick app token
- List current event subscriptions
- Create missing subscriptions
- Delete stale subscriptions

### `KickBridgeSessionStore`

Responsibilities:

- Hold the current bound desktop session
- Track broadcaster user id, username, chatroom id, desktop access token
- Track outbound-chat allowed state

### `KickBridgeEventNormalizer`

Responsibilities:

- Normalize Kick webhook payloads into the same app `StreamEvent` shapes already produced by the remote bridge path
- Preserve support for normalized and passthrough/raw bridge events

### `KickBridgeOutboundChatService`

Responsibilities:

- Send outbound chat through `https://api.kick.com/public/v1/chat`
- Use the bound desktop access token from the active bridge session

## Event parity

Embedded mode must publish the same logical event outputs as the remote bridge path so the rest of the app remains unchanged.

Target parity with `RemoteKickBridgeClient`:

- `Chat`
- `Follow`
- `Subscribe`
- `GiftSubscribe`
- `ChannelPointRedemption`
- `KicksGifted`
- passthrough/raw Kick events where already supported

The rest of the app should continue to consume those through `EventBus`.

## No-double-up enforcement

This must be enforced centrally, not by convention.

### Inbound

- In `EmbeddedExperimental` mode:
  - Start embedded bridge services
  - Do not start remote bridge
  - Do not allow the direct Kick inbound path to publish the same Kick chat/follow/sub/gift event stream

- In `RemoteBridge` mode:
  - Start remote bridge client
  - Do not start embedded bridge

- In `Direct` mode:
  - Start direct Kick runtime only

### Outbound

Keep one authoritative Kick send path:

- Embedded mode -> `EmbeddedKickBridgeClient.SendMessageAsync`
- Remote bridge mode -> `RemoteKickBridgeClient.SendMessageAsync`
- Direct mode -> `KickAdapter.SendMessageAsync`

`MainViewModel.SendChatAsync` already has the right high-level shape; switch it to mode-aware resolution rather than loosely combining `IsConnected` checks.

## UI surface

Keep the existing remote bridge UI intact.

Add a separate experimental section, clearly labeled.

Suggested fields:

- Enable embedded experimental bridge
- Bind host
- Bind port
- TLS enabled
- Certificate path / key path
- Public callback base URL
- Webhook path
- Kick bridge app client id
- Kick bridge app client secret
- Allow outbound chat
- Client token
- Start / Stop / Restart

Suggested status display:

- Current Kick runtime mode
- Listener bound / failed
- Public callback URL
- Last Kick app token result
- Last subscription sync result
- Last webhook event received
- Last webhook error
- Bound broadcaster username / id
- Last outbound chat result

## Startup behavior

### When experimental mode is off

- No behavior change from current release
- DI continues to resolve `IKickBridgeClient` to `RemoteKickBridgeClient`
- Existing remote bridge startup behavior remains unchanged

### When experimental mode is on

- DI resolves `IKickBridgeClient` to `EmbeddedKickBridgeClient`
- Existing `BootstrapKickBridgeFromStoredLoginAsync` flow still runs, but binds in-process instead of over WebSocket
- Remote bridge connect path is skipped

This keeps the rest of the app stable.

## Webhook/public access requirements

Embedded mode still needs an HTTP access path for Kick webhooks.

Minimum requirements:

- local bind host
- local bind port
- externally reachable public callback base URL

Do not assume these are the same thing.

Supported deployment shapes should include:

- direct public HTTPS listener
- reverse proxy in front of local listener
- tunnel/front-door style public HTTPS forwarding

## TLS strategy

For experimental scope:

- Support local HTTP behind a reverse proxy
- Optionally support direct HTTPS with user-supplied cert material

Do not attempt automatic certificate lifecycle management in the first pass.

## Validation rules

Embedded experimental startup should fail fast with explicit status when:

- bind port is invalid or unavailable
- `PublicBaseUrl` is missing
- Kick bridge app client id/secret are missing
- webhook path is missing
- desktop Kick login context is missing for session bootstrap

Failures should be shown in service status, not only logs.

## File-by-file implementation map

### `Steaming.Core/Services/AppSettings.cs`

- Add `EmbeddedKickBridgeExperimentalConfig`
- Keep existing `KickBridgeConfig` unchanged
- Normalize defaults

### `Steaming.Core/Auth/TokenStore.cs`

- Add DPAPI-backed storage for embedded bridge app credentials

### `Steaming.Application/Services/IntegrationConfigService.cs`

- Add get/save methods for embedded experimental config
- Preserve current remote bridge config methods unchanged

### `Steaming.Core/Platforms/IKickBridgeClient.cs`

- Prefer keeping the existing interface unchanged if possible

### `Steaming.Core/Platforms/EmbeddedKickBridgeClient.cs`

- New implementation

### `Steaming.Core/Services/` new files

- `EmbeddedKickBridgeHostService.cs`
- `KickWebhookServer.cs`
- `KickSubscriptionService.cs`
- `KickBridgeSessionStore.cs`
- `KickBridgeEventNormalizer.cs`
- `KickBridgeOutboundChatService.cs`

### `Steaming.WinUI/App.xaml.cs`

- Resolve bridge implementation by mode
- Preserve current startup path when experimental mode is off

### `Steaming.Application/ViewModels/MainViewModel.cs`

- Add Kick runtime-mode awareness
- Preserve existing remote/direct behavior when experimental mode is off
- Ensure Kick send path is single-authority

### `Steaming.WinUI/Pages/ConnectionsPage.xaml(.cs)`

- Add a separate experimental bridge section
- Keep current remote bridge controls intact

## Rollout order

1. Add config model and mode resolution
2. Add `EmbeddedKickBridgeClient` behind `IKickBridgeClient`
3. Add in-process bootstrap/session binding
4. Add outbound Kick chat through embedded mode
5. Add webhook listener
6. Add app-token acquisition and subscription sync
7. Add UI/status surface
8. Add TLS support
9. Add regression verification for no-double-up behavior

## Regression checklist

### Feature off

- Existing remote bridge config still loads and saves
- Existing remote bridge connect/bootstrap still works
- Existing direct Kick behavior still works
- Existing Kick login, polling, overlays, analytics, and chatbot behavior remain unchanged

### Feature on

- Embedded bridge starts without requiring self-WebSocket
- Only one Kick ingress mode is active
- No duplicated Kick chat lines
- No duplicated follow/sub/gift events
- Outbound Kick chat sends exactly once
- Webhook callback receives and normalizes events
- Service status clearly reports bind/auth/subscription failures

## Scope guard

This is experimental infrastructure only.

Do not:

- remove the remote bridge path
- replace the current direct Kick path globally
- change OBS/plugin architecture
- change app behavior when the feature is disabled
