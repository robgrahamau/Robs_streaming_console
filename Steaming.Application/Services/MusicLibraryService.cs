using Steaming.Core.Models;
using Steaming.Core.Services;
using System.IO;

namespace Steaming.Application.Services;

// Scans a root folder (recursively, including subfolders) for audio files and builds
// MusicTrack records. Title/artist/album come from embedded ID3 tags via TagLib when present,
// otherwise fall back to the file name / folder name. Album art and synced lyrics use the
// library's sibling-file convention: <base>.png/.jpg next to <base>.mp3, and <base>.lrc/.srt.
public sealed class MusicLibraryService
{
    private readonly AppSettings _settings;

    public MusicLibraryService(AppSettings settings)
    {
        _settings = settings;
    }

    private static readonly string[] AudioExtensions =
        { ".mp3", ".flac", ".m4a", ".wav", ".ogg", ".opus", ".wma" };
    private static readonly string[] ArtExtensions =
        { ".png", ".jpg", ".jpeg", ".webp" };
    private static readonly string[] FolderArtNames =
        { "cover", "folder", "front", "album" };

    // Scans off the calling thread's caller responsibility — run via Task.Run.
    public List<MusicTrack> Scan(string root)
    {
        var tracks = new List<MusicTrack>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return tracks;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                             .Where(f => AudioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        }
        catch { return tracks; }

        foreach (var file in files)
        {
            try { tracks.Add(BuildTrack(file)); }
            catch { /* skip unreadable file */ }
        }

        tracks.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase));
        return tracks;
    }

    // Builds a single track from a path (used to resolve playlist entries not in the scanned set).
    public MusicTrack? LoadTrack(string file)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return null;
        try { return BuildTrack(file); }
        catch { return null; }
    }

    private MusicTrack BuildTrack(string file)
    {
        var dir = Path.GetDirectoryName(file) ?? "";
        var baseName = Path.GetFileNameWithoutExtension(file);

        var track = new MusicTrack
        {
            FilePath = file,
            Title    = baseName,
            Artist   = "",
            Album    = new DirectoryInfo(dir).Name,
            ArtPath  = FindArt(dir, baseName),
            LrcPath  = FindLyricsSibling(dir, baseName),
        };

        // ID3 / container tags override the filename-derived defaults when populated.
        try
        {
            using var tag = TagLib.File.Create(file);
            var t = tag.Tag;
            if (!string.IsNullOrWhiteSpace(t.Title))             track.Title  = t.Title.Trim();
            if (!string.IsNullOrWhiteSpace(t.FirstPerformer))    track.Artist = t.FirstPerformer.Trim();
            else if (!string.IsNullOrWhiteSpace(t.FirstAlbumArtist)) track.Artist = t.FirstAlbumArtist.Trim();
            if (!string.IsNullOrWhiteSpace(t.Album))             track.Album  = t.Album.Trim();
            if (tag.Properties?.Duration.TotalSeconds > 0)
                track.DurationSeconds = tag.Properties.Duration.TotalSeconds;
        }
        catch { /* not all formats/files have readable tags — keep filename defaults */ }

        if (_settings.Music.TitleOverrides.TryGetValue(file, out var overrideTitle)
            && !string.IsNullOrWhiteSpace(overrideTitle))
            track.Title = overrideTitle.Trim();

        return track;
    }

    // Prefer art with the exact track base name; fall back to a generic folder cover image.
    private static string? FindArt(string dir, string baseName)
    {
        foreach (var ext in ArtExtensions)
        {
            var p = Path.Combine(dir, baseName + ext);
            if (File.Exists(p)) return p;
        }
        foreach (var name in FolderArtNames)
            foreach (var ext in ArtExtensions)
            {
                var p = Path.Combine(dir, name + ext);
                if (File.Exists(p)) return p;
            }
        return null;
    }

    private static string? FindSibling(string dir, string baseName, string ext)
    {
        var p = Path.Combine(dir, baseName + ext);
        return File.Exists(p) ? p : null;
    }

    private static string? FindLyricsSibling(string dir, string baseName)
        => FindSibling(dir, baseName, ".lrc")
        ?? FindSibling(dir, baseName, ".srt");
}
