using Steaming.Application.Models;

namespace Steaming.Application.Services;

public sealed class FaceTrackingDiagnosticsService
{
    private readonly object _lock = new();
    private readonly Queue<DateTimeOffset> _cameraFrames = new();
    private readonly Queue<DateTimeOffset> _trackerFrames = new();
    private string _providerName = "Not started";
    private float _detectorMs;
    private float _landmarksMs;
    private float _retargetMs;
    private int _droppedFrames;
    private float _currentConfidence;
    private bool _faceLocked;

    public void SetProvider(string providerName)
    {
        lock (_lock) _providerName = providerName;
    }

    public void NoteCameraFrame(DateTimeOffset now)
    {
        lock (_lock)
        {
            _cameraFrames.Enqueue(now);
            TrimOld(_cameraFrames, now);
        }
    }

    public void NoteTrackerFrame(DateTimeOffset now, float detectorMs, float landmarksMs, float confidence, bool faceLocked)
    {
        lock (_lock)
        {
            _trackerFrames.Enqueue(now);
            TrimOld(_trackerFrames, now);
            _detectorMs = detectorMs;
            _landmarksMs = landmarksMs;
            _currentConfidence = confidence;
            _faceLocked = faceLocked;
        }
    }

    public void NoteRetarget(float retargetMs)
    {
        lock (_lock) _retargetMs = retargetMs;
    }

    public void NoteDroppedFrame()
    {
        lock (_lock) _droppedFrames++;
    }

    public FaceTrackingDiagnosticsSnapshot Snapshot()
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            TrimOld(_cameraFrames, now);
            TrimOld(_trackerFrames, now);
            return new FaceTrackingDiagnosticsSnapshot(
                _providerName,
                _cameraFrames.Count,
                _trackerFrames.Count,
                _detectorMs,
                _landmarksMs,
                _retargetMs,
                _droppedFrames,
                _currentConfidence,
                _faceLocked);
        }
    }

    private static void TrimOld(Queue<DateTimeOffset> queue, DateTimeOffset now)
    {
        while (queue.Count > 0 && (now - queue.Peek()).TotalSeconds > 1.0)
            queue.Dequeue();
    }
}
