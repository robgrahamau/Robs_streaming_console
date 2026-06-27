import asyncio
import base64
import json
import os
import ssl
from datetime import datetime, timedelta, timezone

import aiohttp
from aiohttp import web as aiohttp_web
from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.asymmetric import padding
from discord.ext import commands
from flask import Blueprint, jsonify, redirect, request, url_for

import console
import db
import web

_LOG_PATH = os.path.join(os.path.dirname(__file__), '..', 'kick_bridge_debug.log')
_LOG_MAX_BYTES = 25 * 1024 * 1024


def _rotate_flog():
    backup = _LOG_PATH + '.1'
    try:
        if os.path.exists(backup):
            os.remove(backup)
    except Exception:
        pass

    try:
        os.replace(_LOG_PATH, backup)
    except FileNotFoundError:
        return
    except Exception:
        return


def _flog(msg: str):
    try:
        ts = datetime.now().strftime('%Y-%m-%d %H:%M:%S.%f')[:-3]
        if os.path.exists(_LOG_PATH) and os.path.getsize(_LOG_PATH) >= _LOG_MAX_BYTES:
            _rotate_flog()
        with open(_LOG_PATH, 'a', encoding='utf-8') as f:
            f.write(f'{ts} {msg}\n')
    except Exception:
        pass


_admin_bp = Blueprint('kick_bridge_admin', __name__, url_prefix='/extensions/kick-bridge')
_webhook_bp = Blueprint('kick_bridge_webhook', __name__)
_ACTIVE_BRIDGE = None

DEFAULT_BRIDGE_EVENTS = [
    'chat.message.sent',
    'channel.followed',
    'channel.subscription.new',
    'channel.subscription.renewal',
    'channel.subscription.gifts',
    'channel.reward.redemption.updated',
    'livestream.status.updated',
    'livestream.metadata.updated',
    'moderation.banned',
    'kicks.gifted',
]

EVENT_NAME_MAP = {
    'chat.message.sent': 'chat',
    'channel.followed': 'follow',
    'channel.subscription.new': 'subscribe',
    'channel.subscription.renewal': 'subscribe',
    'channel.subscription.gifts': 'gift_sub',
}


def _utcnow() -> datetime:
    return datetime.now(timezone.utc)


def _iso_now() -> str:
    return _utcnow().isoformat()


def _ensure_iso_utc(value: str | None) -> str:
    if not value:
        return _iso_now()
    try:
        parsed = datetime.fromisoformat(value.replace('Z', '+00:00'))
        return parsed.astimezone(timezone.utc).isoformat().replace('+00:00', 'Z')
    except Exception:
        return _iso_now()


def _as_bool(value, default: bool = False) -> bool:
    if value is None:
        return default
    if isinstance(value, bool):
        return value
    if isinstance(value, (int, float)):
        return bool(value)
    return str(value).strip().lower() in {'1', 'true', 'yes', 'on'}


def _bridge() -> 'KickBridgeCog | None':
    return _ACTIVE_BRIDGE


def _require_admin():
    if not web.check_is_admin():
        return redirect(url_for('login'))
    return None


@_admin_bp.route('/')
@_admin_bp.route('/status')
def kick_bridge_status():
    guard = _require_admin()
    if guard is not None:
        return guard
    bridge = _bridge()
    if not bridge:
        return jsonify({'ok': False, 'error': 'Kick bridge extension is not loaded'}), 503
    return jsonify({'ok': True, 'status': bridge.status_snapshot()})


@_admin_bp.route('/probe', methods=['POST'])
def kick_bridge_probe():
    guard = _require_admin()
    if guard is not None:
        return guard
    bridge = _bridge()
    if not bridge:
        return jsonify({'ok': False, 'error': 'Kick bridge extension is not loaded'}), 503
    result = bridge.run_probe_sync()
    status = 200 if result.get('ok') else 502
    return jsonify({'ok': result.get('ok', False), 'probe': result, 'status': bridge.status_snapshot()}), status


@_webhook_bp.route('/webhooks/kick-bridge', methods=['POST'])
def kick_bridge_webhook():
    bridge = _bridge()
    if not bridge or not bridge.enabled:
        console.log('[KickBridge] Webhook hit while extension is unavailable')
        return '', 503

    msg_id = request.headers.get('Kick-Event-Message-Id', '')
    timestamp = request.headers.get('Kick-Event-Message-Timestamp', '')
    signature = request.headers.get('Kick-Event-Signature', '')
    event_name = request.headers.get('Kick-Event-Type', '').strip().lower()
    body = request.get_data()
    payload = request.get_json(silent=True) or {}

    ok, error = bridge.verify_webhook_signature(msg_id, timestamp, signature, body)
    if not ok:
        console.log(f'[KickBridge] Webhook signature validation failed for {event_name or "(unknown)"}: {error}')
        bridge.mark_webhook_failure(event_name or '(unknown)', error)
        return '', 403

    try:
        bridge.handle_webhook_sync(event_name, payload)
    except Exception as exc:
        console.log(f'[KickBridge] Webhook processing failed for {event_name or "(unknown)"}: {exc}')
        bridge.mark_webhook_failure(event_name or '(unknown)', str(exc))
        return '', 500

    return '', 200


class KickBridgeCog(commands.Cog):
    def __init__(self, bot):
        self.bot = bot
        self.config = bot._config
        self.enabled = _as_bool(self.config.get('kick_bridge_enabled', False))
        self.host = str(self.config.get('kick_bridge_host', '0.0.0.0') or '0.0.0.0')
        self.port = int(self.config.get('kick_bridge_port', 8765))
        self.path = self._normalize_path(self.config.get('kick_bridge_path', '/ws/kick-bridge'))
        self.use_tls = _as_bool(self.config.get('kick_bridge_use_tls', False))
        self.client_token = str(self.config.get('kick_bridge_client_token', '') or '')
        self.allow_outbound_chat = _as_bool(self.config.get('kick_bridge_allow_outbound_chat', False))
        self.webhook_callback = str(
            self.config.get('kick_bridge_webhook_callback') or ''
        ).strip()
        self.sender_type = str(self.config.get('kick_bridge_chat_sender_type', 'user') or 'user').strip().lower()
        self.subscription_sync_interval = max(
            60, int(self.config.get('kick_bridge_subscription_sync_interval', 900))
        )
        self._bridge_events = self._configured_bridge_events()
        self._runner = None
        self._site = None
        self._session = None
        self._clients = set()
        self._client_sessions = {}
        self._loop = None
        self._task = asyncio.create_task(self._run())
        self._app_token = None
        self._app_token_expiry = datetime.min.replace(tzinfo=timezone.utc)
        self._public_key = None
        self._status = {
            'bridge_enabled': self.enabled,
            'bridge_active': False,
            'websocket_server_bound': False,
            'kick_connection_ok': False,
            'connected_clients': 0,
            'bound_clients': 0,
            'unbound_clients': 0,
            'configured_broadcasters': [],
            'session_broadcasters': [],
            'configured_events': list(self._bridge_events),
            'websocket_host': self.host,
            'websocket_port': self.port,
            'websocket_path': self.path,
            'webhook_path': '/webhooks/kick-bridge',
            'webhook_callback': self.webhook_callback,
            'last_started_at': None,
            'last_probe_at': None,
            'last_probe_ok': None,
            'last_kick_token_ok_at': None,
            'last_kick_api_ok_at': None,
            'last_subscription_sync_at': None,
            'last_subscription_sync_ok': None,
            'last_webhook_at': None,
            'last_webhook_event': None,
            'last_webhook_error': None,
            'last_client_connect_at': None,
            'last_client_disconnect_at': None,
            'last_bootstrap_at': None,
            'last_bootstrap_ok': None,
            'last_bootstrap_username': None,
            'last_bootstrap_broadcaster_user_id': None,
            'last_chat_send_at': None,
            'last_chat_send_ok': None,
            'last_error': None,
        }
        console.log('[KickBridge] Extension loaded')

    def cog_unload(self):
        self._task.cancel()
        asyncio.create_task(self._shutdown())
        global _ACTIVE_BRIDGE
        if _ACTIVE_BRIDGE is self:
            _ACTIVE_BRIDGE = None
        console.log('[KickBridge] Extension unloading')

    def _configured_bridge_events(self) -> list[str]:
        raw = self.config.get('kick_bridge_events', DEFAULT_BRIDGE_EVENTS)
        if isinstance(raw, str):
            raw = [raw]
        events = []
        for item in raw or []:
            event_name = str(item or '').strip()
            if event_name and event_name not in events:
                events.append(event_name)
        return events or list(DEFAULT_BRIDGE_EVENTS)

    @staticmethod
    def _normalize_path(path: str | None) -> str:
        value = str(path or '/ws/kick-bridge').strip()
        if not value.startswith('/'):
            value = '/' + value
        return value

    async def _run(self):
        global _ACTIVE_BRIDGE
        _ACTIVE_BRIDGE = self
        self._loop = asyncio.get_running_loop()
        await self.bot.wait_until_ready()

        if not self.enabled:
            console.log('[KickBridge] Bridge disabled in config')
            return
        if not self.client_token:
            self._set_error('kick_bridge_client_token is required')
            console.log('[KickBridge] Bridge not started: missing client token')
            return

        self._session = aiohttp.ClientSession(timeout=aiohttp.ClientTimeout(total=20))
        try:
            await self._start_websocket_server()
            await self._probe_kick_connection()
            await self._sync_subscriptions()
            while True:
                await asyncio.sleep(self.subscription_sync_interval)
                await self._probe_kick_connection()
                await self._sync_subscriptions()
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            self._set_error(str(exc))
            console.log(f'[KickBridge] Fatal bridge error: {exc}')
        finally:
            await self._shutdown()

    async def _shutdown(self):
        clients = list(self._clients)
        self._clients.clear()
        self._client_sessions.clear()
        for ws in clients:
            try:
                await ws.close()
            except Exception:
                pass
        self._status['connected_clients'] = 0
        self._status['bound_clients'] = 0
        self._status['unbound_clients'] = 0
        self._status['bridge_active'] = False
        self._status['websocket_server_bound'] = False
        if self._runner is not None:
            try:
                await self._runner.cleanup()
            except Exception:
                pass
            self._runner = None
        if self._session is not None and not self._session.closed:
            await self._session.close()
        self._session = None

    async def _start_websocket_server(self):
        app = aiohttp_web.Application()
        app.router.add_get(self.path, self._ws_handler)
        self._runner = aiohttp_web.AppRunner(app, access_log=None)
        await self._runner.setup()
        ssl_context = None
        if self.use_tls:
            certfile = str(self.config.get('kick_bridge_tls_certfile', '') or '').strip()
            keyfile = str(self.config.get('kick_bridge_tls_keyfile', '') or '').strip()
            if not certfile or not keyfile:
                raise RuntimeError('kick_bridge_use_tls=true requires kick_bridge_tls_certfile and kick_bridge_tls_keyfile')
            ssl_context = ssl.create_default_context(ssl.Purpose.CLIENT_AUTH)
            ssl_context.load_cert_chain(certfile, keyfile)
        self._site = aiohttp_web.TCPSite(self._runner, host=self.host, port=self.port, ssl_context=ssl_context)
        await self._site.start()
        self._status['bridge_active'] = True
        self._status['websocket_server_bound'] = True
        self._status['last_started_at'] = _iso_now()
        scheme = 'wss' if self.use_tls else 'ws'
        console.log(f'[KickBridge] WebSocket server listening on {scheme}://{self.host}:{self.port}{self.path}')

    async def _ws_handler(self, req: aiohttp_web.Request):
        auth = req.headers.get('Authorization', '')
        expected = f'Bearer {self.client_token}'
        if auth != expected:
            self._set_error('Unauthorized bridge client attempted to connect')
            console.log(f'[KickBridge] Client auth failed from {req.remote}')
            return aiohttp_web.Response(status=401, text='Unauthorized')

        ws = aiohttp_web.WebSocketResponse(heartbeat=30)
        await ws.prepare(req)
        self._clients.add(ws)
        self._client_sessions[ws] = self._new_client_session()
        self._refresh_client_counters()
        self._status['last_client_connect_at'] = _iso_now()
        _flog(f'[ws] client connected from {req.remote}; total_clients={len(self._clients)}')
        console.log(f'[KickBridge] Client connected from {req.remote}; clients={len(self._clients)}')
        await self._send_to_client(
            ws,
            self._status_packet(
                'Kick bridge connected',
                'Bridge authenticated. Waiting for kick.bootstrap_session.',
                connected=False,
            ),
        )

        try:
            async for msg in ws:
                if msg.type == aiohttp.WSMsgType.TEXT:
                    await self._handle_client_message(ws, msg.data)
                elif msg.type == aiohttp.WSMsgType.ERROR:
                    console.log(f'[KickBridge] Client websocket error: {ws.exception()}')
        finally:
            self._clients.discard(ws)
            session = self._client_sessions.pop(ws, None)
            if session and session.get('bootstrapped'):
                await self._sync_subscriptions()
            self._refresh_client_counters()
            self._status['last_client_disconnect_at'] = _iso_now()
            _flog(f'[ws] client disconnected; remaining_clients={len(self._clients)}')
            console.log(f'[KickBridge] Client disconnected; clients={len(self._clients)}')
        return ws

    async def _handle_client_message(self, ws, raw: str):
        try:
            packet = json.loads(raw)
        except json.JSONDecodeError:
            await self._send_to_client(ws, self._status_packet('Kick bridge rejected packet', 'Invalid JSON packet.'))
            return

        if packet.get('type') == 'kick.bootstrap_session':
            await self._handle_bootstrap_session(ws, packet)
            return

        if packet.get('type') == 'kick.send_chat':
            await self._handle_send_chat(ws, str(packet.get('message') or ''))
            return

        await self._send_to_client(ws, self._status_packet('Kick bridge ignored packet', f'Unsupported packet type: {packet.get("type")}'))

    async def _handle_bootstrap_session(self, ws, packet: dict):
        session = self._client_sessions.get(ws)
        if session is None:
            await self._send_to_client(ws, self._status_packet('Kick bootstrap failed', 'Client session is unavailable.', connected=False))
            return

        username = str(packet.get('username') or '').strip().lower()
        access_token = str(packet.get('accessToken') or '').strip()
        allow_outbound_chat = _as_bool(packet.get('allowOutboundChat', False))
        chatroom_id_raw = packet.get('chatroomId')
        broadcaster_user_id_raw = packet.get('broadcasterUserId')

        self._status['last_bootstrap_at'] = _iso_now()
        self._status['last_bootstrap_username'] = username or None

        if not username:
            self._status['last_bootstrap_ok'] = False
            await self._send_to_client(ws, self._status_packet('Kick bootstrap failed', 'username is required.', connected=False))
            return
        try:
            claimed_broadcaster_user_id = int(broadcaster_user_id_raw)
        except (TypeError, ValueError):
            self._status['last_bootstrap_ok'] = False
            await self._send_to_client(ws, self._status_packet('Kick bootstrap failed', 'broadcasterUserId must be an integer.', connected=False))
            return

        if allow_outbound_chat and not access_token:
            self._status['last_bootstrap_ok'] = False
            await self._send_to_client(ws, self._status_packet('Kick bootstrap failed', 'accessToken is required when allowOutboundChat is true.', connected=False))
            return

        try:
            channel = await self._fetch_channel(username)
            if not channel:
                raise RuntimeError(f'Kick channel "{username}" not found')
            actual_broadcaster_user_id = int(channel.get('broadcaster_user_id') or channel.get('user_id') or 0)
            if actual_broadcaster_user_id != claimed_broadcaster_user_id:
                raise RuntimeError(
                    f'Bootstrap mismatch for "{username}": claimed broadcasterUserId={claimed_broadcaster_user_id}, actual={actual_broadcaster_user_id}'
                )
        except Exception as exc:
            self._status['last_bootstrap_ok'] = False
            self._set_error(str(exc))
            await self._send_to_client(ws, self._status_packet('Kick bootstrap failed', str(exc), connected=False))
            return

        session.update({
            'bootstrapped': True,
            'username': username,
            'broadcaster_user_id': claimed_broadcaster_user_id,
            'chatroom_id': self._optional_int(chatroom_id_raw),
            'allow_outbound_chat': allow_outbound_chat,
            'access_token': access_token,
        })
        self._status['last_bootstrap_ok'] = True
        self._status['last_bootstrap_broadcaster_user_id'] = claimed_broadcaster_user_id
        self._refresh_client_counters()
        _flog(f'[bootstrap] succeeded username={username} broadcaster_user_id={claimed_broadcaster_user_id}')
        await self._sync_subscriptions()
        await self._send_to_client(
            ws,
            self._status_packet(
                'Kick bootstrap succeeded',
                f'Bound session to {username} ({claimed_broadcaster_user_id}).',
                connected=True,
            ),
        )

    async def _handle_send_chat(self, ws, message: str):
        session = self._client_sessions.get(ws) or {}
        console.log(f'[KickBridge] Received kick.send_chat request chars={len(message.strip())}')
        if not session.get('bootstrapped'):
            console.log('[KickBridge] Rejecting outbound chat: session is not bootstrapped')
            await self._send_to_client(
                ws,
                self._status_packet(
                    'Kick outbound chat rejected',
                    'Session is not bootstrapped. Send kick.bootstrap_session first.',
                    connected=False,
                ),
            )
            return
        message = message.strip()
        if not self.allow_outbound_chat:
            console.log('[KickBridge] Rejecting outbound chat: server-side outbound chat is disabled')
            await self._send_to_client(ws, self._status_packet('Kick outbound chat disabled', 'Server-side outbound chat is disabled.'))
            return
        if not session.get('allow_outbound_chat'):
            console.log('[KickBridge] Rejecting outbound chat: desktop bootstrap did not enable outbound chat')
            await self._send_to_client(ws, self._status_packet('Kick outbound chat disabled', 'Desktop bootstrap did not enable outbound chat.'))
            return
        if not message:
            console.log('[KickBridge] Rejecting outbound chat: message is empty')
            await self._send_to_client(ws, self._status_packet('Kick outbound chat rejected', 'Message cannot be empty.'))
            return

        result = await self._post_chat_message(session, message)
        summary = 'Kick chat message sent' if result.get('ok') else 'Kick chat send failed'
        await self._send_to_client(ws, self._status_packet(summary, result.get('details', '')))

    async def _post_chat_message(self, session: dict, message: str) -> dict:
        self._status['last_chat_send_at'] = _iso_now()
        token = str(session.get('access_token') or '').strip()
        if not token:
            self._status['last_chat_send_ok'] = False
            return {'ok': False, 'details': 'Desktop bootstrap accessToken is not available.'}

        body = {'content': message, 'type': self.sender_type or 'user'}
        broadcaster_id = session.get('broadcaster_user_id')
        if body['type'] == 'user' and broadcaster_id:
            body['broadcaster_user_id'] = broadcaster_id

        try:
            async with self._session.post(
                'https://api.kick.com/public/v1/chat',
                headers={
                    'Authorization': f'Bearer {token}',
                    'Content-Type': 'application/json',
                },
                json=body,
            ) as resp:
                payload = await self._safe_json(resp)
                ok = 200 <= resp.status < 300
                self._status['last_chat_send_ok'] = ok
                if ok:
                    console.log('[KickBridge] Outbound chat message sent')
                    return {'ok': True, 'details': 'Chat message accepted by Kick.', 'response': payload}
                detail = payload.get('message') if isinstance(payload, dict) else str(payload)
                console.log(f'[KickBridge] Outbound chat failed with status {resp.status}: {detail}')
                return {'ok': False, 'details': f'Kick chat API returned {resp.status}: {detail}', 'response': payload}
        except Exception as exc:
            self._status['last_chat_send_ok'] = False
            self._set_error(str(exc))
            return {'ok': False, 'details': str(exc)}

    async def _send_to_client(self, ws, packet: dict):
        if ws.closed:
            return
        try:
            await ws.send_json(packet)
        except Exception as exc:
            console.log(f'[KickBridge] Failed to send packet to client: {exc}')

    async def _broadcast(self, packet: dict):
        if not self._clients:
            return
        target_broadcaster_user_id = None
        channel = packet.get('channel')
        if isinstance(channel, dict):
            try:
                target_broadcaster_user_id = int(channel.get('broadcasterUserId') or 0)
            except (TypeError, ValueError):
                target_broadcaster_user_id = 0
        stale = []
        sent_count = 0
        skipped_unbound = 0
        skipped_id_mismatch = 0
        for ws in list(self._clients):
            session = self._client_sessions.get(ws) or {}
            if not session.get('bootstrapped'):
                skipped_unbound += 1
                continue
            if target_broadcaster_user_id and session.get('broadcaster_user_id') != target_broadcaster_user_id:
                skipped_id_mismatch += 1
                console.log(
                    f'[KickBridge] FILTERED: event broadcaster_user_id={target_broadcaster_user_id} '
                    f'but session broadcaster_user_id={session.get("broadcaster_user_id")} '
                    f'for user={session.get("username")} — skipping'
                )
                continue
            try:
                await ws.send_json(packet)
                sent_count += 1
            except Exception:
                stale.append(ws)
        for ws in stale:
            self._clients.discard(ws)
            self._client_sessions.pop(ws, None)
        self._refresh_client_counters()
        _flog(
            f'[broadcast] type={packet.get("type")} event={packet.get("event", "-")} '
            f'sent={sent_count} skipped_unbound={skipped_unbound} skipped_id_mismatch={skipped_id_mismatch}'
        )
        console.log(
            f'[KickBridge] Broadcast type={packet.get("type")} event={packet.get("event", "-")} '
            f'sent={sent_count} skipped_unbound={skipped_unbound} skipped_id_mismatch={skipped_id_mismatch}'
        )

    def verify_webhook_signature(self, msg_id: str, timestamp: str, signature: str, body: bytes) -> tuple[bool, str]:
        if not signature:
            return False, 'Missing Kick-Event-Signature header'
        try:
            public_key = self._get_public_key()
            message = f'{msg_id}.{timestamp}.{body.decode()}'.encode()
            public_key.verify(
                base64.b64decode(signature),
                message,
                padding.PKCS1v15(),
                hashes.SHA256(),
            )
            return True, ''
        except InvalidSignature:
            return False, 'Invalid signature'
        except Exception as exc:
            return False, str(exc)

    def _get_public_key(self):
        if self._public_key is not None:
            return self._public_key
        key = web._get_kick_public_key()
        if key is None:
            raise RuntimeError('Failed to fetch Kick public key')
        self._public_key = key
        return key

    def handle_webhook_sync(self, event_name: str, payload: dict):
        self._status['last_webhook_at'] = _iso_now()
        self._status['last_webhook_event'] = event_name
        self._status['last_webhook_error'] = None
        _flog(f'[webhook] received event={event_name} connected_clients={len(self._clients)} bound={sum(1 for s in self._client_sessions.values() if s.get("bootstrapped"))}')
        console.log(f'[KickBridge] Webhook received: {event_name}')
        if self._loop is None:
            raise RuntimeError('Kick bridge event loop is not ready')
        future = asyncio.run_coroutine_threadsafe(
            self._handle_webhook_async(event_name, payload),
            self._loop,
        )
        future.result(timeout=15)

    def mark_webhook_failure(self, event_name: str, error: str):
        self._status['last_webhook_at'] = _iso_now()
        self._status['last_webhook_event'] = event_name
        self._status['last_webhook_error'] = error
        self._set_error(error)

    async def _handle_webhook_async(self, event_name: str, payload: dict):
        generic = self._generic_event(event_name, payload)
        if generic:
            await self._broadcast(generic)

        normalized = self._normalize_event(event_name, payload)
        if normalized:
            await self._broadcast(normalized)

        if event_name == 'livestream.status.updated':
            broadcaster = payload.get('broadcaster') or {}
            slug = broadcaster.get('channel_slug') or broadcaster.get('username') or 'unknown-channel'
            state = 'live' if payload.get('is_live') else 'offline'
            details = f'{slug} is now {state}.'
            await self._broadcast(self._status_packet('Kick livestream status updated', details))
            return

        if event_name == 'livestream.metadata.updated':
            broadcaster = payload.get('broadcaster') or {}
            slug = broadcaster.get('channel_slug') or broadcaster.get('username') or 'unknown-channel'
            title = (payload.get('metadata') or {}).get('title') or ''
            await self._broadcast(self._status_packet('Kick livestream metadata updated', f'{slug} title changed to "{title}".'))
            return

        if not normalized and not generic:
            console.log(f'[KickBridge] Ignored unsupported Kick webhook event: {event_name}')

    def _generic_event(self, event_name: str, payload: dict) -> dict | None:
        if event_name in EVENT_NAME_MAP:
            return None

        broadcaster = payload.get('broadcaster') or {}
        occurred_at = (
            payload.get('created_at')
            or payload.get('started_at')
            or payload.get('ended_at')
            or (payload.get('metadata') or {}).get('created_at')
            or (payload.get('metadata') or {}).get('updated_at')
        )

        return {
            'type': 'kick.event',
            'event': event_name,
            'occurredAt': _ensure_iso_utc(occurred_at),
            'channel': {
                'broadcasterUserId': int(broadcaster.get('user_id') or 0),
                'slug': str(broadcaster.get('channel_slug') or broadcaster.get('username') or ''),
            },
            'user': self._extract_event_user(payload, broadcaster),
            'data': {
                'payload': payload,
            },
        }

    def _normalize_event(self, event_name: str, payload: dict) -> dict | None:
        normalized_name = EVENT_NAME_MAP.get(event_name)
        if not normalized_name:
            return None

        broadcaster = payload.get('broadcaster') or {}
        user = {}
        data = {
            'message': '',
            'messageId': '',
            'color': '',
            'isModerator': False,
            'isSubscriber': False,
            'isBroadcaster': False,
            'isVip': False,
            'isHighlighted': False,
            'bits': 0,
            'subMonths': 0,
            'recipient': '',
            'count': 0,
            'viewers': 0,
        }
        occurred_at = payload.get('created_at') or payload.get('started_at') or payload.get('ended_at')

        if event_name == 'chat.message.sent':
            sender = payload.get('sender') or {}
            identity = sender.get('identity') or {}
            badges = identity.get('badges') or []
            badge_types = {str(b.get('type') or '').strip().lower() for b in badges}
            subscriber_badge = next((b for b in badges if str(b.get('type') or '').strip().lower() == 'subscriber'), {})
            user = self._compact_user(sender)
            data.update({
                'message': str(payload.get('content') or ''),
                'messageId': str(payload.get('message_id') or ''),
                'color': str(identity.get('username_color') or ''),
                'isModerator': 'moderator' in badge_types,
                'isSubscriber': 'subscriber' in badge_types,
                'isBroadcaster': self._is_broadcaster(sender, broadcaster),
                'isVip': 'vip' in badge_types,
                'isHighlighted': False,
                'subMonths': int(subscriber_badge.get('count') or 0),
            })
        elif event_name == 'channel.followed':
            user = self._compact_user(payload.get('follower') or {})
        elif event_name in {'channel.subscription.new', 'channel.subscription.renewal'}:
            subscriber = payload.get('subscriber') or {}
            user = self._compact_user(subscriber)
            data.update({
                'isSubscriber': True,
                'subMonths': int(payload.get('duration') or 0),
            })
        elif event_name == 'channel.subscription.gifts':
            gifter = payload.get('gifter') or {}
            giftees = payload.get('giftees') or []
            user = self._compact_user(gifter)
            data.update({
                'isSubscriber': True,
                'recipient': ', '.join(str(g.get('username') or '') for g in giftees[:3] if g.get('username')),
                'count': len(giftees),
            })

        return {
            'type': 'kick.event',
            'event': normalized_name,
            'occurredAt': _ensure_iso_utc(occurred_at),
            'channel': {
                'broadcasterUserId': int(broadcaster.get('user_id') or 0),
                'slug': str(broadcaster.get('channel_slug') or broadcaster.get('username') or ''),
            },
            'user': user or {'id': '', 'username': '', 'displayName': ''},
            'data': data,
        }

    @staticmethod
    def _compact_user(user_obj: dict) -> dict:
        username = str(user_obj.get('username') or '')
        return {
            'id': str(user_obj.get('user_id') or ''),
            'username': username,
            'displayName': username,
        }

    def _extract_event_user(self, payload: dict, broadcaster: dict) -> dict:
        for key in (
            'sender',
            'follower',
            'subscriber',
            'gifter',
            'moderator',
            'banned_user',
            'redeemer',
            'user',
            'broadcaster',
        ):
            value = payload.get(key)
            if isinstance(value, dict) and (value.get('user_id') or value.get('username')):
                return self._compact_user(value)
        return self._compact_user(broadcaster)

    @staticmethod
    def _is_broadcaster(user_obj: dict, broadcaster: dict) -> bool:
        return str(user_obj.get('user_id') or '') == str(broadcaster.get('user_id') or '')

    def _status_packet(self, summary: str, details: str, connected: bool = True) -> dict:
        return {
            'type': 'kick.bridge_status',
            'connected': connected,
            'summary': summary,
            'details': details,
        }

    async def _probe_kick_connection(self) -> dict:
        self._status['last_probe_at'] = _iso_now()
        try:
            token = await self._get_app_token()
            if not token:
                raise RuntimeError('No Kick bridge app token received')

            broadcasters = self._configured_broadcasters()
            self._status['configured_broadcasters'] = broadcasters
            self._status['session_broadcasters'] = self._session_broadcasters()
            probe_result = {
                'ok': True,
                'token_ok': True,
                'broadcasters': broadcasters,
                'details': 'Kick bridge app credentials are valid.',
            }

            if broadcasters:
                username = broadcasters[0]
                channel = await self._fetch_channel(username)
                if not channel:
                    raise RuntimeError(f'Kick channel lookup failed for "{username}"')
                probe_result['first_channel'] = {
                    'slug': channel.get('slug'),
                    'broadcaster_user_id': channel.get('broadcaster_user_id') or channel.get('user_id'),
                }

            self._status['kick_connection_ok'] = True
            self._status['last_probe_ok'] = True
            self._status['last_kick_api_ok_at'] = _iso_now()
            return probe_result
        except Exception as exc:
            self._status['kick_connection_ok'] = False
            self._status['last_probe_ok'] = False
            self._set_error(str(exc))
            return {'ok': False, 'details': str(exc)}

    def run_probe_sync(self) -> dict:
        if self._loop is None:
            return {'ok': False, 'details': 'Kick bridge event loop is not ready.'}
        future = asyncio.run_coroutine_threadsafe(self._probe_kick_connection(), self._loop)
        return future.result(timeout=20)

    async def _sync_subscriptions(self):
        desired_usernames = self._configured_broadcasters()
        desired_events = set(self._bridge_events)
        self._status['configured_broadcasters'] = desired_usernames
        self._status['session_broadcasters'] = self._session_broadcasters()

        try:
            local_rows = db.get_all_kick_bridge_subscriptions()
            for row in local_rows:
                if row['username'] not in desired_usernames or row['event_name'] not in desired_events:
                    await self._delete_subscription(row['subscription_id'])
                    db.delete_kick_bridge_subscription(row['subscription_id'])

            for username in desired_usernames:
                channel = await self._fetch_channel(username)
                if not channel:
                    raise RuntimeError(f'Kick channel "{username}" not found')
                broadcaster_user_id = channel.get('broadcaster_user_id') or channel.get('user_id')
                if not broadcaster_user_id:
                    raise RuntimeError(f'No broadcaster user ID returned for "{username}"')

                remote_subs = await self._fetch_event_subscriptions(int(broadcaster_user_id))
                remote_by_event = {}
                for sub in remote_subs:
                    event_name = str(sub.get('event') or '')
                    if event_name:
                        remote_by_event[event_name] = sub
                        if sub.get('id'):
                            db.save_kick_bridge_subscription(sub['id'], str(broadcaster_user_id), username, event_name)

                for event_name in self._bridge_events:
                    if event_name in remote_by_event:
                        continue
                    created = await self._create_subscription(int(broadcaster_user_id), event_name)
                    if created and created.get('subscription_id'):
                        db.save_kick_bridge_subscription(
                            created['subscription_id'],
                            str(broadcaster_user_id),
                            username,
                            event_name,
                        )

            self._status['last_subscription_sync_at'] = _iso_now()
            self._status['last_subscription_sync_ok'] = True
            _flog(f'[sync] subscription sync complete for {len(desired_usernames)} broadcaster(s)')
            console.log(f'[KickBridge] Subscription sync complete for {len(desired_usernames)} broadcaster(s)')
        except Exception as exc:
            self._status['last_subscription_sync_at'] = _iso_now()
            self._status['last_subscription_sync_ok'] = False
            self._set_error(str(exc))
            _flog(f'[sync] subscription sync FAILED: {exc}')
            console.log(f'[KickBridge] Subscription sync failed: {exc}')

    async def _get_app_token(self) -> str | None:
        now = _utcnow()
        if self._app_token and now < self._app_token_expiry:
            return self._app_token

        client_id = str(self.config.get('kick_bridge_client_id', '') or '').strip()
        client_secret = str(self.config.get('kick_bridge_client_secret', '') or '').strip()
        if not client_id or not client_secret:
            raise RuntimeError('kick_bridge_client_id and kick_bridge_client_secret are required')

        async with self._session.post(
            'https://id.kick.com/oauth/token',
            data={
                'grant_type': 'client_credentials',
                'client_id': client_id,
                'client_secret': client_secret,
            },
            headers={'Content-Type': 'application/x-www-form-urlencoded'},
        ) as resp:
            payload = await self._safe_json(resp)
            if resp.status >= 400:
                raise RuntimeError(f'Kick token request failed with {resp.status}: {payload}')
            token = payload.get('access_token')
            if not token:
                raise RuntimeError(f'Kick token response missing access_token: {payload}')
            expires_in = int(payload.get('expires_in') or 3600)
            self._app_token = token
            self._app_token_expiry = now + timedelta(seconds=max(60, expires_in - 60))
            self._status['last_kick_token_ok_at'] = _iso_now()
            return token

    async def _kick_api_get(self, url: str, **kwargs):
        token = await self._get_app_token()
        headers = dict(kwargs.pop('headers', {}))
        headers['Authorization'] = f'Bearer {token}'
        async with self._session.get(url, headers=headers, **kwargs) as resp:
            payload = await self._safe_json(resp)
            if resp.status >= 400:
                raise RuntimeError(f'Kick GET {url} failed with {resp.status}: {payload}')
            self._status['last_kick_api_ok_at'] = _iso_now()
            return payload

    async def _kick_api_post(self, url: str, **kwargs):
        token = await self._get_app_token()
        headers = dict(kwargs.pop('headers', {}))
        headers['Authorization'] = f'Bearer {token}'
        async with self._session.post(url, headers=headers, **kwargs) as resp:
            payload = await self._safe_json(resp)
            if resp.status >= 400:
                raise RuntimeError(f'Kick POST {url} failed with {resp.status}: {payload}')
            self._status['last_kick_api_ok_at'] = _iso_now()
            return payload

    async def _kick_api_delete(self, url: str, **kwargs):
        token = await self._get_app_token()
        headers = dict(kwargs.pop('headers', {}))
        headers['Authorization'] = f'Bearer {token}'
        async with self._session.delete(url, headers=headers, **kwargs) as resp:
            if resp.status not in {200, 204}:
                payload = await self._safe_json(resp)
                raise RuntimeError(f'Kick DELETE {url} failed with {resp.status}: {payload}')
            self._status['last_kick_api_ok_at'] = _iso_now()

    async def _fetch_channel(self, username: str) -> dict | None:
        payload = await self._kick_api_get(
            'https://api.kick.com/public/v1/channels',
            params={'slug': username},
        )
        channels = payload.get('data', []) if isinstance(payload, dict) else []
        return channels[0] if channels else None

    async def _fetch_event_subscriptions(self, broadcaster_user_id: int) -> list[dict]:
        payload = await self._kick_api_get(
            'https://api.kick.com/public/v1/events/subscriptions',
            params={'broadcaster_user_id': broadcaster_user_id},
        )
        return payload.get('data', []) if isinstance(payload, dict) else []

    async def _create_subscription(self, broadcaster_user_id: int, event_name: str) -> dict | None:
        payload = await self._kick_api_post(
            'https://api.kick.com/public/v1/events/subscriptions',
            headers={'Content-Type': 'application/json'},
            json={
                'events': [{'name': event_name, 'version': 1}],
                'broadcaster_user_id': broadcaster_user_id,
                'method': 'webhook',
            },
        )
        data = payload.get('data', []) if isinstance(payload, dict) else []
        if isinstance(data, dict):
            data = [data]
        for item in data:
            if item.get('name') == event_name and not item.get('error'):
                console.log(f'[KickBridge] Registered Kick subscription {event_name} for broadcaster {broadcaster_user_id}')
                return item
        raise RuntimeError(f'Kick subscription registration failed for {event_name}: {payload}')

    async def _delete_subscription(self, subscription_id: str):
        await self._kick_api_delete(
            'https://api.kick.com/public/v1/events/subscriptions',
            params=[('id', subscription_id)],
        )
        console.log(f'[KickBridge] Deleted stale Kick bridge subscription {subscription_id}')

    def _configured_broadcasters(self) -> list[str]:
        usernames = []
        for username in self._session_broadcasters():
            if username and username not in usernames:
                usernames.append(username)

        raw = self.config.get('kick_bridge_broadcasters')
        if raw:
            if isinstance(raw, str):
                raw = [raw]
            for item in raw:
                username = str(item or '').strip().lower()
                if username and username not in usernames:
                    usernames.append(username)
        return usernames

    def _session_broadcasters(self) -> list[str]:
        usernames = []
        for session in self._client_sessions.values():
            if not session.get('bootstrapped'):
                continue
            username = str(session.get('username') or '').strip().lower()
            if username and username not in usernames:
                usernames.append(username)
        return usernames

    @staticmethod
    def _new_client_session() -> dict:
        return {
            'bootstrapped': False,
            'username': '',
            'broadcaster_user_id': None,
            'chatroom_id': None,
            'allow_outbound_chat': False,
            'access_token': '',
        }

    @staticmethod
    def _optional_int(value):
        if value in (None, ''):
            return None
        try:
            return int(value)
        except (TypeError, ValueError):
            return None

    def _refresh_client_counters(self):
        bound = 0
        unbound = 0
        for session in self._client_sessions.values():
            if session.get('bootstrapped'):
                bound += 1
            else:
                unbound += 1
        self._status['connected_clients'] = len(self._clients)
        self._status['bound_clients'] = bound
        self._status['unbound_clients'] = unbound

    async def _safe_json(self, resp) -> dict | list | str:
        text = await resp.text()
        if not text:
            return {}
        try:
            return json.loads(text)
        except json.JSONDecodeError:
            return text

    def _set_error(self, message: str):
        self._status['last_error'] = message

    def status_snapshot(self) -> dict:
        snap = dict(self._status)
        snap['connected_clients'] = len(self._clients)
        snap['bound_clients'] = sum(1 for s in self._client_sessions.values() if s.get('bootstrapped'))
        snap['unbound_clients'] = sum(1 for s in self._client_sessions.values() if not s.get('bootstrapped'))
        snap['bridge_events'] = list(self._bridge_events)
        snap['session_broadcasters'] = self._session_broadcasters()
        snap['websocket_url'] = f'{"wss" if self.use_tls else "ws"}://{self.host}:{self.port}{self.path}'
        return snap


async def setup(bot):
    await bot.add_cog(KickBridgeCog(bot))


def setup_web(app, config):
    app.register_blueprint(_admin_bp)
    app.register_blueprint(_webhook_bp)


def teardown_web(app):
    web.unregister_blueprint('kick_bridge_admin')
    web.unregister_blueprint('kick_bridge_webhook')
