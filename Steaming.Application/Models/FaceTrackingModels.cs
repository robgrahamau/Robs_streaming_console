using System.Numerics;

namespace Steaming.Application.Models;

public enum FaceTrackingMouthMode
{
    CameraAndVoice,
    CameraWithVoiceFallback,
    VoiceOnly
}

public sealed class RawFaceTrackingFrame
{
    public DateTimeOffset Timestamp { get; init; }
    public bool IsTracking { get; init; }
    public float TrackingConfidence { get; init; }
    public float DetectorConfidence { get; init; }
    public float LandmarkConfidence { get; init; }
    public float[] Landmarks { get; init; } = [];
    public Vector4 FaceBox { get; init; }
    public float HeadYaw { get; init; }
    public float HeadPitch { get; init; }
    public float HeadRoll { get; init; }
    public float EyeDistance { get; init; }
    public float EyeBlinkLeft { get; init; }
    public float EyeBlinkRight { get; init; }
    public float EyeOpenLeftRatio { get; init; }
    public float EyeOpenRightRatio { get; init; }
    public float EyeLookHorizontal { get; init; }
    public float EyeLookVertical { get; init; }
    public float JawOpen { get; init; }
    public float MouthOpen { get; init; }
    public float MouthWidth { get; init; }
    public float MouthRound { get; init; }
    public float SmileLeft { get; init; }
    public float SmileRight { get; init; }
    public float BrowUpLeft { get; init; }
    public float BrowUpRight { get; init; }
    public float DetectorMs { get; init; }
    public float LandmarksMs { get; init; }
}

public sealed record FaceTrackingState
{
    public static readonly FaceTrackingState Neutral = new();

    public bool IsTracking { get; init; }
    public float TrackingConfidence { get; init; }
    public float HeadYaw { get; init; }
    public float HeadPitch { get; init; }
    public float HeadRoll { get; init; }
    public float NeckYaw { get; init; }
    public float NeckPitch { get; init; }
    public float NeckRoll { get; init; }
    public float EyeBlinkLeft { get; init; }
    public float EyeBlinkRight { get; init; }
    public float EyeLookUp { get; init; }
    public float EyeLookDown { get; init; }
    public float EyeLookLeft { get; init; }
    public float EyeLookRight { get; init; }
    public float JawOpen { get; init; }
    public float MouthAa { get; init; }
    public float MouthIh { get; init; }
    public float MouthOu { get; init; }
    public float MouthEe { get; init; }
    public float MouthOh { get; init; }
    public float SmileLeft { get; init; }
    public float SmileRight { get; init; }
    public float BrowUpLeft { get; init; }
    public float BrowUpRight { get; init; }
    public float RetargetMs { get; init; }
}

public sealed class FaceTrackingCalibrationProfile
{
    public float NeutralHeadYaw { get; set; }
    public float NeutralHeadPitch { get; set; }
    public float NeutralHeadRoll { get; set; }
    public float NeutralEyeHorizontal { get; set; }
    public float NeutralEyeVertical { get; set; }
    public float NeutralEyeOpenLeftRatio { get; set; } = 0.28f;
    public float NeutralEyeOpenRightRatio { get; set; } = 0.28f;
    public float BlinkClosedLeftEyeOpenRatio { get; set; } = 0.08f;
    public float BlinkClosedRightEyeOpenRatio { get; set; } = 0.08f;
    public float NeutralBlinkLeft { get; set; }
    public float NeutralBlinkRight { get; set; }
    public float NeutralJawOpen { get; set; }
    public float NeutralMouthOpen { get; set; }
    public float NeutralMouthWidth { get; set; }
    public float NeutralMouthRound { get; set; }
    public float NeutralSmileLeft { get; set; }
    public float NeutralSmileRight { get; set; }
    public float NeutralBrowLeft { get; set; }
    public float NeutralBrowRight { get; set; }
    public float EyeOpenOffset { get; set; }
    public float JawOpenScale { get; set; } = 1f;
    public float BlinkClosedThresholdLeft { get; set; } = 0.18f;
    public float BlinkClosedThresholdRight { get; set; } = 0.18f;
    public float JawRange { get; set; } = 0.18f;
    public float MouthWidthRange { get; set; } = 0.22f;
    public float MouthRoundRange { get; set; } = 0.18f;
    public float BrowRange { get; set; } = 0.18f;
    public float HeadSmoothing { get; set; } = 0.32f;
    public float MouthSmoothing { get; set; } = 0.28f;
    public float EyeSmoothing { get; set; } = 0.34f;
    public float BrowSmoothing { get; set; } = 0.25f;
    public float NeutralEyeDistance { get; set; } = 0.5f;
    public float HeadRotationScale { get; set; } = 1.0f;
    public float HeadDeadzone { get; set; } = 0.015f;
    public float EyeDeadzone { get; set; } = 0.02f;
    public float MouthDeadzone { get; set; } = 0.02f;
    public bool InvertYaw { get; set; }
    public bool InvertPitch { get; set; }
    public bool InvertRoll { get; set; }
    // Per-expression output scale — lets user tune each expression without recalibrating.
    public float AaScale { get; set; } = 1.0f;
    public float IhScale { get; set; } = 1.0f;
    public float OuScale { get; set; } = 1.0f;
    public float EeScale { get; set; } = 1.0f;
    public float OhScale { get; set; } = 1.0f;
}

public sealed class FaceTrackingSettings
{
    public string SelectedCameraId { get; set; } = "";
    public bool TrackingEnabled { get; set; } = true;
    public bool AudioFallbackEnabled { get; set; } = true;
    public bool PreviewVisible { get; set; }
    public bool CameraPreviewVisible { get; set; }
    public float VoiceVolumeSensitivity { get; set; } = 1f;
    public FaceTrackingMouthMode MouthMode { get; set; } = FaceTrackingMouthMode.CameraAndVoice;
    public string ProviderPreference { get; set; } = "DirectML";
    public int FpsCap { get; set; } = 15;
    public string SelectedModelPackVersion { get; set; } = "OpenSeeFace-1";
    // "OpenSeeFace" or "MediaPipe"
    public string TrackingModel { get; set; } = "OpenSeeFace";
}

public readonly record struct AudioMouthInput(
    float Amplitude,
    float Aa,
    float Ih,
    float Ou,
    float Ee,
    float Oh);

public readonly record struct CameraFrame(
    byte[] Pixels,
    int Width,
    int Height,
    int Stride,
    DateTimeOffset Timestamp);

public readonly record struct FaceTrackingDiagnosticsSnapshot(
    string ProviderName,
    float CameraFps,
    float TrackerFps,
    float DetectorMs,
    float LandmarksMs,
    float RetargetMs,
    int DroppedFrames,
    float CurrentConfidence,
    bool FaceLocked);
