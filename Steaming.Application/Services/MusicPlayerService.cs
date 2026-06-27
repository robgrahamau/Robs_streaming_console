using NAudio.CoreAudioApi;
using NAudio.Wave;
using Steaming.Core.Models;
using Steaming.Core.Services;

namespace Steaming.Application.Services;

public enum MusicPlaybackState { Stopped, Playing, Paused }

// NAudio-based music playback engine. Routes audio to the output device selected in
// AppSettings.Music.OutputDeviceId (reuses the same device-selection idiom as AppSoundPlayer),
// resampling to the device mix format when needed so WASAPI shared mode never rejects the
// stream. Owns the playlist + shuffle order and auto-advances at end of track.
//
// No UI-framework imports — events are raised on background threads; subscribers must marshal.
public sealed class MusicPlayerService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly object _lock = new();

    private readonly List<MusicTrack> _playlist = new();
    private List<int> _order = new();     // indices into _playlist (shuffled when enabled)
    private int _orderPos = -1;           // position within _order

    private AudioFileReader? _reader;
    private IWavePlayer? _output;
    private bool _suppressAdvance;        // set before a manual Stop so PlaybackStopped doesn't auto-advance
    private float _volume;

    private readonly System.Threading.Timer _posTimer;

    public MusicPlayerService(AppSettings settings)
    {
        _settings = settings;
        _volume = Math.Clamp(settings.Music.Volume, 0f, 1f);
        _posTimer = new System.Threading.Timer(_ => TickPosition(), null, Timeout.Infinite, Timeout.Infinite);
    }

    // ── Events ────────────────────────────────────────────────────────────────
    public event Action<MusicTrack?>? TrackChanged;
    public event Action<MusicPlaybackState>? StateChanged;
    // (position, duration, isPlaying) — ~5/sec while playing.
    public event Action<TimeSpan, TimeSpan, bool>? PositionChanged;

    // ── State ─────────────────────────────────────────────────────────────────
    public MusicTrack? CurrentTrack { get; private set; }
    public MusicPlaybackState State { get; private set; } = MusicPlaybackState.Stopped;
    public bool Shuffle { get; private set; }
    public float Volume => _volume;
    public IReadOnlyList<MusicTrack> Playlist { get { lock (_lock) return _playlist.ToList(); } }

    // ── Playlist ────────────────────────────────────────────────────────────────
    public void SetPlaylist(IEnumerable<MusicTrack> tracks)
    {
        lock (_lock)
        {
            _playlist.Clear();
            _playlist.AddRange(tracks);
            RebuildOrder();
        }
    }

    public void SetShuffle(bool on)
    {
        lock (_lock)
        {
            Shuffle = on;
            RebuildOrder();
        }
        _settings.Music.Shuffle = on;
    }

    // Rebuild the play order, keeping the current track at the new order position.
    private void RebuildOrder()
    {
        var current = CurrentTrack;
        _order = Enumerable.Range(0, _playlist.Count).ToList();
        if (Shuffle)
        {
            var rng = Random.Shared;
            for (int i = _order.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (_order[i], _order[j]) = (_order[j], _order[i]);
            }
        }
        _orderPos = current == null ? -1 : _order.FindIndex(idx => ReferenceEquals(_playlist[idx], current));
    }

    // ── Transport ────────────────────────────────────────────────────────────────
    public void PlayTrack(MusicTrack track)
    {
        lock (_lock)
        {
            int idx = _playlist.FindIndex(t => ReferenceEquals(t, track));
            if (idx < 0) { _playlist.Add(track); RebuildOrder(); idx = _playlist.Count - 1; }
            _orderPos = _order.IndexOf(idx);
            StartCurrent();
        }
    }

    public void PlayPause()
    {
        lock (_lock)
        {
            if (_output == null)
            {
                if (_orderPos < 0 && _order.Count > 0) _orderPos = 0;
                StartCurrent();
                return;
            }
            if (State == MusicPlaybackState.Playing)
            {
                _output.Pause();
                SetState(MusicPlaybackState.Paused);
                _posTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
            else
            {
                _output.Play();
                SetState(MusicPlaybackState.Playing);
                _posTimer.Change(0, 200);
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            StopPlayback();
            SetState(MusicPlaybackState.Stopped);
            RaisePosition();
        }
    }

    public void Next() => Advance(+1, auto: false);
    public void Previous() => Advance(-1, auto: false);

    private void Advance(int dir, bool auto)
    {
        lock (_lock)
        {
            if (_order.Count == 0) return;
            if (_orderPos < 0) _orderPos = 0;
            else _orderPos = (_orderPos + dir + _order.Count) % _order.Count;  // wrap = continuous play
            StartCurrent();
        }
    }

    public void Seek(TimeSpan position)
    {
        lock (_lock)
        {
            if (_reader == null) return;
            var clamped = position < TimeSpan.Zero ? TimeSpan.Zero
                        : position > _reader.TotalTime ? _reader.TotalTime : position;
            _reader.CurrentTime = clamped;
            RaisePosition();
        }
    }

    public void SetVolume(float volume)
    {
        _volume = Math.Clamp(volume, 0f, 1f);
        lock (_lock) { if (_reader != null) _reader.Volume = _volume; }
        _settings.Music.Volume = _volume;
    }

    // ── Internal playback ─────────────────────────────────────────────────────────
    private void StartCurrent()
    {
        if (_orderPos < 0 || _orderPos >= _order.Count) return;
        var track = _playlist[_order[_orderPos]];

        StopPlayback();
        try
        {
            _reader = new AudioFileReader(track.FilePath) { Volume = _volume };
            track.DurationSeconds = _reader.TotalTime.TotalSeconds;

            var (output, feed) = CreateOutput(_reader);
            _output = output;
            _output.PlaybackStopped += OnPlaybackStopped;
            _output.Init(feed);
            _output.Play();
            // Re-arm auto-advance: StopPlayback set _suppressAdvance to silence the previous
            // output's stop event; the new output's natural end must advance to the next track.
            _suppressAdvance = false;

            CurrentTrack = track;
            SetState(MusicPlaybackState.Playing);
            TrackChanged?.Invoke(track);
            RaisePosition();
            _posTimer.Change(0, 200);
        }
        catch
        {
            StopPlayback();
            SetState(MusicPlaybackState.Stopped);
        }
    }

    private (IWavePlayer output, IWaveProvider feed) CreateOutput(AudioFileReader reader)
    {
        var id = _settings.Music.OutputDeviceId;
        if (!string.IsNullOrWhiteSpace(id))
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(id);
                if (device is { State: DeviceState.Active })
                {
                    var wasapi = new WasapiOut(device, AudioClientShareMode.Shared, true, 200);
                    var mix = device.AudioClient.MixFormat;
                    if (reader.WaveFormat.SampleRate != mix.SampleRate ||
                        reader.WaveFormat.Channels != mix.Channels)
                    {
                        var target = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);
                        var resampler = new MediaFoundationResampler(reader, target) { ResamplerQuality = 60 };
                        return (wasapi, resampler);
                    }
                    return (wasapi, reader);
                }
            }
            catch { /* device unplugged/changed — fall back to default below */ }
        }
        return (new WaveOutEvent(), reader);
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        bool manual;
        lock (_lock) { manual = _suppressAdvance; _suppressAdvance = false; }
        if (manual) return;
        // Natural end of track → advance to the next on a thread that isn't the audio callback.
        _ = Task.Run(() => Advance(+1, auto: true));
    }

    private void StopPlayback()
    {
        _posTimer.Change(Timeout.Infinite, Timeout.Infinite);
        if (_output != null)
        {
            _suppressAdvance = true;
            try { _output.PlaybackStopped -= OnPlaybackStopped; } catch { }
            try { _output.Stop(); } catch { }
            try { _output.Dispose(); } catch { }
            _output = null;
        }
        if (_reader != null)
        {
            try { _reader.Dispose(); } catch { }
            _reader = null;
        }
    }

    private void TickPosition()
    {
        lock (_lock)
        {
            if (_reader == null || State != MusicPlaybackState.Playing) return;
            RaisePosition();
        }
    }

    private void RaisePosition()
    {
        var pos = _reader?.CurrentTime ?? TimeSpan.Zero;
        var dur = _reader?.TotalTime ?? (CurrentTrack != null ? TimeSpan.FromSeconds(CurrentTrack.DurationSeconds) : TimeSpan.Zero);
        PositionChanged?.Invoke(pos, dur, State == MusicPlaybackState.Playing);
    }

    private void SetState(MusicPlaybackState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    public void Dispose()
    {
        lock (_lock) StopPlayback();
        _posTimer.Dispose();
    }
}
