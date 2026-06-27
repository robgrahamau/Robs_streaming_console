using System.Runtime.InteropServices;

namespace Steaming.Application.Services;

// Sends BGRA frames to OBS via NDI using the NDI Runtime 6 already installed
// at C:\Program Files\NDI\NDI 6 Runtime\v6\ (installed by DistroAV).
// The NDI_RUNTIME_DIR_V6 environment variable is set by the NDI Runtime installer.
// Loads the DLL at runtime via NativeLibrary so the app still launches if NDI is absent.
public sealed class NdiSendService : IDisposable
{
    private const string SourceName = "Streaming Avatar";

    // ── NDI struct layouts ────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct NdiSendCreateT
    {
        [MarshalAs(UnmanagedType.LPStr)] public string? p_ndi_name;
        [MarshalAs(UnmanagedType.LPStr)] public string? p_groups;
        [MarshalAs(UnmanagedType.I1)]    public bool    clock_video;
        [MarshalAs(UnmanagedType.I1)]    public bool    clock_audio;
    }

    // NDIlib_video_frame_v2_t
    [StructLayout(LayoutKind.Sequential)]
    private struct NdiVideoFrameV2
    {
        public int   xres;
        public int   yres;
        public int   FourCC;              // BGRA = 0x41524742
        public int   frame_rate_N;
        public int   frame_rate_D;
        public float picture_aspect_ratio;
        public int   frame_format_type;   // 1 = progressive
        public long  timecode;            // 0 = auto
        public IntPtr p_data;
        public int   line_stride_in_bytes;
        public IntPtr p_metadata;         // null
        public long  timestamp;           // 0 = auto
    }

    private const int FourCC_BGRA = 0x41524742;
    private const int FrameFormatProgressive = 1;

    // ── Delegates for dynamically-loaded NDI entrypoints ─────────────────────

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool   NdiInitializeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr NdiSendCreateDelegate(ref NdiSendCreateT desc);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void   NdiSendSendVideoV2Delegate(IntPtr instance, ref NdiVideoFrameV2 frame);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void   NdiSendDestroyDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void   NdiDestroyDelegate();

    // ── State ─────────────────────────────────────────────────────────────────

    private IntPtr _lib     = IntPtr.Zero;
    private IntPtr _sender  = IntPtr.Zero;
    private bool   _ready;
    private bool   _disposed;

    private NdiInitializeDelegate?    _fnInit;
    private NdiSendCreateDelegate?    _fnSendCreate;
    private NdiSendSendVideoV2Delegate? _fnSendVideo;
    private NdiSendDestroyDelegate?   _fnSendDestroy;
    private NdiDestroyDelegate?       _fnDestroy;

    public bool IsAvailable => _ready;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public bool TryInitialize()
    {
        if (_ready) return true;

        var dllPath = LocateNdiDll();
        if (dllPath == null) return false;

        try
        {
            _lib = NativeLibrary.Load(dllPath);
        }
        catch
        {
            return false;
        }

        try
        {
            _fnInit        = GetExport<NdiInitializeDelegate>   ("NDIlib_initialize");
            _fnSendCreate  = GetExport<NdiSendCreateDelegate>   ("NDIlib_send_create");
            _fnSendVideo   = GetExport<NdiSendSendVideoV2Delegate>("NDIlib_send_send_video_v2");
            _fnSendDestroy = GetExport<NdiSendDestroyDelegate>  ("NDIlib_send_destroy");
            _fnDestroy     = GetExport<NdiDestroyDelegate>      ("NDIlib_destroy");
        }
        catch
        {
            NativeLibrary.Free(_lib);
            _lib = IntPtr.Zero;
            return false;
        }

        if (!_fnInit())
        {
            NativeLibrary.Free(_lib);
            _lib = IntPtr.Zero;
            return false;
        }

        var desc = new NdiSendCreateT
        {
            p_ndi_name  = SourceName,
            p_groups    = null,
            clock_video = false,  // we drive our own clock
            clock_audio = false,
        };
        _sender = _fnSendCreate(ref desc);
        if (_sender == IntPtr.Zero)
        {
            _fnDestroy();
            NativeLibrary.Free(_lib);
            _lib = IntPtr.Zero;
            return false;
        }

        _ready = true;
        return true;
    }

    // Send one BGRA frame. data must be width×height×4 bytes, row-major.
    // This method is called from the render thread at 30fps.
    public void SendFrame(byte[] data, int width, int height)
    {
        if (!_ready || _sender == IntPtr.Zero) return;

        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var frame = new NdiVideoFrameV2
            {
                xres                  = width,
                yres                  = height,
                FourCC                = FourCC_BGRA,
                frame_rate_N          = 30000,
                frame_rate_D          = 1000,    // 30fps
                picture_aspect_ratio  = (float)width / height,
                frame_format_type     = FrameFormatProgressive,
                timecode              = 0,
                p_data                = handle.AddrOfPinnedObject(),
                line_stride_in_bytes  = width * 4,
                p_metadata            = IntPtr.Zero,
                timestamp             = 0,
            };
            _fnSendVideo!(_sender, ref frame);
        }
        finally
        {
            handle.Free();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_sender != IntPtr.Zero)
        {
            try { _fnSendDestroy?.Invoke(_sender); } catch { }
            _sender = IntPtr.Zero;
        }

        if (_lib != IntPtr.Zero)
        {
            try { _fnDestroy?.Invoke(); } catch { }
            try { NativeLibrary.Free(_lib); } catch { }
            _lib = IntPtr.Zero;
        }

        _ready = false;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? LocateNdiDll()
    {
        // NDI Runtime 6 installer sets NDI_RUNTIME_DIR_V6
        var envDir = Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V6");
        if (!string.IsNullOrWhiteSpace(envDir))
        {
            var candidate = Path.Combine(envDir, "Processing.NDI.Lib.x64.dll");
            if (File.Exists(candidate)) return candidate;
        }

        // Fallback: known install path
        var fallback = @"C:\Program Files\NDI\NDI 6 Runtime\v6\Processing.NDI.Lib.x64.dll";
        if (File.Exists(fallback)) return fallback;

        return null;
    }

    private T GetExport<T>(string name) where T : Delegate
    {
        var ptr = NativeLibrary.GetExport(_lib, name);
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }
}
