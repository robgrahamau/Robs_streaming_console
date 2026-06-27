using System.Diagnostics;
using System.Net.Http;

namespace Steaming.Application.Services.Tts;

// Owns the Kokoro runtime assets so the user never has to download, install, or move anything by
// hand. Everything lives under %AppData%/Steaming/Tts and is fetched on first enable:
//   • model.onnx          ← HuggingFace onnx-community/Kokoro-82M-v1.0-ONNX
//   • voices/<name>.bin   ← same repo (raw float32 style vectors, per voice)
//   • espeak/…            ← espeak-ng.msi, extracted IN PLACE with `msiexec /a` (a silent admin
//                            "extract" — no system install, no UAC, no second app) to obtain
//                            libespeak-ng.dll + espeak-ng-data for in-process phonemization.
public sealed class KokoroAssetService
{
    private const string HfBase   = "https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/main/";
    private const string EspeakMsi = "https://github.com/espeak-ng/espeak-ng/releases/download/1.52.0/espeak-ng.msi";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    // The 50 published voices (af_/am_ = US, bf_/bm_ = UK, plus other locales). Default af_heart.
    public static readonly string[] Voices =
    [
        "af","af_alloy","af_aoede","af_bella","af_heart","af_jessica","af_kore","af_nicole","af_nova",
        "af_river","af_sarah","af_sky","am_adam","am_echo","am_eric","am_fenrir","am_liam","am_michael",
        "am_onyx","am_puck","am_santa","bf_alice","bf_emma","bf_isabella","bf_lily","bm_daniel","bm_fable",
        "bm_george","bm_lewis","ef_dora","em_alex","em_santa","ff_siwis","hf_alpha","hf_beta","hm_omega",
        "hm_psi","if_sara","im_nicola","jf_alpha","jf_gongitsune","jf_nezumi","jf_tebukuro","jm_kumo",
        "pf_dora","pm_alex","pm_santa","zf_xiaobei","zf_xiaoni","zf_xiaoxiao",
    ];

    public string Root        { get; }
    public string ModelPath   { get; }
    public string VoicesDir   { get; }
    public string EspeakDir   { get; }

    public KokoroAssetService(Func<string> modelVariantProvider)
    {
        _modelVariantProvider = modelVariantProvider;
        Root      = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Steaming", "Tts");
        ModelPath = Path.Combine(Root, "model.onnx");
        VoicesDir = Path.Combine(Root, "voices");
        EspeakDir = Path.Combine(Root, "espeak");
    }

    private readonly Func<string> _modelVariantProvider;

    public string VoicePath(string voice) => Path.Combine(VoicesDir, voice + ".bin");

    // Resolved at extract time; null until espeak is present.
    public string? EspeakDllPath { get; private set; }
    public string? EspeakDataDir { get; private set; }   // the dir CONTAINING espeak-ng-data

    public bool ModelReady          => File.Exists(ModelPath);
    public bool VoiceReady(string v) => File.Exists(VoicePath(v));
    public bool EspeakReady         => ResolveEspeak();

    public bool IsReady(string voice) => ModelReady && VoiceReady(voice) && EspeakReady;

    // Downloads whatever is missing for the given voice. `status` reports human-readable progress.
    public async Task<bool> EnsureAsync(string voice, Action<string>? status, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(VoicesDir);

            if (!ModelReady)
            {
                var variant = _modelVariantProvider();
                status?.Invoke($"Downloading Kokoro model ({variant})…");
                await DownloadAsync(HfBase + "onnx/" + variant, ModelPath, ct);
            }

            if (!VoiceReady(voice))
            {
                status?.Invoke($"Downloading voice '{voice}'…");
                await DownloadAsync($"{HfBase}voices/{voice}.bin", VoicePath(voice), ct);
            }

            if (!ResolveEspeak())
            {
                status?.Invoke("Downloading espeak-ng (one-time)…");
                await EnsureEspeakAsync(ct);
            }

            bool ok = IsReady(voice);
            status?.Invoke(ok ? "Kokoro ready." : "Kokoro assets incomplete.");
            return ok;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            status?.Invoke($"Download failed: {ex.Message}");
            return false;
        }
    }

    private async Task EnsureEspeakAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(EspeakDir);
        var msi = Path.Combine(EspeakDir, "espeak-ng.msi");
        if (!File.Exists(msi))
            await DownloadAsync(EspeakMsi, msi, ct);

        // Administrative install = extract the file tree without installing system-wide or prompting.
        var extract = Path.Combine(EspeakDir, "extract");
        Directory.CreateDirectory(extract);
        var psi = new ProcessStartInfo
        {
            FileName        = "msiexec",
            UseShellExecute = false,
            CreateNoWindow  = true,
        };
        psi.ArgumentList.Add("/a");
        psi.ArgumentList.Add(msi);
        psi.ArgumentList.Add("/qn");
        psi.ArgumentList.Add("TARGETDIR=" + extract);
        using var proc = Process.Start(psi);
        if (proc != null) await proc.WaitForExitAsync(ct);

        ResolveEspeak();
    }

    // Locate libespeak-ng.dll + the espeak-ng-data parent inside the extracted tree (layout varies).
    private bool ResolveEspeak()
    {
        if (EspeakDllPath != null && EspeakDataDir != null
            && File.Exists(EspeakDllPath) && Directory.Exists(Path.Combine(EspeakDataDir, "espeak-ng-data")))
            return true;

        if (!Directory.Exists(EspeakDir)) return false;
        try
        {
            var dll = Directory.EnumerateFiles(EspeakDir, "libespeak-ng.dll", SearchOption.AllDirectories).FirstOrDefault();
            var data = Directory.EnumerateDirectories(EspeakDir, "espeak-ng-data", SearchOption.AllDirectories).FirstOrDefault();
            if (dll == null || data == null) return false;
            EspeakDllPath = dll;
            EspeakDataDir = Path.GetDirectoryName(data);   // parent of espeak-ng-data
            return EspeakDataDir != null;
        }
        catch { return false; }
    }

    private static async Task DownloadAsync(string url, string destPath, CancellationToken ct)
    {
        var tmp = destPath + ".part";
        using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tmp);
            await src.CopyToAsync(dst, ct);
        }
        if (File.Exists(destPath)) File.Delete(destPath);
        File.Move(tmp, destPath);
    }
}
