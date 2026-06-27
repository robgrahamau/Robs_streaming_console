using System.Drawing;
using System.Numerics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Steaming.Application.Models;

namespace Steaming.Application.Services;

// MediaPipe Face Mesh ONNX provider.
// Required model files in Assets/FaceTracking/MediaPipe/:
//   face_detection_short.onnx  — BlazeFace short-range detector (Unity inference-engine-blaze-face)
//                                Input:  [1, 128, 128, 3]  float32 NHWC, RGB, -1..1
//                                Output: regressors [1, 896, 16], classificators [1, 896, 1]
//                                896 anchors = 16x16x2 (stride 8) + 8x8x6 (stride 16)
//   face_mesh.onnx             — PINTO 032_FaceMesh, face_mesh_192x192.onnx
//                                Input:  [1, 3, 192, 192]  float32 NCHW, RGB, 0..1
//                                Output: landmarks [1, 1, 1, 1404] (468 pts * xyz), score [1, 1, 1, 1]
public sealed class MediaPipeFaceProvider : IFaceTrackingProvider
{
    private const int DetectSize    = 128;
    private const int MeshSize      = 192;
    private const int LandmarkCount = 468;
    private const float DetectScale = 128f; // SSD decode scale

    // MediaPipe face mesh landmark indices (canonical map)
    private const int ML = 61;   // mouth left corner
    private const int MR = 291;  // mouth right corner
    private const int MUT = 13;  // upper lip inner top center
    private const int MLT = 14;  // lower lip inner bottom center
    private const int JAW_TOP = 13;
    private const int JAW_BOT = 14;
    private const int EL_OUT = 33;  private const int EL_IN  = 133;
    private const int EL_T1  = 159; private const int EL_B1  = 145;
    private const int EL_T2  = 158; private const int EL_B2  = 153;
    private const int ER_OUT = 362; private const int ER_IN  = 263;
    private const int ER_T1  = 386; private const int ER_B1  = 374;
    private const int ER_T2  = 385; private const int ER_B2  = 380;
    private const int BL1 = 70; private const int BL2 = 63; private const int BL3 = 105;
    private const int BR1 = 336; private const int BR2 = 296; private const int BR3 = 334;
    private const int NOSE_TIP  = 4;
    private const int CHIN      = 152;
    private const int L_TEMPLE  = 234;
    private const int R_TEMPLE  = 454;

    private InferenceSession? _detectSession;
    private InferenceSession? _meshSession;
    private string _detectInput = "input";
    private string _meshInput   = "input";
    private RectangleF? _lastFace;
    private float _lastDetectConf;

    // Pre-computed BlazeFace anchor centers (normalized 0..1)
    // 512 anchors from 16x16 grid stride=8, 384 from 8x8 grid stride=16
    private float[] _anchorCx = [];
    private float[] _anchorCy = [];

    public string ProviderName => "MediaPipe";

    public void Load(string assetRoot, SessionOptions options)
    {
        string dir    = Path.Combine(assetRoot, "MediaPipe");
        string detect = Path.Combine(dir, "face_detection_short.onnx");
        string mesh   = Path.Combine(dir, "face_mesh.onnx");

        if (!File.Exists(detect))
            throw new FileNotFoundException($"MediaPipe detection model not found: {detect}");
        if (!File.Exists(mesh))
            throw new FileNotFoundException($"MediaPipe face mesh model not found: {mesh}");

        _detectSession = new InferenceSession(detect, options);
        _meshSession   = new InferenceSession(mesh,   options);
        _detectInput   = _detectSession.InputMetadata.Keys.First();
        _meshInput     = _meshSession.InputMetadata.Keys.First();

        GenerateAnchors();
    }

    public RawFaceTrackingFrame ProcessFrame(CameraFrame frame)
    {
        // BlazeFace runs only when tracking is lost — one model per frame while locked
        if (_lastFace == null)
        {
            _lastFace = Detect(frame, out _lastDetectConf);
            if (_lastFace == null)
            {
                return new RawFaceTrackingFrame
                {
                    Timestamp          = frame.Timestamp,
                    IsTracking         = false,
                    TrackingConfidence = 0f,
                    DetectorConfidence = _lastDetectConf,
                    Landmarks          = []
                };
            }
        }

        var (landmarks, landmarks3d, meshConf) = RunMesh(frame, _lastFace.Value);

        if (meshConf >= 0.5f)
            _lastFace = LandmarkBounds(landmarks, frame.Width, frame.Height);
        else
            _lastFace = null;

        return BuildFrame(frame, landmarks, landmarks3d, meshConf);
    }

    private static RectangleF LandmarkBounds(float[] lms, int w, int h)
    {
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < LandmarkCount; i++)
        {
            float x = lms[i * 3 + 1];
            float y = lms[i * 3];
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }
        return ClampRect(RectangleF.FromLTRB(minX, minY, maxX, maxY), w, h);
    }

    private RectangleF? Detect(CameraFrame frame, out float confidence)
    {
        confidence = 0f;
        if (_detectSession == null) return null;

        // BlazeFace: NHWC [1,128,128,3], RGB -1..1
        var t = new DenseTensor<float>(new[] { 1, DetectSize, DetectSize, 3 });
        FillHwcTensor(frame, t.Buffer.Span, DetectSize, DetectSize, null, scale: 1f / 127.5f, bias: -1f);

        using var result = _detectSession.Run([NamedOnnxValue.CreateFromTensor(_detectInput, t)]);
        var boxes  = result.ElementAt(0).AsTensor<float>(); // [1, 896, 16] regressors
        var scores = result.ElementAt(1).AsTensor<float>(); // [1, 896, 1]  classificators

        int best = -1;
        float bestScore = 0.5f;
        for (int i = 0; i < 896; i++)
        {
            float s = Sigmoid(scores[0, i, 0]);
            if (s > bestScore) { bestScore = s; best = i; }
        }

        confidence = bestScore;
        if (best < 0) return null;

        // SSD anchor decoding: MediaPipe TFLite convention is [cy, cx, h, w] (y before x)
        // Divide by DetectScale then add normalized anchor center to get normalized box center
        float normCx = boxes[0, best, 1] / DetectScale + _anchorCx[best];
        float normCy = boxes[0, best, 0] / DetectScale + _anchorCy[best];
        float normW  = boxes[0, best, 3] / DetectScale;
        float normH  = boxes[0, best, 2] / DetectScale;

        float x = (normCx - normW * 0.5f) * frame.Width;
        float y = (normCy - normH * 0.5f) * frame.Height;
        float w = normW * frame.Width;
        float h = normH * frame.Height;
        return ClampRect(new RectangleF(x, y, w, h), frame.Width, frame.Height);
    }

    private (float[] landmarks, float[] landmarks3d, float confidence) RunMesh(CameraFrame frame, RectangleF face)
    {
        if (_meshSession == null) return (new float[LandmarkCount * 3], new float[LandmarkCount * 3], 0f);

        RectangleF crop = ExpandCrop(face, frame.Width, frame.Height);

        // Face mesh: NCHW [1,3,192,192], RGB 0..1
        var t = new DenseTensor<float>(new[] { 1, 3, MeshSize, MeshSize });
        FillChwTensor(frame, t.Buffer.Span, MeshSize, MeshSize, crop, scale: 1f / 255f, bias: 0f);

        using var result = _meshSession.Run([NamedOnnxValue.CreateFromTensor(_meshInput, t)]);
        var raw  = result.ElementAt(0).AsTensor<float>(); // [1, 1, 1, 1404]
        var flag = result.ElementAt(1).AsTensor<float>(); // [1, 1, 1, 1]

        float conf = Sigmoid(flag[0, 0, 0, 0]);
        float[] lms   = new float[LandmarkCount * 3]; // [y_px, x_px, conf] for overlay
        float[] lms3d = new float[LandmarkCount * 3]; // [x, y, z] in model space (0..192) for metrics
        float sx = crop.Width  / MeshSize;
        float sy = crop.Height / MeshSize;
        for (int i = 0; i < LandmarkCount; i++)
        {
            float mx = raw[0, 0, 0, i * 3];
            float my = raw[0, 0, 0, i * 3 + 1];
            float mz = raw[0, 0, 0, i * 3 + 2];
            // Overlay uses [y_px, x_px, conf] — matches OpenSeeFace convention
            lms[i * 3]     = crop.Y + my * sy;
            lms[i * 3 + 1] = crop.X + mx * sx;
            lms[i * 3 + 2] = conf;
            // 3D uses raw model coords — rotation-invariant distances
            lms3d[i * 3]     = mx;
            lms3d[i * 3 + 1] = my;
            lms3d[i * 3 + 2] = mz;
        }
        return (lms, lms3d, conf);
    }

    private static RawFaceTrackingFrame BuildFrame(CameraFrame frame, float[] lms, float[] lms3d, float conf)
    {
        // Head pose from 2D pixel positions (these ARE projection-dependent by design)
        var leftEye2d  = Midpoint(lms, EL_OUT, EL_IN);
        var rightEye2d = Midpoint(lms, ER_OUT, ER_IN);
        var eyeMid2d   = (leftEye2d + rightEye2d) * 0.5f;
        var noseTip2d  = Pt(lms, NOSE_TIP);
        var mouthL2d   = Pt(lms, ML);
        var mouthR2d   = Pt(lms, MR);
        var mouthCtr2d = (mouthL2d + mouthR2d) * 0.5f;
        float eyeDist2d = Vector2.Distance(leftEye2d, rightEye2d);

        float roll  = MathF.Atan2(rightEye2d.Y - leftEye2d.Y, rightEye2d.X - leftEye2d.X);
        float yaw   = eyeDist2d > 0.001f ? (noseTip2d.X - eyeMid2d.X) / (eyeDist2d * 0.5f) : 0f;
        float pitchBase = MathF.Max(0.001f, mouthCtr2d.Y - eyeMid2d.Y);
        float pitch = ((noseTip2d.Y - eyeMid2d.Y) / pitchBase) - 0.55f;

        // Expression metrics from 3D model-space coords — stable under head rotation
        var leftEye3d  = Midpoint3(lms3d, EL_OUT, EL_IN);
        var rightEye3d = Midpoint3(lms3d, ER_OUT, ER_IN);
        float eyeDist3d = Vector3.Distance(leftEye3d, rightEye3d);
        float faceScale = MathF.Max(eyeDist3d, 1f);

        float jawOpenPx    = Dist3(lms3d, JAW_TOP, JAW_BOT);
        float mouthWidthPx = Dist3(lms3d, ML, MR);
        float jawOpen   = jawOpenPx    / faceScale;
        float mouthWidth = mouthWidthPx / faceScale;
        float mouthOpen  = jawOpen;
        float roundness  = mouthWidthPx > 0.001f ? jawOpenPx / mouthWidthPx : 0f;

        float eyeLeftOpen  = EyeAR3(lms3d, EL_OUT, EL_IN, EL_T1, EL_B1, EL_T2, EL_B2);
        float eyeRightOpen = EyeAR3(lms3d, ER_OUT, ER_IN, ER_T1, ER_B1, ER_T2, ER_B2);

        float browLeft  = (Dist3(lms3d, EL_T1, BL1) + Dist3(lms3d, EL_T1, BL2) + Dist3(lms3d, EL_T1, BL3)) / (3f * faceScale);
        float browRight = (Dist3(lms3d, ER_T1, BR1) + Dist3(lms3d, ER_T1, BR2) + Dist3(lms3d, ER_T1, BR3)) / (3f * faceScale);

        var upperLipCtr3d = Pt3(lms3d, MUT);
        var mouthL3d      = Pt3(lms3d, ML);
        var mouthR3d      = Pt3(lms3d, MR);
        float smileLeft  = MathF.Max(0f, upperLipCtr3d.Y - mouthL3d.Y) / faceScale;
        float smileRight = MathF.Max(0f, upperLipCtr3d.Y - mouthR3d.Y) / faceScale;

        float trackConf = Math.Clamp(conf, 0f, 1f);

        return new RawFaceTrackingFrame
        {
            Timestamp          = frame.Timestamp,
            IsTracking         = conf >= 0.5f,
            TrackingConfidence = trackConf,
            DetectorConfidence = trackConf,
            LandmarkConfidence = trackConf,
            Landmarks          = lms,
            FaceBox            = new System.Numerics.Vector4(0, 0, frame.Width, frame.Height),
            HeadYaw            = yaw,
            HeadPitch          = pitch,
            HeadRoll           = roll,
            EyeDistance        = eyeDist3d / faceScale,
            EyeBlinkLeft       = Math.Clamp(1f - (eyeLeftOpen  / 0.30f), 0f, 1f),
            EyeBlinkRight      = Math.Clamp(1f - (eyeRightOpen / 0.30f), 0f, 1f),
            EyeOpenLeftRatio   = eyeLeftOpen,
            EyeOpenRightRatio  = eyeRightOpen,
            EyeLookHorizontal  = eyeDist2d > 0.001f ? (noseTip2d.X - eyeMid2d.X) / eyeDist2d : 0f,
            EyeLookVertical    = pitch,
            JawOpen            = jawOpen,
            MouthOpen          = mouthOpen,
            MouthWidth         = mouthWidth,
            MouthRound         = roundness,
            SmileLeft          = smileLeft,
            SmileRight         = smileRight,
            BrowUpLeft         = browLeft,
            BrowUpRight        = browRight,
            DetectorMs         = 0f,
            LandmarksMs        = 0f
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // NHWC tensor fill for BlazeFace (model expects HWC layout)
    private static void FillHwcTensor(CameraFrame frame, Span<float> dst, int dw, int dh,
                                      RectangleF? crop, float scale, float bias)
    {
        var src = crop ?? new RectangleF(0, 0, frame.Width, frame.Height);
        for (int y = 0; y < dh; y++)
        {
            float sy = src.Y + ((y + 0.5f) / dh) * src.Height;
            int   iy = Math.Clamp((int)sy, 0, frame.Height - 1);
            for (int x = 0; x < dw; x++)
            {
                float sx = src.X + ((x + 0.5f) / dw) * src.Width;
                int   ix = Math.Clamp((int)sx, 0, frame.Width - 1);
                int   si = iy * frame.Stride + ix * 4; // BGRA source
                int   di = (y * dw + x) * 3;
                dst[di]     = frame.Pixels[si + 2] * scale + bias; // R
                dst[di + 1] = frame.Pixels[si + 1] * scale + bias; // G
                dst[di + 2] = frame.Pixels[si]     * scale + bias; // B
            }
        }
    }

    // NCHW tensor fill for FaceMesh (model expects CHW layout: all R, then all G, then all B)
    private static void FillChwTensor(CameraFrame frame, Span<float> dst, int dw, int dh,
                                      RectangleF? crop, float scale, float bias)
    {
        var src  = crop ?? new RectangleF(0, 0, frame.Width, frame.Height);
        int area = dw * dh;
        for (int y = 0; y < dh; y++)
        {
            float sy = src.Y + ((y + 0.5f) / dh) * src.Height;
            int   iy = Math.Clamp((int)sy, 0, frame.Height - 1);
            for (int x = 0; x < dw; x++)
            {
                float sx = src.X + ((x + 0.5f) / dw) * src.Width;
                int   ix = Math.Clamp((int)sx, 0, frame.Width - 1);
                int   si = iy * frame.Stride + ix * 4; // BGRA source
                int   pi = y * dw + x;
                dst[pi]          = frame.Pixels[si + 2] * scale + bias; // R plane
                dst[area + pi]   = frame.Pixels[si + 1] * scale + bias; // G plane
                dst[2 * area + pi] = frame.Pixels[si]   * scale + bias; // B plane
            }
        }
    }

    private void GenerateAnchors()
    {
        // BlazeFace short-range 128x128 — 896 anchors
        // Layer 0: stride=8  → 16x16 grid × 2 anchors = 512
        // Layer 1: stride=16 → 8x8  grid × 6 anchors = 384
        int[] gridSizes   = [16, 8];
        int[] perCell     = [2, 6];
        _anchorCx = new float[896];
        _anchorCy = new float[896];
        int idx = 0;
        for (int layer = 0; layer < 2; layer++)
        {
            int g = gridSizes[layer];
            int n = perCell[layer];
            for (int r = 0; r < g; r++)
                for (int c = 0; c < g; c++)
                    for (int a = 0; a < n; a++)
                    {
                        _anchorCx[idx] = (c + 0.5f) / g;
                        _anchorCy[idx] = (r + 0.5f) / g;
                        idx++;
                    }
        }
    }

    // 2D helpers — use lms [y_px, x_px, conf], returns Vector2(x,y)
    private static Vector2 Pt(float[] lms, int i)
        => new(lms[i * 3 + 1], lms[i * 3]);

    private static Vector2 Midpoint(float[] lms, int a, int b)
        => (Pt(lms, a) + Pt(lms, b)) * 0.5f;

    // 3D helpers — use lms3d [x, y, z] in model space
    private static Vector3 Pt3(float[] lms3d, int i)
        => new(lms3d[i * 3], lms3d[i * 3 + 1], lms3d[i * 3 + 2]);

    private static Vector3 Midpoint3(float[] lms3d, int a, int b)
        => (Pt3(lms3d, a) + Pt3(lms3d, b)) * 0.5f;

    private static float Dist3(float[] lms3d, int a, int b)
        => Vector3.Distance(Pt3(lms3d, a), Pt3(lms3d, b));

    private static float EyeAR3(float[] lms3d, int outer, int inner, int t1, int b1, int t2, int b2)
    {
        float h = Dist3(lms3d, t1, b1) + Dist3(lms3d, t2, b2);
        float w = Dist3(lms3d, outer, inner);
        return w > 0.001f ? h / (2f * w) : 0f;
    }

    private static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));

    private static RectangleF BlendRect(RectangleF a, RectangleF b, float t)
        => RectangleF.FromLTRB(
            a.Left   + (b.Left   - a.Left)   * t,
            a.Top    + (b.Top    - a.Top)    * t,
            a.Right  + (b.Right  - a.Right)  * t,
            a.Bottom + (b.Bottom - a.Bottom) * t);

    private static RectangleF ExpandCrop(RectangleF face, int w, int h)
    {
        float x1 = face.X - face.Width  * 0.15f;
        float y1 = face.Y - face.Height * 0.20f;
        float x2 = face.Right  + face.Width  * 0.15f;
        float y2 = face.Bottom + face.Height * 0.15f;
        return ClampRect(RectangleF.FromLTRB(x1, y1, x2, y2), w, h);
    }

    private static RectangleF ClampRect(RectangleF r, int w, int h)
    {
        float x = Math.Clamp(r.X, 0, w - 1);
        float y = Math.Clamp(r.Y, 0, h - 1);
        return RectangleF.FromLTRB(x, y, Math.Clamp(r.Right, x + 1, w), Math.Clamp(r.Bottom, y + 1, h));
    }

    public void Dispose()
    {
        _detectSession?.Dispose();
        _meshSession?.Dispose();
    }
}
