using System.Numerics;
using System.Text.Json;
using System.Threading;
using Steaming.Application.Models;
using Steaming.Application.Services;

namespace Steaming.Application.ViewModels;

// All state for the VTuber avatar feature.
// Zero WinUI/WPF imports — pure MVVM.
public sealed class AvatarViewModel : ViewModelBase, IDisposable
{
    private readonly MicCaptureService   _mic;
    private readonly NdiSendService      _ndi;
    private readonly AvatarRenderService _renderer;
    private readonly CameraCaptureService _cameraCapture;
    private readonly FaceTrackingService _faceTracking;
    private readonly FaceRetargetService _retarget;
    private readonly FaceTrackingPersistenceService _facePersistence;
    private readonly FaceTrackingDiagnosticsService _faceDiagnostics;

    // ── Bindable state ─────────────────────────────────────────────────────────

    private string  _modelPath  = "";
    private bool    _isRunning;
    private bool    _ndiEnabled;
    private bool    _ndiAvailable;
    private string  _statusText = "No model loaded.";
    private string  _selectedMicId = "";
    private List<(string Id, string Name)> _micDevices = [];
    private List<(string Id, string Name)> _cameraDevices = [];
    // While true, settings setters do NOT persist. Prevents the UI populating combos during
    // page load (which fires SelectionChanged → setter → Save) from overwriting the saved file
    // with default values before the real settings are read back.
    private bool _suppressPersist;
    private string _selectedCameraId = "";
    private bool _faceTrackingEnabled = true;
    private bool _cameraPreviewVisible;
    private bool _audioFallbackEnabled = true;
    private bool _voiceOnlyMouthEnabled;
    private float _voiceVolumeSensitivity = 1f;
    private string _trackingProviderName = "Not started";
    private string _trackingModel = "OpenSeeFace";
    private bool _isTrackingFace;
    private float _trackingConfidence;
    private float _detectorMs;
    private float _landmarksMs;
    private float _retargetMs;
    private float _cameraFps;
    private float _trackerFps;
    private int _droppedFrames;
    private FaceTrackingMouthMode _mouthMode = FaceTrackingMouthMode.CameraAndVoice;
    private FaceTrackingCalibrationProfile _calibration = new();
    private string _trackingStatusText = "Tracking idle.";
    private string _calibrationStatusText = "Neutral calibration not captured.";
    private string _rawTrackingDebugText = "No tracking sample.";
    private string _retargetDebugText = "No avatar retarget output.";
    private int _trackingFpsCap = 15;
    private DateTimeOffset _lastTrackingUiUpdate = DateTimeOffset.MinValue;

    public string ModelPath
    {
        get => _modelPath;
        set => Set(ref _modelPath, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            Set(ref _isRunning, value);
            StartCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
        }
    }

    public bool NdiEnabled
    {
        get => _ndiEnabled;
        set { Set(ref _ndiEnabled, value); ApplyNdiState(); }
    }

    public bool NdiAvailable
    {
        get => _ndiAvailable;
        private set => Set(ref _ndiAvailable, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string SelectedMicId
    {
        get => _selectedMicId;
        set { Set(ref _selectedMicId, value); if (_isRunning) _mic.Start(value); SaveSettings(); }
    }

    public List<(string Id, string Name)> MicDevices
    {
        get => _micDevices;
        private set => Set(ref _micDevices, value);
    }

    public List<(string Id, string Name)> CameraDevices
    {
        get => _cameraDevices;
        private set => Set(ref _cameraDevices, value);
    }

    public string SelectedCameraId
    {
        get => _selectedCameraId;
        set
        {
            Set(ref _selectedCameraId, value);
            SaveFaceTrackingSettings();
        }
    }

    public bool FaceTrackingEnabled
    {
        get => _faceTrackingEnabled;
        set
        {
            Set(ref _faceTrackingEnabled, value);
            SaveFaceTrackingSettings();
            if (_isRunning)
            {
                if (value) _ = StartFaceTrackingAsync();
                else _ = StopFaceTrackingAsync();
            }
        }
    }

    public bool CameraPreviewVisible
    {
        get => _cameraPreviewVisible;
        set
        {
            Set(ref _cameraPreviewVisible, value);
            SaveFaceTrackingSettings();
        }
    }

    public bool AudioFallbackEnabled
    {
        get => _audioFallbackEnabled;
        set
        {
            Set(ref _audioFallbackEnabled, value);
            SaveFaceTrackingSettings();
        }
    }

    public bool VoiceOnlyMouthEnabled
    {
        get => _voiceOnlyMouthEnabled;
        set
        {
            Set(ref _voiceOnlyMouthEnabled, value);
            MouthMode = value ? FaceTrackingMouthMode.VoiceOnly : FaceTrackingMouthMode.CameraAndVoice;
        }
    }

    public FaceTrackingMouthMode MouthMode
    {
        get => _mouthMode;
        set
        {
            Set(ref _mouthMode, value);
            _voiceOnlyMouthEnabled = value == FaceTrackingMouthMode.VoiceOnly;
            Notify(nameof(VoiceOnlyMouthEnabled));
            SaveFaceTrackingSettings();
        }
    }

    public Array MouthModeOptions => Enum.GetValues(typeof(FaceTrackingMouthMode));

    public float VoiceVolumeSensitivity
    {
        get => _voiceVolumeSensitivity;
        set
        {
            Set(ref _voiceVolumeSensitivity, value);
            SaveFaceTrackingSettings();
        }
    }

    public float EyeOpenOffset
    {
        get => _calibration.EyeOpenOffset;
        set
        {
            if (Math.Abs(_calibration.EyeOpenOffset - value) < 0.0001f)
                return;

            _calibration.EyeOpenOffset = value;
            Notify();
            SaveFaceTrackingCalibration();
            CalibrationStatusText = $"Eye open trim set to {value:0.00}.";
        }
    }

    public float JawOpenScale
    {
        get => _calibration.JawOpenScale;
        set
        {
            float clamped = Math.Clamp(value, 0f, 2f);
            if (Math.Abs(_calibration.JawOpenScale - clamped) < 0.0001f)
                return;

            _calibration.JawOpenScale = clamped;
            Notify();
            SaveFaceTrackingCalibration();
            CalibrationStatusText = $"Jaw strength set to {clamped:0.00}.";
        }
    }

    public float HeadRotationScale
    {
        get => _calibration.HeadRotationScale;
        set
        {
            float clamped = Math.Clamp(value, 0.15f, 1.5f);
            if (Math.Abs(_calibration.HeadRotationScale - clamped) < 0.0001f)
                return;

            _calibration.HeadRotationScale = clamped;
            Notify();
            SaveFaceTrackingCalibration();
            CalibrationStatusText = $"Head rotation scale set to {clamped:0.00}.";
        }
    }

    public float AaScale
    {
        get => _calibration.AaScale;
        set
        {
            float clamped = Math.Clamp(value, 0.1f, 3.0f);
            if (Math.Abs(_calibration.AaScale - clamped) < 0.001f) return;
            _calibration.AaScale = clamped;
            Notify();
            SaveFaceTrackingCalibration();
        }
    }

    public float IhScale
    {
        get => _calibration.IhScale;
        set
        {
            float clamped = Math.Clamp(value, 0.1f, 3.0f);
            if (Math.Abs(_calibration.IhScale - clamped) < 0.001f) return;
            _calibration.IhScale = clamped;
            Notify();
            SaveFaceTrackingCalibration();
        }
    }

    public float OuScale
    {
        get => _calibration.OuScale;
        set
        {
            float clamped = Math.Clamp(value, 0.1f, 3.0f);
            if (Math.Abs(_calibration.OuScale - clamped) < 0.001f) return;
            _calibration.OuScale = clamped;
            Notify();
            SaveFaceTrackingCalibration();
        }
    }

    public float EeScale
    {
        get => _calibration.EeScale;
        set
        {
            float clamped = Math.Clamp(value, 0.1f, 3.0f);
            if (Math.Abs(_calibration.EeScale - clamped) < 0.001f) return;
            _calibration.EeScale = clamped;
            Notify();
            SaveFaceTrackingCalibration();
        }
    }

    public float OhScale
    {
        get => _calibration.OhScale;
        set
        {
            float clamped = Math.Clamp(value, 0.1f, 3.0f);
            if (Math.Abs(_calibration.OhScale - clamped) < 0.001f) return;
            _calibration.OhScale = clamped;
            Notify();
            SaveFaceTrackingCalibration();
        }
    }

    public string TrackingProviderName
    {
        get => _trackingProviderName;
        private set => Set(ref _trackingProviderName, value);
    }

    public string TrackingModel
    {
        get => _trackingModel;
        set
        {
            if (_trackingModel == value) return;
            _trackingModel = value;
            Notify();
            _faceTracking.SetTrackingModel(value);
            // Reset calibration ranges to defaults when switching providers.
            // Saved ranges from one provider are wrong for another (different output magnitudes).
            // Session baselines re-initialize neutral automatically; only ranges need resetting.
            _calibration.JawRange         = 0.18f;
            _calibration.MouthRoundRange  = 0.18f;
            _calibration.MouthWidthRange  = 0.22f;
            _calibration.BrowRange        = 0.18f;
            _retarget.UpdateCalibration(_calibration);
            SaveFaceTrackingSettings();
        }
    }

    public List<string> TrackingModelOptions { get; } = ["OpenSeeFace", "MediaPipe"];

    public bool IsTrackingFace
    {
        get => _isTrackingFace;
        private set => Set(ref _isTrackingFace, value);
    }

    public float TrackingConfidence
    {
        get => _trackingConfidence;
        private set => Set(ref _trackingConfidence, value);
    }

    public float DetectorMs
    {
        get => _detectorMs;
        private set => Set(ref _detectorMs, value);
    }

    public float LandmarksMs
    {
        get => _landmarksMs;
        private set => Set(ref _landmarksMs, value);
    }

    public float RetargetMs
    {
        get => _retargetMs;
        private set => Set(ref _retargetMs, value);
    }

    public float CameraFps
    {
        get => _cameraFps;
        private set => Set(ref _cameraFps, value);
    }

    public float TrackerFps
    {
        get => _trackerFps;
        private set => Set(ref _trackerFps, value);
    }

    public int DroppedFrames
    {
        get => _droppedFrames;
        private set => Set(ref _droppedFrames, value);
    }

    public string TrackingStatusText
    {
        get => _trackingStatusText;
        private set => Set(ref _trackingStatusText, value);
    }

    public string CalibrationStatusText
    {
        get => _calibrationStatusText;
        private set => Set(ref _calibrationStatusText, value);
    }

    public string RawTrackingDebugText
    {
        get => _rawTrackingDebugText;
        private set => Set(ref _rawTrackingDebugText, value);
    }

    public string RetargetDebugText
    {
        get => _retargetDebugText;
        private set => Set(ref _retargetDebugText, value);
    }

    // ── Expression overrides (0..1) ───────────────────────────────────────────

    private float _exprAa, _exprIh, _exprOu, _exprEe, _exprOh;

    public float ExpressionAa
    {
        get => _exprAa;
        set { _exprAa = value; Volatile.Write(ref _renderer.ExprAa, value); Notify(); }
    }
    public float ExpressionIh
    {
        get => _exprIh;
        set { _exprIh = value; Volatile.Write(ref _renderer.ExprIh, value); Notify(); }
    }
    public float ExpressionOu
    {
        get => _exprOu;
        set { _exprOu = value; Volatile.Write(ref _renderer.ExprOu, value); Notify(); }
    }
    public float ExpressionEe
    {
        get => _exprEe;
        set { _exprEe = value; Volatile.Write(ref _renderer.ExprEe, value); Notify(); }
    }
    public float ExpressionOh
    {
        get => _exprOh;
        set { _exprOh = value; Volatile.Write(ref _renderer.ExprOh, value); Notify(); }
    }

    // ── Bone control ──────────────────────────────────────────────────────────

    private List<string> _boneNames = [];
    public List<string> BoneNames
    {
        get => _boneNames;
        private set => Set(ref _boneNames, value);
    }

    private string _selectedBone = "";
    public string SelectedBone
    {
        get => _selectedBone;
        set
        {
            Set(ref _selectedBone, value);
            // Fire rotation property changes so UI sliders sync
            Notify(nameof(BoneRotX));
            Notify(nameof(BoneRotY));
            Notify(nameof(BoneRotZ));
        }
    }

    private bool _boneControlMode;
    public bool BoneControlMode
    {
        get => _boneControlMode;
        set => Set(ref _boneControlMode, value);
    }
    private HashSet<string> _ikEffectorNames = [];
    private string? _activeIKTarget;
    private Vector3 _ikTargetPos;
    private List<PoseSave> _savedPoses = [];
    public IReadOnlyList<string> SavedPoseNames => _savedPoses.Select(p => p.Name).ToList();

    // ── Diagnostics ───────────────────────────────────────────────────────────

    private int _diagExprCount;
    public int DiagExprCount
    {
        get => _diagExprCount;
        private set => Set(ref _diagExprCount, value);
    }

    private record PoseSave(
        string Name,
        Dictionary<string, float[]> Rotations,
        Dictionary<string, float[]> IKTargets);

    // Current selected bone rotation (degrees)
    public float BoneRotX
    {
        get => string.IsNullOrEmpty(_selectedBone) ? 0f : _renderer.GetBoneRotation(_selectedBone).X;
        set
        {
            if (string.IsNullOrEmpty(_selectedBone)) return;
            var r = _renderer.GetBoneRotation(_selectedBone);
            r.X = value;
            _renderer.SetBoneRotation(_selectedBone, r);
            Notify();
        }
    }
    public float BoneRotY
    {
        get => string.IsNullOrEmpty(_selectedBone) ? 0f : _renderer.GetBoneRotation(_selectedBone).Y;
        set
        {
            if (string.IsNullOrEmpty(_selectedBone)) return;
            var r = _renderer.GetBoneRotation(_selectedBone);
            r.Y = value;
            _renderer.SetBoneRotation(_selectedBone, r);
            Notify();
        }
    }
    public float BoneRotZ
    {
        get => string.IsNullOrEmpty(_selectedBone) ? 0f : _renderer.GetBoneRotation(_selectedBone).Z;
        set
        {
            if (string.IsNullOrEmpty(_selectedBone)) return;
            var r = _renderer.GetBoneRotation(_selectedBone);
            r.Z = value;
            _renderer.SetBoneRotation(_selectedBone, r);
            Notify();
        }
    }

    public void ResetAllBones()
    {
        _renderer.ClearAllBoneRotations();
        _activeIKTarget = null;
        Notify(nameof(BoneRotX));
        Notify(nameof(BoneRotY));
        Notify(nameof(BoneRotZ));
        SaveSettings();
    }

    public bool IsIKEffector(string boneName) => _ikEffectorNames.Contains(boneName);

    public void BeginIKDrag(string effectorName)
    {
        _activeIKTarget = effectorName;
        _ikTargetPos = _renderer.GetBoneWorldPos(effectorName);
        if (_ikTargetPos == Vector3.Zero)
            _ikTargetPos = new Vector3(0f, 1f, 0f);
        _renderer.SetIKTarget(effectorName, _ikTargetPos);
    }

    public void EndIKDrag()
    {
        _activeIKTarget = null;
        SaveSettings();
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand  { get; }

    // ── Construction ──────────────────────────────────────────────────────────

    public AvatarViewModel(
        MicCaptureService mic,
        NdiSendService ndi,
        AvatarRenderService renderer,
        CameraCaptureService cameraCapture,
        FaceTrackingService faceTracking,
        FaceRetargetService retarget,
        FaceTrackingPersistenceService facePersistence,
        FaceTrackingDiagnosticsService faceDiagnostics)
    {
        _mic      = mic;
        _ndi      = ndi;
        _renderer = renderer;
        _cameraCapture = cameraCapture;
        _faceTracking = faceTracking;
        _retarget = retarget;
        _facePersistence = facePersistence;
        _faceDiagnostics = faceDiagnostics;

        StartCommand = new RelayCommand(StartRender, () => !_isRunning && !string.IsNullOrWhiteSpace(_modelPath));
        StopCommand  = new RelayCommand(StopRender,  () => _isRunning);

        MicDevices = MicCaptureService.EnumerateInputDevices();
        _faceTracking.FrameReady += OnFaceTrackingFrame;
    }

    // ── Initialise (call once from UI after navigation) ───────────────────────
    // Loads persisted settings (last model path + mic ID) asynchronously.

    public async Task InitAsync()
    {
        // Guard the entire load so no setter (or UI combo populating before load) persists
        // default values over the saved file.
        _suppressPersist = true;
        try
        {
            await LoadSettingsAsync();
            // Load saved face-tracking settings (selected camera, tracking model) BEFORE
            // enumerating cameras, so the restored camera id is already set and the enumerate
            // step does not overwrite it with the first device.
            LoadFaceTrackingSettings();
            await RefreshCameraDevicesAsync();
        }
        finally
        {
            _suppressPersist = false;
        }
    }

    // Page calls this before it starts populating combos in OnNavigatedTo, so the combo
    // SelectionChanged → setter → Save chain cannot clobber the saved file before InitAsync runs.
    public void BeginSettingsLoad() => _suppressPersist = true;

    // ── Model loading ─────────────────────────────────────────────────────────

    public async Task LoadModelAsync(string path)
    {
        // If renderer is running, stop it first to prevent crash during resource swap
        bool wasRunning = _isRunning;
        if (wasRunning) StopRender();

        StatusText = "Loading model…";
        try
        {
            var model = await Task.Run(() => VrmLoaderService.Load(path));
            _renderer.LoadModel(model);
            ModelPath  = path;
            SaveSettings();

            // Populate bone names for skeleton control
            var bones = _renderer.GetBoneNames().ToList();
            BoneNames = bones;
            _ikEffectorNames = _renderer.GetIKEffectorNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (bones.Count > 0)
            {
                // Default selected bone: "Head" if present, otherwise first bone
                SelectedBone = bones.Contains("Head") ? "Head"
                             : bones.Contains("head") ? "head"
                             : bones[0];
            }

            int exprCount = model.Expressions.Count;
            DiagExprCount = exprCount;
            var mappedKeys = _renderer.GetMappedExpressionKeys();
            string mappedInfo = mappedKeys.Length > 0
                ? $" | Mapped: {string.Join(" ", mappedKeys)}"
                : " ⚠ no mouth/eye expressions mapped";
            StatusText = $"Loaded: {Path.GetFileName(path)} " +
                         $"| {model.Meshes.Count} meshes | {exprCount} expressions | {model.Skins.Count} skins" +
                         mappedInfo;

            StartCommand.RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load model: {ex.Message}";
        }

        if (wasRunning) StartRender();
    }

    // ── Render lifecycle ──────────────────────────────────────────────────────

    private void StartRender()
    {
        if (_isRunning) return;

        if (_ndiEnabled && !_ndi.IsAvailable)
        {
            bool ok = _ndi.TryInitialize();
            NdiAvailable = ok;
            if (!ok) StatusText = "NDI not available — preview only.";
        }

        _mic.Start(_selectedMicId);
        _renderer.Start();
        if (_faceTrackingEnabled)
            _ = StartFaceTrackingAsync();
        IsRunning  = true;
        StatusText = _ndi.IsAvailable && _ndiEnabled
            ? "Running — NDI sending as \"Streaming Avatar\""
            : "Running — preview only (NDI not active)";
    }

    private void StopRender()
    {
        if (!_isRunning) return;
        _ = StopFaceTrackingAsync();
        _renderer.Stop();
        _mic.Stop();
        IsRunning  = false;
        StatusText = "Stopped.";
    }

    private void ApplyNdiState()
    {
        if (!_isRunning) return;
        if (_ndiEnabled && !_ndi.IsAvailable)
        {
            bool ok = _ndi.TryInitialize();
            NdiAvailable = ok;
        }
        StatusText = _ndi.IsAvailable && _ndiEnabled
            ? "Running — NDI sending as \"Streaming Avatar\""
            : "Running — preview only";
    }

    // ── Camera + bone — mouse drag ────────────────────────────────────────────

    // Sensitivity: ~270px drag = 180 degrees for orbit / bone rotation
    private const float OrbitSensitivity  = MathF.PI / 270f;
    private const float BoneRotSensitivity = 180f / 270f;  // degrees per pixel
    private const float PanSensitivity    = 0.004f;
    private const float ZoomSensitivity   = 0.002f;

    public void OrbitCamera(float dx, float dy)
    {
        if (_boneControlMode && !string.IsNullOrEmpty(_selectedBone))
        {
            if (_activeIKTarget != null)
            {
                _ikTargetPos += ScreenDeltaToWorld(_activeIKTarget, dx, dy);
                _renderer.SetIKTarget(_activeIKTarget, _ikTargetPos);
            }
            else
            {
                var rot = _renderer.GetBoneRotation(_selectedBone);
                rot.Y = Math.Clamp(rot.Y + dx * BoneRotSensitivity, -180f, 180f);
                rot.X = Math.Clamp(rot.X + dy * BoneRotSensitivity, -90f, 90f);
                _renderer.SetBoneRotation(_selectedBone, rot);
                Notify(nameof(BoneRotX));
                Notify(nameof(BoneRotY));
            }
        }
        else
        {
            float yaw   = Volatile.Read(ref _renderer.CameraYaw)   + dx * OrbitSensitivity;
            float pitch = Math.Clamp(
                Volatile.Read(ref _renderer.CameraPitch) - dy * OrbitSensitivity,
                -1.4f, 1.4f);
            Volatile.Write(ref _renderer.CameraYaw,   yaw);
            Volatile.Write(ref _renderer.CameraPitch, pitch);
        }
    }

    private Vector3 ScreenDeltaToWorld(string effectorName, float dx, float dy)
    {
        _ = effectorName;
        float yaw   = Volatile.Read(ref _renderer.CameraYaw);
        float pitch = Volatile.Read(ref _renderer.CameraPitch);
        float dist  = Volatile.Read(ref _renderer.CameraDistance);
        float panX  = Volatile.Read(ref _renderer.CameraPanX);
        float panY  = Volatile.Read(ref _renderer.CameraPanY);

        var eye = new Vector3(
            panX + dist * MathF.Sin(yaw) * MathF.Cos(pitch),
            panY + dist * MathF.Sin(pitch),
           -dist * MathF.Cos(yaw) * MathF.Cos(pitch));
        var view = Matrix4x4.CreateLookAtLeftHanded(eye, new Vector3(panX, panY, 0f), Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(
            MathF.PI / 4f, (float)AvatarRenderService.RenderWidth / AvatarRenderService.RenderHeight, 0.01f, 100f);
        var vp = Matrix4x4.CreateScale(-1f, 1f, 1f) * view * proj;
        if (!Matrix4x4.Invert(vp, out var invVp))
            return Vector3.Zero;

        var refClip = Vector4.Transform(new Vector4(_ikTargetPos, 1f), vp);
        if (MathF.Abs(refClip.W) < 1e-5f)
            return Vector3.Zero;

        float ndcZ  = refClip.Z / refClip.W;
        float ndcDX = dx / (AvatarRenderService.RenderWidth * 0.5f);
        float ndcDY = -dy / (AvatarRenderService.RenderHeight * 0.5f);

        var a = Vector4.Transform(new Vector4(0f, 0f, ndcZ, 1f), invVp);
        var b = Vector4.Transform(new Vector4(ndcDX, ndcDY, ndcZ, 1f), invVp);
        if (MathF.Abs(a.W) < 1e-5f || MathF.Abs(b.W) < 1e-5f)
            return Vector3.Zero;

        a /= a.W;
        b /= b.W;
        return new Vector3(b.X - a.X, b.Y - a.Y, b.Z - a.Z);
    }

    public void PanCamera(float dx, float dy)
    {
        float px = Volatile.Read(ref _renderer.CameraPanX) - dx * PanSensitivity;
        float py = Volatile.Read(ref _renderer.CameraPanY) + dy * PanSensitivity;
        Volatile.Write(ref _renderer.CameraPanX, px);
        Volatile.Write(ref _renderer.CameraPanY, py);
    }

    public void ZoomCamera(float wheelDelta)
    {
        float d = Math.Clamp(
            Volatile.Read(ref _renderer.CameraDistance) - wheelDelta * ZoomSensitivity,
            0.2f, 8f);
        Volatile.Write(ref _renderer.CameraDistance, d);
    }

    public void ResetCamera()
    {
        Volatile.Write(ref _renderer.CameraYaw,      0f);
        Volatile.Write(ref _renderer.CameraPitch,    0.15f);
        Volatile.Write(ref _renderer.CameraDistance, 1.5f);
        Volatile.Write(ref _renderer.CameraPanX,     0f);
        Volatile.Write(ref _renderer.CameraPanY,     1.2f);
        SaveSettings();
    }

    // ── Mic device refresh ────────────────────────────────────────────────────

    public void RefreshMicDevices()
        => MicDevices = MicCaptureService.EnumerateInputDevices();

    public async Task RefreshCameraDevicesAsync()
    {
        CameraDevices = await _cameraCapture.EnumerateCamerasAsync();
        // Keep the restored/selected camera if it is still present; only fall back to the first
        // device when the saved id is empty or that camera is no longer attached.
        bool savedPresent = _cameraDevices.Any(d => d.Id == _selectedCameraId);
        if (!savedPresent && _cameraDevices.Count > 0)
            _selectedCameraId = _cameraDevices[0].Id;
        Notify(nameof(SelectedCameraId));
    }

    public async Task StartFaceTrackingAsync()
    {
        if (string.IsNullOrWhiteSpace(_selectedCameraId))
        {
            StatusText = "No camera selected for face tracking.";
            TrackingStatusText = "Tracking blocked: no camera selected.";
            return;
        }

        _faceTracking.SetFpsCap(_trackingFpsCap);
        bool cameraOk = await _cameraCapture.StartCameraAsync(_selectedCameraId, _faceDiagnostics);
        if (!cameraOk)
        {
            StatusText = "Face tracking camera failed: " + _cameraCapture.LastError;
            TrackingStatusText = "Tracking failed: " + _cameraCapture.LastError;
            return;
        }

        _faceTracking.Start();
        StatusText = "Face tracking active.";
        TrackingStatusText = $"Camera started. Provider {_trackingProviderName}. Waiting for face lock.";
    }

    public async Task StopFaceTrackingAsync()
    {
        _faceTracking.Stop();
        await _cameraCapture.StopCameraAsync();
        _renderer.SetFaceTrackingState(FaceTrackingState.Neutral);
        IsTrackingFace = false;
        TrackingConfidence = 0f;
        DetectorMs = 0f;
        LandmarksMs = 0f;
        RetargetMs = 0f;
        CameraFps = 0f;
        TrackerFps = 0f;
        TrackingStatusText = "Tracking stopped.";
    }

    public void CaptureFaceNeutral()
    {
        var raw = _faceTracking.LatestFrame;
        if (raw.Timestamp == default)
        {
            CalibrationStatusText = "Neutral capture failed: no tracking sample received yet.";
            StatusText = CalibrationStatusText;
            return;
        }

        if (!raw.IsTracking && raw.LandmarkConfidence < 0.40f)
        {
            CalibrationStatusText =
                $"Neutral capture failed: face not locked (landmarks {raw.LandmarkConfidence:0.00}, overall {raw.TrackingConfidence:0.00}).";
            StatusText = CalibrationStatusText;
            return;
        }

        _calibration.NeutralHeadYaw = raw.HeadYaw;
        _calibration.NeutralHeadPitch = raw.HeadPitch;
        _calibration.NeutralHeadRoll = raw.HeadRoll;
        if (raw.EyeDistance > 0.001f)
            _calibration.NeutralEyeDistance = raw.EyeDistance;
        _calibration.NeutralEyeHorizontal = raw.EyeLookHorizontal;
        _calibration.NeutralEyeVertical = raw.EyeLookVertical;
        _calibration.NeutralEyeOpenLeftRatio = raw.EyeOpenLeftRatio;
        _calibration.NeutralEyeOpenRightRatio = raw.EyeOpenRightRatio;
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
        _retarget.UpdateCalibration(_calibration);
        SaveFaceTrackingCalibration();
        CalibrationStatusText =
            $"Neutral captured. Head offsets Y/P/R = {raw.HeadYaw:0.000} / {raw.HeadPitch:0.000} / {raw.HeadRoll:0.000}. Mouth baseline reset.";
        StatusText = CalibrationStatusText;
    }

    public void CaptureBlinkClosedCalibration()
    {
        var raw = _faceTracking.LatestFrame;
        if (!CanCaptureCalibration(raw, out var error))
        {
            CalibrationStatusText = error;
            StatusText = error;
            return;
        }

        _calibration.BlinkClosedThresholdLeft = Math.Max(0.05f, raw.EyeBlinkLeft);
        _calibration.BlinkClosedThresholdRight = Math.Max(0.05f, raw.EyeBlinkRight);
        _calibration.BlinkClosedLeftEyeOpenRatio = Math.Max(0.01f, raw.EyeOpenLeftRatio);
        _calibration.BlinkClosedRightEyeOpenRatio = Math.Max(0.01f, raw.EyeOpenRightRatio);
        _retarget.UpdateCalibration(_calibration);
        SaveFaceTrackingCalibration();
        CalibrationStatusText =
            $"Blink closed captured. L/R open ratios = {_calibration.BlinkClosedLeftEyeOpenRatio:0.000} / {_calibration.BlinkClosedRightEyeOpenRatio:0.000}.";
        StatusText = CalibrationStatusText;
    }

    public void CaptureJawOpenCalibration()
    {
        var raw = _faceTracking.LatestFrame;
        if (!CanCaptureCalibration(raw, out var error))
        {
            CalibrationStatusText = error;
            StatusText = error;
            return;
        }

        _calibration.JawRange = Math.Max(0.06f, raw.JawOpen - _calibration.NeutralJawOpen);
        _retarget.UpdateCalibration(_calibration);
        SaveFaceTrackingCalibration();
        CalibrationStatusText = $"Jaw open captured. Jaw range = {_calibration.JawRange:0.000}.";
        StatusText = CalibrationStatusText;
    }

    public void CaptureSmileCalibration()
    {
        var raw = _faceTracking.LatestFrame;
        if (!CanCaptureCalibration(raw, out var error))
        {
            CalibrationStatusText = error;
            StatusText = error;
            return;
        }

        _calibration.MouthWidthRange = Math.Max(0.08f, raw.MouthWidth - _calibration.NeutralMouthWidth);
        _retarget.UpdateCalibration(_calibration);
        SaveFaceTrackingCalibration();
        CalibrationStatusText = $"Smile captured. Mouth width range = {_calibration.MouthWidthRange:0.000}.";
        StatusText = CalibrationStatusText;
    }

    public void CaptureOhCalibration()
    {
        var raw = _faceTracking.LatestFrame;
        if (!CanCaptureCalibration(raw, out var error))
        {
            CalibrationStatusText = error;
            StatusText = error;
            return;
        }

        _calibration.MouthRoundRange = Math.Max(0.04f, raw.MouthRound - _calibration.NeutralMouthRound);
        _retarget.UpdateCalibration(_calibration);
        SaveFaceTrackingCalibration();
        CalibrationStatusText = $"OH captured. Mouth round range = {_calibration.MouthRoundRange:0.000}.";
        StatusText = CalibrationStatusText;
    }

    public bool TryCopyCameraPreviewFrame(byte[] destination, out int width, out int height)
    {
        return _cameraCapture.TryCopyLatestFrame(destination, out width, out height);
    }

    public bool TryGetTrackingOverlay(
        out float[] landmarks,
        out Vector4 faceBox,
        out int frameWidth,
        out int frameHeight,
        out bool isTracking,
        out float confidence)
    {
        var raw = _faceTracking.LatestFrame;
        if (!_cameraCapture.TryGetLatestFrame(out var frame)
            || raw.Timestamp == default
            || raw.Landmarks.Length == 0)
        {
            landmarks = [];
            faceBox = Vector4.Zero;
            frameWidth = 0;
            frameHeight = 0;
            isTracking = false;
            confidence = 0f;
            return false;
        }

        landmarks = raw.Landmarks;
        faceBox = raw.FaceBox;
        frameWidth = frame.Width;
        frameHeight = frame.Height;
        isTracking = raw.IsTracking;
        confidence = raw.TrackingConfidence;
        return true;
    }

    private void OnFaceTrackingFrame(RawFaceTrackingFrame raw)
    {
        var state = _retarget.Retarget(raw);
        _renderer.SetFaceTrackingState(state);
        if ((raw.Timestamp - _lastTrackingUiUpdate) < TimeSpan.FromMilliseconds(200))
            return;

        _lastTrackingUiUpdate = raw.Timestamp;
        TrackingConfidence = state.TrackingConfidence;
        IsTrackingFace = state.IsTracking;
        RetargetMs = state.RetargetMs;
        DetectorMs = raw.DetectorMs;
        LandmarksMs = raw.LandmarksMs;
        var snapshot = _faceDiagnostics.Snapshot();
        TrackingProviderName = snapshot.ProviderName;
        CameraFps = snapshot.CameraFps;
        TrackerFps = snapshot.TrackerFps;
        DroppedFrames = snapshot.DroppedFrames;
        RawTrackingDebugText =
            $"Face: {(raw.IsTracking ? "LOCKED" : "SEARCHING")}  Conf {raw.TrackingConfidence:0.00}\n" +
            $"EyeOpen L/R: {raw.EyeOpenLeftRatio:0.000} / {raw.EyeOpenRightRatio:0.000}\n" +
            $"Blink L/R: {raw.EyeBlinkLeft:0.000} / {raw.EyeBlinkRight:0.000}\n" +
            $"Jaw/Mouth: {raw.JawOpen:0.000} / {raw.MouthOpen:0.000}\n" +
            $"Width/Round: {raw.MouthWidth:0.000} / {raw.MouthRound:0.000}\n" +
            $"Smile L/R: {raw.SmileLeft:0.000} / {raw.SmileRight:0.000}\n" +
            $"Head Y/P/R: {raw.HeadYaw:0.000} / {raw.HeadPitch:0.000} / {raw.HeadRoll:0.000}";
        RetargetDebugText =
            $"Blink L/R: {state.EyeBlinkLeft:0.000} / {state.EyeBlinkRight:0.000}\n" +
            $"Jaw: {state.JawOpen:0.000}\n" +
            $"AA IH OU EE OH: {state.MouthAa:0.000}  {state.MouthIh:0.000}  {state.MouthOu:0.000}  {state.MouthEe:0.000}  {state.MouthOh:0.000}\n" +
            $"Smile L/R: {state.SmileLeft:0.000} / {state.SmileRight:0.000}\n" +
            $"Look L/R/U/D: {state.EyeLookLeft:0.000} / {state.EyeLookRight:0.000} / {state.EyeLookUp:0.000} / {state.EyeLookDown:0.000}\n" +
            $"Head Y/P/R: {state.HeadYaw:0.000} / {state.HeadPitch:0.000} / {state.HeadRoll:0.000}";
        TrackingStatusText = snapshot.FaceLocked
            ? $"Tracking locked. Confidence {snapshot.CurrentConfidence:0.00}. Camera {snapshot.CameraFps:0} fps, tracker {snapshot.TrackerFps:0} fps."
            : $"Tracking running. Searching for face. Confidence {snapshot.CurrentConfidence:0.00}.";
    }

    private static bool CanCaptureCalibration(RawFaceTrackingFrame raw, out string error)
    {
        if (raw.Timestamp == default)
        {
            error = "Calibration failed: no tracking sample received yet.";
            return false;
        }

        // Calibration should follow actual landmark lock quality, not the blended
        // detector+landmark score used for display/fallback decisions.
        if (!raw.IsTracking && raw.LandmarkConfidence < 0.40f)
        {
            error = $"Calibration failed: face not locked (landmarks {raw.LandmarkConfidence:0.00}, overall {raw.TrackingConfidence:0.00}).";
            return false;
        }

        error = "";
        return true;
    }

    // ── Preview ───────────────────────────────────────────────────────────────

    public bool TryGetPreviewFrame(byte[] buffer)
        => _renderer.TryGetPreviewFrame(buffer);

    public Vector3 GetPoseWorldPos(string boneName)
    {
        if (string.IsNullOrWhiteSpace(boneName)) return Vector3.Zero;
        if (IsIKEffector(boneName))
        {
            var target = _renderer.GetIKTarget(boneName);
            if (target != Vector3.Zero) return target;
        }
        return _renderer.GetBoneWorldPos(boneName);
    }

    public bool TryProjectWorldToNormalized(Vector3 worldPos, out Vector2 normalized)
    {
        normalized = Vector2.Zero;

        float yaw   = Volatile.Read(ref _renderer.CameraYaw);
        float pitch = Volatile.Read(ref _renderer.CameraPitch);
        float dist  = Volatile.Read(ref _renderer.CameraDistance);
        float panX  = Volatile.Read(ref _renderer.CameraPanX);
        float panY  = Volatile.Read(ref _renderer.CameraPanY);

        var eye = new Vector3(
            panX + dist * MathF.Sin(yaw) * MathF.Cos(pitch),
            panY + dist * MathF.Sin(pitch),
           -dist * MathF.Cos(yaw) * MathF.Cos(pitch));
        var view = Matrix4x4.CreateLookAtLeftHanded(eye, new Vector3(panX, panY, 0f), Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(
            MathF.PI / 4f, (float)AvatarRenderService.RenderWidth / AvatarRenderService.RenderHeight, 0.01f, 100f);
        var vp = Matrix4x4.CreateScale(-1f, 1f, 1f) * view * proj;

        var clip = Vector4.Transform(new Vector4(worldPos, 1f), vp);
        if (MathF.Abs(clip.W) < 1e-5f) return false;

        float ndcX = clip.X / clip.W;
        float ndcY = clip.Y / clip.W;
        float ndcZ = clip.Z / clip.W;
        if (ndcZ < 0f || ndcZ > 1f) return false;

        normalized = new Vector2(
            ndcX * 0.5f + 0.5f,
            -ndcY * 0.5f + 0.5f);
        return normalized.X >= 0f && normalized.X <= 1f
            && normalized.Y >= 0f && normalized.Y <= 1f;
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    public void SaveCurrentPose(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        var rotations = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var bone in _boneNames)
        {
            var r = _renderer.GetBoneRotation(bone);
            if (r != Vector3.Zero) rotations[bone] = [r.X, r.Y, r.Z];
        }

        var ikTargets = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var effector in _ikEffectorNames)
        {
            var target = _renderer.GetIKTarget(effector);
            if (target != Vector3.Zero) ikTargets[effector] = [target.X, target.Y, target.Z];
        }

        var pose = new PoseSave(name, rotations, ikTargets);
        int existing = _savedPoses.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) _savedPoses[existing] = pose;
        else _savedPoses.Add(pose);

        SavePosesFile();
        Notify(nameof(SavedPoseNames));
    }

    public void LoadPose(string name)
    {
        var pose = _savedPoses.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (pose == null) return;

        _renderer.ClearAllBoneRotations();
        foreach (var (bone, r) in pose.Rotations)
            _renderer.SetBoneRotation(bone, new Vector3(r[0], r[1], r[2]));
        foreach (var (effector, target) in pose.IKTargets)
            _renderer.SetIKTarget(effector, new Vector3(target[0], target[1], target[2]));

        _activeIKTarget = null;
        Notify(nameof(BoneRotX));
        Notify(nameof(BoneRotY));
        Notify(nameof(BoneRotZ));
        SaveSettings();
    }

    public void DeletePose(string name)
    {
        _savedPoses.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        SavePosesFile();
        Notify(nameof(SavedPoseNames));
    }

    private static readonly string SettingsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "Steaming", "avatar.json");
    private static readonly string PosesPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "Steaming", "poses.json");

    private record AvatarSettings(
        string ModelPath,
        string MicId,
        float CameraYaw,
        float CameraPitch,
        float CameraDistance,
        float CameraPanX,
        float CameraPanY,
        Dictionary<string, float[]>? Rotations,
        Dictionary<string, float[]>? IKTargets);

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var json = JsonSerializer.Serialize(new AvatarSettings(
                ModelPath,
                SelectedMicId,
                Volatile.Read(ref _renderer.CameraYaw),
                Volatile.Read(ref _renderer.CameraPitch),
                Volatile.Read(ref _renderer.CameraDistance),
                Volatile.Read(ref _renderer.CameraPanX),
                Volatile.Read(ref _renderer.CameraPanY),
                CaptureBoneRotations(),
                CaptureIKTargets()));
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            LoadPosesFile();
            if (!File.Exists(SettingsPath)) return;
            var json = await File.ReadAllTextAsync(SettingsPath);
            var s    = JsonSerializer.Deserialize<AvatarSettings>(json);
            if (s == null) return;

            // Restore mic
            if (!string.IsNullOrEmpty(s.MicId) && _micDevices.Any(m => m.Id == s.MicId))
                _selectedMicId = s.MicId;

            Volatile.Write(ref _renderer.CameraYaw, s.CameraYaw);
            Volatile.Write(ref _renderer.CameraPitch, s.CameraPitch);
            Volatile.Write(ref _renderer.CameraDistance, s.CameraDistance > 0f ? s.CameraDistance : 1.5f);
            Volatile.Write(ref _renderer.CameraPanX, s.CameraPanX);
            Volatile.Write(ref _renderer.CameraPanY, s.CameraPanY);

            // Restore model
            if (!string.IsNullOrEmpty(s.ModelPath) && File.Exists(s.ModelPath))
            {
                await LoadModelAsync(s.ModelPath);
                RestorePoseState(s.Rotations, s.IKTargets);
            }
        }
        catch { }
    }

    private void LoadFaceTrackingSettings()
    {
        var settings = _facePersistence.LoadSettings();
        _calibration = _facePersistence.LoadCalibration();
        _selectedCameraId = settings.SelectedCameraId;
        _faceTrackingEnabled = settings.TrackingEnabled;
        _cameraPreviewVisible = settings.CameraPreviewVisible || settings.PreviewVisible;
        _audioFallbackEnabled = settings.AudioFallbackEnabled;
        _voiceVolumeSensitivity = settings.VoiceVolumeSensitivity;
        _mouthMode = settings.MouthMode;
        _voiceOnlyMouthEnabled = settings.MouthMode == FaceTrackingMouthMode.VoiceOnly;
        _trackingModel = settings.TrackingModel;
        _trackingFpsCap = Math.Clamp(settings.FpsCap <= 0 ? 15 : settings.FpsCap, 10, 15);
        _faceTracking.SetTrackingModel(_trackingModel);
        _calibration.JawOpenScale = Math.Clamp(_calibration.JawOpenScale, 0f, 2f);
        _calibration.MouthWidthRange = Math.Max(0.08f, _calibration.MouthWidthRange);
        _calibration.JawRange = Math.Max(0.06f, _calibration.JawRange);
        _retarget.UpdateCalibration(_calibration);
        _retarget.UpdateSettings(settings);
        Notify(nameof(SelectedCameraId));
        Notify(nameof(FaceTrackingEnabled));
        Notify(nameof(CameraPreviewVisible));
        Notify(nameof(AudioFallbackEnabled));
        Notify(nameof(VoiceVolumeSensitivity));
        Notify(nameof(MouthMode));
        Notify(nameof(VoiceOnlyMouthEnabled));
        Notify(nameof(EyeOpenOffset));
        Notify(nameof(JawOpenScale));
        Notify(nameof(HeadRotationScale));
        TrackingStatusText = _faceTrackingEnabled
            ? "Tracking enabled. Start tracking to open the camera."
            : "Tracking disabled.";
        CalibrationStatusText = "Neutral calibration loaded from saved settings.";
    }

    private void SaveFaceTrackingSettings()
    {
        if (_suppressPersist) return;
        var settings = new FaceTrackingSettings
        {
            SelectedCameraId = _selectedCameraId,
            TrackingEnabled = _faceTrackingEnabled,
            AudioFallbackEnabled = _audioFallbackEnabled,
            PreviewVisible = _cameraPreviewVisible,
            CameraPreviewVisible = _cameraPreviewVisible,
            VoiceVolumeSensitivity = _voiceVolumeSensitivity,
            MouthMode = _mouthMode,
            ProviderPreference = _trackingProviderName,
            FpsCap = _trackingFpsCap,
            SelectedModelPackVersion = "OpenSeeFace-1",
            TrackingModel = _trackingModel
        };
        _facePersistence.SaveSettings(settings);
        _retarget.UpdateSettings(settings);
    }

    private void SaveFaceTrackingCalibration()
    {
        if (_suppressPersist) return;
        _facePersistence.SaveCalibration(_calibration);
        _retarget.UpdateCalibration(_calibration);
    }

    public void SaveFaceTrackingProfile()
    {
        SaveFaceTrackingSettings();
        SaveFaceTrackingCalibration();
        CalibrationStatusText = "Face tracking calibration and trims saved.";
        StatusText = CalibrationStatusText;
    }

    public void PersistState()
    {
        SaveSettings();
        SaveFaceTrackingSettings();
        SaveFaceTrackingCalibration();
    }

    private Dictionary<string, float[]> CaptureBoneRotations()
    {
        var rotations = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var bone in _boneNames)
        {
            var r = _renderer.GetBoneRotation(bone);
            if (r != Vector3.Zero) rotations[bone] = [r.X, r.Y, r.Z];
        }
        return rotations;
    }

    private Dictionary<string, float[]> CaptureIKTargets()
    {
        var ikTargets = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var effector in _ikEffectorNames)
        {
            var target = _renderer.GetIKTarget(effector);
            if (target != Vector3.Zero) ikTargets[effector] = [target.X, target.Y, target.Z];
        }
        return ikTargets;
    }

    private void RestorePoseState(
        Dictionary<string, float[]>? rotations,
        Dictionary<string, float[]>? ikTargets)
    {
        if ((rotations == null || rotations.Count == 0)
            && (ikTargets == null || ikTargets.Count == 0))
        {
            return;
        }

        _renderer.ClearAllBoneRotations();

        if (rotations != null)
        {
            foreach (var (bone, r) in rotations)
            {
                if (r.Length >= 3)
                    _renderer.SetBoneRotation(bone, new Vector3(r[0], r[1], r[2]));
            }
        }

        if (ikTargets != null)
        {
            foreach (var (effector, target) in ikTargets)
            {
                if (target.Length >= 3)
                    _renderer.SetIKTarget(effector, new Vector3(target[0], target[1], target[2]));
            }
        }
    }

    private void SavePosesFile()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PosesPath)!);
            File.WriteAllText(PosesPath, JsonSerializer.Serialize(_savedPoses));
        }
        catch { }
    }

    private void LoadPosesFile()
    {
        try
        {
            if (!File.Exists(PosesPath)) return;
            var loaded = JsonSerializer.Deserialize<List<PoseSave>>(File.ReadAllText(PosesPath));
            if (loaded != null)
            {
                _savedPoses = loaded;
                Notify(nameof(SavedPoseNames));
            }
        }
        catch { }
    }

    // ── Disposal ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        PersistState();
        StopRender();
        _ndi.Dispose();
        _mic.Dispose();
        _faceTracking.Dispose();
        _renderer.Dispose();
    }
}
