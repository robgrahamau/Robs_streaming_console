using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using Steaming.Application.Services;
using Steaming.Application.Services.Tts;
using Steaming.Application.ViewModels;
using System.Threading;
using Windows.Foundation;
using Windows.Devices.Enumeration;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace Steaming.WinUI.Pages;

public sealed partial class SettingsPage : Page
{
    private MainViewModel? _vm;
    private bool _loading;
    private MediaPlayer? _ttsPlayer;
    private TaskCompletionSource<object?>? _ttsPlaybackTcs;
    private TypedEventHandler<MediaPlayer, object>? _ttsMediaEndedHandler;
    private TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs>? _ttsMediaFailedHandler;

    // Parallel lists so SelectedIndex maps to the device ID
    private readonly List<string> _deviceIds   = [];
    private readonly List<string> _deviceNames = [];
    private readonly List<(string Id, string Name)> _soundDevices = [];

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not MainViewModel vm) return;
        _vm = vm;
        _loading = true;

        DataFolderBox.Text = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Steaming");

        // TTS voices â€” use WinUI-specific saved name, not WPF SAPI name
        try
        {
            var voices = SpeechSynthesizer.AllVoices.Select(v => v.DisplayName).ToList();
            TtsVoicePicker.ItemsSource = voices;
            var current = vm.Settings.TtsVoiceNameWinUI;
            if (!string.IsNullOrWhiteSpace(current) && voices.Contains(current))
                TtsVoicePicker.SelectedItem = current;
            else if (voices.Count > 0)
                TtsVoicePicker.SelectedIndex = 0;
        }
        catch { /* SpeechSynthesizer unavailable */ }

        // Audio output devices
        try
        {
            var selector = MediaDevice.GetAudioRenderSelector();
            var devices  = await DeviceInformation.FindAllAsync(selector);

            _deviceIds.Clear();
            _deviceNames.Clear();

            // Default device always first
            _deviceIds.Add("");
            _deviceNames.Add("Default audio device");

            foreach (var d in devices)
            {
                _deviceIds.Add(d.Id);
                _deviceNames.Add(d.Name);
            }

            TtsDevicePicker.ItemsSource = _deviceNames;

            var savedId = vm.Settings.TtsAudioDeviceId;
            var idx = string.IsNullOrEmpty(savedId) ? 0 : _deviceIds.IndexOf(savedId);
            TtsDevicePicker.SelectedIndex = idx >= 0 ? idx : 0;
        }
        catch { TtsDevicePicker.IsEnabled = false; }

        ShowTimestampsCheck.IsChecked = vm.ShowChatTimestampsInApp;
        TtsEnabledCheck.IsChecked     = vm.ChatTtsEnabled;
        AlertTtsEnabledCheck.IsChecked = vm.AlertTtsEnabled;
        TtsSpeedSlider.Value          = ClampTtsSpeed(vm.Settings.TtsSpeed);
        TtsSpeedValueText.Text        = $"{TtsSpeedSlider.Value:0.00}x";
        TtsIgnoredUsersBox.Text       = vm.Settings.TtsIgnoredUsers;

        // TTS engine + Kokoro config
        var engine = string.Equals(vm.TtsEngine, "Kokoro", StringComparison.OrdinalIgnoreCase) ? "Kokoro" : "WinRt";
        SelectEngineItem(engine);
        PopulateKokoroVoices();
        UpdateKokoroPanelVisibility(engine);
        RefreshKokoroStatus();

        // Alert & command sounds output device (NAudio MMDevice IDs)
        try
        {
            _soundDevices.Clear();
            _soundDevices.AddRange(AppSoundPlayer.EnumerateOutputDevices());
            SoundDevicePicker.ItemsSource = _soundDevices.Select(d => d.Name).ToList();
            var savedSoundId = vm.Settings.SoundAudioDeviceId;
            var soundIdx = string.IsNullOrEmpty(savedSoundId) ? 0 : _soundDevices.FindIndex(d => d.Id == savedSoundId);
            SoundDevicePicker.SelectedIndex = soundIdx >= 0 ? soundIdx : 0;
        }
        catch { SoundDevicePicker.IsEnabled = false; }

        _loading = false;
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Steaming");
        if (System.IO.Directory.Exists(path))
            System.Diagnostics.Process.Start("explorer.exe", path);
    }

    private void TtsVoicePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _vm == null || TtsVoicePicker.SelectedItem is not string voice) return;
        _vm.SetTtsVoiceWinUI(voice);
    }

    private void TtsDevicePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _vm == null) return;
        var idx = TtsDevicePicker.SelectedIndex;
        if (idx < 0 || idx >= _deviceIds.Count) return;
        _vm.SetTtsAudioDevice(_deviceIds[idx]);
    }

    private void TtsSpeedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (TtsSpeedValueText == null) return;
        TtsSpeedValueText.Text = $"{e.NewValue:0.00}x";
        if (_loading || _vm == null) return;
        _vm.SetTtsSpeed(e.NewValue);
    }

    private void TtsIgnoredUsers_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _vm == null) return;
        _vm.SetTtsIgnoredUsers(TtsIgnoredUsersBox.Text);
    }

    private void SoundDevicePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _vm == null) return;
        var idx = SoundDevicePicker.SelectedIndex;
        if (idx < 0 || idx >= _soundDevices.Count) return;
        _vm.SetSoundAudioDevice(_soundDevices[idx].Id);
    }

    private void ShowTimestamps_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _vm == null) return;
        _vm.SetShowChatTimestampsInApp(ShowTimestampsCheck.IsChecked == true);
    }

    // ── TTS engine / Kokoro ────────────────────────────────────────────────────

    private void SelectEngineItem(string engine)
    {
        foreach (var item in TtsEnginePicker.Items)
            if (item is ComboBoxItem cbi && (cbi.Tag as string) == engine)
            {
                TtsEnginePicker.SelectedItem = cbi;
                return;
            }
        TtsEnginePicker.SelectedIndex = 0;
    }

    private void UpdateKokoroPanelVisibility(string engine)
        => KokoroPanel.Visibility = engine == "Kokoro" ? Visibility.Visible : Visibility.Collapsed;

    private void PopulateKokoroVoices()
    {
        if (_vm == null) return;
        KokoroVoicePicker.ItemsSource = KokoroAssetService.Voices;
        var current = _vm.KokoroVoiceName;
        KokoroVoicePicker.SelectedItem = Array.IndexOf(KokoroAssetService.Voices, current) >= 0 ? current : "af_heart";
    }

    private void TtsEnginePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var engine = (TtsEnginePicker.SelectedItem as ComboBoxItem)?.Tag as string ?? "WinRt";
        UpdateKokoroPanelVisibility(engine);
        if (_loading || _vm == null) return;
        _vm.SetTtsEngine(engine);
        RefreshKokoroStatus();
    }

    private void KokoroVoicePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _vm == null) return;
        if (KokoroVoicePicker.SelectedItem is string v && !string.IsNullOrWhiteSpace(v))
        {
            _vm.SetKokoroVoiceName(v);
            RefreshKokoroStatus();
        }
    }

    private void RefreshKokoroStatus()
    {
        var assets = App.Services?.GetService<KokoroAssetService>();
        if (assets == null || _vm == null) { KokoroStatusText.Text = ""; return; }
        KokoroStatusText.Text = assets.IsReady(_vm.KokoroVoiceName)
            ? "Ready — downloaded and in-process."
            : "Not downloaded yet — click Download / verify (one-time, ~330 MB).";
    }

    private async void KokoroDownload_Click(object sender, RoutedEventArgs e)
    {
        var assets = App.Services?.GetService<KokoroAssetService>();
        if (assets == null || _vm == null) return;
        KokoroDownloadBtn.IsEnabled = false;
        try
        {
            await assets.EnsureAsync(
                _vm.KokoroVoiceName,
                s => DispatcherQueue.TryEnqueue(() => KokoroStatusText.Text = s),
                CancellationToken.None);
        }
        finally
        {
            KokoroDownloadBtn.IsEnabled = true;
            RefreshKokoroStatus();
        }
    }

    private void TtsEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _vm == null) return;
        _vm.SetChatTtsEnabled(TtsEnabledCheck.IsChecked == true);
    }

    private void AlertTtsEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _vm == null) return;
        _vm.SetAlertTtsEnabled(AlertTtsEnabledCheck.IsChecked == true);
    }

    private void ClearUiPref_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        _vm.Settings.UiPreference = null;
        _vm.Settings.Save();
        ClearUiPrefMsg.Visibility = Visibility.Visible;
    }

    private async void TtsSpeak_Click(object sender, RoutedEventArgs e)
    {
        var text = TtsTestBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        TtsStopCurrentPlayer();

        try
        {
            TtsStatusText.Text       = "Speakingâ€¦";
            TtsStatusText.Visibility = Visibility.Visible;
            TtsSpeakBtn.IsEnabled    = false;

            // Test through whichever engine is selected so the user can actually "check things".
            IRandomAccessStream stream;
            string contentType;
            if (string.Equals(_vm?.TtsEngine, "Kokoro", StringComparison.OrdinalIgnoreCase))
            {
                var kokoro = App.Services?.GetService<KokoroTtsBackend>();
                var audio = kokoro != null
                    ? await kokoro.SynthesizeAsync(text, ClampTtsSpeed(TtsSpeedSlider.Value), _vm?.KokoroVoiceName, CancellationToken.None)
                    : null;
                if (audio != null) { stream = audio.Stream; contentType = audio.ContentType; }
                else                { (stream, contentType) = await SynthWinRtAsync(text); }
            }
            else
            {
                (stream, contentType) = await SynthWinRtAsync(text);
            }

            var tcs    = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

            _ttsPlayer = new MediaPlayer();
            _ttsPlaybackTcs = tcs;
            var player = _ttsPlayer;

            // Route to the selected audio device if one was chosen
            var idx = TtsDevicePicker.SelectedIndex;
            if (idx > 0 && idx < _deviceIds.Count && !string.IsNullOrEmpty(_deviceIds[idx]))
            {
                try
                {
                    var deviceInfo = await DeviceInformation.CreateFromIdAsync(_deviceIds[idx]);
                    player.AudioDevice = deviceInfo;
                }
                catch { /* device unavailable, fall through to default */ }
            }

            _ttsMediaEndedHandler = (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                TtsStatusText.Visibility = Visibility.Collapsed;
                TtsSpeakBtn.IsEnabled    = true;
                tcs.TrySetResult(null);
            });
            _ttsMediaFailedHandler = (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                TtsStatusText.Text    = "Playback failed.";
                TtsStatusText.Visibility = Visibility.Visible;
                TtsSpeakBtn.IsEnabled = true;
                tcs.TrySetResult(null);
            });
            player.MediaEnded  += _ttsMediaEndedHandler;
            player.MediaFailed += _ttsMediaFailedHandler;
            player.Source = MediaSource.CreateFromStream(stream, contentType);
            player.Play();

            await tcs.Task;
            if (ReferenceEquals(_ttsPlaybackTcs, tcs))
                _ttsPlaybackTcs = null;
            if (ReferenceEquals(_ttsPlayer, player))
                CleanupTtsPlayer();
        }
        catch (Exception ex)
        {
            _ttsPlaybackTcs?.TrySetResult(null);
            TtsStatusText.Text       = $"Error: {ex.Message}";
            TtsStatusText.Visibility = Visibility.Visible;
            TtsSpeakBtn.IsEnabled    = true;
            CleanupTtsPlayer();
        }
    }

    // WinRT synthesis for the test button (and the Kokoro fallback). The SpeechSynthesisStream
    // holds its own buffer, so disposing the synthesizer afterwards is safe.
    private async Task<(IRandomAccessStream stream, string contentType)> SynthWinRtAsync(string text)
    {
        using var synth = new SpeechSynthesizer();
        if (TtsVoicePicker.SelectedItem is string voiceName && !string.IsNullOrWhiteSpace(voiceName))
        {
            var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.DisplayName == voiceName);
            if (voice != null) synth.Voice = voice;
        }
        synth.Options.SpeakingRate = ClampTtsSpeed(TtsSpeedSlider.Value);
        var s = await synth.SynthesizeTextToStreamAsync(text);
        return (s, s.ContentType);
    }

    private void TtsStop_Click(object sender, RoutedEventArgs e)
    {
        TtsStopCurrentPlayer();
        TtsStatusText.Visibility = Visibility.Collapsed;
        TtsSpeakBtn.IsEnabled    = true;
    }

    private void TtsStopCurrentPlayer()
    {
        _ttsPlaybackTcs?.TrySetResult(null);
        _ttsPlaybackTcs = null;
        CleanupTtsPlayer();
    }

    private void CleanupTtsPlayer()
    {
        if (_ttsPlayer == null) return;
        if (_ttsMediaEndedHandler != null)
            _ttsPlayer.MediaEnded -= _ttsMediaEndedHandler;
        if (_ttsMediaFailedHandler != null)
            _ttsPlayer.MediaFailed -= _ttsMediaFailedHandler;
        _ttsMediaEndedHandler = null;
        _ttsMediaFailedHandler = null;
        try { _ttsPlayer.Pause(); } catch { }
        _ttsPlayer.Dispose();
        _ttsPlayer = null;
    }

    private static double ClampTtsSpeed(double speed) => Math.Clamp(speed, 0.5, 6.0);
}
