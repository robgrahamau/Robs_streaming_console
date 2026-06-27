using Microsoft.Data.Sqlite;

namespace Steaming.Data;

public record StreamSession(
    long Id,
    string Platform,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int PeakViewers,
    double AvgViewers,
    int TotalFollows,
    int TotalSubs,
    int SampleCount,
    string Title = "",
    string Category = "",
    int UniqueChatters = 0,
    int TwitchPeakViewers = 0,
    int KickPeakViewers   = 0,
    double TwitchAvgViewers = 0,
    double KickAvgViewers   = 0,
    int TwitchSampleCount = 0,
    int KickSampleCount   = 0,
    long? KickSessionId   = null,
    int YouTubePeakViewers = 0,
    double YouTubeAvgViewers = 0,
    int YouTubeSampleCount = 0,
    // Session ids merged into this (combined) row, so the snapshot chart can union them.
    IReadOnlyList<long>? MergedSessionIds = null)
{
    public TimeSpan Duration => (EndedAt ?? DateTimeOffset.UtcNow) - StartedAt;
    public bool IsLive => EndedAt is null;
}

public record ViewerSnapshot(
    long SessionId,
    DateTimeOffset Timestamp,
    int TwitchViewers,
    int KickViewers,
    int ChatCount = 0,
    int YouTubeViewers = 0)
{
    public int Total => TwitchViewers + KickViewers + YouTubeViewers;
}

public record AllTimeStats(
    int TotalStreams,
    TimeSpan TotalStreamTime,
    int TotalFollows,
    int TotalSubs,
    int PeakViewers,
    double AvgViewers,
    int TwitchFollows,
    int KickFollows,
    int TwitchSubs,
    int KickSubs,
    int YouTubeFollows = 0,
    int YouTubeSubs = 0);

public class AnalyticsRepository : IDisposable
{
    private static readonly string DbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Steaming", "analytics.db");

    private readonly SqliteConnection _conn;
    private readonly object _lock = new();

    public AnalyticsRepository()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        _conn = new SqliteConnection($"Data Source={DbPath}");
        _conn.Open();
        Migrate();
    }

    private void Migrate()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS stream_sessions (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                platform        TEXT    NOT NULL DEFAULT 'Both',
                started_at      INTEGER NOT NULL,
                ended_at        INTEGER,
                peak_viewers    INTEGER NOT NULL DEFAULT 0,
                avg_viewers     REAL    NOT NULL DEFAULT 0,
                total_follows   INTEGER NOT NULL DEFAULT 0,
                total_subs      INTEGER NOT NULL DEFAULT 0,
                sample_count    INTEGER NOT NULL DEFAULT 0,
                title           TEXT    NOT NULL DEFAULT '',
                category        TEXT    NOT NULL DEFAULT '',
                unique_chatters INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS viewer_snapshots (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id      INTEGER NOT NULL REFERENCES stream_sessions(id),
                timestamp       INTEGER NOT NULL,
                twitch_viewers  INTEGER NOT NULL DEFAULT 0,
                kick_viewers    INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_snapshots_session
                ON viewer_snapshots(session_id, timestamp);
            CREATE INDEX IF NOT EXISTS idx_sessions_started
                ON stream_sessions(started_at DESC);
            """;
        cmd.ExecuteNonQuery();

        // Safe migration: add new columns to existing databases that lack them.
        // We check the existing columns first (PRAGMA table_info) and only ALTER the missing
        // ones. Blindly ALTERing and catching the "duplicate column" error works, but it throws
        // a first-chance SqliteException per already-present column on every startup — noise in
        // the debugger output (10 of them on an up-to-date DB).
        AddMissingColumns("stream_sessions", new[] {
            ("title",               "TEXT    NOT NULL DEFAULT ''"),
            ("category",            "TEXT    NOT NULL DEFAULT ''"),
            ("unique_chatters",     "INTEGER NOT NULL DEFAULT 0"),
            ("twitch_peak_viewers", "INTEGER NOT NULL DEFAULT 0"),
            ("kick_peak_viewers",   "INTEGER NOT NULL DEFAULT 0"),
            ("twitch_avg_viewers",  "REAL    NOT NULL DEFAULT 0"),
            ("kick_avg_viewers",    "REAL    NOT NULL DEFAULT 0"),
            ("twitch_sample_count", "INTEGER NOT NULL DEFAULT 0"),
            ("kick_sample_count",   "INTEGER NOT NULL DEFAULT 0"),
            // YouTube — additive. Existing rows default 0 = "no YouTube data for this session", not
            // "zero viewers"; the combined views only count a platform's column when that platform
            // actually has a session in the cluster, so a 0 here never fabricates a fake data point.
            ("youtube_peak_viewers", "INTEGER NOT NULL DEFAULT 0"),
            ("youtube_avg_viewers",  "REAL    NOT NULL DEFAULT 0"),
            ("youtube_sample_count", "INTEGER NOT NULL DEFAULT 0"),
        });

        AddMissingColumns("viewer_snapshots", new[] {
            ("chat_count",      "INTEGER NOT NULL DEFAULT 0"),
            ("youtube_viewers", "INTEGER NOT NULL DEFAULT 0"),
        });
    }

    // Adds only the columns not already present on the table, so an up-to-date DB performs
    // zero ALTERs (and throws zero first-chance exceptions).
    private void AddMissingColumns(string table, (string col, string def)[] columns)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = _conn.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info({table})";
            using var r = info.ExecuteReader();
            while (r.Read())
                existing.Add(r.GetString(1)); // column 1 = name
        }

        foreach (var (col, def) in columns)
        {
            if (existing.Contains(col)) continue;
            using var alter = _conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {col} {def}";
            alter.ExecuteNonQuery();
        }
    }

    // ── Session management ────────────────────────────────────────────────────

    public long StartSession(DateTimeOffset startedAt, string platform = "Both", string title = "", string category = "")
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO stream_sessions(started_at,platform,title,category) VALUES($ts,$p,$t,$c); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$ts", startedAt.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$p", platform);
            cmd.Parameters.AddWithValue("$t", title);
            cmd.Parameters.AddWithValue("$c", category);
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public void UpdateSessionMeta(long sessionId, string title, string category, string? platform = null)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            if (platform != null)
            {
                cmd.CommandText = "UPDATE stream_sessions SET title=$t, category=$c, platform=$p WHERE id=$id";
                cmd.Parameters.AddWithValue("$p", platform);
            }
            else
            {
                cmd.CommandText = "UPDATE stream_sessions SET title=$t, category=$c WHERE id=$id";
            }
            cmd.Parameters.AddWithValue("$t", title);
            cmd.Parameters.AddWithValue("$c", category);
            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.ExecuteNonQuery();
        }
    }

    public void UpdateChatters(long sessionId, int count)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE stream_sessions SET unique_chatters=$c WHERE id=$id";
            cmd.Parameters.AddWithValue("$c", count);
            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.ExecuteNonQuery();
        }
    }

    public void CloseOrphanedSessions(DateTimeOffset closedAt)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE stream_sessions SET ended_at=$e WHERE ended_at IS NULL";
            cmd.Parameters.AddWithValue("$e", closedAt.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        }
    }

    // Resume an existing session for this platform instead of inserting a duplicate, so a live-flap or
    // an app restart mid-stream continues the SAME row (the "merge if one already exists" behaviour).
    //   matchByStart=true  (Twitch): match a session whose started_at == startedAt (the API stream-start
    //                                 uniquely identifies the broadcast). A new broadcast → new row.
    //   matchByStart=false (Kick):   no stable API start, so resume the most recent session for the
    //                                 platform that is still open or ended within resumeWindow.
    // Returns the (possibly existing) session id and its real started_at.
    public (long Id, DateTimeOffset Started) ResumeOrStartSession(
        DateTimeOffset startedAt, string platform, string title, string category,
        TimeSpan resumeWindow, bool matchByStart)
    {
        lock (_lock)
        {
            long cutoff = DateTimeOffset.UtcNow.Subtract(resumeWindow).ToUnixTimeSeconds();
            long? foundId = null; long foundStart = 0;

            using (var find = _conn.CreateCommand())
            {
                if (matchByStart)
                {
                    find.CommandText =
                        "SELECT id, started_at FROM stream_sessions " +
                        "WHERE platform=$p AND started_at=$ts AND (ended_at IS NULL OR ended_at >= $cutoff) " +
                        "ORDER BY id DESC LIMIT 1";
                    find.Parameters.AddWithValue("$ts", startedAt.ToUnixTimeSeconds());
                }
                else
                {
                    find.CommandText =
                        "SELECT id, started_at FROM stream_sessions " +
                        "WHERE platform=$p AND (ended_at IS NULL OR ended_at >= $cutoff) " +
                        "ORDER BY started_at DESC LIMIT 1";
                }
                find.Parameters.AddWithValue("$p", platform);
                find.Parameters.AddWithValue("$cutoff", cutoff);
                using var r = find.ExecuteReader();
                if (r.Read()) { foundId = r.GetInt64(0); foundStart = r.GetInt64(1); }
            }

            if (foundId.HasValue)
            {
                using var re = _conn.CreateCommand();
                re.CommandText = "UPDATE stream_sessions SET ended_at=NULL WHERE id=$id"; // reopen
                re.Parameters.AddWithValue("$id", foundId.Value);
                re.ExecuteNonQuery();
                return (foundId.Value, DateTimeOffset.FromUnixTimeSeconds(foundStart));
            }

            using var ins = _conn.CreateCommand();
            ins.CommandText =
                "INSERT INTO stream_sessions(started_at,platform,title,category) VALUES($ts,$p,$t,$c); SELECT last_insert_rowid();";
            ins.Parameters.AddWithValue("$ts", startedAt.ToUnixTimeSeconds());
            ins.Parameters.AddWithValue("$p", platform);
            ins.Parameters.AddWithValue("$t", title);
            ins.Parameters.AddWithValue("$c", category);
            return ((long)ins.ExecuteScalar()!, startedAt);
        }
    }

    // Closes only genuinely-stale open sessions (last activity older than staleAfter), ending them at
    // their last snapshot time (NOT "now", which would inflate duration). Recent open sessions are left
    // open so ResumeOrStartSession can continue them after an app restart.
    public void CloseStaleOpenSessions(TimeSpan staleAfter)
    {
        lock (_lock)
        {
            long cutoff = DateTimeOffset.UtcNow.Subtract(staleAfter).ToUnixTimeSeconds();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE stream_sessions
                SET ended_at = COALESCE(
                    (SELECT MAX(timestamp) FROM viewer_snapshots v WHERE v.session_id = stream_sessions.id),
                    started_at)
                WHERE ended_at IS NULL
                  AND COALESCE(
                    (SELECT MAX(timestamp) FROM viewer_snapshots v WHERE v.session_id = stream_sessions.id),
                    started_at) < $cutoff
                """;
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            cmd.ExecuteNonQuery();
        }
    }

    public void EndSession(long sessionId, DateTimeOffset endedAt, int uniqueChatters = 0)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE stream_sessions SET ended_at=$e, unique_chatters=$uc WHERE id=$id";
            cmd.Parameters.AddWithValue("$e", endedAt.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$uc", uniqueChatters);
            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.ExecuteNonQuery();
        }
    }

    public void AddSnapshot(long sessionId, DateTimeOffset timestamp, int twitchViewers, int kickViewers, int chatCount = 0, int youtubeViewers = 0)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();

            using var ins = _conn.CreateCommand();
            ins.CommandText =
                "INSERT INTO viewer_snapshots(session_id,timestamp,twitch_viewers,kick_viewers,chat_count,youtube_viewers) VALUES($sid,$ts,$tv,$kv,$cc,$yv)";
            ins.Parameters.AddWithValue("$sid", sessionId);
            ins.Parameters.AddWithValue("$ts", timestamp.ToUnixTimeSeconds());
            ins.Parameters.AddWithValue("$tv", twitchViewers);
            ins.Parameters.AddWithValue("$kv", kickViewers);
            ins.Parameters.AddWithValue("$cc", chatCount);
            ins.Parameters.AddWithValue("$yv", youtubeViewers);
            ins.ExecuteNonQuery();

            // Update session rolling stats — combined and per-platform
            int total = twitchViewers + kickViewers + youtubeViewers;
            using var upd = _conn.CreateCommand();
            upd.CommandText = """
                UPDATE stream_sessions SET
                    peak_viewers        = MAX(peak_viewers, $total),
                    avg_viewers         = (avg_viewers * sample_count + $total) / (sample_count + 1),
                    sample_count        = sample_count + 1,
                    twitch_peak_viewers = MAX(twitch_peak_viewers, $tv),
                    twitch_avg_viewers  = CASE WHEN twitch_sample_count = 0
                                              THEN $tv
                                              ELSE (twitch_avg_viewers * twitch_sample_count + $tv) / (twitch_sample_count + 1) END,
                    twitch_sample_count = twitch_sample_count + 1,
                    kick_peak_viewers   = MAX(kick_peak_viewers, $kv),
                    kick_avg_viewers    = CASE WHEN kick_sample_count = 0
                                              THEN $kv
                                              ELSE (kick_avg_viewers * kick_sample_count + $kv) / (kick_sample_count + 1) END,
                    kick_sample_count   = kick_sample_count + 1,
                    youtube_peak_viewers = MAX(youtube_peak_viewers, $yv),
                    youtube_avg_viewers  = CASE WHEN youtube_sample_count = 0
                                              THEN $yv
                                              ELSE (youtube_avg_viewers * youtube_sample_count + $yv) / (youtube_sample_count + 1) END,
                    youtube_sample_count = youtube_sample_count + 1
                WHERE id = $sid
                """;
            upd.Parameters.AddWithValue("$total", total);
            upd.Parameters.AddWithValue("$tv",    twitchViewers);
            upd.Parameters.AddWithValue("$kv",    kickViewers);
            upd.Parameters.AddWithValue("$yv",    youtubeViewers);
            upd.Parameters.AddWithValue("$sid",   sessionId);
            upd.ExecuteNonQuery();

            tx.Commit();
        }
    }

    public void IncrementFollows(long sessionId, string platform)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE stream_sessions SET total_follows = total_follows + 1 WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.ExecuteNonQuery();
        }
    }

    public void IncrementSubs(long sessionId, int count = 1)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE stream_sessions SET total_subs = total_subs + $c WHERE id=$id";
            cmd.Parameters.AddWithValue("$c", count);
            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.ExecuteNonQuery();
        }
    }

    // ── Queries ───────────────────────────────────────────────────────────────

    private const string SessionColumns =
        "id,platform,started_at,ended_at,peak_viewers,avg_viewers,total_follows,total_subs,sample_count,title,category,unique_chatters," +
        "twitch_peak_viewers,kick_peak_viewers,twitch_avg_viewers,kick_avg_viewers,twitch_sample_count,kick_sample_count," +
        "youtube_peak_viewers,youtube_avg_viewers,youtube_sample_count";

    // Platform filter tokens accepted by the analytics queries:
    //   null              → All (merge Twitch + Kick + YouTube)
    //   "Twitch"/"Kick"/"YouTube"        → Single (that platform's own sessions, no merge)
    //   "Twitch+Kick"/"Twitch+YouTube"/"Kick+YouTube" → Dual (merge just those two)
    //   "Both" (legacy)   → treated as Twitch+Kick
    private static readonly string[] AllPlatforms = { "Twitch", "Kick", "YouTube" };

    private static (HashSet<string> include, bool single) ParsePlatforms(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
            return (new HashSet<string>(AllPlatforms, StringComparer.OrdinalIgnoreCase), false);
        if (platform.Equals("Both", StringComparison.OrdinalIgnoreCase))
            return (new HashSet<string>(new[] { "Twitch", "Kick" }, StringComparer.OrdinalIgnoreCase), false);
        var parts = platform.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var set = new HashSet<string>(parts, StringComparer.OrdinalIgnoreCase);
        return (set, set.Count <= 1);
    }

    public List<StreamSession> GetSessions(int limit = 50, string? platform = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        lock (_lock)
        {
            var (include, single) = ParsePlatforms(platform);
            var rows = LoadSessions(include, from, to);
            // Single platform → return its own rows untouched. Dual/All → cluster sessions that
            // overlap in time across the included platforms into one combined row each.
            var combined = single ? rows : MergeSessions(rows);
            return combined.OrderByDescending(s => s.StartedAt).Take(limit).ToList();
        }
    }

    // Loads single-platform session rows for the included platforms (assumes _lock held).
    // 'Both' is a legacy label from before the two-session design — fold it into Twitch+Kick views.
    private List<StreamSession> LoadSessions(HashSet<string> include, DateTimeOffset? from, DateTimeOffset? to)
    {
        var wanted = new List<string>(include);
        bool includeLegacyBoth = include.Contains("Twitch") && include.Contains("Kick");
        if (includeLegacyBoth) wanted.Add("Both");

        using var cmd = _conn.CreateCommand();
        var placeholders = new List<string>();
        for (int i = 0; i < wanted.Count; i++)
        {
            placeholders.Add($"$p{i}");
            cmd.Parameters.AddWithValue($"$p{i}", wanted[i]);
        }
        var dateFilter = BuildDateFilter(from, to, cmd);
        cmd.CommandText =
            $"SELECT {SessionColumns} FROM stream_sessions WHERE platform IN ({string.Join(",", placeholders)}){dateFilter} ORDER BY started_at DESC";

        using var r = cmd.ExecuteReader();
        var list = new List<StreamSession>();
        while (r.Read())
            list.Add(ReadSession(r));
        return list;
    }

    // Two single-platform sessions belong together when their time ranges intersect (an open session
    // is treated as +24h) and they start within an hour of each other — the original pairing rule.
    private static bool Overlaps(StreamSession a, StreamSession b)
    {
        long aStart = a.StartedAt.ToUnixTimeSeconds();
        long bStart = b.StartedAt.ToUnixTimeSeconds();
        long aEnd = (a.EndedAt ?? a.StartedAt.AddSeconds(86400)).ToUnixTimeSeconds();
        long bEnd = (b.EndedAt ?? b.StartedAt.AddSeconds(86400)).ToUnixTimeSeconds();
        return aStart < bEnd && bStart < aEnd && Math.Abs(aStart - bStart) < 3600;
    }

    // Clusters overlapping sessions (union-find by transitive overlap) and folds each cluster into one
    // combined StreamSession. Works for any platform count, so Dual and All share one code path.
    private static List<StreamSession> MergeSessions(List<StreamSession> rows)
    {
        var clusters = new List<List<StreamSession>>();
        foreach (var row in rows.OrderBy(s => s.StartedAt))
        {
            var hit = clusters.FirstOrDefault(c => c.Any(s => Overlaps(s, row)));
            if (hit != null) hit.Add(row);
            else clusters.Add(new List<StreamSession> { row });
        }

        var result = new List<StreamSession>();
        foreach (var cluster in clusters)
            result.Add(cluster.Count == 1 ? cluster[0] : CombineCluster(cluster));
        return result;
    }

    private static StreamSession CombineCluster(List<StreamSession> cluster)
    {
        StreamSession? Of(string p) => cluster.FirstOrDefault(s => s.Platform.Equals(p, StringComparison.OrdinalIgnoreCase));
        var tw = Of("Twitch"); var kk = Of("Kick"); var yt = Of("YouTube");

        var platforms = cluster.Select(s => s.Platform)
            .Where(p => p is "Twitch" or "Kick" or "YouTube")
            .Distinct().ToList();
        // 3 platforms → "All"; 2 → "Both" (keeps existing dual behaviour); else the single label.
        string label = platforms.Count >= 3 ? "All" : platforms.Count == 2 ? "Both" : (platforms.FirstOrDefault() ?? cluster[0].Platform);

        int totalSamples = cluster.Sum(s => s.SampleCount);
        double avg = totalSamples == 0 ? 0 : cluster.Sum(s => s.AvgViewers * s.SampleCount) / totalSamples;
        var rep = tw ?? cluster.OrderBy(s => s.StartedAt).First();

        return new StreamSession(
            Id: rep.Id,
            Platform: label,
            StartedAt: cluster.Min(s => s.StartedAt),
            // Open if ANY member is still open (matches the original NULL-if-either-open rule).
            EndedAt: cluster.Any(s => s.EndedAt is null) ? null : cluster.Max(s => s.EndedAt),
            PeakViewers: cluster.Sum(s => s.PeakViewers),
            AvgViewers: avg,
            TotalFollows: cluster.Sum(s => s.TotalFollows),
            TotalSubs: cluster.Sum(s => s.TotalSubs),
            SampleCount: totalSamples,
            Title: (tw ?? rep).Title,
            Category: (tw ?? rep).Category,
            UniqueChatters: cluster.Max(s => s.UniqueChatters),
            TwitchPeakViewers: tw?.TwitchPeakViewers ?? 0,
            KickPeakViewers: kk?.KickPeakViewers ?? 0,
            TwitchAvgViewers: tw?.TwitchAvgViewers ?? 0,
            KickAvgViewers: kk?.KickAvgViewers ?? 0,
            TwitchSampleCount: tw?.TwitchSampleCount ?? 0,
            KickSampleCount: kk?.KickSampleCount ?? 0,
            KickSessionId: kk?.Id,
            YouTubePeakViewers: yt?.YouTubePeakViewers ?? 0,
            YouTubeAvgViewers: yt?.YouTubeAvgViewers ?? 0,
            YouTubeSampleCount: yt?.YouTubeSampleCount ?? 0,
            MergedSessionIds: cluster.Select(s => s.Id).ToList());
    }

    public StreamSession? GetSession(long id)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT {SessionColumns} FROM stream_sessions WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadSession(r) : null;
        }
    }

    public List<ViewerSnapshot> GetSnapshots(long sessionId)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "SELECT session_id,timestamp,twitch_viewers,kick_viewers,chat_count,youtube_viewers FROM viewer_snapshots WHERE session_id=$sid ORDER BY timestamp ASC";
            cmd.Parameters.AddWithValue("$sid", sessionId);

            using var r = cmd.ExecuteReader();
            var list = new List<ViewerSnapshot>();
            while (r.Read())
                list.Add(new ViewerSnapshot(
                    r.GetInt64(0),
                    DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(1)),
                    r.GetInt32(2),
                    r.GetInt32(3),
                    r.FieldCount > 4 && !r.IsDBNull(4) ? r.GetInt32(4) : 0,
                    r.FieldCount > 5 && !r.IsDBNull(5) ? r.GetInt32(5) : 0));
            return list;
        }
    }

    // For a combined (Dual/All) session: merges the cluster's per-platform snapshots into 60-second
    // buckets. Each platform's snapshots carry only its own column (the others 0), so grouping by minute
    // and taking the MAX of each column reassembles the combined viewer line.
    public List<ViewerSnapshot> GetMergedSnapshots(IEnumerable<long> sessionIds)
    {
        lock (_lock)
        {
            var ids = sessionIds.Distinct().ToList();
            if (ids.Count == 0) return new List<ViewerSnapshot>();

            using var cmd = _conn.CreateCommand();
            var placeholders = new List<string>();
            for (int i = 0; i < ids.Count; i++)
            {
                placeholders.Add($"$s{i}");
                cmd.Parameters.AddWithValue($"$s{i}", ids[i]);
            }
            cmd.CommandText = $"""
                SELECT {ids[0]}, (timestamp / 60) * 60 AS bucket,
                       MAX(twitch_viewers), MAX(kick_viewers), MAX(chat_count), MAX(youtube_viewers)
                FROM viewer_snapshots
                WHERE session_id IN ({string.Join(",", placeholders)})
                GROUP BY bucket
                ORDER BY bucket ASC
                """;

            using var r = cmd.ExecuteReader();
            var list = new List<ViewerSnapshot>();
            while (r.Read())
                list.Add(new ViewerSnapshot(
                    r.GetInt64(0),
                    DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(1)),
                    r.GetInt32(2),
                    r.GetInt32(3),
                    r.FieldCount > 4 && !r.IsDBNull(4) ? r.GetInt32(4) : 0,
                    r.FieldCount > 5 && !r.IsDBNull(5) ? r.GetInt32(5) : 0));
            return list;
        }
    }

    public AllTimeStats GetAllTimeStats(string? platform = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        lock (_lock)
        {
            var (include, _) = ParsePlatforms(platform);
            var wanted = new List<string>(include);
            if (include.Contains("Twitch") && include.Contains("Kick")) wanted.Add("Both");

            using var cmd = _conn.CreateCommand();
            var placeholders = new List<string>();
            for (int i = 0; i < wanted.Count; i++)
            {
                placeholders.Add($"$p{i}");
                cmd.Parameters.AddWithValue($"$p{i}", wanted[i]);
            }
            string dateFilter = BuildDateFilter(from, to, cmd);
            cmd.CommandText = $"""
                SELECT
                    COUNT(*),
                    SUM(CASE WHEN ended_at IS NOT NULL THEN ended_at - started_at ELSE 0 END),
                    SUM(total_follows),
                    SUM(total_subs),
                    MAX(peak_viewers),
                    AVG(avg_viewers)
                FROM stream_sessions
                WHERE platform IN ({string.Join(",", placeholders)}){dateFilter}
                """;

            using var r = cmd.ExecuteReader();
            if (!r.Read()) return new AllTimeStats(0, TimeSpan.Zero, 0, 0, 0, 0, 0, 0, 0, 0);

            var totalStreams = r.IsDBNull(0) ? 0 : (int)r.GetInt64(0);
            var totalSecs    = r.IsDBNull(1) ? 0L : r.GetInt64(1);
            var totalFollows = r.IsDBNull(2) ? 0 : (int)r.GetInt64(2);
            var totalSubs    = r.IsDBNull(3) ? 0 : (int)r.GetInt64(3);
            var peakViewers  = r.IsDBNull(4) ? 0 : (int)r.GetInt64(4);
            var avgViewers   = r.IsDBNull(5) ? 0.0 : r.GetDouble(5);

            var (twitchFollows, kickFollows, ytFollows) = GetPlatformFollows();
            var (twitchSubs, kickSubs, ytSubs)          = GetPlatformSubs();

            return new AllTimeStats(totalStreams, TimeSpan.FromSeconds(totalSecs),
                totalFollows, totalSubs, peakViewers, avgViewers,
                twitchFollows, kickFollows, twitchSubs, kickSubs, ytFollows, ytSubs);
        }
    }

    private (int twitch, int kick, int youtube) GetPlatformFollows()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT platform, SUM(total_follows) FROM stream_sessions GROUP BY platform";
        using var r = cmd.ExecuteReader();
        int tw = 0, kick = 0, yt = 0;
        while (r.Read())
        {
            var p = r.GetString(0);
            var v = r.IsDBNull(1) ? 0 : (int)r.GetInt64(1);
            if (p == "Twitch") tw += v;
            else if (p == "Kick") kick += v;
            else if (p == "YouTube") yt += v;
            else { tw += v / 2; kick += v / 2; } // 'Both'
        }
        return (tw, kick, yt);
    }

    private (int twitch, int kick, int youtube) GetPlatformSubs()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT platform, SUM(total_subs) FROM stream_sessions GROUP BY platform";
        using var r = cmd.ExecuteReader();
        int tw = 0, kick = 0, yt = 0;
        while (r.Read())
        {
            var p = r.GetString(0);
            var v = r.IsDBNull(1) ? 0 : (int)r.GetInt64(1);
            if (p == "Twitch") tw += v;
            else if (p == "Kick") kick += v;
            else if (p == "YouTube") yt += v;
            else { tw += v / 2; kick += v / 2; }
        }
        return (tw, kick, yt);
    }

    // Per-stream follows/subs chart data (last N sessions)
    public record SessionTrend(
        DateTimeOffset Date, int Follows, int Subs, int PeakViewers, double AvgViewers,
        int UniqueChatters, long DurationSecs, string Title, string Category,
        int TwitchPeakViewers = 0, double TwitchAvgViewers = 0,
        int KickPeakViewers   = 0, double KickAvgViewers   = 0,
        int YouTubePeakViewers = 0, double YouTubeAvgViewers = 0);

    // Trends, sessions and all-time stats now share ONE merge path (GetSessions), so a Dual/All chart
    // can never disagree with the session list. Charts want oldest-first, so the newest-first session
    // list is reversed.
    public List<SessionTrend> GetSessionTrends(int limit = 20,
        DateTimeOffset? from = null, DateTimeOffset? to = null, string? platform = null)
    {
        var sessions = GetSessions(limit, platform, from, to);
        var list = sessions.Select(s => new SessionTrend(
            s.StartedAt, s.TotalFollows, s.TotalSubs, s.PeakViewers, s.AvgViewers,
            s.UniqueChatters,
            s.EndedAt.HasValue ? (long)(s.EndedAt.Value - s.StartedAt).TotalSeconds : 0,
            s.Title, s.Category,
            s.TwitchPeakViewers, s.TwitchAvgViewers,
            s.KickPeakViewers,   s.KickAvgViewers,
            s.YouTubePeakViewers, s.YouTubeAvgViewers)).ToList();
        list.Reverse();
        return list;
    }

    private static string BuildDateFilter(DateTimeOffset? from, DateTimeOffset? to,
        SqliteCommand cmd)
    {
        var filter = "";
        if (from.HasValue)
        {
            filter += " AND started_at >= $from";
            cmd.Parameters.AddWithValue("$from", from.Value.ToUnixTimeSeconds());
        }
        if (to.HasValue)
        {
            filter += " AND started_at <= $to";
            cmd.Parameters.AddWithValue("$to", to.Value.ToUnixTimeSeconds());
        }
        return filter;
    }

    // Reads a single-platform session row selected with SessionColumns (no kick_id / merged ids —
    // those are only set when the combine step builds a cross-platform row in C#).
    private static StreamSession ReadSession(SqliteDataReader r)
        => new(
            r.GetInt64(0),
            r.GetString(1),
            DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(2)),
            r.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(3)),
            r.IsDBNull(4) ? 0 : (int)r.GetInt64(4),
            r.IsDBNull(5) ? 0 : r.GetDouble(5),
            r.IsDBNull(6) ? 0 : (int)r.GetInt64(6),
            r.IsDBNull(7) ? 0 : (int)r.GetInt64(7),
            r.IsDBNull(8) ? 0 : (int)r.GetInt64(8),
            r.FieldCount > 9  && !r.IsDBNull(9)  ? r.GetString(9)      : "",
            r.FieldCount > 10 && !r.IsDBNull(10) ? r.GetString(10)     : "",
            r.FieldCount > 11 && !r.IsDBNull(11) ? (int)r.GetInt64(11) : 0,
            r.FieldCount > 12 && !r.IsDBNull(12) ? (int)r.GetInt64(12) : 0,
            r.FieldCount > 13 && !r.IsDBNull(13) ? (int)r.GetInt64(13) : 0,
            r.FieldCount > 14 && !r.IsDBNull(14) ? r.GetDouble(14)     : 0,
            r.FieldCount > 15 && !r.IsDBNull(15) ? r.GetDouble(15)     : 0,
            r.FieldCount > 16 && !r.IsDBNull(16) ? (int)r.GetInt64(16) : 0,
            r.FieldCount > 17 && !r.IsDBNull(17) ? (int)r.GetInt64(17) : 0,
            null,
            r.FieldCount > 18 && !r.IsDBNull(18) ? (int)r.GetInt64(18) : 0,
            r.FieldCount > 19 && !r.IsDBNull(19) ? r.GetDouble(19)     : 0,
            r.FieldCount > 20 && !r.IsDBNull(20) ? (int)r.GetInt64(20) : 0,
            null);

    public void Dispose() => _conn.Dispose();
}
