using NAudio.CoreAudioApi;
using NAudio.Wave;
using Steaming.Core.Services;

namespace Steaming.Application.Services;

// Plays app-local sounds (event-card sounds, chatbot command sounds) on the output device
// selected in settings (SoundAudioDeviceId, an MMDevice ID). Empty = system default.
// Shared by WPF and WinUI so both apps route sounds identically.
public sealed class AppSoundPlayer(AppSettings settings)
{
    public void Play(string path, float volume)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var reader = new AudioFileReader(path) { Volume = Math.Clamp(volume, 0f, 1f) };
                using var output = CreateOutput();
                var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                output.PlaybackStopped += (_, _) => tcs.TrySetResult(null);
                output.Init(reader);
                output.Play();
                await tcs.Task.ConfigureAwait(false);
            }
            catch { }
        });
    }

    private IWavePlayer CreateOutput()
    {
        var id = settings.SoundAudioDeviceId;
        if (!string.IsNullOrWhiteSpace(id))
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(id);
                if (device is { State: DeviceState.Active })
                    return new WasapiOut(device, AudioClientShareMode.Shared, false, 200);
            }
            catch { /* device unplugged/changed — fall back to default below */ }
        }
        return new WaveOutEvent();
    }

    // (Id, FriendlyName) pairs for settings pickers. First entry is always the default device.
    public static List<(string Id, string Name)> EnumerateOutputDevices()
    {
        var list = new List<(string, string)> { ("", "Default audio device") };
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                list.Add((d.ID, d.FriendlyName));
        }
        catch { }
        return list;
    }
}
