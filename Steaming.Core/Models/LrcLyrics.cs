using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Steaming.Core.Models;

// One timed lyric line.
public readonly record struct LyricLine(int TimeMs, string Text);

// Parser for synced lyric files used by the music library.
// Supported formats:
//   LRC:
//     [mm:ss.xx]text            single timed line
//     [mm:ss.xxx]text           millisecond precision also accepted
//     [00:12.00][00:47.00]text  repeated timestamps → one entry per timestamp
//     [00:00.00][Verse 1]       leading timestamp + bracketed section label kept as the text
//     [ar:Artist] / [ti:..]     metadata header tags (no timestamp) → ignored
//   SRT:
//     1
//     00:01:02,345 --> 00:01:05,000
//     first line
//     second line
public static class LrcLyrics
{
    // Matches a single leading [mm:ss(.xx)] timestamp tag at the current position.
    private static readonly Regex TimeTag =
        new(@"^\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
    private static readonly Regex SrtTimeLine =
        new(@"^\s*(\d{1,2}):(\d{2}):(\d{2})[,.](\d{1,3})\s*-->\s*(\d{1,2}):(\d{2}):(\d{2})[,.](\d{1,3})",
            RegexOptions.Compiled);

    public static IReadOnlyList<LyricLine> Parse(string text)
    {
        var lines = new List<LyricLine>();
        if (string.IsNullOrEmpty(text)) return lines;

        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var s = raw;
            var times = new List<int>();

            // Consume any number of leading timestamp tags.
            while (true)
            {
                var m = TimeTag.Match(s);
                if (!m.Success) break;
                int min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                int sec = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                int frac = 0;
                if (m.Groups[3].Success)
                {
                    var f = m.Groups[3].Value;
                    // Normalise to milliseconds (".xx" = centiseconds, ".xxx" = ms).
                    if (f.Length == 1) frac = int.Parse(f, CultureInfo.InvariantCulture) * 100;
                    else if (f.Length == 2) frac = int.Parse(f, CultureInfo.InvariantCulture) * 10;
                    else frac = int.Parse(f[..3], CultureInfo.InvariantCulture);
                }
                times.Add((min * 60 + sec) * 1000 + frac);
                s = s[m.Length..];
            }

            if (times.Count == 0) continue; // header tag / untimed line — skip

            var lyric = s.Trim();
            foreach (var t in times)
                lines.Add(new LyricLine(t, lyric));
        }

        // Stable sort so lines sharing a timestamp keep their written order (List.Sort is unstable).
        return lines.OrderBy(l => l.TimeMs).ToList();
    }

    public static IReadOnlyList<LyricLine> ParseFile(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            return string.Equals(Path.GetExtension(path), ".srt", StringComparison.OrdinalIgnoreCase)
                ? ParseSrt(text)
                : Parse(text);
        }
        catch { return Array.Empty<LyricLine>(); }
    }

    public static IReadOnlyList<LyricLine> ParseSrt(string text)
    {
        var lines = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(text)) return lines;

        var blocks = Regex.Split(text.Replace("\r\n", "\n").Replace('\r', '\n').Trim(), @"\n\s*\n");
        foreach (var block in blocks)
        {
            var parts = block.Split('\n')
                .Select(static s => s.Trim())
                .Where(static s => !string.IsNullOrWhiteSpace(s))
                .ToList();
            if (parts.Count < 2) continue;

            int timeIndex = SrtTimeLine.IsMatch(parts[0]) ? 0 : 1;
            if (timeIndex >= parts.Count) continue;

            var m = SrtTimeLine.Match(parts[timeIndex]);
            if (!m.Success) continue;

            var lyric = string.Join(" ", parts.Skip(timeIndex + 1)).Trim();
            if (string.IsNullOrWhiteSpace(lyric)) continue;

            lines.Add(new LyricLine(ParseSrtTimeMs(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value), lyric));
        }

        return lines.OrderBy(l => l.TimeMs).ToList();
    }

    private static int ParseSrtTimeMs(string hour, string minute, string second, string fraction)
    {
        int h = int.Parse(hour, CultureInfo.InvariantCulture);
        int m = int.Parse(minute, CultureInfo.InvariantCulture);
        int s = int.Parse(second, CultureInfo.InvariantCulture);
        int frac = fraction.Length switch
        {
            1 => int.Parse(fraction, CultureInfo.InvariantCulture) * 100,
            2 => int.Parse(fraction, CultureInfo.InvariantCulture) * 10,
            _ => int.Parse(fraction[..3], CultureInfo.InvariantCulture),
        };
        return ((h * 60 + m) * 60 + s) * 1000 + frac;
    }

    // Index of the active line for a given playback position, or -1 if before the first line.
    public static int ActiveIndex(IReadOnlyList<LyricLine> lines, int positionMs)
    {
        if (lines.Count == 0) return -1;
        int lo = 0, hi = lines.Count - 1, result = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (lines[mid].TimeMs <= positionMs) { result = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return result;
    }
}
