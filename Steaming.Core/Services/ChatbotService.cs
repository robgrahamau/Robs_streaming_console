using System.Text.Json;
using System.Text.RegularExpressions;
using Steaming.Core.Models;
using Microsoft.Extensions.Logging;

namespace Steaming.Core.Services;

public enum BotReplyTarget
{
    Both,
    Twitch,
    Kick,
    YouTube,
}

public class BotCommand
{
    public string Name            { get; set; } = "";
    public string Response        { get; set; } = "";
    public bool   Enabled         { get; set; } = true;
    public int    CooldownSeconds { get; set; } = 10;
    public BotReplyTarget Target  { get; set; } = BotReplyTarget.Both;
    public DateTimeOffset LastUsed { get; set; }
    public string SoundFile       { get; set; } = "";   // optional audio played when the command fires
    public float  SoundVolume     { get; set; } = 1.0f;
    public bool   ModOnly         { get; set; }          // only mods + broadcaster may use it
    public string AllowedUsers    { get; set; } = "";    // comma-separated usernames also allowed
    public bool   AlertEnabled    { get; set; }          // also fire a "Unique" alert when this command runs
    public string AlertName       { get; set; } = "";    // which CustomAlert (by name) to fire
}

public class BotTimer
{
    public string Name            { get; set; } = "";
    public string Message         { get; set; } = "";
    public bool   Enabled         { get; set; } = true;
    public int    IntervalMinutes { get; set; } = 30;
    public BotReplyTarget Target  { get; set; } = BotReplyTarget.Both;
    public DateTimeOffset LastSent { get; set; }
}

// Event-triggered response — fires when the matching event type occurs on the matching platform.
// Response supports: {user}, {months} (Subscribe), {viewers} (Raid), {count} (GiftSubscribe).
public class BotShout
{
    public EventType      EventFilter { get; set; } = EventType.Follow;
    public BotReplyTarget Target      { get; set; } = BotReplyTarget.Both;
    public bool           Enabled     { get; set; } = true;
    public string         Response    { get; set; } = "";
}

public class AutoModSettings
{
    public bool         FilterLinks         { get; set; }
    public bool         FilterAllCaps       { get; set; }
    public List<string> BlockedWords        { get; set; } = [];
    public bool         TimeoutOnViolation  { get; set; }
    public int          TimeoutSeconds      { get; set; } = 60;
}

// Chatbot: command processing, interval timers, shout responses, and auto-moderation.
// Set SendMessage / TimeoutUser / GetCurrentGame / GetCurrentTitle / GetCurrentUptime delegates
// from the host before calling Start().
public class ChatbotService
{
    private static readonly Regex _linkRegex = new(@"https?://|www\.", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly EventBus _bus;
    private readonly ILogger<ChatbotService> _logger;
    private System.Timers.Timer? _timerTick;

    private readonly List<BotCommand> _commands = [];
    private readonly List<BotTimer>   _timers   = [];
    private readonly List<BotShout>   _shouts   = [];
    private readonly object _listLock = new();

    public AutoModSettings AutoMod { get; } = new();

    // Delegates wired by App.xaml.cs / AppStartupCoordinator after platform connection
    public Func<string, BotReplyTarget, Task>? SendMessage    { get; set; }
    public Func<string, int, Task>?            TimeoutUser    { get; set; }
    public Func<string, Task>?                 DeleteMessage  { get; set; }
    public Action<string, float>?              PlaySound      { get; set; }   // (filePath, volume)
    // Fires a user-defined "Unique" alert by name. (alertName, invokingUser, commandArg)
    public Func<string, StreamUser, string, Task>? TriggerCustomAlert { get; set; }

    // Token sources — wired to StreamDataService by the host
    public Func<string>? GetCurrentGame   { get; set; }
    public Func<string>? GetCurrentTitle  { get; set; }
    public Func<string>? GetCurrentUptime { get; set; }
    public Func<bool>?   IsLive           { get; set; }   // timers only fire while live

    // Going-live announcement. Sent once on the offline→live transition; doubles as a
    // start-of-stream health check that outbound chat (incl. the Kick bridge token) works.
    public bool   AnnounceLiveEnabled { get; set; } = true;
    public string AnnounceLiveMessage { get; set; } = "Is now live: {title} / {game}";

    public IReadOnlyList<BotCommand> Commands { get { lock (_listLock) return _commands.ToList(); } }
    public IReadOnlyList<BotTimer>   Timers   { get { lock (_listLock) return _timers.ToList(); } }
    public IReadOnlyList<BotShout>   Shouts   { get { lock (_listLock) return _shouts.ToList(); } }

    public ChatbotService(EventBus bus, ILogger<ChatbotService> logger)
    {
        _bus    = bus;
        _logger = logger;
    }

    private bool _started;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _bus.Subscribe(OnEvent);
        _timerTick = new System.Timers.Timer(60_000) { AutoReset = true };
        _timerTick.Elapsed += (_, _) => CheckTimers();
        _timerTick.Start();
    }

    public void Stop()
    {
        _timerTick?.Stop();
        _timerTick?.Dispose();
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public void AddCommand(BotCommand cmd)
    {
        lock (_listLock)
        {
            _commands.RemoveAll(c => c.Name.Equals(cmd.Name, StringComparison.OrdinalIgnoreCase));
            _commands.Add(cmd);
        }
    }

    public void RemoveCommand(string name)
    {
        lock (_listLock) _commands.RemoveAll(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    // ── Timers ────────────────────────────────────────────────────────────────

    public void AddTimer(BotTimer timer)
    {
        lock (_listLock)
        {
            _timers.RemoveAll(t => t.Name.Equals(timer.Name, StringComparison.OrdinalIgnoreCase));
            _timers.Add(timer);
        }
    }

    public void RemoveTimer(string name)
    {
        lock (_listLock) _timers.RemoveAll(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    // ── Shouts ────────────────────────────────────────────────────────────────

    public void AddShout(BotShout shout)
    {
        lock (_listLock) _shouts.Add(shout);
    }

    public void RemoveShout(BotShout shout)
    {
        lock (_listLock) _shouts.Remove(shout);
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Steaming");

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        object data;
        lock (_listLock) data = new
        {
            Commands = _commands.ToList(),
            Timers   = _timers.ToList(),
            Shouts   = _shouts.ToList(),
            AutoMod,
            AnnounceLiveEnabled,
            AnnounceLiveMessage
        };
        File.WriteAllText(Path.Combine(DataDir, "chatbot.json"),
            JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Load()
    {
        var path = Path.Combine(DataDir, "chatbot.json");
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("Commands", out var cmds))
                foreach (var c in cmds.EnumerateArray())
                    _commands.Add(new BotCommand
                    {
                        Name            = c.GetProperty("Name").GetString()     ?? "",
                        Response        = c.GetProperty("Response").GetString() ?? "",
                        Enabled         = !c.TryGetProperty("Enabled", out var e) || e.GetBoolean(),
                        CooldownSeconds = c.TryGetProperty("CooldownSeconds", out var cd) ? cd.GetInt32() : 10,
                        Target          = TryReadReplyTarget(c, out var ct) ? ct : BotReplyTarget.Both,
                        SoundFile       = c.TryGetProperty("SoundFile", out var sf) ? sf.GetString() ?? "" : "",
                        SoundVolume     = c.TryGetProperty("SoundVolume", out var sv) && sv.TryGetSingle(out var svf) ? svf : 1f,
                        ModOnly         = c.TryGetProperty("ModOnly", out var mo) && mo.ValueKind == JsonValueKind.True,
                        AllowedUsers    = c.TryGetProperty("AllowedUsers", out var au) ? au.GetString() ?? "" : "",
                        AlertEnabled    = c.TryGetProperty("AlertEnabled", out var ae) && ae.ValueKind == JsonValueKind.True,
                        AlertName       = c.TryGetProperty("AlertName", out var an) ? an.GetString() ?? "" : "",
                    });

            if (root.TryGetProperty("Timers", out var timers))
                foreach (var t in timers.EnumerateArray())
                    _timers.Add(new BotTimer
                    {
                        Name            = t.GetProperty("Name").GetString()    ?? "",
                        Message         = t.GetProperty("Message").GetString() ?? "",
                        Enabled         = !t.TryGetProperty("Enabled", out var te) || te.GetBoolean(),
                        IntervalMinutes = t.TryGetProperty("IntervalMinutes", out var ti) ? ti.GetInt32() : 30,
                        Target          = TryReadReplyTarget(t, out var tt) ? tt : BotReplyTarget.Both,
                    });

            if (root.TryGetProperty("Shouts", out var shouts))
                foreach (var s in shouts.EnumerateArray())
                {
                    EventType evtType = EventType.Follow;
                    if (s.TryGetProperty("EventFilter", out var ef))
                    {
                        // Save() writes enums as numbers; GetString() on a number THROWS and
                        // aborted the whole Load — silently wiping all shouts on every restart.
                        if (ef.ValueKind == JsonValueKind.Number && ef.TryGetInt32(out var efNum)
                            && Enum.IsDefined(typeof(EventType), efNum))
                            evtType = (EventType)efNum;
                        else if (ef.ValueKind == JsonValueKind.String)
                            Enum.TryParse(ef.GetString(), true, out evtType);
                    }
                    _shouts.Add(new BotShout
                    {
                        EventFilter = evtType,
                        Target      = TryReadReplyTarget(s, out var st) ? st : BotReplyTarget.Both,
                        Enabled     = !s.TryGetProperty("Enabled", out var se) || se.GetBoolean(),
                        Response    = s.TryGetProperty("Response", out var sr) ? sr.GetString() ?? "" : "",
                    });
                }

            if (root.TryGetProperty("AnnounceLiveEnabled", out var ale) &&
                (ale.ValueKind == JsonValueKind.True || ale.ValueKind == JsonValueKind.False))
                AnnounceLiveEnabled = ale.GetBoolean();
            if (root.TryGetProperty("AnnounceLiveMessage", out var alm) && alm.ValueKind == JsonValueKind.String)
                AnnounceLiveMessage = alm.GetString() ?? AnnounceLiveMessage;

            if (root.TryGetProperty("AutoMod", out var am))
            {
                if (am.TryGetProperty("FilterLinks",        out var fl)) AutoMod.FilterLinks        = fl.GetBoolean();
                if (am.TryGetProperty("FilterAllCaps",      out var fc)) AutoMod.FilterAllCaps      = fc.GetBoolean();
                if (am.TryGetProperty("TimeoutOnViolation", out var tv)) AutoMod.TimeoutOnViolation = tv.GetBoolean();
                if (am.TryGetProperty("TimeoutSeconds",     out var ts)) AutoMod.TimeoutSeconds     = ts.GetInt32();
                if (am.TryGetProperty("BlockedWords", out var bw))
                    foreach (var w in bw.EnumerateArray())
                        if (w.GetString() is string wstr) AutoMod.BlockedWords.Add(wstr);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[Chatbot] Load failed: {Message}", ex.Message);
        }
    }

    // ── Event handler ─────────────────────────────────────────────────────────

    private async Task OnEvent(StreamEvent evt)
    {
        // Auto-mod (chat only, Twitch only)
        if (evt.Type == EventType.Chat && evt.Platform == Platform.Twitch)
        {
            var text   = evt.Data.TryGetValue("message",   out var m)   ? m?.ToString() ?? "" : "";
            var userId = evt.User.Id;
            var msgId  = evt.Data.TryGetValue("messageId", out var mid) ? mid?.ToString() ?? "" : "";

            if (IsViolation(text))
            {
                _logger.LogInformation("[AutoMod] Flagged message from {User}: {Text}", evt.User.DisplayName, text);
                if (!string.IsNullOrEmpty(msgId) && DeleteMessage != null)
                    await DeleteMessage(msgId);
                if (AutoMod.TimeoutOnViolation && TimeoutUser != null && !string.IsNullOrEmpty(userId))
                    await TimeoutUser(userId, AutoMod.TimeoutSeconds);
                return;
            }

            // Commands
            if (text.StartsWith('!'))
            {
                var space   = text.IndexOf(' ');
                var cmdName = (space > 0 ? text[1..space] : text[1..]).ToLowerInvariant();
                var arg     = space > 0 ? text[(space + 1)..].Trim().TrimStart('@') : "";

                BotCommand? cmd;
                lock (_listLock)
                    cmd = _commands.FirstOrDefault(c =>
                        c.Name.Equals(cmdName, StringComparison.OrdinalIgnoreCase) && c.Enabled);
                if (cmd != null)
                {
                    await TryRunCommandAsync(cmd, evt, arg, cmd.Target);
                    return;
                }
            }
        }
        else if (evt.Type == EventType.Chat && evt.Platform == Platform.Kick)
        {
            // Commands from Kick chat
            var text = evt.Data.TryGetValue("message", out var m) ? m?.ToString() ?? "" : "";
            if (text.StartsWith('!'))
            {
                var space   = text.IndexOf(' ');
                var cmdName = (space > 0 ? text[1..space] : text[1..]).ToLowerInvariant();
                var arg     = space > 0 ? text[(space + 1)..].Trim().TrimStart('@') : "";

                BotCommand? cmd;
                lock (_listLock)
                    cmd = _commands.FirstOrDefault(c =>
                        c.Name.Equals(cmdName, StringComparison.OrdinalIgnoreCase) && c.Enabled &&
                        (c.Target == BotReplyTarget.Both || c.Target == BotReplyTarget.Kick));
                if (cmd != null)
                    await TryRunCommandAsync(cmd, evt, arg, BotReplyTarget.Kick);
            }
        }
        else if (evt.Type == EventType.Chat && evt.Platform == Platform.YouTube)
        {
            var text = evt.Data.TryGetValue("message", out var m) ? m?.ToString() ?? "" : "";
            if (text.StartsWith('!'))
            {
                var space   = text.IndexOf(' ');
                var cmdName = (space > 0 ? text[1..space] : text[1..]).ToLowerInvariant();
                var arg     = space > 0 ? text[(space + 1)..].Trim().TrimStart('@') : "";

                BotCommand? cmd;
                lock (_listLock)
                    cmd = _commands.FirstOrDefault(c =>
                        c.Name.Equals(cmdName, StringComparison.OrdinalIgnoreCase) && c.Enabled &&
                        (c.Target == BotReplyTarget.Both || c.Target == BotReplyTarget.YouTube));
                if (cmd != null)
                    await TryRunCommandAsync(cmd, evt, arg, BotReplyTarget.YouTube);
            }
        }

        // Shouts — any non-chat event type
        if (evt.Type != EventType.Chat)
        {
            List<BotShout> matching;
            lock (_listLock)
                matching = _shouts.Where(s =>
                    s.Enabled &&
                    s.EventFilter == evt.Type &&
                    (s.Target == BotReplyTarget.Both ||
                     (s.Target == BotReplyTarget.Twitch && evt.Platform == Platform.Twitch) ||
                     (s.Target == BotReplyTarget.Kick   && evt.Platform == Platform.Kick) ||
                     (s.Target == BotReplyTarget.YouTube && evt.Platform == Platform.YouTube))).ToList();

            foreach (var shout in matching)
            {
                // {platform} resolves to the platform the event originated on, so an "All" shout can
                // announce "...on Twitch" while still posting into every live chat.
                var response = ExpandTokens(shout.Response, evt.User.DisplayName, "", evt.Platform, evt.Data);
                if (SendMessage != null)
                    await SendMessage(response, shout.Target);
            }
        }
    }

    private async Task TryRunCommandAsync(BotCommand cmd, StreamEvent evt, string arg, BotReplyTarget sendTarget)
    {
        if (!IsAuthorized(cmd, evt.User)) return;

        // Mods and the broadcaster bypass the cooldown — a 10s default cooldown silently
        // swallowed back-to-back mod !so calls during a live stream.
        var now = DateTimeOffset.UtcNow;
        bool bypassCooldown = evt.User.IsBroadcaster || evt.User.IsMod;
        if (!bypassCooldown && (now - cmd.LastUsed).TotalSeconds < cmd.CooldownSeconds) return;

        cmd.LastUsed = now;
        PlayCommandSound(cmd);
        var response = ExpandTokens(cmd.Response, evt.User.DisplayName, arg, evt.Platform, evt.Data);
        if (SendMessage != null)
            await SendMessage(response, sendTarget);

        // Optionally also fire a unique alert tied to this command.
        if (cmd.AlertEnabled && !string.IsNullOrWhiteSpace(cmd.AlertName) && TriggerCustomAlert != null)
        {
            try { await TriggerCustomAlert(cmd.AlertName, evt.User, arg); }
            catch (Exception ex) { _logger.LogWarning("[Chatbot] Command alert failed for !{Name}: {Msg}", cmd.Name, ex.Message); }
        }
    }

    private static bool IsAuthorized(BotCommand cmd, StreamUser user)
    {
        if (!cmd.ModOnly && string.IsNullOrWhiteSpace(cmd.AllowedUsers)) return true;
        if (user.IsBroadcaster) return true;
        if (cmd.ModOnly && user.IsMod) return true;
        if (!string.IsNullOrWhiteSpace(cmd.AllowedUsers))
            foreach (var entry in cmd.AllowedUsers.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var name = entry.TrimStart('@');
                if (name.Equals(user.Username, StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(user.DisplayName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        return false;
    }

    private void PlayCommandSound(BotCommand cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.SoundFile) || PlaySound == null) return;
        try
        {
            if (File.Exists(cmd.SoundFile))
                PlaySound(cmd.SoundFile, Math.Clamp(cmd.SoundVolume, 0f, 1f));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[Chatbot] Command sound failed for !{Name}: {Msg}", cmd.Name, ex.Message);
        }
    }

    private string ExpandTokens(string template, string user, string arg, Platform? platform, IReadOnlyDictionary<string, object> data)
    {
        var months  = data.TryGetValue("months",  out var mo) ? mo?.ToString() ?? "1" : "1";
        var viewers = data.TryGetValue("viewers", out var vi) ? vi?.ToString() ?? "0" : "0";
        var count   = data.TryGetValue("count",   out var co) ? co?.ToString() ?? "1" : "1";
        var bits    = data.TryGetValue("bits",    out var bi) ? bi?.ToString() ?? "0" : "0";
        // Kick gift-sub events carry the gift size under "amount"; bits events use "bits". Fall back
        // to bits so {amount} works for both. {amountDisplay} prefers the platform's formatted string
        // (e.g. YouTube Super Chat "$5.00") and falls back to the raw amount.
        var amount  = data.TryGetValue("amount", out var am) ? am?.ToString() ?? bits : bits;
        var amountDisplay = data.TryGetValue("amountDisplay", out var ad) ? ad?.ToString() ?? "" : "";
        if (string.IsNullOrEmpty(amountDisplay)) amountDisplay = amount;

        return template
            .Replace("{user}",    user,                        StringComparison.OrdinalIgnoreCase)
            .Replace("{arg}",     arg,                         StringComparison.OrdinalIgnoreCase)
            .Replace("{platform}", platform?.ToString() ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{months}",  months,                      StringComparison.OrdinalIgnoreCase)
            .Replace("{viewers}", viewers,                     StringComparison.OrdinalIgnoreCase)
            .Replace("{count}",   count,                       StringComparison.OrdinalIgnoreCase)
            .Replace("{bits}",    bits,                        StringComparison.OrdinalIgnoreCase)
            .Replace("{amountDisplay}", amountDisplay,         StringComparison.OrdinalIgnoreCase)
            .Replace("{amount}",  amount,                      StringComparison.OrdinalIgnoreCase)
            .Replace("{game}",    GetCurrentGame?.Invoke()  ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{title}",   GetCurrentTitle?.Invoke() ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{uptime}",  GetCurrentUptime?.Invoke() ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{channel}", "",                          StringComparison.OrdinalIgnoreCase);
    }

    private bool IsViolation(string text)
    {
        if (AutoMod.FilterLinks && _linkRegex.IsMatch(text))
            return true;

        if (AutoMod.FilterAllCaps && text.Length > 8)
        {
            int upper = text.Count(char.IsUpper);
            int alpha = text.Count(char.IsLetter);
            if (alpha > 0 && (float)upper / alpha > 0.70f)
                return true;
        }

        foreach (var word in AutoMod.BlockedWords)
            if (!string.IsNullOrEmpty(word) &&
                text.Contains(word, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    private bool? _wasLive;

    private void CheckTimers()
    {
        var live = IsLive == null || IsLive();
        HandleLiveAnnounce(live);

        // Timers are stream announcements — never send them to an offline chat.
        if (!live) return;

        List<BotTimer> snapshot;
        lock (_listLock) snapshot = _timers.Where(x => x.Enabled).ToList();

        var now = DateTimeOffset.UtcNow;
        foreach (var t in snapshot)
        {
            if ((now - t.LastSent).TotalMinutes >= t.IntervalMinutes)
            {
                t.LastSent = now;
                if (SendMessage != null)
                    ObserveBackgroundTask(SendMessage(t.Message, t.Target), $"timer '{t.Name}'");
            }
        }
    }

    private void HandleLiveAnnounce(bool live)
    {
        var was = _wasLive;
        _wasLive = live;
        // First tick after startup only records the current state — announcing on every
        // app restart while already live would spam chat.
        if (was == null || was == true || !live || !AnnounceLiveEnabled) return;

        var msg = ExpandTokens(AnnounceLiveMessage, "", "", null, new Dictionary<string, object>());
        if (string.IsNullOrWhiteSpace(msg) || SendMessage == null) return;
        _logger.LogInformation("[Chatbot] Stream went live — sending announcement.");
        ObserveBackgroundTask(SendMessage(msg, BotReplyTarget.Both), "live announcement");
    }

    private void ObserveBackgroundTask(Task task, string operation)
    {
        _ = task.ContinueWith(t =>
        {
            var ex = t.Exception?.GetBaseException();
            if (ex != null)
                _logger.LogWarning("[Chatbot] Background send failed for {Operation}: {Message}", operation, ex.Message);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private static bool TryReadReplyTarget(JsonElement element, out BotReplyTarget target)
    {
        target = BotReplyTarget.Both;
        if (!element.TryGetProperty("Target", out var targetEl)) return false;

        if (targetEl.ValueKind == JsonValueKind.Number)
        {
            var raw = targetEl.GetInt32();
            if (Enum.IsDefined(typeof(BotReplyTarget), raw))
            {
                target = (BotReplyTarget)raw;
                return true;
            }
            return false;
        }

        if (targetEl.ValueKind == JsonValueKind.String &&
            Enum.TryParse<BotReplyTarget>(targetEl.GetString(), true, out var parsed))
        {
            target = parsed;
            return true;
        }

        return false;
    }
}
