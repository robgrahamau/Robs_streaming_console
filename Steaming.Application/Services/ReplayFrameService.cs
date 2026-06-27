using Steaming.Application.Models;

namespace Steaming.Application.Services;

public sealed class ReplayFrameService
{
    private readonly object _lock = new();
    private readonly List<CameraFrame> _frames = [];
    private bool _recording;

    public bool IsRecording
    {
        get
        {
            lock (_lock) return _recording;
        }
    }

    public void StartRecording()
    {
        lock (_lock)
        {
            _frames.Clear();
            _recording = true;
        }
    }

    public void StopRecording()
    {
        lock (_lock) _recording = false;
    }

    public void AddFrame(CameraFrame frame)
    {
        lock (_lock)
        {
            if (!_recording)
                return;

            _frames.Add(new CameraFrame((byte[])frame.Pixels.Clone(), frame.Width, frame.Height, frame.Stride, frame.Timestamp));
            if (_frames.Count > 300)
                _frames.RemoveAt(0);
        }
    }

    public IReadOnlyList<CameraFrame> GetFrames()
    {
        lock (_lock)
            return _frames.ToArray();
    }
}
