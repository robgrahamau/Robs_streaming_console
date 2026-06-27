using Microsoft.Data.Sqlite;
var db = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Steaming", "analytics.db");
using var conn = new SqliteConnection($"Data Source={db}");
conn.Open();

// Show all snapshots for session 1
Console.WriteLine("=== viewer_snapshots for session 1 ===");
using var cmd2 = conn.CreateCommand();
cmd2.CommandText = "SELECT timestamp, twitch_viewers, kick_viewers, twitch_viewers+kick_viewers as total FROM viewer_snapshots WHERE session_id=1 ORDER BY timestamp;";
using var r2 = cmd2.ExecuteReader();
int kPeak = 0; double kSum = 0; int tPeak = 0; double tSum = 0; int totalPeak = 0; double totalSum = 0; int count = 0;
while (r2.Read())
{
    var ts = DateTimeOffset.FromUnixTimeSeconds(r2.GetInt64(0)).ToLocalTime().ToString("HH:mm:ss");
    var tv = r2.GetInt32(1); var kv = r2.GetInt32(2); var tot = r2.GetInt32(3);
    Console.WriteLine($"  {ts}  Twitch={tv}  Kick={kv}  Total={tot}");
    if (kv > kPeak) kPeak = kv;
    if (tv > tPeak) tPeak = tv;
    if (tot > totalPeak) totalPeak = tot;
    kSum += kv; tSum += tv; totalSum += tot; count++;
}
Console.WriteLine();
if (count > 0)
{
    Console.WriteLine($"Snapshots: {count}");
    Console.WriteLine($"Kick:   peak={kPeak}  avg={kSum/count:F1}");
    Console.WriteLine($"Twitch: peak={tPeak}  avg={tSum/count:F1}");
    Console.WriteLine($"Total:  peak={totalPeak}  avg={totalSum/count:F1}");
    Console.WriteLine();
    Console.WriteLine("Patching session 1 with correct values...");
    using var upd = conn.CreateCommand();
    upd.CommandText = """
        UPDATE stream_sessions SET
            peak_viewers        = $tp,
            avg_viewers         = $ta,
            twitch_peak_viewers = $twp,
            twitch_avg_viewers  = $twa,
            twitch_sample_count = $tsc,
            kick_peak_viewers   = $kp,
            kick_avg_viewers    = $ka,
            kick_sample_count   = $ksc,
            sample_count        = $sc
        WHERE id = 1
        """;
    upd.Parameters.AddWithValue("$tp",  totalPeak);
    upd.Parameters.AddWithValue("$ta",  totalSum / count);
    upd.Parameters.AddWithValue("$twp", tPeak);
    upd.Parameters.AddWithValue("$twa", tSum / count);
    upd.Parameters.AddWithValue("$tsc", count);
    upd.Parameters.AddWithValue("$kp",  kPeak);
    upd.Parameters.AddWithValue("$ka",  kSum / count);
    upd.Parameters.AddWithValue("$ksc", count);
    upd.Parameters.AddWithValue("$sc",  count);
    upd.ExecuteNonQuery();
    Console.WriteLine("Done.");
}
else
    Console.WriteLine("No snapshots found — cannot reconstruct from raw data.");
