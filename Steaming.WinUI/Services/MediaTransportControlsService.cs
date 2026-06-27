using System;
using Steaming.Application.Services;
using Windows.Media;
using Windows.Storage.Streams;

namespace Steaming.WinUI.Services;

// Bridges MusicPlayerService to the Windows System Media Transport Controls (SMTC) so the
// keyboard media keys (play/pause/next/prev/stop) control playback and the current track shows
// in the Windows media flyout (title/artist/album-art). Uses the documented WinUI 3 interop:
// Windows.Media.SystemMediaTransportControlsInterop.GetForWindow(hwnd).
public sealed class MediaTransportControlsService
{
    private readonly MusicPlayerService _player;
    private SystemMediaTransportControls? _smtc;

    public MediaTransportControlsService(MusicPlayerService player) => _player = player;

    public void Initialize(nint hwnd)
    {
        if (_smtc != null) return;
        _smtc = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
        _smtc.IsEnabled = true;
        _smtc.IsPlayEnabled = true;
        _smtc.IsPauseEnabled = true;
        _smtc.IsStopEnabled = true;
        _smtc.IsNextEnabled = true;
        _smtc.IsPreviousEnabled = true;
        _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
        _smtc.ButtonPressed += OnButtonPressed;

        _player.TrackChanged += OnTrackChanged;
        _player.StateChanged += OnStateChanged;
    }

    private void OnButtonPressed(SystemMediaTransportControls sender,
                                SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        // MusicPlayerService methods are thread-safe; SMTC raises this on a background thread.
        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play:
            case SystemMediaTransportControlsButton.Pause:
                _player.PlayPause();   // toggles to match the requested state
                break;
            case SystemMediaTransportControlsButton.Stop:
                _player.Stop();
                break;
            case SystemMediaTransportControlsButton.Next:
                _player.Next();
                break;
            case SystemMediaTransportControlsButton.Previous:
                _player.Previous();
                break;
        }
    }

    private void OnStateChanged(MusicPlaybackState state)
    {
        if (_smtc == null) return;
        _smtc.PlaybackStatus = state switch
        {
            MusicPlaybackState.Playing => MediaPlaybackStatus.Playing,
            MusicPlaybackState.Paused  => MediaPlaybackStatus.Paused,
            _                          => MediaPlaybackStatus.Stopped,
        };
    }

    private void OnTrackChanged(Steaming.Core.Models.MusicTrack? track)
    {
        if (_smtc == null) return;
        var updater = _smtc.DisplayUpdater;
        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = track?.Title ?? "";
        updater.MusicProperties.Artist = track?.Artist ?? "";
        try
        {
            updater.Thumbnail = (!string.IsNullOrEmpty(track?.ArtPath))
                ? RandomAccessStreamReference.CreateFromUri(new Uri(track!.ArtPath))
                : null;
        }
        catch { updater.Thumbnail = null; }
        updater.Update();
    }
}
