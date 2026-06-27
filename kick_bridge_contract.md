# Kick Bridge Contract

This file defines the desktop-side contract for the remote Kick bridge.

The current intended server-side bot is:

- `Y:\AI\discord_voice_chat`

That bot already has existing Kick polling, webhook registration, and Kick subscription storage.
This contract is specifically for the additional remote desktop bridge layer.

## Desktop Configuration

The desktop app now expects these settings:

- `Enabled`
- `Host`
- `Port`
- `UseTls`
- `WebSocketPath`
- `ClientToken`
- `AllowOutboundChat`

Meaning:

- `Host` may be a network name or an IP address
- `Port` is the remote bridge port
- `UseTls=true` means `wss`
- `UseTls=false` means `ws`
- default `WebSocketPath` is `/ws/kick-bridge`

Resulting connection target:

- `wss://HOST:PORT/PATH`
- or `ws://HOST:PORT/PATH`

Auth:

- the desktop client sends `Authorization: Bearer <ClientToken>` in the WebSocket handshake

## Connection Model

The desktop app opens one authenticated WebSocket connection to the remote bridge.

Expected server behavior:

- accept authenticated WebSocket clients on the configured path
- reject unauthorized clients
- send Kick event packets over the socket
- optionally accept outbound chat command packets from the client
- accept a desktop bootstrap packet after connect so the server does not need manual broadcaster guessing

## Required Desktop Bootstrap Packet

After WebSocket connect, the desktop sends its logged-in Kick session context to the bridge.

```json
{
  "type": "kick.bootstrap_session",
  "accessToken": "desktop-user-access-token",
  "username": "channelslug",
  "broadcasterUserId": 123456,
  "chatroomId": 123456,
  "allowOutboundChat": true
}
```

Rules:

- this packet is authoritative for the desktop session identity
- the server should use it to bind the remote client to the correct broadcaster/channel
- the server should not require a separate manual `kick_bridge_broadcasters` list when this packet is present
- `broadcasterUserId` is the primary identity field
- `chatroomId` is also sent because the current desktop app already stores it from Kick login

Important separation:

- the server bridge should still keep its own Kick application credentials, token flow, and webhook registration
- the desktop bootstrap packet provides session/broadcaster identity from the desktop login
- it does not mean the bridge should collapse back into the main bot's existing Kick app/scopes

## Server To Desktop Packets

### Kick event

```json
{
  "type": "kick.event",
  "event": "chat|follow|subscribe|gift_sub|<raw-kick-webhook-name>",
  "occurredAt": "2026-06-01T00:00:00Z",
  "channel": {
    "broadcasterUserId": 123,
    "slug": "example-channel"
  },
  "user": {
    "id": "456",
    "username": "viewername",
    "displayName": "ViewerName"
  },
  "data": {
    "message": "hello world",
    "messageId": "event-or-message-id",
    "color": "#FFFFFF",
    "isModerator": false,
    "isSubscriber": false,
    "isBroadcaster": false,
    "isVip": false,
    "isHighlighted": false,
    "bits": 0,
    "subMonths": 0,
    "recipient": "",
    "count": 0,
    "viewers": 0,
    "payload": {}
  }
}
```

Rules:

- `type` must be `kick.event`
- `event` is either:
  - one of the normalized desktop event names:
    - `chat`
    - `follow`
    - `subscribe`
    - `gift_sub`
  - or the raw Kick webhook event name for passthrough events such as:
    - `channel.reward.redemption.updated`
    - `livestream.status.updated`
    - `livestream.metadata.updated`
    - `moderation.banned`
    - `kicks.gifted`
- `occurredAt` should be ISO 8601 UTC
- all top-level objects shown above should always be present
- for normalized events, unused `data` fields can be zero, false, or empty string
- for passthrough webhook events, the bridge should preserve the original webhook body in `data.payload`

Current live bridge behavior:

- the bridge subscribes to the configured Kick webhook event list and acts as a bridge for those webhook items
- it normalizes this subset into simplified desktop event names:
  - `chat.message.sent` -> `chat`
  - `channel.followed` -> `follow`
  - `channel.subscription.new` -> `subscribe`
  - `channel.subscription.renewal` -> `subscribe`
  - `channel.subscription.gifts` -> `gift_sub`
- all other subscribed webhook events are still forwarded as `kick.event` packets with the raw Kick event name in `event`

Default bridge event subscription set in the live bridge:

- `chat.message.sent`
- `channel.followed`
- `channel.subscription.new`
- `channel.subscription.renewal`
- `channel.subscription.gifts`
- `channel.reward.redemption.updated`
- `livestream.status.updated`
- `livestream.metadata.updated`
- `moderation.banned`
- `kicks.gifted`

Desktop behavior:

- the desktop client maps `kick.event` into its local `EventBus`
- platform is `Kick`
- normalized event mapping:
  - `chat` -> `Chat`
  - `follow` -> `Follow`
  - `subscribe` -> `Subscribe`
  - `gift_sub` -> `GiftSubscribe`
- non-normalized/raw webhook events are bridge passthrough packets and should remain available to desktop features that want direct Kick webhook semantics

## Optional Server Status Packet

The desktop client also understands:

```json
{
  "type": "kick.bridge_status",
  "connected": true,
  "summary": "Kick bridge connected",
  "details": "Bridge authenticated and subscribed."
}
```

This is optional but useful for in-app status reporting.

The live bridge also emits `kick.bridge_status` packets for certain non-normalized state changes, for example:

- after WebSocket authentication
- after bootstrap success/failure
- after outbound chat success/failure
- for `livestream.status.updated`
- for `livestream.metadata.updated`

## Desktop To Server Packets

If outbound chat is enabled in desktop settings, the app sends:

```json
{
  "type": "kick.send_chat",
  "message": "hello from desktop"
}
```

Server expectation:

- only accept this from authenticated clients
- send the message through Kick's official chat API path using the desktop user access token from `kick.bootstrap_session`
- only accept this after a valid `kick.bootstrap_session` has been received for that connection
- use chat payload `type: "user"` for this desktop-token path
- include `broadcaster_user_id` from the validated bootstrap session
- ignore or reject it if outbound chat is disabled server-side

## Test Checklist

The server-side implementation is testable against this desktop build when:

1. desktop bridge settings are filled in with host/network name, port, token, and path
2. desktop `Connect` succeeds
3. server emits a `kick.bridge_status` or `kick.event` packet
4. the desktop status page shows the bridge connection state
5. Kick events appear in the desktop activity/chat/event flow through the existing event bus
6. no manual broadcaster guessing is required on the server when the desktop bootstrap packet has been sent
