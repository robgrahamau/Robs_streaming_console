using System.Text.Json;
using Microsoft.Extensions.Logging;
using Steaming.Core.Ipc;
using Steaming.Core.Models;
using Steaming.Core.Services;
using TwitchLib.Client;
using TwitchLib.Client.Events;
using TwitchLib.Client.Models;

namespace Steaming.Core.Platforms;

public class TwitchAdapter : IAsyncDisposable
{
    private readonly EventBus _bus;
    private readonly ILogger<TwitchAdapter> _logger;
    private TwitchClient? _client;

    private string? _oauthToken;
    private string? _clientId;
    private TwitchClient? _botClient;
    private string? _botUsername;

    public bool    IsConnected    => _client?.IsConnected ?? false;
    public bool    IsBotConnected => _botClient?.IsConnected ?? false;
    public string? BotUsername    => _botUsername;
    public string? Channel        { get; private set; }
    public string? UserId         { get; private set; }

    public event Action<ThirdPartyEmoteService.InitSummary>? ThirdPartyEmoteStatusChanged;

    public TwitchAdapter(EventBus bus, ILogger<TwitchAdapter> logger)
    {
        _bus    = bus;
        _logger = logger;
    }

    public async Task ConnectAsync(string username, string oauthToken, string channel)
    {
        if (_client != null)
            await _client.DisconnectAsync();

        Channel      = channel;
        _oauthToken  = oauthToken;

        var creds = new ConnectionCredentials(username, oauthToken);
        _client = new TwitchClient();
        _client.Initialize(creds, channel);

        _client.OnConnected    += (_, e) => { _logger.LogInformation("[Twitch] Connected as {User} to #{Ch}", e.BotUsername, channel); return Task.CompletedTask; };
        _client.OnDisconnected += (_, _) => { _logger.LogWarning("[Twitch] Disconnected."); return Task.CompletedTask; };
        _client.OnMessageReceived += OnMessageReceived;

        await _client.ConnectAsync();
    }

    // Call after ConnectAsync to initialise badge + third-party emote services.
    // broadcasterId is the numeric Twitch user ID of the channel being watched.
    public async Task InitializeServicesAsync(string clientId, string broadcasterId)
    {
        _clientId = clientId;

        if (string.IsNullOrEmpty(_oauthToken) || string.IsNullOrEmpty(clientId)) return;

        await TwitchBadgeService.Instance.InitializeAsync(_oauthToken, clientId, broadcasterId);
        _logger.LogInformation("[Twitch] Badge service initialised for broadcaster {Id}", broadcasterId);

        _ = Task.Run(async () =>
        {
            try
            {
                var summary = await ThirdPartyEmoteService.Instance.InitializeForChannelAsync(broadcasterId, Channel ?? "");
                ThirdPartyEmoteStatusChanged?.Invoke(summary);
                _logger.LogInformation("[Twitch] Third-party emote services initialised for broadcaster {Id}", broadcasterId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("[Twitch] Third-party emote init skipped: {Msg}", ex.Message);
            }
        });
    }

    public async Task<string?> FetchUserIdAsync(string token, string clientId, string login)
    {
        _clientId = clientId;
        try
        {
            using var http = new System.Net.Http.HttpClient();
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
            http.DefaultRequestHeaders.Add("Client-Id", clientId);
            using var resp = await http.GetAsync(
                $"https://api.twitch.tv/helix/users?login={Uri.EscapeDataString(login)}");
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data");
            UserId = data.GetArrayLength() > 0 ? data[0].GetProperty("id").GetString() : null;
            return UserId;
        }
        catch { return null; }
    }

    public async Task ConnectBotAsync(string botUsername, string botOauthToken)
    {
        if (_botClient != null)
            await _botClient.DisconnectAsync();

        _botUsername = botUsername;
        var creds = new ConnectionCredentials(botUsername, botOauthToken);
        _botClient = new TwitchClient();
        _botClient.Initialize(creds, Channel ?? botUsername);
        _botClient.OnConnected    += (_, e) => { _logger.LogInformation("[Twitch Bot] Connected as {User}", e.BotUsername); return Task.CompletedTask; };
        _botClient.OnDisconnected += (_, _) => { _logger.LogWarning("[Twitch Bot] Disconnected."); return Task.CompletedTask; };
        await _botClient.ConnectAsync();
    }

    public async Task DisconnectBotAsync()
    {
        if (_botClient != null)
        {
            await _botClient.DisconnectAsync();
            _botClient  = null;
            _botUsername = null;
        }
    }

    // Sends as the bot account if connected, otherwise falls back to the broadcaster account.
    public async Task SendMessageAsync(string message)
    {
        try
        {
            if (_botClient?.IsConnected == true && Channel != null)
                await _botClient.SendMessageAsync(Channel, message);
            else if (_client?.IsConnected == true && Channel != null)
                await _client.SendMessageAsync(Channel, message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[Twitch] SendMessage failed: {Msg}", ex.Message);
        }
    }

    private async Task OnMessageReceived(object? _, OnMessageReceivedArgs e)
    {
        var msg = e.ChatMessage;

        // ── Badges ────────────────────────────────────────────────────────────
        // TwitchLib v4 has no IsSubscriber/IsModerator/IsVip — derive from Badges list.
        bool isModerator  = msg.Badges.Any(b => b.Key.Equals("moderator",  StringComparison.OrdinalIgnoreCase))
                         || msg.UserType == TwitchLib.Client.Enums.UserType.Moderator;
        bool isSubscriber = msg.Badges.Any(b => b.Key.Equals("subscriber", StringComparison.OrdinalIgnoreCase))
                         || msg.SubscribedMonthCount > 0;
        bool isVip        = msg.Badges.Any(b => b.Key.Equals("vip",        StringComparison.OrdinalIgnoreCase));

        int subMonths = msg.SubscribedMonthCount;
        if (subMonths == 0 && isSubscriber)
        {
            var subBadge = msg.Badges.FirstOrDefault(b =>
                b.Key.Equals("subscriber", StringComparison.OrdinalIgnoreCase));
            if (int.TryParse(subBadge.Value, out int m) && m > 0) subMonths = m;
            else subMonths = 1;
        }

        // Actual badge images (empty path = pill fallback in renderer)
        var badgePaths = TwitchBadgeService.Instance.GetBadgePaths(msg.Badges);

        // ── Emotes — native Twitch (TwitchLib provides ImageUrl per emote) ────
        var emoteSegments = new List<EmoteSegment>();
        if (msg.EmoteSet?.Emotes != null)
        {
            foreach (var emote in msg.EmoteSet.Emotes)
            {
                var path = await EmoteCache.Instance
                    .GetOrDownloadAsync(emote.Id, emote.ImageUrl).ConfigureAwait(false);
                emoteSegments.Add(new EmoteSegment(emote.StartIndex, emote.EndIndex, path ?? ""));
            }
        }

        // ── Third-party emotes (BTTV / FFZ / 7TV) ────────────────────────────
        emoteSegments = await ThirdPartyEmoteService.Instance
            .FindAndDownloadAsync(msg.Message, emoteSegments).ConfigureAwait(false);

        var user = new StreamUser(msg.UserId, msg.Username, msg.DisplayName,
            IsBroadcaster: msg.IsBroadcaster);

        var data = new Dictionary<string, object>
        {
            ["message"]        = msg.Message,
            ["color"]          = msg.HexColor ?? "#FFFFFF",
            ["messageId"]      = msg.Id,
            ["isBroadcaster"]  = msg.IsBroadcaster,
            ["isModerator"]    = isModerator,
            ["isSubscriber"]   = isSubscriber,
            ["isVip"]          = isVip,
            ["isHighlighted"]  = msg.IsHighlighted,
            ["isFirstMessage"] = msg.IsFirstMessage,
            ["bits"]           = msg.Bits,
            ["subMonths"]      = subMonths,
            ["emotes"]         = emoteSegments,
            ["badgePaths"]     = badgePaths,
        };

        await _bus.PublishAsync(new StreamEvent(Platform.Twitch, EventType.Chat, user, data, DateTimeOffset.UtcNow));
    }

    public async ValueTask DisposeAsync()
    {
        if (_botClient != null)
            await _botClient.DisconnectAsync();
        if (_client != null)
            await _client.DisconnectAsync();
    }
}
