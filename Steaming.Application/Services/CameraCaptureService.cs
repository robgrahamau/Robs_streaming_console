using Steaming.Application.Models;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace Steaming.Application.Services;

public sealed class CameraCaptureService : IAsyncDisposable
{
    private readonly object _frameLock = new();
    private MediaCapture? _mediaCapture;
    private MediaFrameReader? _reader;
    private CameraFrame _latestFrame;
    private string _lastError = "";
    private bool _isRunning;

    public string LastError
    {
        get
        {
            lock (_frameLock) return _lastError;
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_frameLock) return _isRunning;
        }
    }

    public async Task<List<(string Id, string Name)>> EnumerateCamerasAsync()
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        return devices.Select(d => (d.Id, d.Name)).ToList();
    }

    public async Task<bool> StartCameraAsync(string cameraId, FaceTrackingDiagnosticsService diagnostics, CancellationToken cancellationToken = default)
    {
        await StopCameraAsync();

        try
        {
            _mediaCapture = new MediaCapture();
            var settings = new MediaCaptureInitializationSettings
            {
                VideoDeviceId = cameraId,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu
            };

            await _mediaCapture.InitializeAsync(settings).AsTask(cancellationToken);
            var source = _mediaCapture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
            if (source == null)
            {
                SetError("No color camera source available.");
                await StopCameraAsync();
                return false;
            }

            _reader = await _mediaCapture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            _reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            _reader.FrameArrived += (_, _) => OnFrameArrived(diagnostics);

            var status = await _reader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                SetError($"Camera start failed: {status}");
                await StopCameraAsync();
                return false;
            }

            lock (_frameLock)
            {
                _isRunning = true;
                _lastError = "";
            }

            return true;
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
            await StopCameraAsync();
            return false;
        }
    }

    public async Task StopCameraAsync()
    {
        MediaFrameReader? reader;
        MediaCapture? capture;
        lock (_frameLock)
        {
            _isRunning = false;
            reader = _reader;
            capture = _mediaCapture;
            _reader = null;
            _mediaCapture = null;
        }

        if (reader != null)
        {
            try
            {
                await reader.StopAsync();
            }
            catch { }
            reader.Dispose();
        }

        capture?.Dispose();
    }

    public bool TryGetLatestFrame(out CameraFrame frame)
    {
        lock (_frameLock)
        {
            if (_latestFrame.Pixels == null || _latestFrame.Pixels.Length == 0)
            {
                frame = default;
                return false;
            }

            frame = _latestFrame;
            return true;
        }
    }

    public bool TryCopyLatestFrame(byte[] destination, out int width, out int height)
    {
        lock (_frameLock)
        {
            if (_latestFrame.Pixels == null || _latestFrame.Pixels.Length == 0)
            {
                width = 0;
                height = 0;
                return false;
            }

            if (destination.Length < _latestFrame.Pixels.Length)
            {
                width = _latestFrame.Width;
                height = _latestFrame.Height;
                return false;
            }

            Buffer.BlockCopy(_latestFrame.Pixels, 0, destination, 0, _latestFrame.Pixels.Length);
            width = _latestFrame.Width;
            height = _latestFrame.Height;
            return true;
        }
    }

    private void OnFrameArrived(FaceTrackingDiagnosticsService diagnostics)
    {
        if (_reader == null)
            return;

        using var frame = _reader.TryAcquireLatestFrame();
        var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (bitmap == null)
            return;

        SoftwareBitmap? converted = null;
        var bgra = bitmap;
        if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || bitmap.BitmapAlphaMode != BitmapAlphaMode.Ignore)
        {
            converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            bgra = converted;
        }

        int requiredBytes = bgra.PixelWidth * bgra.PixelHeight * 4;
        var frameBuffer = new byte[requiredBytes];
        bgra.CopyToBuffer(frameBuffer.AsBuffer());

        lock (_frameLock)
        {
            _latestFrame = new CameraFrame(frameBuffer, bgra.PixelWidth, bgra.PixelHeight, bgra.PixelWidth * 4, DateTimeOffset.UtcNow);
        }

        converted?.Dispose();

        diagnostics.NoteCameraFrame(DateTimeOffset.UtcNow);
    }

    private void SetError(string message)
    {
        lock (_frameLock)
            _lastError = message;
    }

    public async ValueTask DisposeAsync()
    {
        await StopCameraAsync();
    }
}
