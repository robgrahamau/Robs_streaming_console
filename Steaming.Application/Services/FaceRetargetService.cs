using System.Diagnostics;
using Steaming.Application.Models;

namespace Steaming.Application.Services;

public sealed class FaceRetargetService
{
    private readonly MicCaptureService _mic;
    private readonly FaceTrackingDiagnosticsService _diagnostics;
    private readonly object _lock = new();
    private FaceTrackingCalibrationProfile _calibration = new();
    private FaceTrackingSettings _settings = new();
    private FaceTrackingState _current = FaceTrackingState.Neutral;

    public FaceRetargetService(MicCaptureService mic, FaceTrackingDiagnosticsService diagnostics)
    {
        _mic = mic;
        _diagnostics = diagnostics;
    }

    public void UpdateCalibration(FaceTrackingCalibrationProfile calibration)
    {
        lock (_lock) _calibration = calibration;
    }

    public void UpdateSettings(FaceTrackingSettings settings)
    {
        lock (_lock) _settings = settings;
    }

    public AudioMouthInput GetAudioInput(float sensitivity)
    {
        const float NoiseFloor = 0.035f;
        const float FullSpeech = 0.20f;

        float ampRaw = Math.Clamp(_mic.Amplitude * sensitivity, 0f, 1f);
        float amp = Math.Clamp((ampRaw - NoiseFloor) / (FullSpeech - NoiseFloor), 0f, 1f);
        if (amp <= 0f)
            return new AudioMouthInput(0f, 0f, 0f, 0f, 0f, 0f);

        float aa = _mic.VisemeAaHint;
        float ih = _mic.VisemeIhHint;
        float ou = _mic.VisemeOuHint;
        float ee = _mic.VisemeEeHint;
        float oh = _mic.VisemeOhHint;

        float hintFloor = MathF.Min(aa, MathF.Min(ih, MathF.Min(ou, MathF.Min(ee, oh))));
        aa = Math.Max(0f, aa - hintFloor);
        ih = Math.Max(0f, ih - hintFloor);
        ou = Math.Max(0f, ou - hintFloor);
        ee = Math.Max(0f, ee - hintFloor);
        oh = Math.Max(0f, oh - hintFloor);

        const float HintContrast = 1.85f;
        aa = MathF.Pow(aa, HintContrast);
        ih = MathF.Pow(ih, HintContrast);
        ou = MathF.Pow(ou, HintContrast) * 1.18f;
        ee = MathF.Pow(ee, HintContrast);
        oh = MathF.Pow(oh, HintContrast) * 1.12f;

        float sum = aa + ih + ou + ee + oh;
        if (sum > 0.0001f)
        {
            aa /= sum;
            ih /= sum;
            ou /= sum;
            ee /= sum;
            oh /= sum;
        }
        else
        {
            aa = ih = ou = ee = oh = 0f;
        }

        return new AudioMouthInput(
            amp,
            Math.Clamp(aa * amp, 0f, 1f),
            Math.Clamp(ih * amp, 0f, 1f),
            Math.Clamp(ou * amp, 0f, 1f),
            Math.Clamp(ee * amp, 0f, 1f),
            Math.Clamp(oh * amp, 0f, 1f));
    }

    public FaceTrackingState RetargetAudioOnly()
        => Retarget(new RawFaceTrackingFrame
        {
            Timestamp = DateTimeOffset.UtcNow,
            IsTracking = false,
            TrackingConfidence = 0f,
            DetectorConfidence = 0f,
            LandmarkConfidence = 0f,
            Landmarks = []
        });

    public FaceTrackingState Retarget(RawFaceTrackingFrame raw)
    {
        var sw = Stopwatch.StartNew();
        FaceTrackingCalibrationProfile calibration;
        FaceTrackingSettings settings;
        FaceTrackingState current;
        lock (_lock)
        {
            calibration = _calibration;
            settings = _settings;
            current = _current;
        }

        var audio = GetAudioInput(settings.VoiceVolumeSensitivity);
        float confidence = Math.Clamp(raw.TrackingConfidence, 0f, 1f);
        float fallbackAssist = settings.AudioFallbackEnabled ? 1f : 0f;
        // CameraAndVoice: camera dominates when confidence is high; voice fills in when confidence is low.
        float mouthAssist = settings.MouthMode switch
        {
            FaceTrackingMouthMode.VoiceOnly => 1f,
            FaceTrackingMouthMode.CameraWithVoiceFallback => (1f - confidence) * fallbackAssist,
            _ => Math.Max(0f, 0.45f - confidence * 0.40f)
        };
        float cameraWeight = settings.MouthMode == FaceTrackingMouthMode.VoiceOnly ? 0f : Math.Clamp(1f - mouthAssist, 0f, 1f);

        // raw.HeadYaw ≈ 2·tan(angle) from the landmark formula.
        // atan(delta/2) recovers the actual angle; this keeps small movements accurate
        // and prevents the ~10–30% over-rotation that the raw value would cause.
        float rawYawDelta = raw.HeadYaw - calibration.NeutralHeadYaw;
        float headYaw = MathF.Atan(rawYawDelta * 0.5f);
        headYaw = ApplyDeadzone(headYaw, calibration.HeadDeadzone) * calibration.HeadRotationScale;

        float headPitch = ApplyDeadzone(raw.HeadPitch - calibration.NeutralHeadPitch, calibration.HeadDeadzone)
                          * calibration.HeadRotationScale;
        float headRoll = ApplyDeadzone(raw.HeadRoll - calibration.NeutralHeadRoll, calibration.HeadDeadzone)
                         * calibration.HeadRotationScale;
        if (calibration.InvertYaw) headYaw = -headYaw;
        if (calibration.InvertPitch) headPitch = -headPitch;
        if (calibration.InvertRoll) headRoll = -headRoll;

        // Explicit calibration is authoritative. The user captures neutral with a relaxed
        // face; those values are the resting baseline. (A previous session-adaptive baseline
        // drifted with metric noise and ratcheted below rest, making a near-closed mouth read
        // as wide open even right after calibration — removed.)
        float effectiveNeutralMouthRound = calibration.NeutralMouthRound;
        float effectiveNeutralMouthWidth = calibration.NeutralMouthWidth;
        float effectiveNeutralMouthOpen  = calibration.NeutralMouthOpen;
        float effectiveNeutralJawOpen    = calibration.NeutralJawOpen;

        // 2D landmark mouth distances distort under head yaw (eyeDistance shrinks, inflating ratios).
        // Suppress camera mouth contribution quadratically as yaw grows; audio fallback fills in.
        float yawAbs = MathF.Abs(raw.HeadYaw - calibration.NeutralHeadYaw);
        float yawMouthScale = Math.Max(0f, 1f - yawAbs * yawAbs * 1.2f);

        float jawCamera = Normalize(raw.JawOpen, effectiveNeutralJawOpen, calibration.JawRange) * yawMouthScale;
        float mouthOpen = Normalize(raw.MouthOpen, effectiveNeutralMouthOpen, calibration.JawRange) * yawMouthScale;
        float effectiveMouthWidthRange = Math.Max(0.10f, calibration.MouthWidthRange);
        float effectiveSmileRange = Math.Max(0.08f, calibration.MouthWidthRange);
        float mouthWidth = Normalize(raw.MouthWidth, effectiveNeutralMouthWidth, effectiveMouthWidthRange) * yawMouthScale;
        float mouthRound = Normalize(raw.MouthRound, effectiveNeutralMouthRound, calibration.MouthRoundRange) * yawMouthScale;
        float smileLeft = Normalize(raw.SmileLeft, calibration.NeutralSmileLeft, effectiveSmileRange);
        float smileRight = Normalize(raw.SmileRight, calibration.NeutralSmileRight, effectiveSmileRange);
        if (settings.MouthMode == FaceTrackingMouthMode.VoiceOnly)
        {
            smileLeft = 0f;
            smileRight = 0f;
        }
        float browLeft = Normalize(raw.BrowUpLeft, calibration.NeutralBrowLeft, calibration.BrowRange);
        float browRight = Normalize(raw.BrowUpRight, calibration.NeutralBrowRight, calibration.BrowRange);
        float eyeHorizontal = ApplyDeadzone(raw.EyeLookHorizontal - calibration.NeutralEyeHorizontal, calibration.EyeDeadzone);
        float eyeVertical = ApplyDeadzone(raw.EyeLookVertical - calibration.NeutralEyeVertical, calibration.EyeDeadzone);

        // IH and EE are closed-teeth shapes — they must go to near-zero when jaw is open.
        // MouthWidthRange is often calibrated small (e.g. 0.08), so mouthWidth clips at 1.0 easily.
        // Without aggressive suppression, IH/EE co-fire on any plain mouth-open and their cross-sum
        // normalization pushes AA down from ~0.58 to ~0.38, leaving the avatar barely opening.
        float openSuppressIhEe = Math.Max(0f, 1f - mouthOpen * 1.4f);

        // AA: primary open-mouth shape.
        float cameraAa = MathF.Max(0f, mouthOpen * (1f - mouthRound * 0.35f));
        // IH: narrow teeth shape — nearly zero when jaw is open.
        float cameraIh = MathF.Max(0f, mouthWidth * openSuppressIhEe * (1f - mouthRound * 0.5f));
        // OU: rounded and open.
        float cameraOu = MathF.Max(0f, mouthRound * mouthOpen * 0.90f);
        // EE: wide grin — nearly zero when jaw is open.
        float cameraEe = MathF.Max(0f, (mouthWidth * 0.5f + Math.Max(smileLeft, smileRight) * 0.5f)
                                        * (1f - mouthRound * 0.85f) * openSuppressIhEe);
        // OH: lip-rounding shape. Uses a lower gate threshold than AA/IH/EE so it
        // can fire without full jaw opening, but still requires minimal movement to
        // prevent MouthRound drift at rest from showing OH on the avatar.
        float cameraOh = MathF.Max(0f, mouthRound * 1.2f);
        // Gate: ramp 0→1 over normalised mouthOpen [0.06, 0.16].
        // Prevents all expressions from firing at rest due to baseline mismatch.
        float mouthGate = Math.Clamp((mouthOpen - 0.06f) / 0.10f, 0f, 1f);
        float ohGate    = Math.Clamp((mouthOpen - 0.02f) / 0.06f, 0f, 1f);
        cameraAa *= mouthGate;
        cameraIh *= mouthGate;
        cameraOu *= mouthGate;
        cameraEe *= mouthGate;
        cameraOh *= ohGate;

        // Apply per-expression scales, then clamp.
        cameraAa = Math.Min(cameraAa * calibration.AaScale, 1f);
        cameraIh = Math.Min(cameraIh * calibration.IhScale, 1f);
        cameraOu = Math.Min(cameraOu * calibration.OuScale, 1f);
        cameraEe = Math.Min(cameraEe * calibration.EeScale, 1f);
        cameraOh = Math.Min(cameraOh * calibration.OhScale, 1f);

        float jaw = Blend(cameraWeight * jawCamera, mouthAssist * audio.Amplitude);
        float aa = Blend(cameraWeight * cameraAa, mouthAssist * audio.Aa * (0.65f + audio.Amplitude * 0.35f));
        float ih = Blend(cameraWeight * cameraIh, mouthAssist * audio.Ih);
        float ou = Blend(cameraWeight * cameraOu, mouthAssist * audio.Ou);
        float ee = Blend(cameraWeight * cameraEe, mouthAssist * audio.Ee);
        float oh = Blend(cameraWeight * cameraOh, mouthAssist * audio.Oh);

        if (settings.MouthMode == FaceTrackingMouthMode.CameraAndVoice)
        {
            float speechImpulse = audio.Amplitude * 0.25f;
            jaw = Math.Clamp(jaw + speechImpulse, 0f, 1f);
            aa = Math.Clamp(aa + speechImpulse * 0.35f, 0f, 1f);
            oh = Math.Clamp(oh + speechImpulse * 0.18f, 0f, 1f);
        }

        // Jaw strength is a true gain: 0 = jaw off, 1 = unchanged, 2 = double.
        jaw = Math.Clamp(jaw * calibration.JawOpenScale, 0f, 1f);

        // Eye closure must come from calibrated eye-open ratios, not from mouth activity.
        // The previous blink path mixed mouth-open correction into blink and caused the
        // avatar eyes to collapse while speaking even when the camera eyes stayed open.
        float blinkLeft = ComputeBlink(
            raw.EyeOpenLeftRatio,
            calibration.NeutralEyeOpenLeftRatio,
            calibration.BlinkClosedLeftEyeOpenRatio,
            calibration.NeutralBlinkLeft,
            calibration.BlinkClosedThresholdLeft);
        float blinkRight = ComputeBlink(
            raw.EyeOpenRightRatio,
            calibration.NeutralEyeOpenRightRatio,
            calibration.BlinkClosedRightEyeOpenRatio,
            calibration.NeutralBlinkRight,
            calibration.BlinkClosedThresholdRight);
        blinkLeft = Math.Clamp(blinkLeft - calibration.EyeOpenOffset, 0f, 1f);
        blinkRight = Math.Clamp(blinkRight - calibration.EyeOpenOffset, 0f, 1f);

        var next = new FaceTrackingState
        {
            IsTracking = raw.IsTracking,
            TrackingConfidence = confidence,
            HeadYaw = SmoothStable(current.HeadYaw, ClampSigned(headYaw, 0.50f), calibration.HeadSmoothing, 0.008f),
            HeadPitch = SmoothStable(current.HeadPitch, ClampSigned(headPitch, 0.42f), calibration.HeadSmoothing, 0.008f),
            HeadRoll = SmoothStable(current.HeadRoll, ClampSigned(headRoll, 0.38f), calibration.HeadSmoothing, 0.008f),
            NeckYaw = SmoothStable(current.NeckYaw, ClampSigned(headYaw * 0.35f, 0.20f), calibration.HeadSmoothing, 0.006f),
            NeckPitch = SmoothStable(current.NeckPitch, ClampSigned(headPitch * 0.25f, 0.18f), calibration.HeadSmoothing, 0.006f),
            NeckRoll = SmoothStable(current.NeckRoll, ClampSigned(headRoll * 0.25f, 0.15f), calibration.HeadSmoothing, 0.006f),
            EyeBlinkLeft = SmoothStable(current.EyeBlinkLeft, blinkLeft, calibration.EyeSmoothing, 0.02f),
            EyeBlinkRight = SmoothStable(current.EyeBlinkRight, blinkRight, calibration.EyeSmoothing, 0.02f),
            EyeLookLeft = SmoothStable(current.EyeLookLeft, Math.Max(0f, -eyeHorizontal), calibration.EyeSmoothing, 0.015f),
            EyeLookRight = SmoothStable(current.EyeLookRight, Math.Max(0f, eyeHorizontal), calibration.EyeSmoothing, 0.015f),
            EyeLookUp = SmoothStable(current.EyeLookUp, Math.Max(0f, -eyeVertical), calibration.EyeSmoothing, 0.015f),
            EyeLookDown = SmoothStable(current.EyeLookDown, Math.Max(0f, eyeVertical), calibration.EyeSmoothing, 0.015f),
            JawOpen = SmoothStable(current.JawOpen, jaw, calibration.MouthSmoothing, 0.02f),
            MouthAa = SmoothStable(current.MouthAa, aa, calibration.MouthSmoothing, 0.018f),
            MouthIh = SmoothStable(current.MouthIh, ih, calibration.MouthSmoothing, 0.018f),
            MouthOu = SmoothStable(current.MouthOu, ou, calibration.MouthSmoothing, 0.018f),
            MouthEe = SmoothStable(current.MouthEe, ee, calibration.MouthSmoothing, 0.018f),
            MouthOh = SmoothStable(current.MouthOh, oh, calibration.MouthSmoothing, 0.018f),
            SmileLeft = SmoothStable(current.SmileLeft, smileLeft, calibration.MouthSmoothing, 0.02f),
            SmileRight = SmoothStable(current.SmileRight, smileRight, calibration.MouthSmoothing, 0.02f),
            BrowUpLeft = SmoothStable(current.BrowUpLeft, browLeft, calibration.BrowSmoothing, 0.015f),
            BrowUpRight = SmoothStable(current.BrowUpRight, browRight, calibration.BrowSmoothing, 0.015f),
            RetargetMs = 0f
        };

        sw.Stop();
        next = next with { RetargetMs = (float)sw.Elapsed.TotalMilliseconds };
        lock (_lock) _current = next;
        _diagnostics.NoteRetarget(next.RetargetMs);
        return next;
    }

    public void CaptureNeutral(RawFaceTrackingFrame raw)
    {
        lock (_lock)
        {
            _calibration.NeutralHeadYaw = raw.HeadYaw;
            _calibration.NeutralHeadPitch = raw.HeadPitch;
            _calibration.NeutralHeadRoll = raw.HeadRoll;
            _calibration.NeutralEyeHorizontal = raw.EyeLookHorizontal;
            _calibration.NeutralEyeVertical = raw.EyeLookVertical;
            _calibration.NeutralBlinkLeft = raw.EyeBlinkLeft;
            _calibration.NeutralBlinkRight = raw.EyeBlinkRight;
            _calibration.NeutralJawOpen = raw.JawOpen;
            _calibration.NeutralMouthOpen = raw.MouthOpen;
            _calibration.NeutralMouthWidth = raw.MouthWidth;
            _calibration.NeutralMouthRound = raw.MouthRound;
            _calibration.NeutralSmileLeft = raw.SmileLeft;
            _calibration.NeutralSmileRight = raw.SmileRight;
            _calibration.NeutralBrowLeft = raw.BrowUpLeft;
            _calibration.NeutralBrowRight = raw.BrowUpRight;
        }
    }

    private static float Normalize(float value, float min, float range)
        => Math.Clamp((value - min) / Math.Max(range, 0.0001f), 0f, 1f);

    private static float ApplyDeadzone(float value, float deadzone)
    {
        if (MathF.Abs(value) <= deadzone)
            return 0f;

        float sign = MathF.Sign(value);
        return sign * (MathF.Abs(value) - deadzone);
    }

    private static float ClampSigned(float value, float maxAbs)
        => Math.Clamp(value, -maxAbs, maxAbs);

    private static float Smooth(float current, float target, float factor)
        => current + (target - current) * Math.Clamp(factor, 0.01f, 1f);

    private static float SmoothStable(float current, float target, float factor, float stableEpsilon)
    {
        // Within the deadband, settle exactly to the target rather than freezing at the
        // stale current value — otherwise a channel can never reach 0 (or any new resting
        // level) and a "× 0" control leaves a residual it can never decay past.
        if (MathF.Abs(target - current) <= stableEpsilon)
            return target;

        return Smooth(current, target, factor);
    }

    private static float Blend(float camera, float voice)
        => Math.Clamp(camera + voice, 0f, 1f);

    private static float ComputeBlink(
        float rawEyeOpenRatio,
        float neutralEyeOpenRatio,
        float blinkClosedEyeOpenRatio,
        float neutralBlink,
        float blinkClosedThreshold)
    {
        if (neutralEyeOpenRatio > blinkClosedEyeOpenRatio + 0.0001f)
        {
            float openNorm = Math.Clamp(
                (rawEyeOpenRatio - blinkClosedEyeOpenRatio)
                / (neutralEyeOpenRatio - blinkClosedEyeOpenRatio),
                0f,
                1f);
            return 1f - openNorm;
        }

        return Math.Clamp(neutralBlink, 0f, 1f);
    }
}
