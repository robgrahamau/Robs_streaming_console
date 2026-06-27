using Steaming.Core.Ipc;
using Steaming.Core.Models;
using Steaming.Core.Services;
using System.Text;

namespace Steaming.Application.Services;

// Bridges MusicPlayerService → the OBS plugin's Now-Playing + Lyrics sources over the named pipe.
// Sends NowPlaying + parsed Lyrics on each track change, a lightweight Position tick (~5/sec) for
// lyric sync, and the two style-settings payloads on change / on (re)connect.
//
// Wire layouts (mirrored in C++ music_source.cpp / lyrics_source.cpp):
//   MusicNowPlaying          : [2+N]title [2+N]artist [2+N]artPath [4]durationMs_le
//   MusicPosition            : [4]positionMs_le [1]isPlaying
//   MusicLyrics              : [2]count_le then per line [4]timeMs_le [2+N]text
//   MusicNowPlayingSettings  : [4]textColor_argb [2]titleSize [2]artistSize [1]showArt [2+N]fontFamily
//   MusicLyricsSettings      : [4]textColor_argb [4]activeColor_argb [4]bgColor_argb [2]fontSize [1]lineCount [1]horizontal [2]minLineMs [2+N]fontFamily
public sealed class MusicOverlayDispatcher
{
    private readonly MusicPlayerService _player;
    private readonly PluginPipeServer _pipe;
    private readonly AppSettings _settings;
    private bool _started;

    public MusicOverlayDispatcher(MusicPlayerService player, PluginPipeServer pipe, AppSettings settings)
    {
        _player = player;
        _pipe = pipe;
        _settings = settings;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _player.TrackChanged += OnTrackChanged;
        _player.PositionChanged += OnPositionChanged;
        _player.StateChanged += OnStateChanged;
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _player.TrackChanged -= OnTrackChanged;
        _player.PositionChanged -= OnPositionChanged;
        _player.StateChanged -= OnStateChanged;
    }

    private void OnTrackChanged(MusicTrack? track)
    {
        SendNowPlaying(track);
        SendLyrics(track);
    }

    private void OnPositionChanged(TimeSpan pos, TimeSpan dur, bool playing)
        => SendPosition((int)pos.TotalMilliseconds, playing);

    private void OnStateChanged(MusicPlaybackState state)
    {
        if (state == MusicPlaybackState.Playing)
        {
            SendNowPlaying(_player.CurrentTrack);
            SendLyrics(_player.CurrentTrack);
            return;
        }

        if (state == MusicPlaybackState.Paused || state == MusicPlaybackState.Stopped)
        {
            SendNowPlaying(null);   // empty title clears the overlay
            SendLyrics(null);
            SendPosition(0, false);
        }
    }

    // ── Senders ───────────────────────────────────────────────────────────────
    public void SendNowPlaying(MusicTrack? track)
    {
        var w = new PayloadWriter();
        w.Str(track?.Title ?? "");
        w.Str(track?.Artist ?? "");
        w.Str(track?.ArtPath ?? "");
        w.U32((uint)Math.Max(0, (int)((track?.DurationSeconds ?? 0) * 1000)));
        Send(PipeMessageType.MusicNowPlaying, w);
    }

    public void SendLyrics(MusicTrack? track)
    {
        var lines = track?.LrcPath is { } p && track.HasLyrics
            ? LrcLyrics.ParseFile(p)
            : (IReadOnlyList<LyricLine>)Array.Empty<LyricLine>();

        var w = new PayloadWriter();
        w.U16((ushort)Math.Min(lines.Count, ushort.MaxValue));
        foreach (var line in lines)
        {
            w.U32((uint)Math.Max(0, line.TimeMs));
            w.Str(line.Text);
        }
        Send(PipeMessageType.MusicLyrics, w);
    }

    public void SendPosition(int positionMs, bool playing)
    {
        var w = new PayloadWriter();
        w.U32((uint)Math.Max(0, positionMs));
        w.U8(playing ? (byte)1 : (byte)0);
        Send(PipeMessageType.MusicPosition, w);
    }

    public void SendNowPlayingSettings()
    {
        var m = _settings.Music;
        var w = new PayloadWriter();
        w.U32(m.NpTextColor);
        w.U16((ushort)Math.Clamp(m.NpTitleSize, 6, 200));
        w.U16((ushort)Math.Clamp(m.NpArtistSize, 6, 200));
        w.U8(m.NpShowArt ? (byte)1 : (byte)0);
        w.Str(string.IsNullOrWhiteSpace(m.NpFontFamily) ? "Segoe UI" : m.NpFontFamily);
        Send(PipeMessageType.MusicNowPlayingSettings, w);
    }

    public void SendLyricsSettings()
    {
        var m = _settings.Music;
        var w = new PayloadWriter();
        w.U32(m.LyTextColor);
        w.U32(m.LyActiveColor);
        w.U32(m.LyBackgroundColor);
        w.U16((ushort)Math.Clamp(m.LyFontSize, 6, 200));
        w.U8((byte)Math.Clamp(m.LyLineCount, 1, 15));
        w.U8(m.LyHorizontal ? (byte)1 : (byte)0);
        w.U16((ushort)Math.Clamp(m.LyMinLineMs, 0, 5000));
        w.Str(string.IsNullOrWhiteSpace(m.LyFontFamily) ? "Segoe UI" : m.LyFontFamily);
        Send(PipeMessageType.MusicLyricsSettings, w);
    }

    // Sends both style payloads + the current track — call on pipe (re)connect.
    public void SendAllAsync()
    {
        SendNowPlayingSettings();
        SendLyricsSettings();
        SendNowPlaying(_player.CurrentTrack);
        SendLyrics(_player.CurrentTrack);
    }

    private void Send(PipeMessageType type, PayloadWriter w)
        => _ = _pipe.SendAsync(type, w.ToArray());

    // Small append-only binary writer matching the wire idiom used by OverlayDispatcher
    // (u16 length-prefixed UTF-8 strings, little-endian integers).
    private sealed class PayloadWriter
    {
        private readonly List<byte> _b = new();
        public void U8(byte v) => _b.Add(v);
        public void U16(ushort v) { _b.Add((byte)(v & 0xFF)); _b.Add((byte)(v >> 8)); }
        public void U32(uint v)
        {
            _b.Add((byte)(v & 0xFF)); _b.Add((byte)((v >> 8) & 0xFF));
            _b.Add((byte)((v >> 16) & 0xFF)); _b.Add((byte)((v >> 24) & 0xFF));
        }
        public void Str(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s ?? "");
            U16((ushort)Math.Min(bytes.Length, ushort.MaxValue));
            _b.AddRange(bytes);
        }
        public byte[] ToArray() => _b.ToArray();
    }
}
