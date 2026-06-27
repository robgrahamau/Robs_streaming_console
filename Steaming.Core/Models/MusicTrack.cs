using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Steaming.Core.Models;

// One playable track discovered by MusicLibraryService. The library follows a sibling-file
// convention: <base>.mp3 + <base>.png/.jpg (art) + <base>.lrc/.srt (synced lyrics).
public sealed class MusicTrack : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private string _title = "";
    private string _artist = "";
    private string _album = "";
    private string? _artPath;
    private string? _lrcPath;
    private double _durationSeconds;

    public string FilePath  { get; init; } = "";   // absolute path to the audio file
    public string Title
    {
        get => _title;
        set
        {
            if (_title == value) return;
            _title = value;
            Notify();
            Notify(nameof(Display));
        }
    }   // ID3 title, else file name (no extension)
    public string Artist
    {
        get => _artist;
        set
        {
            if (_artist == value) return;
            _artist = value;
            Notify();
            Notify(nameof(Display));
        }
    }   // ID3 artist, else ""
    public string Album
    {
        get => _album;
        set
        {
            if (_album == value) return;
            _album = value;
            Notify();
        }
    }   // ID3 album, else folder name
    public string? ArtPath
    {
        get => _artPath;
        set
        {
            if (_artPath == value) return;
            _artPath = value;
            Notify();
        }
    }          // sibling image (png/jpg/jpeg/webp) or null
    public string? LrcPath
    {
        get => _lrcPath;
        set
        {
            if (_lrcPath == value) return;
            _lrcPath = value;
            Notify();
            Notify(nameof(HasLyrics));
        }
    }          // sibling .lrc/.srt file or null
    public double DurationSeconds
    {
        get => _durationSeconds;
        set
        {
            if (_durationSeconds.Equals(value)) return;
            _durationSeconds = value;
            Notify();
        }
    }    // 0 if unknown until played

    // Display label used in playlists. "Artist — Title" when artist known, else Title.
    public string Display =>
        string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} — {Title}";

    public bool HasLyrics => !string.IsNullOrEmpty(LrcPath);

    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
