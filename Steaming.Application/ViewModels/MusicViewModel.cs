using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Steaming.Application.Services;
using Steaming.Core.Models;
using Steaming.Core.Services;

namespace Steaming.Application.ViewModels;

// Drives the Music page and the Dashboard mini-player. Registered as a singleton so both
// views share one instance and stay in sync. Owns library scanning, the transport commands,
// and the OBS overlay style settings (persisted to AppSettings + pushed to the plugin).
public sealed class MusicViewModel : ViewModelBase
{
    private readonly MusicLibraryService _library;
    private readonly MusicPlayerService _player;
    private readonly MusicOverlayDispatcher _overlay;
    private readonly AppSettings _settings;
    private readonly IDispatcherService _dispatcher;

    public ObservableCollection<MusicTrack> Tracks { get; } = new();          // scanned library (source)
    public ObservableCollection<MusicTrack> PlaylistTracks { get; } = new();  // current playlist contents
    public ObservableCollection<string> PlaylistNames { get; } = new();
    public ObservableCollection<DeviceItem> OutputDevices { get; } = new();
    public ObservableCollection<LyricPreviewLine> LyricLines { get; } = new(); // in-app WYSIWYG preview window

    private IReadOnlyList<LyricLine> _lyrics = Array.Empty<LyricLine>();
    private int _activeLyric = -2;
    private long _lastLyricCommitTicks;   // for the minimum-line-time safeguard

    // Library tracks keyed by path, for resolving playlist entries fast.
    private readonly Dictionary<string, MusicTrack> _byPath =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _loadingPlaylist;

    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand PreviousCommand { get; }
    public RelayCommand ToggleShuffleCommand { get; }
    public RelayCommand ScanCommand { get; }
    public RelayCommand PlayPlaylistCommand { get; }
    public RelayCommand DeletePlaylistCommand { get; }
    public RelayCommand SaveTitleOverrideCommand { get; }
    public RelayCommand ClearTitleOverrideCommand { get; }

    public MusicViewModel(MusicLibraryService library, MusicPlayerService player,
                          MusicOverlayDispatcher overlay, AppSettings settings,
                          IDispatcherService dispatcher)
    {
        _library = library;
        _player = player;
        _overlay = overlay;
        _settings = settings;
        _dispatcher = dispatcher;

        var m = settings.Music;
        _libraryRoot = m.LibraryRoot;
        _selectedOutputDeviceId = m.OutputDeviceId;
        _shuffle = m.Shuffle;
        _volume = Math.Clamp(m.Volume, 0f, 1f);

        _npFontFamily = m.NpFontFamily;
        _npTitleSize = m.NpTitleSize;
        _npArtistSize = m.NpArtistSize;
        _npTextColorHex = ToHex(m.NpTextColor);
        _npShowArt = m.NpShowArt;
        _lyFontFamily = m.LyFontFamily;
        _lyFontSize = m.LyFontSize;
        _lyTextColorHex = ToHex(m.LyTextColor);
        _lyActiveColorHex = ToHex(m.LyActiveColor);
        _lyBackgroundColorHex = ToHexArgb(m.LyBackgroundColor);
        _lyLineCount = m.LyLineCount;
        _lyHorizontal = m.LyHorizontal;
        _lyMinLineMs = m.LyMinLineMs;

        PlayPauseCommand = new RelayCommand(_ => _player.PlayPause());
        StopCommand = new RelayCommand(_ => _player.Stop());
        NextCommand = new RelayCommand(_ => _player.Next());
        PreviousCommand = new RelayCommand(_ => _player.Previous());
        ToggleShuffleCommand = new RelayCommand(_ => Shuffle = !Shuffle);
        ScanCommand = new RelayCommand(async _ => await ScanAsync());
        PlayPlaylistCommand = new RelayCommand(_ => PlayPlaylist());
        DeletePlaylistCommand = new RelayCommand(_ => DeleteSelectedPlaylist());
        SaveTitleOverrideCommand = new RelayCommand(_ => SaveTitleOverride());
        ClearTitleOverrideCommand = new RelayCommand(_ => ClearTitleOverride());

        // Persist the active playlist whenever its contents change (add / remove / reorder).
        PlaylistTracks.CollectionChanged += (_, _) => { if (!_loadingPlaylist) PersistCurrentPlaylist(); };

        _player.SetShuffle(_shuffle);
        _player.SetVolume(_volume);

        _player.TrackChanged += t => _dispatcher.Invoke(() => OnTrackChanged(t));
        _player.StateChanged += s => _dispatcher.Invoke(() => OnStateChanged(s));
        _player.PositionChanged += (p, d, playing) => _dispatcher.Invoke(() => OnPositionChanged(p, d, playing));

        LoadOutputDevices();
    }

    private bool _loaded;
    // Called by the page when first shown — scans the saved library if not already loaded.
    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        _loaded = true;
        if (!string.IsNullOrWhiteSpace(_libraryRoot))
            await ScanAsync();
    }

    // ── Library ─────────────────────────────────────────────────────────────────
    public async Task ScanAsync()
    {
        if (string.IsNullOrWhiteSpace(_libraryRoot)) return;
        IsScanning = true;
        StatusText = "Scanning…";
        var root = _libraryRoot;
        var found = await Task.Run(() => _library.Scan(root));
        Tracks.Clear();
        _byPath.Clear();
        foreach (var t in found)
        {
            Tracks.Add(t);
            _byPath[t.FilePath] = t;
        }
        _player.SetPlaylist(found);
        IsScanning = false;
        StatusText = found.Count == 0 ? "No audio files found." : $"{found.Count} tracks";

        LoadPlaylistNames();
    }

    // Double-clicking a library track plays it in the library context (auto-advances the library).
    public void PlaySelectedLibrary(MusicTrack track)
    {
        if (track == null) return;
        _player.SetPlaylist(Tracks);
        _player.PlayTrack(track);
    }

    // Double-clicking a playlist track plays it in the playlist context.
    public void PlaySelectedPlaylist(MusicTrack track)
    {
        if (track == null) return;
        _player.SetPlaylist(PlaylistTracks);
        _player.PlayTrack(track);
    }

    // "Play playlist" — load the active playlist as the queue and start from the top.
    public void PlayPlaylist()
    {
        if (PlaylistTracks.Count == 0) return;
        _player.SetPlaylist(PlaylistTracks);
        _player.PlayTrack(PlaylistTracks[0]);
    }

    // ── Playlists ─────────────────────────────────────────────────────────────────
    private void LoadPlaylistNames()
    {
        PlaylistNames.Clear();
        foreach (var pl in _settings.Music.Playlists)
            PlaylistNames.Add(pl.Name);
        SelectedPlaylistName = PlaylistNames.FirstOrDefault();
    }

    private string? _selectedPlaylistName;
    public string? SelectedPlaylistName
    {
        get => _selectedPlaylistName;
        set { if (Set(ref _selectedPlaylistName, value)) LoadCurrentPlaylistTracks(); }
    }

    private void LoadCurrentPlaylistTracks()
    {
        _loadingPlaylist = true;
        PlaylistTracks.Clear();
        var pl = _settings.Music.Playlists.FirstOrDefault(p => p.Name == _selectedPlaylistName);
        if (pl != null)
            foreach (var path in pl.TrackPaths)
            {
                var t = _byPath.TryGetValue(path, out var lib) ? lib : _library.LoadTrack(path);
                if (t != null) PlaylistTracks.Add(t);
            }
        _loadingPlaylist = false;
    }

    public void CreatePlaylist(string name)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (_settings.Music.Playlists.Any(p => p.Name == name)) { SelectedPlaylistName = name; return; }
        _settings.Music.Playlists.Add(new MusicPlaylist { Name = name });
        _settings.Save();
        PlaylistNames.Add(name);
        SelectedPlaylistName = name;
    }

    public void DeleteSelectedPlaylist()
    {
        var name = _selectedPlaylistName;
        if (string.IsNullOrEmpty(name)) return;
        _settings.Music.Playlists.RemoveAll(p => p.Name == name);
        _settings.Save();
        PlaylistNames.Remove(name);
        SelectedPlaylistName = PlaylistNames.FirstOrDefault();
    }

    // Adds a library track to the active playlist (auto-creating one if none selected).
    public void AddToCurrentPlaylist(MusicTrack track)
    {
        if (track == null) return;
        if (string.IsNullOrEmpty(_selectedPlaylistName))
            CreatePlaylist("My Playlist");
        if (PlaylistTracks.Any(t => string.Equals(t.FilePath, track.FilePath, StringComparison.OrdinalIgnoreCase)))
            return; // no duplicates
        PlaylistTracks.Add(track);   // CollectionChanged persists
    }

    public void RemoveFromCurrentPlaylist(MusicTrack track)
    {
        if (track != null) PlaylistTracks.Remove(track); // CollectionChanged persists
    }

    private void PersistCurrentPlaylist()
    {
        var pl = _settings.Music.Playlists.FirstOrDefault(p => p.Name == _selectedPlaylistName);
        if (pl == null) return;
        pl.TrackPaths = PlaylistTracks.Select(t => t.FilePath).ToList();
        _settings.Save();
    }

    private void LoadOutputDevices()
    {
        OutputDevices.Clear();
        foreach (var (id, name) in AppSoundPlayer.EnumerateOutputDevices())
            OutputDevices.Add(new DeviceItem(id, name));
    }

    // ── Player event handlers (already on UI thread) ──────────────────────────────
    private void OnTrackChanged(MusicTrack? t)
    {
        CurrentTrack = t;
        Title = t?.Title ?? "";
        Artist = t?.Artist ?? "";
        EditableTitle = t?.Title ?? "";
        ArtPath = t?.ArtPath;
        Notify(nameof(HasTrack));
        Notify(nameof(HasTitleOverride));

        _lyrics = (t?.LrcPath is { } p && t.HasLyrics)
            ? LrcLyrics.ParseFile(p)
            : Array.Empty<LyricLine>();
        _activeLyric = -2;
        _lastLyricCommitTicks = 0;
        RebuildLyricWindow();
    }

    private void OnStateChanged(MusicPlaybackState s)
    {
        IsPlaying = s == MusicPlaybackState.Playing;
        if (s == MusicPlaybackState.Stopped)
        {
            PositionSeconds = 0;
            Notify(nameof(PositionText));
        }
    }

    private bool _seeking;
    private void OnPositionChanged(TimeSpan pos, TimeSpan dur, bool playing)
    {
        DurationSeconds = dur.TotalSeconds;
        if (!_seeking) Set(ref _positionSeconds, pos.TotalSeconds, nameof(PositionSeconds));
        Notify(nameof(PositionText));
        Notify(nameof(DurationText));
        UpdateLyricsForPosition(pos.TotalSeconds, applyThrottle: true);
    }

    // Minimum-line-time safeguard: while lines change faster than LyMinLineMs, hold the current
    // line, then jump straight to the latest (skipping unreadable intermediates). Never lags.
    private int ThrottleActive(int trueActive)
    {
        if (_lyMinLineMs <= 0) return trueActive;          // safeguard off
        if (trueActive == _activeLyric) return _activeLyric;
        long now = Environment.TickCount64;
        if (_activeLyric < -1 || now - _lastLyricCommitTicks >= _lyMinLineMs)
        {
            _lastLyricCommitTicks = now;
            return trueActive;
        }
        return _activeLyric; // hold until the threshold elapses
    }

    // Builds the visible lyric window (LyLineCount lines centred on the active line), mirroring
    // the OBS lyrics overlay's font/size/colour so the in-app preview is WYSIWYG.
    private void RebuildLyricWindow()
    {
        LyricLines.Clear();
        if (_lyrics.Count == 0) return;

        // Horizontal = single current line.
        if (_lyHorizontal)
        {
            int idx = Math.Clamp(_activeLyric < 0 ? 0 : _activeLyric, 0, _lyrics.Count - 1);
            bool active = _activeLyric >= 0;
            LyricLines.Add(new LyricPreviewLine
            {
                Text = _lyrics[idx].Text,
                FontSize = (int)(_lyFontSize * 1.18),
                IsActive = active,
                ColorHex = active ? _lyActiveColorHex : _lyTextColorHex,
                FontFamily = _lyFontFamily,
            });
            return;
        }

        int lineCount = Math.Clamp(_lyLineCount, 1, 15);
        int activeSize = (int)(_lyFontSize * 1.18);
        int center = Math.Clamp(_activeLyric < 0 ? 0 : _activeLyric, 0, _lyrics.Count - 1);
        int half = lineCount / 2;
        int first = center - half;
        int last = center + (lineCount - half - 1);

        for (int i = first; i <= last; i++)
        {
            if (i < 0 || i >= _lyrics.Count)
            {
                LyricLines.Add(new LyricPreviewLine { Text = "", FontSize = _lyFontSize, FontFamily = _lyFontFamily });
                continue;
            }
            bool isActive = i == _activeLyric;
            LyricLines.Add(new LyricPreviewLine
            {
                Text = _lyrics[i].Text,
                FontSize = isActive ? activeSize : _lyFontSize,
                IsActive = isActive,
                ColorHex = isActive ? _lyActiveColorHex : _lyTextColorHex,
                FontFamily = _lyFontFamily,
            });
        }
    }

    // Page calls this while dragging the seek slider so playback updates don't fight the drag.
    public void BeginSeek() => _seeking = true;
    public void EndSeek(double seconds)
    {
        if (!_seeking) return;   // PointerReleased + PointerCaptureLost can both fire; seek once
        _seeking = false;
        UpdateLyricsForPosition(seconds, applyThrottle: false);
        _player.Seek(TimeSpan.FromSeconds(seconds));
    }

    // ── Transport-bound state ─────────────────────────────────────────────────────
    private MusicTrack? _currentTrack;
    public MusicTrack? CurrentTrack { get => _currentTrack; private set => Set(ref _currentTrack, value); }
    public bool HasTrack => _currentTrack != null;
    public bool HasTitleOverride =>
        _currentTrack != null
        && _settings.Music.TitleOverrides.TryGetValue(_currentTrack.FilePath, out var title)
        && !string.IsNullOrWhiteSpace(title);

    private string _title = "";
    public string Title { get => _title; private set => Set(ref _title, value); }
    private string _artist = "";
    public string Artist { get => _artist; private set => Set(ref _artist, value); }
    private string _editableTitle = "";
    public string EditableTitle { get => _editableTitle; set => Set(ref _editableTitle, value); }
    private string? _artPath;
    public string? ArtPath { get => _artPath; private set => Set(ref _artPath, value); }

    private bool _isPlaying;
    public bool IsPlaying { get => _isPlaying; private set => Set(ref _isPlaying, value); }

    private double _positionSeconds;
    public double PositionSeconds
    {
        get => _positionSeconds;
        set
        {
            if (!Set(ref _positionSeconds, value)) return;
            if (_seeking)
            {
                Notify(nameof(PositionText));
                UpdateLyricsForPosition(value, applyThrottle: false);
            }
        }
    }
    private double _durationSeconds;
    public double DurationSeconds { get => _durationSeconds; private set => Set(ref _durationSeconds, value); }

    public string PositionText => Fmt(_positionSeconds);
    public string DurationText => Fmt(_durationSeconds);

    private bool _isScanning;
    public bool IsScanning { get => _isScanning; private set => Set(ref _isScanning, value); }
    private string _statusText = "";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    private string _libraryRoot;
    public string LibraryRoot
    {
        get => _libraryRoot;
        set { if (Set(ref _libraryRoot, value)) { _settings.Music.LibraryRoot = value; _settings.Save(); } }
    }

    private bool _shuffle;
    public bool Shuffle
    {
        get => _shuffle;
        set { if (Set(ref _shuffle, value)) { _player.SetShuffle(value); _settings.Save(); } }
    }

    private float _volume;
    public double Volume
    {
        get => _volume;
        set { var v = (float)value; if (Set(ref _volume, v)) { _player.SetVolume(v); _settings.Save(); } }
    }

    private string _selectedOutputDeviceId;
    public string SelectedOutputDeviceId
    {
        get => _selectedOutputDeviceId;
        set { if (Set(ref _selectedOutputDeviceId, value)) { _settings.Music.OutputDeviceId = value ?? ""; _settings.Save(); } }
    }

    // ── Now-Playing overlay style ─────────────────────────────────────────────────
    private string _npFontFamily;
    public string NpFontFamily { get => _npFontFamily; set { if (Set(ref _npFontFamily, value)) { _settings.Music.NpFontFamily = value; PushNowPlayingSettings(); } } }
    private int _npTitleSize;
    public int NpTitleSize { get => _npTitleSize; set { if (Set(ref _npTitleSize, value)) { _settings.Music.NpTitleSize = value; PushNowPlayingSettings(); } } }
    private int _npArtistSize;
    public int NpArtistSize { get => _npArtistSize; set { if (Set(ref _npArtistSize, value)) { _settings.Music.NpArtistSize = value; PushNowPlayingSettings(); } } }
    private string _npTextColorHex;
    public string NpTextColorHex { get => _npTextColorHex; set { if (Set(ref _npTextColorHex, value)) { _settings.Music.NpTextColor = FromHex(value, 0xFFFFFFFF); PushNowPlayingSettings(); } } }
    private bool _npShowArt;
    public bool NpShowArt { get => _npShowArt; set { if (Set(ref _npShowArt, value)) { _settings.Music.NpShowArt = value; PushNowPlayingSettings(); } } }

    // ── Lyrics overlay style ──────────────────────────────────────────────────────
    private string _lyFontFamily;
    public string LyFontFamily { get => _lyFontFamily; set { if (Set(ref _lyFontFamily, value)) { _settings.Music.LyFontFamily = value; PushLyricsSettings(); } } }
    private int _lyFontSize;
    public int LyFontSize { get => _lyFontSize; set { if (Set(ref _lyFontSize, value)) { _settings.Music.LyFontSize = value; PushLyricsSettings(); } } }
    private string _lyTextColorHex;
    public string LyTextColorHex { get => _lyTextColorHex; set { if (Set(ref _lyTextColorHex, value)) { _settings.Music.LyTextColor = FromHex(value, 0xFFB0B0B0); PushLyricsSettings(); } } }
    private string _lyActiveColorHex;
    public string LyActiveColorHex { get => _lyActiveColorHex; set { if (Set(ref _lyActiveColorHex, value)) { _settings.Music.LyActiveColor = FromHex(value, 0xFFFFFFFF); PushLyricsSettings(); } } }
    private int _lyLineCount;
    public int LyLineCount { get => _lyLineCount; set { if (Set(ref _lyLineCount, value)) { _settings.Music.LyLineCount = value; PushLyricsSettings(); } } }
    private string _lyBackgroundColorHex;
    public string LyBackgroundColorHex { get => _lyBackgroundColorHex; set { if (Set(ref _lyBackgroundColorHex, value)) { _settings.Music.LyBackgroundColor = FromHex(value, 0x00000000); PushLyricsSettings(); } } }
    private bool _lyHorizontal;
    public bool LyHorizontal { get => _lyHorizontal; set { if (Set(ref _lyHorizontal, value)) { _settings.Music.LyHorizontal = value; PushLyricsSettings(); } } }
    private int _lyMinLineMs;
    public int LyMinLineMs { get => _lyMinLineMs; set { if (Set(ref _lyMinLineMs, value)) { _settings.Music.LyMinLineMs = value; PushLyricsSettings(); } } }

    private void PushNowPlayingSettings() { _settings.Save(); _overlay.SendNowPlayingSettings(); }
    private void PushLyricsSettings() { _settings.Save(); _overlay.SendLyricsSettings(); RebuildLyricWindow(); }

    // ── Helpers ───────────────────────────────────────────────────────────────────
    private new bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }

    private static string Fmt(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds)) return "0:00";
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    private static string ToHex(uint argb) => $"#{(argb & 0xFFFFFF):X6}";
    private static string ToHexArgb(uint argb) => $"#{argb:X8}";

    private void SaveTitleOverride()
    {
        if (_currentTrack == null) return;
        var title = (EditableTitle ?? "").Trim();
        if (string.IsNullOrWhiteSpace(title)) return;

        _settings.Music.TitleOverrides[_currentTrack.FilePath] = title;
        _settings.Save();

        _currentTrack.Title = title;
        Title = title;
        EditableTitle = title;
        Notify(nameof(HasTitleOverride));
        _overlay.SendNowPlaying(_currentTrack);
    }

    private void ClearTitleOverride()
    {
        if (_currentTrack == null) return;
        if (!_settings.Music.TitleOverrides.Remove(_currentTrack.FilePath)) return;
        _settings.Save();

        var reloaded = _library.LoadTrack(_currentTrack.FilePath);
        var restored = string.IsNullOrWhiteSpace(reloaded?.Title)
            ? Path.GetFileNameWithoutExtension(_currentTrack.FilePath)
            : reloaded.Title;

        _currentTrack.Title = restored;
        Title = restored;
        EditableTitle = restored;
        Notify(nameof(HasTitleOverride));
        _overlay.SendNowPlaying(_currentTrack);
    }

    private void UpdateLyricsForPosition(double seconds, bool applyThrottle)
    {
        int trueActive = LrcLyrics.ActiveIndex(_lyrics, (int)Math.Max(0, TimeSpan.FromSeconds(seconds).TotalMilliseconds));
        int display = applyThrottle ? ThrottleActive(trueActive) : trueActive;
        if (display != _activeLyric)
        {
            _activeLyric = display;
            if (!applyThrottle)
                _lastLyricCommitTicks = Environment.TickCount64;
            RebuildLyricWindow();
        }
    }

    private static uint FromHex(string hex, uint fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var s = hex.TrimStart('#');
        if (s.Length == 6 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return 0xFF000000 | rgb;
        if (s.Length == 8 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
            return argb;
        return fallback;
    }

    public sealed record DeviceItem(string Id, string Name)
    {
        public override string ToString() => Name;
    }
}
