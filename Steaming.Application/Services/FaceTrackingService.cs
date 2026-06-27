using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Steaming.Application.Models;

namespace Steaming.Application.Services;

public sealed class FaceTrackingService : IDisposable
{
    private const int LandmarkCount = 66;
    private const float RgbRScale = 1f / (255f * 0.229f);
    private const float RgbGScale = 1f / (255f * 0.224f);
    private const float RgbBScale = 1f / (255f * 0.225f);
    private const float RgbRBias = -(0.485f / 0.229f);
    private const float RgbGBias = -(0.456f / 0.224f);
    private const float RgbBBias = -(0.406f / 0.225f);
    private readonly CameraCaptureService _cameraCapture;
    private readonly FaceTrackingDiagnosticsService _diagnostics;
    private readonly ReplayFrameService _replay;
    private InferenceSession? _landmarkSession;
    private InferenceSession? _detectionSession;
    private string _landmarkInputName = "input";
    private string _detectionInputName = "input";
    private IFaceTrackingProvider? _externalProvider;
    private string _trackingModel = "OpenSeeFace";
    private Thread? _thread;
    private volatile bool _running;
    private volatile RawFaceTrackingFrame _latest = new();
    private volatile bool _disposed;
    private RectangleF? _lastFace;
    private float _lastDetectorConfidence;
    private int _frameCounter;
    private int _fpsCap = 30;

    public event Action<RawFaceTrackingFrame>? FrameReady;

    public FaceTrackingService(
        CameraCaptureService cameraCapture,
        FaceTrackingDiagnosticsService diagnostics,
        ReplayFrameService replay)
    {
        _cameraCapture = cameraCapture;
        _diagnostics = diagnostics;
        _replay = replay;
    }

    public RawFaceTrackingFrame LatestFrame => _latest;

    public void SetFpsCap(int fpsCap) => _fpsCap = Math.Clamp(fpsCap, 10, 60);

    public void SetTrackingModel(string model)
    {
        if (_trackingModel == model) return;
        _trackingModel = model;
        // Tear down existing sessions so EnsureSessions rebuilds with new model.
        _externalProvider?.Dispose();
        _externalProvider = null;
        _landmarkSession?.Dispose();  _landmarkSession  = null;
        _detectionSession?.Dispose(); _detectionSession = null;
    }

    public void Start()
    {
        if (_running || _disposed)
            return;

        EnsureSessions();
        _running = true;
        _thread = new Thread(TrackLoop) { IsBackground = true, Name = "FaceTracking" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    private void EnsureSessions()
    {
        if (_externalProvider != null) return;
        if (_trackingModel == "MediaPipe" && _externalProvider == null)
        {
            string assetRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "FaceTracking");
            var opts = BuildSessionOptions();
            try { opts.AppendExecutionProvider_DML(0); } catch { /* fall back to CPU */ }
            var provider = new MediaPipeFaceProvider();
            provider.Load(assetRoot, opts);
            _externalProvider = provider;
            _diagnostics.SetProvider("MediaPipe/DirectML");
            return;
        }

        if (_landmarkSession != null && _detectionSession != null)
            return;

        string assetRoot2 = Path.Combine(AppContext.BaseDirectory, "Assets", "FaceTracking");
        string landmarkPath  = Path.Combine(assetRoot2, "lm_model3_opt.onnx");
        string detectionPath = Path.Combine(assetRoot2, "mnv3_detection_opt.onnx");

        var dmlOptions = BuildSessionOptions();
        try
        {
            dmlOptions.AppendExecutionProvider_DML(0);
            _landmarkSession  = new InferenceSession(landmarkPath,  dmlOptions);
            _detectionSession = new InferenceSession(detectionPath, dmlOptions);
            _diagnostics.SetProvider("DirectML");
        }
        catch
        {
            dmlOptions.Dispose();
            var cpuOptions = BuildSessionOptions();
            _landmarkSession  = new InferenceSession(landmarkPath,  cpuOptions);
            _detectionSession = new InferenceSession(detectionPath, cpuOptions);
            _diagnostics.SetProvider("CPU");
        }

        _landmarkInputName = _landmarkSession.InputMetadata.Keys.First();
        _detectionInputName = _detectionSession.InputMetadata.Keys.First();
    }

    private void TrackLoop()
    {
        int targetMs = 1000 / Math.Max(1, _fpsCap);
        while (_running && !_disposed)
        {
            var start = DateTimeOffset.UtcNow;
            try
            {
                if (_cameraCapture.TryGetLatestFrame(out var frame))
                {
                    _replay.AddFrame(frame);
                    var tracked = ProcessFrame(frame);
                    _latest = tracked;
                    FrameReady?.Invoke(tracked);
                }
            }
            catch
            {
                _diagnostics.NoteDroppedFrame();
            }

            int wait = targetMs - (int)(DateTimeOffset.UtcNow - start).TotalMilliseconds;
            if (wait > 0)
                Thread.Sleep(wait);
        }
    }

    private RawFaceTrackingFrame ProcessFrame(CameraFrame frame)
    {
        if (_externalProvider != null)
        {
            var r = _externalProvider.ProcessFrame(frame);
            _diagnostics.NoteTrackerFrame(frame.Timestamp, 0f, 0f, r.TrackingConfidence, r.IsTracking);
            return r;
        }

        float detectorMs = 0f;
        float landmarksMs = 0f;
        RectangleF? detectedFace = _lastFace;
        float detectorConfidence = _lastDetectorConfidence;

        bool runDetector = detectedFace == null || _frameCounter % 10 == 0;
        _frameCounter++;
        if (runDetector)
        {
            var sw = Stopwatch.StartNew();
            detectedFace = DetectFace(frame, out detectorConfidence);
            sw.Stop();
            detectorMs = (float)sw.Elapsed.TotalMilliseconds;
            _lastDetectorConfidence = detectorConfidence;
            _lastFace = detectedFace;
        }

        if (detectedFace == null)
        {
            var lost = new RawFaceTrackingFrame
            {
                Timestamp = frame.Timestamp,
                IsTracking = false,
                TrackingConfidence = 0f,
                DetectorConfidence = detectorConfidence,
                LandmarkConfidence = 0f,
                DetectorMs = detectorMs,
                LandmarksMs = 0f
            };
            _diagnostics.NoteTrackerFrame(frame.Timestamp, detectorMs, 0f, 0f, false);
            return lost;
        }

        var landmarkWatch = Stopwatch.StartNew();
        float[] landmarks = TrackLandmarks(frame, detectedFace.Value, out float landmarkConfidence);
        landmarkWatch.Stop();
        landmarksMs = (float)landmarkWatch.Elapsed.TotalMilliseconds;

        if (landmarkConfidence < 0.45f)
        {
            _lastFace = null;
        }
        else
        {
            _lastFace = BoundsFromLandmarks(landmarks, frame.Width, frame.Height);
        }

        var raw = BuildRawFrame(frame, landmarks, detectedFace.Value, detectorConfidence, landmarkConfidence, detectorMs, landmarksMs);
        _diagnostics.NoteTrackerFrame(frame.Timestamp, detectorMs, landmarksMs, raw.TrackingConfidence, raw.IsTracking);
        return raw;
    }

    private static SessionOptions BuildSessionOptions() => new()
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        EnableMemoryPattern = false,
        IntraOpNumThreads = 2,
        InterOpNumThreads = 1,
    };

    private RectangleF? DetectFace(CameraFrame frame, out float confidence)
    {
        confidence = 0f;
        if (_detectionSession == null)
            return null;

        var input = new DenseTensor<float>(new[] { 1, 3, 224, 224 });
        FillResizedRgbTensor(frame, input.Buffer.Span, 224, 224, normalizeForDetection: true, null);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_detectionInputName, input)
        };
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = _detectionSession.Run(inputs);
        var outputMap = outputs.ElementAt(0).AsTensor<float>();
        var poolMap = outputs.ElementAt(1).AsTensor<float>();

        int bestIndex = -1;
        float bestScore = 0f;
        for (int y = 0; y < 56; y++)
        {
            for (int x = 0; x < 56; x++)
            {
                float score = outputMap[0, 0, y, x];
                if (MathF.Abs(score - poolMap[0, 0, y, x]) > 1e-5f)
                    continue;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = y * 56 + x;
                }
            }
        }

        confidence = bestScore;
        if (bestIndex < 0 || bestScore < 0.60f)
            return null;

        int cellY = bestIndex / 56;
        int cellX = bestIndex % 56;
        float radius = outputMap[0, 1, cellY, cellX] * 112f;
        float xCenter = cellX * 4f;
        float yCenter = cellY * 4f;
        float boxX = (xCenter - radius) * frame.Width / 224f;
        float boxY = (yCenter - radius) * frame.Height / 224f;
        float boxW = (radius * 2f) * frame.Width / 224f;
        float boxH = (radius * 2f) * frame.Height / 224f;
        return ClampRect(new RectangleF(boxX, boxY, boxW, boxH), frame.Width, frame.Height);
    }

    private float[] TrackLandmarks(CameraFrame frame, RectangleF face, out float confidence)
    {
        confidence = 0f;
        if (_landmarkSession == null)
            return new float[LandmarkCount * 3];

        RectangleF crop = ExpandCrop(face, frame.Width, frame.Height);
        var input = new DenseTensor<float>(new[] { 1, 3, 224, 224 });
        FillResizedRgbTensor(frame, input.Buffer.Span, 224, 224, normalizeForDetection: false, crop);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_landmarkInputName, input)
        };
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = _landmarkSession.Run(inputs);
        var tensor = outputs.ElementAt(0).AsTensor<float>();
        var raw = tensor.ToArray();
        return DecodeLandmarks(raw, crop, out confidence);
    }

    private RawFaceTrackingFrame BuildRawFrame(
        CameraFrame frame,
        float[] landmarks,
        RectangleF face,
        float detectorConfidence,
        float landmarkConfidence,
        float detectorMs,
        float landmarksMs)
    {
        float eyeLeftOpen = EyeAspectRatio(landmarks, 36, 39, 37, 41, 38, 40);
        float eyeRightOpen = EyeAspectRatio(landmarks, 42, 45, 43, 47, 44, 46);
        var leftEye = Midpoint(landmarks, 36, 39);
        var rightEye = Midpoint(landmarks, 42, 45);
        var eyeMid = (leftEye + rightEye) * 0.5f;
        var nose = Point(landmarks, 30);
        var mouthCenter = Midpoint(landmarks, 51, 57);
        var upperLipCenter = AveragePoint(landmarks, 50, 51, 52);
        float eyeDistance = Vector2.Distance(leftEye, rightEye);
        float mouthWidthPx = Distance(landmarks, 48, 54);
        float mouthOpenPx = Distance(landmarks, 51, 57);
        // OpenSeeFace 66-point drops iBUG68 inner-lip corners (60, 64), shifting inner lip indices.
        // iBUG68 inner top center (62) → index 61; iBUG68 inner bottom center (66) → index 64.
        float jawOpenPx = Distance(landmarks, 61, 64);
        if (jawOpenPx <= 0f)
            jawOpenPx = mouthOpenPx;

        // Convert tracked distances into face-relative ratios so the retarget
        // calibration ranges are stable instead of exploding from raw pixels.
        float faceScale = MathF.Max(eyeDistance, MathF.Max(face.Width, face.Height) * 0.25f);
        faceScale = MathF.Max(faceScale, 1f);
        float mouthWidth = mouthWidthPx / faceScale;
        float mouthOpen = mouthOpenPx / faceScale;
        float jawOpen = jawOpenPx / faceScale;
        float roll = MathF.Atan2(rightEye.Y - leftEye.Y, rightEye.X - leftEye.X);
        float yaw = eyeDistance > 0.001f ? (nose.X - eyeMid.X) / (eyeDistance * 0.5f) : 0f;
        float pitchBase = MathF.Max(0.001f, mouthCenter.Y - eyeMid.Y);
        float pitch = ((nose.Y - eyeMid.Y) / pitchBase) - 0.55f;

        float browLeft = VerticalDistance(landmarks, 37, 19) / faceScale;
        float browRight = VerticalDistance(landmarks, 44, 24) / faceScale;
        // Use upper-lip corner lift, not mouth-center delta. Mouth-center rises/falls with
        // jaw opening and was falsely driving smile/happy when the user only opened the mouth.
        float smileLeft = Math.Max(0f, upperLipCenter.Y - Point(landmarks, 48).Y) / faceScale;
        float smileRight = Math.Max(0f, upperLipCenter.Y - Point(landmarks, 54).Y) / faceScale;
        float roundness = mouthWidthPx > 0.001f ? mouthOpenPx / mouthWidthPx : 0f;
        float eyeLookHorizontal = eyeDistance > 0.001f ? (nose.X - eyeMid.X) / eyeDistance : 0f;
        float eyeLookVertical = pitch;
        float trackConfidence = Math.Clamp((detectorConfidence + landmarkConfidence) * 0.5f, 0f, 1f);

        return new RawFaceTrackingFrame
        {
            Timestamp = frame.Timestamp,
            IsTracking = landmarkConfidence >= 0.45f,
            TrackingConfidence = trackConfidence,
            DetectorConfidence = detectorConfidence,
            LandmarkConfidence = landmarkConfidence,
            Landmarks = landmarks,
            FaceBox = new Vector4(face.X, face.Y, face.Width, face.Height),
            HeadYaw = yaw,
            HeadPitch = pitch,
            HeadRoll = roll,
            EyeDistance = eyeDistance / faceScale,
            EyeBlinkLeft = Math.Clamp(1f - (eyeLeftOpen / 0.30f), 0f, 1f),
            EyeBlinkRight = Math.Clamp(1f - (eyeRightOpen / 0.30f), 0f, 1f),
            EyeOpenLeftRatio = eyeLeftOpen,
            EyeOpenRightRatio = eyeRightOpen,
            EyeLookHorizontal = eyeLookHorizontal,
            EyeLookVertical = eyeLookVertical,
            JawOpen = jawOpen,
            MouthOpen = mouthOpen,
            MouthWidth = mouthWidth,
            MouthRound = roundness,
            SmileLeft = smileLeft,
            SmileRight = smileRight,
            BrowUpLeft = browLeft,
            BrowUpRight = browRight,
            DetectorMs = detectorMs,
            LandmarksMs = landmarksMs
        };
    }

    private float[] DecodeLandmarks(float[] tensor, RectangleF crop, out float confidence)
    {
        confidence = 0f;
        float[] lms = new float[LandmarkCount * 3];
        const int outResI = 28;
        const float outRes = 27f;
        const float res = 223f;
        const float logitFactor = 16f;
        float scaleX = crop.Width / 224f;
        float scaleY = crop.Height / 224f;

        float confidenceSum = 0f;
        for (int i = 0; i < LandmarkCount; i++)
        {
            int best = 0;
            float bestConf = float.MinValue;
            for (int j = 0; j < outResI * outResI; j++)
            {
                float conf = tensor[i * outResI * outResI + j];
                if (conf > bestConf)
                {
                    bestConf = conf;
                    best = j;
                }
            }

            float offX = tensor[(LandmarkCount + i) * outResI * outResI + best];
            float offY = tensor[(LandmarkCount * 2 + i) * outResI * outResI + best];
            offX = res * Logit(offX, logitFactor);
            offY = res * Logit(offY, logitFactor);

            float x = crop.Y + scaleY * (res * MathF.Floor(best / (float)outResI) / outRes + offX);
            float y = crop.X + scaleX * (res * MathF.Floor(best % outResI) / outRes + offY);
            lms[i * 3] = x;
            lms[i * 3 + 1] = y;
            lms[i * 3 + 2] = bestConf;
            confidenceSum += bestConf;
        }

        confidence = confidenceSum / LandmarkCount;
        return lms;
    }

    private static float Logit(float p, float factor)
    {
        p = Math.Clamp(p, 0.0000001f, 0.9999999f);
        return MathF.Log(p / (1f - p)) / factor;
    }

    private static RectangleF ExpandCrop(RectangleF face, int width, int height)
    {
        float x1 = face.X - face.Width * 0.1f;
        float y1 = face.Y - face.Height * 0.125f;
        float x2 = face.Right + face.Width * 0.1f;
        float y2 = face.Bottom + face.Height * 0.125f;
        return ClampRect(RectangleF.FromLTRB(x1, y1, x2, y2), width, height);
    }

    private static RectangleF ClampRect(RectangleF rect, int width, int height)
    {
        float x = Math.Clamp(rect.X, 0, width - 1);
        float y = Math.Clamp(rect.Y, 0, height - 1);
        float right = Math.Clamp(rect.Right, x + 1, width);
        float bottom = Math.Clamp(rect.Bottom, y + 1, height);
        return RectangleF.FromLTRB(x, y, right, bottom);
    }

    private static RectangleF BoundsFromLandmarks(float[] landmarks, int width, int height)
    {
        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;
        for (int i = 0; i < LandmarkCount; i++)
        {
            float x = landmarks[i * 3 + 1];
            float y = landmarks[i * 3];
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }
        return ClampRect(RectangleF.FromLTRB(minX, minY, maxX, maxY), width, height);
    }

    private static void FillResizedRgbTensor(
        CameraFrame frame,
        Span<float> destination,
        int dstWidth,
        int dstHeight,
        bool normalizeForDetection,
        RectangleF? crop)
    {
        RectangleF sourceRect = crop ?? new RectangleF(0, 0, frame.Width, frame.Height);
        for (int y = 0; y < dstHeight; y++)
        {
            float srcY = sourceRect.Y + ((y + 0.5f) / dstHeight) * sourceRect.Height;
            int iy = Math.Clamp((int)srcY, 0, frame.Height - 1);
            for (int x = 0; x < dstWidth; x++)
            {
                float srcX = sourceRect.X + ((x + 0.5f) / dstWidth) * sourceRect.Width;
                int ix = Math.Clamp((int)srcX, 0, frame.Width - 1);
                int srcIndex = iy * frame.Stride + ix * 4;
                float b = frame.Pixels[srcIndex];
                float g = frame.Pixels[srcIndex + 1];
                float r = frame.Pixels[srcIndex + 2];

                _ = normalizeForDetection;
                // OpenSeeFace expects RGB normalized as:
                // (pixel / (255 * std)) - (mean / std)
                r = (r * RgbRScale) + RgbRBias;
                g = (g * RgbGScale) + RgbGBias;
                b = (b * RgbBScale) + RgbBBias;

                int dstIndex = y * dstWidth + x;
                destination[dstIndex] = r;
                destination[dstWidth * dstHeight + dstIndex] = g;
                destination[dstWidth * dstHeight * 2 + dstIndex] = b;
            }
        }
    }

    private static Vector2 Point(float[] landmarks, int index)
        => index * 3 + 1 < landmarks.Length
            ? new Vector2(landmarks[index * 3 + 1], landmarks[index * 3])
            : Vector2.Zero;

    private static Vector2 Midpoint(float[] landmarks, int a, int b)
        => (Point(landmarks, a) + Point(landmarks, b)) * 0.5f;

    private static Vector2 AveragePoint(float[] landmarks, params int[] indices)
    {
        if (indices.Length == 0)
            return Vector2.Zero;

        Vector2 sum = Vector2.Zero;
        foreach (int index in indices)
            sum += Point(landmarks, index);
        return sum / indices.Length;
    }

    private static float Distance(float[] landmarks, int a, int b)
    {
        if (a * 3 + 1 >= landmarks.Length || b * 3 + 1 >= landmarks.Length)
            return 0f;

        return Vector2.Distance(Point(landmarks, a), Point(landmarks, b));
    }

    private static float VerticalDistance(float[] landmarks, int lower, int upper)
    {
        var a = Point(landmarks, lower);
        var b = Point(landmarks, upper);
        return Math.Max(0f, a.Y - b.Y);
    }

    private static float EyeAspectRatio(float[] landmarks, int left, int right, int top1, int bottom1, int top2, int bottom2)
    {
        float width = Distance(landmarks, left, right);
        if (width <= 0.0001f)
            return 0f;

        float height = (Distance(landmarks, top1, bottom1) + Distance(landmarks, top2, bottom2)) * 0.5f;
        return height / width;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();
        _externalProvider?.Dispose();
        _landmarkSession?.Dispose();
        _detectionSession?.Dispose();
    }
}
