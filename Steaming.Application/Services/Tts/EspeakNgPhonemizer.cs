using System.Runtime.InteropServices;
using System.Text;

namespace Steaming.Application.Services.Tts;

// In-process grapheme→phoneme using libespeak-ng.dll, loaded directly from the app's data folder
// (no espeak-ng.exe, no system install, no second process). espeak-ng is not thread-safe, so all
// calls are serialized behind a lock. Produces IPA, which KokoroTokenizer maps to model token ids.
public sealed class EspeakNgPhonemizer(KokoroAssetService assets) : IPhonemizer
{
    private const int espeakCHARS_UTF8 = 1;
    private const int phonememode_IPA  = 0x02;   // bit 1: output IPA as UTF-8

    private readonly object _lock = new();
    private bool _initialized;
    private nint _lib;
    private Initialize? _init;
    private SetVoiceByName? _setVoice;
    private TextToPhonemes? _textToPhonemes;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Initialize(int output, int buflength, nint path, int options);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetVoiceByName(nint name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint TextToPhonemes(nint textptr, int textmode, int phonememode);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string? lpPathName);

    public bool IsAvailable => assets.EspeakReady;

    public Task<string> ToPhonemesAsync(string text, string lang, CancellationToken ct)
        => Task.Run(() => ToPhonemes(text, lang), ct);

    private string ToPhonemes(string text, string lang)
    {
        lock (_lock)
        {
            EnsureInit(lang);
            if (_textToPhonemes == null) return "";

            var sb = new StringBuilder();
            nint textBuf = Marshal.StringToCoTaskMemUTF8(text);
            nint pp = Marshal.AllocHGlobal(nint.Size);
            try
            {
                Marshal.WriteIntPtr(pp, textBuf);
                // espeak processes one clause per call, advancing *pp, until it reaches the end.
                while (true)
                {
                    nint res = _textToPhonemes(pp, espeakCHARS_UTF8, phonememode_IPA);
                    if (res != 0)
                        sb.Append(Marshal.PtrToStringUTF8(res));
                    if (Marshal.ReadIntPtr(pp) == 0) break;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pp);
                Marshal.FreeCoTaskMem(textBuf);
            }
            return sb.ToString().Replace("\r", " ").Replace("\n", " ").Trim();
        }
    }

    private void EnsureInit(string lang)
    {
        if (_initialized) return;
        if (!assets.EspeakReady || assets.EspeakDllPath == null || assets.EspeakDataDir == null)
            throw new InvalidOperationException("espeak-ng runtime not available");

        // Let the loader find any sibling DLLs next to libespeak-ng.dll.
        var dllDir = Path.GetDirectoryName(assets.EspeakDllPath);
        SetDllDirectory(dllDir);
        _lib = NativeLibrary.Load(assets.EspeakDllPath);
        SetDllDirectory(null);

        _init           = Marshal.GetDelegateForFunctionPointer<Initialize>(NativeLibrary.GetExport(_lib, "espeak_Initialize"));
        _setVoice       = Marshal.GetDelegateForFunctionPointer<SetVoiceByName>(NativeLibrary.GetExport(_lib, "espeak_SetVoiceByName"));
        _textToPhonemes = Marshal.GetDelegateForFunctionPointer<TextToPhonemes>(NativeLibrary.GetExport(_lib, "espeak_TextToPhonemes"));

        // AUDIO_OUTPUT_RETRIEVAL (1) — don't open any audio device; we only want phonemes.
        nint dataPath = Marshal.StringToHGlobalAnsi(assets.EspeakDataDir);
        nint voice    = Marshal.StringToHGlobalAnsi(string.IsNullOrWhiteSpace(lang) ? "en-us" : lang);
        try
        {
            if (_init(1, 0, dataPath, 0) < 0)
                throw new InvalidOperationException("espeak_Initialize failed");
            _setVoice(voice);
        }
        finally
        {
            Marshal.FreeHGlobal(dataPath);
            Marshal.FreeHGlobal(voice);
        }
        _initialized = true;
    }
}
