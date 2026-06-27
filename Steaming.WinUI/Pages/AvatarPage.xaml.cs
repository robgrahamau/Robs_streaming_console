using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Steaming.Application.Services;
using Steaming.Application.ViewModels;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Steaming.WinUI.Pages;

public sealed partial class AvatarPage : Page
{
    private sealed class TrackingOverlayVisuals
    {
        public Microsoft.UI.Xaml.Shapes.Rectangle FaceRect { get; } = new()
        {
            StrokeThickness = 2,
            Visibility = Visibility.Collapsed
        };

        public TextBlock StatusLabel { get; } = new()
        {
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
            FontSize = 11,
            Visibility = Visibility.Collapsed
        };

        public List<Microsoft.UI.Xaml.Shapes.Ellipse> Dots { get; } = [];
    }

    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush LockedStrokeBrush = new(Microsoft.UI.Colors.LimeGreen);
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush LostStrokeBrush = new(Microsoft.UI.Colors.OrangeRed);
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush LockedDotBrush = new(Microsoft.UI.Colors.DeepSkyBlue);
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush LostDotBrush = new(Microsoft.UI.Colors.Orange);

    private AvatarViewModel? _vm;
    private WriteableBitmap? _previewBitmap;
    private WriteableBitmap? _largeCameraPreviewBitmap;
    private byte[]           _previewBuffer = new byte[AvatarRenderService.RenderWidth * AvatarRenderService.RenderHeight * 4];
    private byte[]           _cameraPreviewBuffer = [];
    private DispatcherTimer? _previewTimer;
    private readonly Dictionary<Canvas, TrackingOverlayVisuals> _trackingOverlayPools = [];

    private bool                     _isDragging;
    private bool                     _isRightDrag;
    private Windows.Foundation.Point _lastDragPos;
    private bool                     _suppressMicCombo;
    private bool                     _suppressCameraCombo;
    private bool                     _suppressPoseCombo;
    private bool                     _suppressTrackingModelCombo;

    public AvatarPage() { InitializeComponent(); }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _vm = App.Services?.GetRequiredService<AvatarViewModel>();
        if (_vm == null) return;

        _vm.PropertyChanged += OnVmPropertyChanged;
        // Suppress persistence while we populate combos below — setting SelectedItem fires
        // SelectionChanged → VM setter → Save, which would overwrite the saved file with
        // default values before InitAsync reads the real ones back.
        _vm.BeginSettingsLoad();

        _previewBitmap = new WriteableBitmap(AvatarRenderService.RenderWidth, AvatarRenderService.RenderHeight);
        PreviewImage.Source = _previewBitmap;
        UpdateTrackingStatus();
        UpdateCalibrationStatus();
        UpdateTrackingDebug();

        PopulateMicDevices();
        PopulateCameraDevices();
        ModelPathBox.Text   = _vm.ModelPath;
        StatusLabel.Text    = _vm.StatusText;
        NdiToggle.IsChecked = _vm.NdiEnabled;
        FaceTrackingToggle.IsChecked = _vm.FaceTrackingEnabled;
        AudioFallbackToggle.IsChecked = _vm.AudioFallbackEnabled;
        VoiceOnlyToggle.IsChecked = _vm.VoiceOnlyMouthEnabled;
        VoiceSensitivitySlider.Value = _vm.VoiceVolumeSensitivity;
        EyeOpenOffsetSlider.Value = _vm.EyeOpenOffset;
        JawOpenScaleSlider.Value = _vm.JawOpenScale;
        HeadRotationScaleSlider.Value = _vm.HeadRotationScale;
        AaScaleSlider.Value = _vm.AaScale;
        IhScaleSlider.Value = _vm.IhScale;
        OuScaleSlider.Value = _vm.OuScale;
        EeScaleSlider.Value = _vm.EeScale;
        OhScaleSlider.Value = _vm.OhScale;
        _suppressTrackingModelCombo = true;
        TrackingModelCombo.Items.Clear();
        foreach (var m in _vm.TrackingModelOptions) TrackingModelCombo.Items.Add(m);
        TrackingModelCombo.SelectedItem = _vm.TrackingModel;
        _suppressTrackingModelCombo = false;

        MouthModeCombo.Items.Clear();
        foreach (var mode in _vm.MouthModeOptions)
            MouthModeCombo.Items.Add(mode);
        MouthModeCombo.SelectedItem = _vm.MouthMode;

        bool hasBones = _vm.BoneNames.Count > 0;
        ResetBonesBtn.IsEnabled  = hasBones;
        PoseModeToggle.IsEnabled = hasBones;
        BoneZoneLabel.Text = hasBones ? "Enable Pose Mode to pose bones" : "No model loaded";
        RefreshPoseList();

        SyncRunButtons();

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _previewTimer.Tick += OnPreviewTick;
        _previewTimer.Start();

        // Load persisted settings, THEN apply them to the controls deterministically.
        await _vm.InitAsync();
        SyncPersistedControls();
    }

    // Applies the loaded persisted settings to every control. Called after InitAsync completes
    // so the dropdowns/sliders reflect what was saved, not the pre-load defaults.
    private void SyncPersistedControls()
    {
        if (_vm == null) return;
        PopulateCameraDevices();
        _suppressTrackingModelCombo = true;
        TrackingModelCombo.SelectedItem = _vm.TrackingModel;
        _suppressTrackingModelCombo = false;
        MouthModeCombo.SelectedItem = _vm.MouthMode;
        FaceTrackingToggle.IsChecked = _vm.FaceTrackingEnabled;
        AudioFallbackToggle.IsChecked = _vm.AudioFallbackEnabled;
        VoiceOnlyToggle.IsChecked = _vm.VoiceOnlyMouthEnabled;
        VoiceSensitivitySlider.Value = _vm.VoiceVolumeSensitivity;
        EyeOpenOffsetSlider.Value = _vm.EyeOpenOffset;
        JawOpenScaleSlider.Value = _vm.JawOpenScale;
        HeadRotationScaleSlider.Value = _vm.HeadRotationScale;
        AaScaleSlider.Value = _vm.AaScale;
        IhScaleSlider.Value = _vm.IhScale;
        OuScaleSlider.Value = _vm.OuScale;
        EeScaleSlider.Value = _vm.EeScale;
        OhScaleSlider.Value = _vm.OhScale;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _previewTimer?.Stop();
        _previewTimer = null;
        _vm?.PersistState();
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
    }

    // ── VM property sync ──────────────────────────────────────────────────────

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_vm == null) return;
            switch (e.PropertyName)
            {
                case nameof(AvatarViewModel.StatusText):
                    StatusLabel.Text = _vm.StatusText; break;
                case nameof(AvatarViewModel.IsRunning):
                    SyncRunButtons(); break;
                case nameof(AvatarViewModel.ModelPath):
                    ModelPathBox.Text = _vm.ModelPath; SyncRunButtons(); break;
                case nameof(AvatarViewModel.MicDevices):
                    PopulateMicDevices(); break;
                case nameof(AvatarViewModel.SelectedMicId):
                    SyncMicSelection(); break;
                case nameof(AvatarViewModel.CameraDevices):
                    PopulateCameraDevices(); break;
                case nameof(AvatarViewModel.SelectedCameraId):
                    SyncCameraSelection(); break;
                case nameof(AvatarViewModel.BoneNames):
                    bool has = _vm.BoneNames.Count > 0;
                    ResetBonesBtn.IsEnabled = has;
                    PoseModeToggle.IsEnabled = has;
                    BoneZoneLabel.Text = has ? "Enable Pose Mode to pose bones" : "No model loaded";
                    break;
                case nameof(AvatarViewModel.SavedPoseNames):
                    RefreshPoseList();
                    break;
                case nameof(AvatarViewModel.SelectedBone):
                    UpdatePoseMarker();
                    break;
                case nameof(AvatarViewModel.TrackingProviderName):
                case nameof(AvatarViewModel.TrackingConfidence):
                case nameof(AvatarViewModel.CameraFps):
                case nameof(AvatarViewModel.TrackerFps):
                case nameof(AvatarViewModel.DetectorMs):
                case nameof(AvatarViewModel.LandmarksMs):
                case nameof(AvatarViewModel.RetargetMs):
                case nameof(AvatarViewModel.IsTrackingFace):
                    UpdateTrackingStatus();
                    break;
                case nameof(AvatarViewModel.TrackingStatusText):
                    UpdateTrackingStatus();
                    break;
                case nameof(AvatarViewModel.CalibrationStatusText):
                    UpdateCalibrationStatus();
                    break;
                case nameof(AvatarViewModel.RawTrackingDebugText):
                case nameof(AvatarViewModel.RetargetDebugText):
                    UpdateTrackingDebug();
                    break;
                case nameof(AvatarViewModel.EyeOpenOffset):
                    EyeOpenOffsetSlider.Value = _vm.EyeOpenOffset;
                    break;
                case nameof(AvatarViewModel.JawOpenScale):
                    JawOpenScaleSlider.Value = _vm.JawOpenScale;
                    break;
                case nameof(AvatarViewModel.HeadRotationScale):
                    HeadRotationScaleSlider.Value = _vm.HeadRotationScale;
                    break;
                case nameof(AvatarViewModel.AaScale):
                    AaScaleSlider.Value = _vm.AaScale; break;
                case nameof(AvatarViewModel.IhScale):
                    IhScaleSlider.Value = _vm.IhScale; break;
                case nameof(AvatarViewModel.OuScale):
                    OuScaleSlider.Value = _vm.OuScale; break;
                case nameof(AvatarViewModel.EeScale):
                    EeScaleSlider.Value = _vm.EeScale; break;
                case nameof(AvatarViewModel.OhScale):
                    OhScaleSlider.Value = _vm.OhScale; break;
                case nameof(AvatarViewModel.TrackingModel):
                    _suppressTrackingModelCombo = true;
                    TrackingModelCombo.SelectedItem = _vm.TrackingModel;
                    _suppressTrackingModelCombo = false;
                    break;
            }
        });
    }

    private void SyncRunButtons()
    {
        bool running = _vm?.IsRunning ?? false;
        StartBtn.IsEnabled = !running && !string.IsNullOrWhiteSpace(_vm?.ModelPath);
        StopBtn.IsEnabled  = running;
    }

    // ── Preview timer ─────────────────────────────────────────────────────────

    private void OnPreviewTick(object? sender, object e)
    {
        if (_vm == null || _previewBitmap == null) return;
        if (!_vm.TryGetPreviewFrame(_previewBuffer)) return;
        using var stream = _previewBitmap.PixelBuffer.AsStream();
        stream.Seek(0, SeekOrigin.Begin);
        stream.Write(_previewBuffer, 0, _previewBuffer.Length);
        _previewBitmap.Invalidate();
        UpdateCameraPreview();
        UpdatePoseMarker();
    }

    // ── File picker ───────────────────────────────────────────────────────────

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".vrm");
        picker.FileTypeFilter.Add(".glb");
        InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;
        await _vm!.LoadModelAsync(file.Path);
        SyncRunButtons();
    }

    // ── Mic device ────────────────────────────────────────────────────────────

    private void PopulateMicDevices()
    {
        _suppressMicCombo = true;
        MicCombo.Items.Clear();
        var devices = _vm?.MicDevices ?? [];
        foreach (var (_, name) in devices) MicCombo.Items.Add(name);
        SyncMicSelection();
        _suppressMicCombo = false;
    }

    private void SyncMicSelection()
    {
        if (_vm == null) return;
        var devices = _vm.MicDevices;
        int idx = devices.FindIndex(d => d.Id == _vm.SelectedMicId);
        _suppressMicCombo = true;
        MicCombo.SelectedIndex = idx >= 0 ? idx : (devices.Count > 0 ? 0 : -1);
        _suppressMicCombo = false;
    }

    private void PopulateCameraDevices()
    {
        if (_vm == null) return;
        _suppressCameraCombo = true;
        CameraCombo.Items.Clear();
        foreach (var (_, name) in _vm.CameraDevices)
            CameraCombo.Items.Add(name);
        SyncCameraSelection();
        _suppressCameraCombo = false;
    }

    private void SyncCameraSelection()
    {
        if (_vm == null) return;
        _suppressCameraCombo = true;
        int idx = _vm.CameraDevices.FindIndex(d => d.Id == _vm.SelectedCameraId);
        CameraCombo.SelectedIndex = idx >= 0 ? idx : -1;
        _suppressCameraCombo = false;
    }

    private void MicCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressMicCombo || _vm == null) return;
        var devices = _vm.MicDevices;
        int idx = MicCombo.SelectedIndex;
        if (idx >= 0 && idx < devices.Count) _vm.SelectedMicId = devices[idx].Id;
    }

    private void RefreshMic_Click(object sender, RoutedEventArgs e)
    {
        _vm?.RefreshMicDevices();
        PopulateMicDevices();
    }

    private async void RefreshCamera_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        await _vm.RefreshCameraDevicesAsync();
        PopulateCameraDevices();
    }

    private void CameraCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm == null || _suppressCameraCombo) return;
        int idx = CameraCombo.SelectedIndex;
        if (idx >= 0 && idx < _vm.CameraDevices.Count)
            _vm.SelectedCameraId = _vm.CameraDevices[idx].Id;
    }

    private async void StartTracking_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        await _vm.StartFaceTrackingAsync();
        UpdateTrackingStatus();
    }

    private async void StopTracking_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        await _vm.StopFaceTrackingAsync();
        UpdateTrackingStatus();
    }

    private void FaceTrackingToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_vm != null) _vm.FaceTrackingEnabled = FaceTrackingToggle.IsChecked == true;
    }

    private void AudioFallbackToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_vm != null) _vm.AudioFallbackEnabled = AudioFallbackToggle.IsChecked == true;
    }

    private void VoiceOnlyToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_vm != null) _vm.VoiceOnlyMouthEnabled = VoiceOnlyToggle.IsChecked == true;
    }

    private void TrackingModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTrackingModelCombo || _vm == null) return;
        if (TrackingModelCombo.SelectedItem is string model)
            _vm.TrackingModel = model;
    }

    private void MouthModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm == null || MouthModeCombo.SelectedItem is not Steaming.Application.Models.FaceTrackingMouthMode mode)
            return;

        _vm.MouthMode = mode;
    }

    private void VoiceSensitivity_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_vm != null)
            _vm.VoiceVolumeSensitivity = (float)e.NewValue;
    }

    private void CaptureNeutral_Click(object sender, RoutedEventArgs e)
    {
        _vm?.CaptureFaceNeutral();
    }

    private void CaptureBlink_Click(object sender, RoutedEventArgs e)
    {
        _vm?.CaptureBlinkClosedCalibration();
    }

    private void CaptureJawOpen_Click(object sender, RoutedEventArgs e)
    {
        _vm?.CaptureJawOpenCalibration();
    }

    private void CaptureSmile_Click(object sender, RoutedEventArgs e)
    {
        _vm?.CaptureSmileCalibration();
    }

    private void CaptureOh_Click(object sender, RoutedEventArgs e)
    {
        _vm?.CaptureOhCalibration();
    }

    private void EyeOpenOffset_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_vm != null)
            _vm.EyeOpenOffset = (float)e.NewValue;
    }

    private void JawOpenScale_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_vm != null)
            _vm.JawOpenScale = (float)e.NewValue;
    }

    private void HeadRotationScale_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_vm != null)
            _vm.HeadRotationScale = (float)e.NewValue;
    }

    private void AaScale_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_vm != null) _vm.AaScale = (float)e.NewValue;
    }

    private void IhScale_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_vm != null) _vm.IhScale = (float)e.NewValue;
    }

    private void OuScale_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_vm != null) _vm.OuScale = (float)e.NewValue;
    }

    private void EeScale_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_vm != null) _vm.EeScale = (float)e.NewValue;
    }

    private void OhScale_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_vm != null) _vm.OhScale = (float)e.NewValue;
    }

    private void SaveCalibration_Click(object sender, RoutedEventArgs e)
    {
        _vm?.SaveFaceTrackingProfile();
    }

    private void UpdateTrackingStatus()
    {
        if (_vm == null) return;
        TrackingStatusLabel.Text =
            $"{_vm.TrackingStatusText}\nProvider: {_vm.TrackingProviderName} | Face: {(_vm.IsTrackingFace ? "locked" : "lost")} | " +
            $"Conf {_vm.TrackingConfidence:0.00} | Cam {_vm.CameraFps:0} fps | Track {_vm.TrackerFps:0} fps | " +
            $"Detect {_vm.DetectorMs:0.0} ms | Landmarks {_vm.LandmarksMs:0.0} ms | Retarget {_vm.RetargetMs:0.0} ms";
    }

    private void UpdateCalibrationStatus()
    {
        if (_vm == null) return;
        CalibrationStatusLabel.Text = _vm.CalibrationStatusText;
    }

    private void UpdateTrackingDebug()
    {
        if (_vm == null) return;
        RawTrackingDebugLabel.Text = _vm.RawTrackingDebugText;
        RetargetDebugLabel.Text = _vm.RetargetDebugText;
    }

    private void UpdateCameraPreview()
    {
        if (_vm == null)
        {
            HideTrackingOverlay(LargeTrackingOverlayCanvas);
            return;
        }

        int width = _largeCameraPreviewBitmap?.PixelWidth ?? 0;
        int height = _largeCameraPreviewBitmap?.PixelHeight ?? 0;
        if (!_vm.TryCopyCameraPreviewFrame(_cameraPreviewBuffer, out width, out height))
        {
            int requiredBytes = width * height * 4;
            if (requiredBytes <= 0)
            {
                HideTrackingOverlay(LargeTrackingOverlayCanvas);
                return;
            }

            _cameraPreviewBuffer = new byte[requiredBytes];
            if (!_vm.TryCopyCameraPreviewFrame(_cameraPreviewBuffer, out width, out height))
            {
                HideTrackingOverlay(LargeTrackingOverlayCanvas);
                return;
            }
        }

        int pixelBytes = width * height * 4;

        if (_largeCameraPreviewBitmap == null || _largeCameraPreviewBitmap.PixelWidth != width || _largeCameraPreviewBitmap.PixelHeight != height)
        {
            _largeCameraPreviewBitmap = new WriteableBitmap(width, height);
            LargeCameraPreviewImage.Source = _largeCameraPreviewBitmap;
        }

        using (var stream = _largeCameraPreviewBitmap.PixelBuffer.AsStream())
        {
            stream.Seek(0, SeekOrigin.Begin);
            stream.Write(_cameraPreviewBuffer, 0, pixelBytes);
        }
        _largeCameraPreviewBitmap.Invalidate();

        DrawTrackingOverlay(LargeTrackingOverlayCanvas, LargeCameraPreviewImage);
    }

    private void DrawTrackingOverlay(Canvas targetCanvas, Image hostImage)
    {
        if (_vm == null
            || !_vm.TryGetTrackingOverlay(out var landmarks, out var faceBox, out var overlayWidth, out var overlayHeight, out var isTracking, out var confidence)
            || overlayWidth <= 0
            || overlayHeight <= 0)
        {
            HideTrackingOverlay(targetCanvas);
            return;
        }

        double hostWidth = hostImage.ActualWidth;
        double hostHeight = hostImage.ActualHeight;
        if (hostWidth <= 0 || hostHeight <= 0)
        {
            HideTrackingOverlay(targetCanvas);
            return;
        }

        var visuals = GetOrCreateTrackingOverlay(targetCanvas);

        targetCanvas.Width = hostWidth;
        targetCanvas.Height = hostHeight;
        double scale = Math.Min(hostWidth / overlayWidth, hostHeight / overlayHeight);
        double drawWidth = overlayWidth * scale;
        double drawHeight = overlayHeight * scale;
        double offsetX = (hostWidth - drawWidth) * 0.5;
        double offsetY = (hostHeight - drawHeight) * 0.5;

        visuals.FaceRect.Width = faceBox.Z * scale;
        visuals.FaceRect.Height = faceBox.W * scale;
        visuals.FaceRect.Stroke = isTracking ? LockedStrokeBrush : LostStrokeBrush;
        visuals.FaceRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(visuals.FaceRect, offsetX + faceBox.X * scale);
        Canvas.SetTop(visuals.FaceRect, offsetY + faceBox.Y * scale);

        int landmarkCount = landmarks.Length / 3;
        for (int i = 0; i < landmarkCount; i++)
        {
            if (i >= visuals.Dots.Count)
            {
                var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Width = 4,
                    Height = 4,
                    Visibility = Visibility.Collapsed
                };
                visuals.Dots.Add(dot);
                targetCanvas.Children.Add(dot);
            }

            var dotVisual = visuals.Dots[i];
            dotVisual.Fill = isTracking ? LockedDotBrush : LostDotBrush;
            dotVisual.Visibility = Visibility.Visible;
            Canvas.SetLeft(dotVisual, offsetX + landmarks[i * 3 + 1] * scale - 2);
            Canvas.SetTop(dotVisual, offsetY + landmarks[i * 3] * scale - 2);
        }

        for (int i = landmarkCount; i < visuals.Dots.Count; i++)
            visuals.Dots[i].Visibility = Visibility.Collapsed;

        visuals.StatusLabel.Text = $"{(isTracking ? "LOCKED" : "SEARCHING")} {confidence:0.00}";
        visuals.StatusLabel.Visibility = Visibility.Visible;
        Canvas.SetLeft(visuals.StatusLabel, offsetX + 6);
        Canvas.SetTop(visuals.StatusLabel, offsetY + 6);
    }

    private TrackingOverlayVisuals GetOrCreateTrackingOverlay(Canvas targetCanvas)
    {
        if (_trackingOverlayPools.TryGetValue(targetCanvas, out var visuals))
            return visuals;

        visuals = new TrackingOverlayVisuals();
        targetCanvas.Children.Add(visuals.FaceRect);
        targetCanvas.Children.Add(visuals.StatusLabel);
        _trackingOverlayPools[targetCanvas] = visuals;
        return visuals;
    }

    private void HideTrackingOverlay(Canvas targetCanvas)
    {
        if (!_trackingOverlayPools.TryGetValue(targetCanvas, out var visuals))
            return;

        visuals.FaceRect.Visibility = Visibility.Collapsed;
        visuals.StatusLabel.Visibility = Visibility.Collapsed;
        foreach (var dot in visuals.Dots)
            dot.Visibility = Visibility.Collapsed;
    }

    // ── NDI toggle ────────────────────────────────────────────────────────────

    private void NdiToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_vm != null) _vm.NdiEnabled = NdiToggle.IsChecked == true;
    }

    // ── Expression sliders ────────────────────────────────────────────────────

    private void SliderExpr_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_vm == null) return;
        if      (ReferenceEquals(sender, SliderAa)) _vm.ExpressionAa = (float)e.NewValue;
        else if (ReferenceEquals(sender, SliderIh)) _vm.ExpressionIh = (float)e.NewValue;
        else if (ReferenceEquals(sender, SliderOu)) _vm.ExpressionOu = (float)e.NewValue;
        else if (ReferenceEquals(sender, SliderEe)) _vm.ExpressionEe = (float)e.NewValue;
        else if (ReferenceEquals(sender, SliderOh)) _vm.ExpressionOh = (float)e.NewValue;
    }

    private void ResetExpressions_Click(object sender, RoutedEventArgs e)
    {
        SliderAa.Value = SliderIh.Value = SliderOu.Value = SliderEe.Value = SliderOh.Value = 0;
    }

    private void RefreshPoseList()
    {
        _suppressPoseCombo = true;
        PoseListCombo.Items.Clear();
        if (_vm != null)
        {
            foreach (var name in _vm.SavedPoseNames)
                PoseListCombo.Items.Add(name);
        }
        PoseListCombo.SelectedIndex = -1;
        LoadPoseBtn.IsEnabled = false;
        DeletePoseBtn.IsEnabled = false;
        _suppressPoseCombo = false;
    }

    private void SavePose_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || string.IsNullOrWhiteSpace(PoseNameBox.Text)) return;
        _vm.SaveCurrentPose(PoseNameBox.Text.Trim());
        RefreshPoseList();
    }

    private void PoseList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPoseCombo) return;
        bool selected = PoseListCombo.SelectedIndex >= 0;
        LoadPoseBtn.IsEnabled = selected;
        DeletePoseBtn.IsEnabled = selected;
    }

    private void LoadPose_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || PoseListCombo.SelectedItem is not string name) return;
        _vm.LoadPose(name);
    }

    private void DeletePose_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || PoseListCombo.SelectedItem is not string name) return;
        _vm.DeletePose(name);
        RefreshPoseList();
    }

    // ── Bone / pose mode controls ─────────────────────────────────────────────

    private void PoseMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        bool poseOn = PoseModeToggle.IsChecked == true;
        _vm.BoneControlMode = poseOn;
        PreviewHintLabel.Text = poseOn
            ? "Left-drag body part to pose  ·  Right-drag: pan  ·  Scroll: zoom"
            : "Left-drag: orbit camera  ·  Right-drag: pan  ·  Scroll: zoom";
        BoneZoneLabel.Text = poseOn ? "Click a body part to pose it" : "Enable Pose Mode to pose bones";
        UpdatePoseMarker();
    }

    private void ResetBones_Click(object sender, RoutedEventArgs e)
    {
        _vm?.ResetAllBones();
        BoneZoneLabel.Text = PoseModeToggle.IsChecked == true
            ? "Click a body part to pose it"
            : "Enable Pose Mode to pose bones";
        UpdatePoseMarker();
    }

    // ── Camera reset ──────────────────────────────────────────────────────────

    private void ResetCamera_Click(object sender, RoutedEventArgs e)
        => _vm?.ResetCamera();

    // ── Start / Stop ──────────────────────────────────────────────────────────

    private void Start_Click(object sender, RoutedEventArgs e) => _vm?.StartCommand.Execute(null);
    private void Stop_Click (object sender, RoutedEventArgs e) => _vm?.StopCommand.Execute(null);

    // ── Preview pointer — bone zone click + camera orbit ─────────────────────

    private void Preview_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt    = e.GetCurrentPoint(PreviewGrid);
        var props = pt.Properties;
        bool left  = props.IsLeftButtonPressed;
        bool right = props.IsRightButtonPressed;
        if (!left && !right) return;

        _isDragging  = true;
        _isRightDrag = right;
        _lastDragPos = pt.Position;

        // Zone detection only when Pose Mode is explicitly enabled
        if (_vm != null && left && _vm.BoneControlMode && _vm.BoneNames.Count > 0)
        {
            var (nx, ny) = NormalizeToImage(pt.Position);
            string? bone = PickBone(nx, ny);
            if (bone != null)
            {
                _vm.SelectedBone   = bone;
                if (_vm.IsIKEffector(bone))
                {
                    _vm.BeginIKDrag(bone);
                    BoneZoneLabel.Text = bone + " (IK)";
                }
                else
                {
                    BoneZoneLabel.Text = bone;
                }
                UpdatePoseMarker();
            }
        }

        ((UIElement)sender).CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Preview_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging || _vm == null) return;
        var pos = e.GetCurrentPoint(PreviewGrid).Position;
        float dx = (float)(pos.X - _lastDragPos.X);
        float dy = (float)(pos.Y - _lastDragPos.Y);
        _lastDragPos = pos;

        if (_isRightDrag)
            _vm.PanCamera(dx, dy);
        else
            _vm.OrbitCamera(dx, dy);

        e.Handled = true;
    }

    private void Preview_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isDragging = false;
        _vm?.EndIKDrag();
        UpdatePoseMarker();
        ((UIElement)sender).ReleasePointerCaptures();
        e.Handled = true;
    }

    private void Preview_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_vm == null) return;
        float delta = e.GetCurrentPoint(PreviewGrid).Properties.MouseWheelDelta;
        _vm.ZoomCamera(delta);
        e.Handled = true;
    }

    // ── Bone zone detection ───────────────────────────────────────────────────

    // Returns (nx, ny) in 0..1 relative to the actual rendered image pixels,
    // accounting for Stretch=Uniform letterboxing inside PreviewGrid.
    private (double nx, double ny) NormalizeToImage(Windows.Foundation.Point pos)
    {
        double gw = PreviewGrid.ActualWidth;
        double gh = PreviewGrid.ActualHeight;
        const double srcAspect = 540.0 / 960.0;   // portrait 9:16
        double ga = gw / gh;

        double imgW, imgH, ox, oy;
        if (ga > srcAspect)
        {
            imgH = gh; imgW = gh * srcAspect;
            ox = (gw - imgW) / 2.0; oy = 0;
        }
        else
        {
            imgW = gw; imgH = gw / srcAspect;
            ox = 0; oy = (gh - imgH) / 2.0;
        }
        return ((pos.X - ox) / imgW, (pos.Y - oy) / imgH);
    }

    private Windows.Foundation.Point ImageNormalizedToGrid(double nx, double ny)
    {
        double gw = PreviewGrid.ActualWidth;
        double gh = PreviewGrid.ActualHeight;
        const double srcAspect = 540.0 / 960.0;
        double ga = gw / gh;

        double imgW, imgH, ox, oy;
        if (ga > srcAspect)
        {
            imgH = gh;
            imgW = gh * srcAspect;
            ox = (gw - imgW) / 2.0;
            oy = 0;
        }
        else
        {
            imgW = gw;
            imgH = gw / srcAspect;
            ox = 0;
            oy = (gh - imgH) / 2.0;
        }

        return new Windows.Foundation.Point(ox + nx * imgW, oy + ny * imgH);
    }

    private void UpdatePoseMarker()
    {
        if (_vm == null
            || !_vm.BoneControlMode
            || string.IsNullOrWhiteSpace(_vm.SelectedBone)
            || !_vm.IsIKEffector(_vm.SelectedBone))
        {
            IkTargetMarker.Visibility = Visibility.Collapsed;
            return;
        }

        var world = _vm.GetPoseWorldPos(_vm.SelectedBone);
        if (world == System.Numerics.Vector3.Zero
            || !_vm.TryProjectWorldToNormalized(world, out var normalized))
        {
            IkTargetMarker.Visibility = Visibility.Collapsed;
            return;
        }

        var pt = ImageNormalizedToGrid(normalized.X, normalized.Y);
        Canvas.SetLeft(IkTargetMarker, pt.X - IkTargetMarker.Width / 2d);
        Canvas.SetTop(IkTargetMarker, pt.Y - IkTargetMarker.Height / 2d);
        IkTargetMarker.Visibility = Visibility.Visible;
    }

    private string? PickBone(double nx, double ny)
    {
        if (nx < 0 || nx > 1 || ny < 0 || ny > 1) return null;
        if (_vm == null) return null;

        var hits = new List<(string Bone, double DistSq, bool IsEffector)>();
        AddBoneHit(hits, nx, ny, FindBone("head"), 0.055);
        AddBoneHit(hits, nx, ny, FindBone("neck"), 0.050);
        AddBoneHit(hits, nx, ny, FindBone("upperchest", "chest"), 0.060);
        AddBoneHit(hits, nx, ny, FindBone("spine"), 0.060);
        AddBoneHit(hits, nx, ny, FindBone("hips", "hip"), 0.065);

        AddLimbHits(hits, nx, ny,
            FindBone("_L_UpperArm", "LUpperArm", "leftupperarm"),
            FindBone("_L_LowerArm", "LLowerArm", "leftlowerarm"),
            FindBone("_L_Hand", "LHand", "left_hand", "lefthand"),
            0.060, 0.055, 0.050);
        AddLimbHits(hits, nx, ny,
            FindBone("_R_UpperArm", "RUpperArm", "rightupperarm"),
            FindBone("_R_LowerArm", "RLowerArm", "rightlowerarm"),
            FindBone("_R_Hand", "RHand", "right_hand", "righthand"),
            0.060, 0.055, 0.050);
        AddLimbHits(hits, nx, ny,
            FindBone("_L_UpperLeg", "LUpperLeg", "leftupperleg"),
            FindBone("_L_LowerLeg", "LLowerLeg", "leftlowerleg"),
            FindBone("_L_Foot", "LFoot", "leftfoot"),
            0.065, 0.060, 0.055);
        AddLimbHits(hits, nx, ny,
            FindBone("_R_UpperLeg", "RUpperLeg", "rightupperleg"),
            FindBone("_R_LowerLeg", "RLowerLeg", "rightlowerleg"),
            FindBone("_R_Foot", "RFoot", "rightfoot"),
            0.065, 0.060, 0.055);

        if (hits.Count == 0) return null;

        return hits
            .OrderBy(h => h.DistSq)
            .ThenByDescending(h => h.IsEffector)
            .Select(h => h.Bone)
            .FirstOrDefault();
    }

    private void AddLimbHits(
        List<(string Bone, double DistSq, bool IsEffector)> hits,
        double nx,
        double ny,
        string? upper,
        string? lower,
        string? effector,
        double upperRadius,
        double lowerRadius,
        double effectorRadius)
    {
        if (_vm == null) return;

        if (!string.IsNullOrWhiteSpace(upper) && !string.IsNullOrWhiteSpace(lower))
        {
            var upperPos = _vm.GetPoseWorldPos(upper);
            var lowerPos = _vm.GetPoseWorldPos(lower);
            AddWorldHit(hits, nx, ny, upper, System.Numerics.Vector3.Lerp(upperPos, lowerPos, 0.35f), upperRadius);
        }

        if (!string.IsNullOrWhiteSpace(lower) && !string.IsNullOrWhiteSpace(effector))
        {
            var lowerPos = _vm.GetPoseWorldPos(lower);
            var effPos   = _vm.GetPoseWorldPos(effector);
            AddWorldHit(hits, nx, ny, lower, System.Numerics.Vector3.Lerp(lowerPos, effPos, 0.45f), lowerRadius);
        }

        AddBoneHit(hits, nx, ny, effector, effectorRadius);
    }

    private void AddBoneHit(
        List<(string Bone, double DistSq, bool IsEffector)> hits,
        double nx,
        double ny,
        string? boneName,
        double radius)
    {
        if (_vm == null || string.IsNullOrWhiteSpace(boneName)) return;
        AddWorldHit(hits, nx, ny, boneName, _vm.GetPoseWorldPos(boneName), radius);
    }

    private void AddWorldHit(
        List<(string Bone, double DistSq, bool IsEffector)> hits,
        double nx,
        double ny,
        string boneName,
        System.Numerics.Vector3 worldPos,
        double radius)
    {
        if (_vm == null
            || worldPos == System.Numerics.Vector3.Zero
            || !_vm.TryProjectWorldToNormalized(worldPos, out var projected))
        {
            return;
        }

        double dx = projected.X - nx;
        double dy = projected.Y - ny;
        double distSq = dx * dx + dy * dy;
        if (distSq <= radius * radius)
            hits.Add((boneName, distSq, _vm.IsIKEffector(boneName)));
    }

    private string? FindBone(params string[] keywords)
    {
        if (_vm?.BoneNames is not { Count: > 0 } bones) return null;
        foreach (var kw in keywords)
        {
            var match = bones.FirstOrDefault(b => b.Contains(kw, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return null;
    }
}
