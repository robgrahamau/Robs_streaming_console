using Microsoft.Data.Sqlite;

var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Steaming", "analytics.db");
Console.WriteLine($"DB: {dbPath}\n");

using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
conn.Open();

Console.WriteLine("=== SESSIONS (last 20) ===");
using var cmd = conn.CreateCommand();
cmd.CommandText = """
    SELECT id, platform,
           strftime('%Y-%m-%d %H:%M', started_at, 'unixepoch', 'localtime') as start,
           CASE WHEN ended_at IS NULL THEN 'live' ELSE strftime('%H:%M', ended_at, 'unixepoch', 'localtime') END as ended,
           peak_viewers, ROUND(avg_viewers,1) as avg, sample_count,
           twitch_peak_viewers, kick_peak_viewers,
           ROUND(twitch_avg_viewers,2) as t_avg, ROUND(kick_avg_viewers,2) as k_avg,
           twitch_sample_count, kick_sample_count,
           SUBSTR(title,1,35) as title
    FROM stream_sessions
    ORDER BY started_at DESC LIMIT 20
    """;
using var r = cmd.ExecuteReader();
Console.WriteLine($"{"id",-4} {"plat",-7} {"start",-17} {"end",-5} {"peak",-5} {"avg",-5} {"samp",-5} {"t_pk",-5} {"k_pk",-5} {"t_av",-5} {"k_av",-5} {"t_sc",-5} {"k_sc",-5} title");
Console.WriteLine(new string('-', 130));
while (r.Read())
    Console.WriteLine($"{r[0],-4} {r[1],-7} {r[2],-17} {r[3],-5} {r[4],-5} {r[5],-5} {r[6],-5} {r[7],-5} {r[8],-5} {r[9],-5} {r[10],-5} {r[11],-5} {r[12],-5} {r[13]}");
r.Close();

Console.WriteLine("\n=== SNAPSHOT STATS per session (last 3 days) ===");
using var cmd2 = conn.CreateCommand();
cmd2.CommandText = """
    SELECT s.id, s.platform, COUNT(v.id) as snaps,
           MIN(v.twitch_viewers) as t_min, MAX(v.twitch_viewers) as t_max,
           MIN(v.kick_viewers)   as k_min, MAX(v.kick_viewers)   as k_max,
           ROUND(AVG(v.twitch_viewers),2) as t_avg_snap, ROUND(AVG(v.kick_viewers),2) as k_avg_snap
    FROM stream_sessions s
    LEFT JOIN viewer_snapshots v ON v.session_id = s.id
    WHERE s.started_at > strftime('%s', 'now') - 86400*3
    GROUP BY s.id
    ORDER BY s.started_at DESC
    """;
using var r2 = cmd2.ExecuteReader();
Console.WriteLine($"{"id",-4} {"plat",-7} {"snaps",-6} {"t_min",-6} {"t_max",-6} {"k_min",-6} {"k_max",-6} {"t_avg",-7} {"k_avg",-7}");
Console.WriteLine(new string('-', 65));
while (r2.Read())
    Console.WriteLine($"{r2[0],-4} {r2[1],-7} {r2[2],-6} {r2[3],-6} {r2[4],-6} {r2[5],-6} {r2[6],-6} {r2[7],-7} {r2[8],-7}");
