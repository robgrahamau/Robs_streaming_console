using Steaming.Core;
using Steaming.Core.Models;
using Steaming.Core.Services;
using Steaming.Data;

namespace Steaming.Application.Services;

public sealed class AnalyticsCollectorService : IDisposable
{
    private readonly EventBus _bus;
    private readonly StreamDataService _streamData;
    private readonly AnalyticsRepository _repo;

    // Independent sessions — one per platform. "Both"/"All" are query/report concepts only.
    private long? _twitchSessionId;
    private long? _kickSessionId;
    private long? _youtubeSessionId;
    private DateTimeOffset? _twitchSessionStart;
    private DateTimeOffset? _kickSessionStart;
    private DateTimeOffset? _youtubeSessionStart;
    private DateTimeOffset? _lastKnownTwitchApiStart;

    // Per-platform chatter tracking so each platform's chatters floor its viewer count
    private readonly HashSet<string> _allChatters          = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _twitchSessionChatters = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _kickSessionChatters   = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _youtubeSessionChatters = new(StringComparer.OrdinalIgnoreCase);
    private int _intervalChatCount;

    private string _lastTwitchTitle    = "";
    private string _lastTwitchCategory = "";
    private string _lastKickTitle      = "";
    private string _lastKickCategory   = "";

    // Resume (rather than duplicate) a session if one for the platform is still open or ended within
    // this window — covers app restarts and transient "not live" flaps mid-stream.
    private static readonly TimeSpan ResumeWindow = TimeSpan.FromMinutes(20);
    // Require this many consecutive "not live" polls before ending a session (debounce API hiccups).
    private const int OffStreakToEnd = 2;
    private int _twitchOffStreak;
    private int _kickOffStreak;
    private int _youtubeOffStreak;

    private readonly CancellationTokenSource _cts = new();
    private Task _pollTask = Task.CompletedTask;
    private bool _started;

    public long? CurrentSessionId => _twitchSessionId ?? _kickSessionId;

    public AnalyticsCollectorService(EventBus bus, StreamDataService streamData, AnalyticsRepository repo)
    {
        _bus = bus;
        _streamData = streamData;
        _repo = repo;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        // Close only genuinely-stale orphans (crash leftovers). Recent open sessions are left open so
        // a mid-stream restart resumes the SAME session instead of starting a duplicate.
        _repo.CloseStaleOpenSessions(ResumeWindow);
        _bus.Subscribe(OnEventAsync);
        _pollTask = WatchSessionAsync(_cts.Token);
    }

    private async Task WatchSessionAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                bool twitchLive     = _streamData.TwitchIsLive;
                bool kickLive       = _streamData.KickIsLive;
                bool youtubeLive    = _streamData.YouTubeIsLive;
                var  twitchApiStart = _streamData.StreamStartedAt;
                var  twitchTitle    = _streamData.StreamTitle;
                var  twitchCategory = _streamData.StreamCategory;
                var  kickTitle      = _streamData.KickStreamTitle;
                var  kickCategory   = _streamData.KickStreamCategory;

                var chatCount = Interlocked.Exchange(ref _intervalChatCount, 0);

                // Viewer count floor: if API returns 0 but we know chatters were present, use chatter count.
                // A chatter is proof of at least 1 viewer — chat requires being in the stream.
                int twitchViewers = Math.Max(_streamData.TwitchViewerCount, _twitchSessionChatters.Count);
                int kickViewers   = Math.Max(_streamData.KickViewerCount,   _kickSessionChatters.Count);
                int youtubeViewers = Math.Max(_streamData.YouTubeViewerCount, _youtubeSessionChatters.Count);

                // ── Twitch session ────────────────────────────────────────────
                if (twitchLive)
                {
                    bool twitchRestarted = _twitchSessionId.HasValue
                        && twitchApiStart.HasValue
                        && twitchApiStart != _lastKnownTwitchApiStart
                        && twitchApiStart > _twitchSessionStart!.Value.AddMinutes(5);

                    if (!_twitchSessionId.HasValue || twitchRestarted)
                    {
                        if (twitchRestarted) EndTwitchSession();
                        var start = twitchApiStart ?? DateTimeOffset.UtcNow;
                        // Resume the existing session for this broadcast (matched by API start) instead
                        // of inserting a duplicate row; only a genuinely new broadcast creates a new row.
                        var (sid, realStart) = _repo.ResumeOrStartSession(
                            start, "Twitch", twitchTitle, twitchCategory, ResumeWindow, matchByStart: true);
                        _twitchSessionId         = sid;
                        _twitchSessionStart      = realStart;
                        _lastKnownTwitchApiStart = twitchApiStart;
                        _lastTwitchTitle         = twitchTitle;
                        _lastTwitchCategory      = twitchCategory;
                        _repo.AddSnapshot(sid, DateTimeOffset.UtcNow, twitchViewers, 0, chatCount);
                    }
                    else
                    {
                        _lastKnownTwitchApiStart = twitchApiStart;
                        _repo.AddSnapshot(_twitchSessionId.Value, DateTimeOffset.UtcNow,
                            twitchViewers, 0, chatCount);
                        _repo.UpdateChatters(_twitchSessionId.Value, _allChatters.Count);
                        if (twitchTitle != _lastTwitchTitle || twitchCategory != _lastTwitchCategory)
                        {
                            _repo.UpdateSessionMeta(_twitchSessionId.Value, twitchTitle, twitchCategory);
                            _lastTwitchTitle    = twitchTitle;
                            _lastTwitchCategory = twitchCategory;
                        }
                    }
                    _twitchOffStreak = 0;
                }
                else if (_twitchSessionId.HasValue)
                {
                    // Debounce: only end after several consecutive not-live polls (API can blip).
                    if (++_twitchOffStreak >= OffStreakToEnd) EndTwitchSession();
                }

                // ── Kick session ──────────────────────────────────────────────
                if (kickLive)
                {
                    if (!_kickSessionId.HasValue)
                    {
                        var start = DateTimeOffset.UtcNow;
                        // Kick has no stable API stream-start, so resume the most recent Kick session
                        // that's still open or ended within the window (restart / flap); else new row.
                        var (sid, realStart) = _repo.ResumeOrStartSession(
                            start, "Kick", kickTitle, kickCategory, ResumeWindow, matchByStart: false);
                        _kickSessionId    = sid;
                        _kickSessionStart = realStart;
                        _lastKickTitle    = kickTitle;
                        _lastKickCategory = kickCategory;
                        _repo.AddSnapshot(sid, DateTimeOffset.UtcNow, 0, kickViewers, chatCount);
                    }
                    else
                    {
                        _repo.AddSnapshot(_kickSessionId.Value, DateTimeOffset.UtcNow,
                            0, kickViewers, chatCount);
                        _repo.UpdateChatters(_kickSessionId.Value, _allChatters.Count);
                        if (kickTitle != _lastKickTitle || kickCategory != _lastKickCategory)
                        {
                            _repo.UpdateSessionMeta(_kickSessionId.Value, kickTitle, kickCategory);
                            _lastKickTitle    = kickTitle;
                            _lastKickCategory = kickCategory;
                        }
                    }
                    _kickOffStreak = 0;
                }
                else if (_kickSessionId.HasValue)
                {
                    if (++_kickOffStreak >= OffStreakToEnd) EndKickSession();
                }

                // ── YouTube session ───────────────────────────────────────────
                if (youtubeLive)
                {
                    if (!_youtubeSessionId.HasValue)
                    {
                        var start = DateTimeOffset.UtcNow;
                        // YouTube (like Kick) has no stable API stream-start, so resume the most recent
                        // YouTube session still open or ended within the window; else start a new row.
                        var (sid, realStart) = _repo.ResumeOrStartSession(
                            start, "YouTube", "", "", ResumeWindow, matchByStart: false);
                        _youtubeSessionId    = sid;
                        _youtubeSessionStart = realStart;
                        _repo.AddSnapshot(sid, DateTimeOffset.UtcNow, 0, 0, chatCount, youtubeViewers);
                    }
                    else
                    {
                        _repo.AddSnapshot(_youtubeSessionId.Value, DateTimeOffset.UtcNow,
                            0, 0, chatCount, youtubeViewers);
                        _repo.UpdateChatters(_youtubeSessionId.Value, _allChatters.Count);
                    }
                    _youtubeOffStreak = 0;
                }
                else if (_youtubeSessionId.HasValue)
                {
                    if (++_youtubeOffStreak >= OffStreakToEnd) EndYouTubeSession();
                }

                if (!twitchLive && !kickLive && !youtubeLive)
                {
                    _allChatters.Clear();
                    _twitchSessionChatters.Clear();
                    _kickSessionChatters.Clear();
                    _youtubeSessionChatters.Clear();
                }
            }
            catch { }

            try { await Task.Delay(30_000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void EndTwitchSession()
    {
        if (!_twitchSessionId.HasValue) return;
        try { _repo.EndSession(_twitchSessionId.Value, DateTimeOffset.UtcNow, _allChatters.Count); }
        catch { }
        _twitchSessionId         = null;
        _twitchSessionStart      = null;
        _lastKnownTwitchApiStart = null;
        _twitchSessionChatters.Clear();
    }

    private void EndKickSession()
    {
        if (!_kickSessionId.HasValue) return;
        try { _repo.EndSession(_kickSessionId.Value, DateTimeOffset.UtcNow, _allChatters.Count); }
        catch { }
        _kickSessionId    = null;
        _kickSessionStart = null;
        _kickSessionChatters.Clear();
    }

    private void EndYouTubeSession()
    {
        if (!_youtubeSessionId.HasValue) return;
        try { _repo.EndSession(_youtubeSessionId.Value, DateTimeOffset.UtcNow, _allChatters.Count); }
        catch { }
        _youtubeSessionId    = null;
        _youtubeSessionStart = null;
        _youtubeSessionChatters.Clear();
    }

    private Task OnEventAsync(StreamEvent evt)
    {
        switch (evt.Type)
        {
            case EventType.Chat:
                if (!string.IsNullOrWhiteSpace(evt.User.Username))
                {
                    _allChatters.Add(evt.User.Username);
                    if (evt.Platform == Platform.Twitch)
                        _twitchSessionChatters.Add(evt.User.Username);
                    else if (evt.Platform == Platform.Kick)
                        _kickSessionChatters.Add(evt.User.Username);
                    else if (evt.Platform == Platform.YouTube)
                        _youtubeSessionChatters.Add(evt.User.Username);
                }
                Interlocked.Increment(ref _intervalChatCount);
                break;

            case EventType.Follow:
            {
                var sid = evt.Platform switch
                {
                    Platform.Kick    => _kickSessionId,
                    Platform.YouTube => _youtubeSessionId,
                    _                => _twitchSessionId,
                };
                if (sid.HasValue)
                {
                    var id = sid.Value;
                    _ = Task.Run(() => { try { _repo.IncrementFollows(id, evt.Platform.ToString()); } catch { } });
                }
                break;
            }

            case EventType.Subscribe:
            case EventType.GiftSubscribe:
            {
                var sid = evt.Platform switch
                {
                    Platform.Kick    => _kickSessionId,
                    Platform.YouTube => _youtubeSessionId,
                    _                => _twitchSessionId,
                };
                if (sid.HasValue)
                {
                    var id = sid.Value;
                    var count = evt.Data.TryGetValue("count", out var c) && c is int ci ? Math.Max(1, ci) : 1;
                    _ = Task.Run(() => { try { _repo.IncrementSubs(id, count); } catch { } });
                }
                break;
            }
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _cts.Cancel();
        EndTwitchSession();
        EndKickSession();
        EndYouTubeSession();
        try { _pollTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
