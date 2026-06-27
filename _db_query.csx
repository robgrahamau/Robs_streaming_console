#!/usr/bin/env dotnet-script
#r "nuget: Microsoft.Data.Sqlite, 8.0.0"
using Microsoft.Data.Sqlite;

var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Steaming", "analytics.db");
Console.WriteLine($"DB: {dbPath}");

using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
conn.Open();

Console.WriteLine("\n=== SESSIONS (last 20, newest first) ===");
using var cmd = conn.CreateCommand();
cmd.CommandText = """
    SELECT id, platform,
           strftime('%Y-%m-%d %H:%M', started_at, 'unixepoch', 'localtime') as start,
           strftime('%H:%M', ended_at, 'unixepoch', 'localtime') as ended,
           peak_viewers, ROUND(avg_viewers,1) as avg, sample_count,
           twitch_peak_viewers, kick_peak_viewers,
           ROUND(twitch_avg_viewers,2) as t_avg, ROUND(kick_avg_viewers,2) as k_avg,
           twitch_sample_count, kick_sample_count,
           SUBSTR(title,1,40) as title
    FROM stream_sessions
    ORDER BY started_at DESC LIMIT 20
    """;
using var r = cmd.ExecuteReader();
Console.WriteLine($"{"id",-5} {"platform",-8} {"start",-17} {"end",-6} {"peak",-5} {"avg",-5} {"samp",-5} {"t_peak",-7} {"k_peak",-7} {"t_avg",-6} {"k_avg",-6} {"t_samp",-7} {"k_samp",-7} title");
Console.WriteLine(new string('-', 120));
while (r.Read())
{
    Console.WriteLine($"{r[0],-5} {r[1],-8} {r[2],-17} {(r.IsDBNull(3) ? "live" : r[3]),-6} {r[4],-5} {r[5],-5} {r[6],-5} {r[7],-7} {r[8],-7} {r[9],-6} {r[10],-6} {r[11],-7} {r[12],-7} {r[13]}");
}
r.Close();

Console.WriteLine("\n=== TODAY's SNAPSHOTS (per session) ===");
using var cmd2 = conn.CreateCommand();
cmd2.CommandText = """
    SELECT s.id, s.platform, COUNT(v.id) as snap_count,
           MIN(v.twitch_viewers) as t_min, MAX(v.twitch_viewers) as t_max,
           MIN(v.kick_viewers) as k_min,   MAX(v.kick_viewers) as k_max
    FROM stream_sessions s
    LEFT JOIN viewer_snapshots v ON v.session_id = s.id
    WHERE s.started_at > strftime('%s', 'now') - 86400*3
    GROUP BY s.id
    ORDER BY s.started_at DESC
    """;
using var r2 = cmd2.ExecuteReader();
Console.WriteLine($"{"id",-5} {"platform",-8} {"snaps",-6} {"t_min",-6} {"t_max",-6} {"k_min",-6} {"k_max",-6}");
Console.WriteLine(new string('-', 60));
while (r2.Read())
    Console.WriteLine($"{r2[0],-5} {r2[1],-8} {r2[2],-6} {r2[3],-6} {r2[4],-6} {r2[5],-6} {r2[6],-6}");
