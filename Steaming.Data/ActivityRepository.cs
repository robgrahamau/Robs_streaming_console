using Microsoft.Data.Sqlite;

namespace Steaming.Data;

public record ActivityEntry(
    long Id,
    DateTimeOffset Timestamp,
    string Platform,
    string EventType,
    string Username,
    string Description);

// SQLite-backed activity log.
// Thread-safe: all writes serialise through a lock so the timer thread
// and the UI thread can both call Insert without racing.
public class ActivityRepository : IDisposable
{
    private static readonly string DbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Steaming", "activity.db");

    private readonly SqliteConnection _conn;
    private readonly object _lock = new();

    public ActivityRepository()
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
            CREATE TABLE IF NOT EXISTS activity (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp   INTEGER NOT NULL,
                platform    TEXT    NOT NULL,
                event_type  TEXT    NOT NULL,
                username    TEXT    NOT NULL,
                description TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_activity_ts ON activity(timestamp DESC);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Insert(DateTimeOffset timestamp, string platform, string eventType,
                       string username, string description)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO activity(timestamp,platform,event_type,username,description) VALUES($ts,$p,$t,$u,$d)";
            cmd.Parameters.AddWithValue("$ts", timestamp.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$p",  platform);
            cmd.Parameters.AddWithValue("$t",  eventType);
            cmd.Parameters.AddWithValue("$u",  username);
            cmd.Parameters.AddWithValue("$d",  description);
            cmd.ExecuteNonQuery();
        }
    }

    public List<ActivityEntry> GetRecent(int limit = 200)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "SELECT id,timestamp,platform,event_type,username,description FROM activity ORDER BY id DESC LIMIT $lim";
            cmd.Parameters.AddWithValue("$lim", limit);
            using var reader = cmd.ExecuteReader();
            var list = new List<ActivityEntry>();
            while (reader.Read())
                list.Add(new ActivityEntry(
                    reader.GetInt64(0),
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5)));
            return list;
        }
    }

    public List<ActivityEntry> GetByDate(DateOnly date, int limit = 1000)
    {
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(date.ToDateTime(TimeOnly.MinValue));
        var from = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), localOffset);
        var to   = from.AddDays(1);
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "SELECT id,timestamp,platform,event_type,username,description " +
                "FROM activity WHERE timestamp >= $from AND timestamp < $to " +
                "ORDER BY timestamp ASC LIMIT $lim";
            cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$to",   to.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$lim",  limit);
            using var reader = cmd.ExecuteReader();
            var list = new List<ActivityEntry>();
            while (reader.Read())
                list.Add(new ActivityEntry(
                    reader.GetInt64(0),
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5)));
            return list;
        }
    }

    public void Dispose() => _conn.Dispose();
}
