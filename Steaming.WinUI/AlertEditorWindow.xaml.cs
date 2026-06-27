using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingFrameDimension = System.Drawing.Imaging.FrameDimension;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingImage = System.Drawing.Image;
using DrawingImageFormat = System.Drawing.Imaging.ImageFormat;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using NAudio.Wave;
using Steaming.Application.ViewModels;
using Steaming.Core.Models;
using Steaming.Core.Services;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Microsoft.UI.Text;
using Windows.UI;
using Windows.ApplicationModel.DataTransfer;
using WinUI.Dock;
using WinRT.Interop;

namespace Steaming.WinUI;

public record AlertEditorResult(string LayoutJson, string? SoundFile, float Volume, float Duration);

public sealed partial class AlertEditorWindow : Window, IDockAdapter, IDockBehavior
{
    private const string LogPrefix = "[AlertEditorWindow]";
    private static readonly List<AlertEditorWindow> OpenEditors = new();

    // ─── State ──────────────────────────────────────────────────────────────────
    private readonly AlertEditorViewModel _vm;
    private readonly MainViewModel? _mainVm;
    private readonly string? _eventKey;
    private readonly string? _labelGoalKey;
    private readonly TaskCompletionSource<AlertEditorResult?> _tcs = new();

    // ─── Pre-built panel content ────────────────────────────────────────────────
    private UIElement _layersContent = null!;
    private UIElement _canvasContent = null!;
    private UIElement _propsContent  = null!;
    private UIElement _timelineContent = null!;
    private UIElement _transitionsContent = null!;

    // Layers controls
    private ListView _layersList = null!;
    private Button _layerHideBtn = null!, _layerLockBtn = null!;

    // Canvas controls
    private Canvas _innerCanvas = null!;
    private readonly Dictionary<string, FrameworkElement> _elemControls = new();
    private readonly Dictionary<string, TextRenderCacheEntry> _textRenderCache = new();
    private readonly Dictionary<string, GifFrameCacheEntry> _gifCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _gifLastFrameIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task> _gifLoadTasks = new(StringComparer.OrdinalIgnoreCase);
    // Editor video preview = on-demand single-frame extraction (Premiere/AE-style scrubbing), NOT a
    // live media pipeline. A MediaComposition per file yields the frame at any timestamp; we decode
    // ONLY when the requested frame time actually changes, coalescing to the latest request. There is
    // no continuous decoder, no MediaPlayer, nothing to spam when the scrub isn't moving.
    private readonly Dictionary<string, Windows.Media.Editing.MediaComposition> _videoCompositions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _videoLastFrameTime = new();   // elId → last requested time
    private readonly Dictionary<string, double> _videoPendingTime   = new();   // elId → newer request while decoding
    private readonly HashSet<string>             _videoDecoding      = new();   // elId currently decoding
    private readonly HashSet<string>             _videoAspectFitted  = new();
    private readonly Border[] _resizeHandles = new Border[8];
    private Border  _selBorder   = null!;
    private Ellipse _rotHandle   = null!;

    // Properties controls
    private TextBlock _noSelLabel    = null!;
    private StackPanel _geomPanel    = null!;
    private TextBox _propX = null!, _propY = null!, _propW = null!, _propH = null!;
    private Slider  _propOpacity = null!;
    private TextBox _propRot = null!;
    private ComboBox _kfEasing = null!;
    private static readonly TextTransitionType[] _transitionTypes = Enum.GetValues<TextTransitionType>();
    private StackPanel _rectPanel    = null!;
    private TextBox _propFillColor   = null!;
    private TextBox _propCornerRad   = null!;
    private TextBox _propOutlineW    = null!;
    private StackPanel _textPanel    = null!;
    private RichEditBox _richBox     = null!;
    private bool _suppressRich;
    private List<TextSpan>? _pendingProgrammaticRichSpans;
    private int _richProgrammaticLoadVersion;
    private int _programmaticPropsVersion;
    // Last non-empty RichEditBox selection. Opening the colour-picker flyout moves focus off the
    // RichEditBox and collapses its live selection, so colour/format must be applied to this
    // captured range instead of Document.Selection.
    private int _richSelStart = -1, _richSelEnd = -1;
    private ComboBox _propFontFamily = null!;
    private TextBox _propFontSize    = null!;
    private CheckBox _propBold = null!, _propItalic = null!;
    // Rect extras
    private ComboBox _propShapeType  = null!;
    private Slider   _propFillOpacity = null!;
    private Border   _fillSwatch     = null!;
    private FrameworkElement _cornerRow = null!;
    // Text alignment + colour
    private ComboBox _propAlignH = null!, _propAlignV = null!;
    private TextBox  _propTextColor = null!;
    private Border   _textSwatch = null!;
    // Shadow
    private CheckBox _propShadowOn = null!;
    private TextBox  _propShadowColor = null!;
    private Border   _shadowSwatch = null!;
    private Slider   _propShadowOpacity = null!, _propShadowAngle = null!, _propShadowDist = null!, _propShadowBlur = null!;
    private StackPanel _shadowOpts = null!;
    // Outline
    private CheckBox _propOutlineOn = null!;
    private TextBox  _propOutlineColor = null!;
    private Border   _outlineSwatch = null!;
    private StackPanel _outlineOpts = null!;
    private StackPanel _imagePanel   = null!;
    private TextBox _propFilePath    = null!;
    private StackPanel _videoPanel     = null!;
    private TextBox    _propVideoPath  = null!;
    private ComboBox   _propVideoEnd   = null!;
    private CheckBox   _propVideoMute  = null!;
    private Slider     _propVideoVolume = null!;
    private StackPanel _audioPanel   = null!;
    private TextBox _propAudioPath   = null!;
    private TextBox _propStartTime = null!, _propFadeIn = null!, _propFadeOut = null!;
    private TextBox _propVolL = null!, _propVolR = null!;
    private StackPanel _kfPanel      = null!;
    private ListView _kfList         = null!;
    private TextBox _kfTimeInput     = null!;
    private TextBox _previewUserBox = null!, _previewMessageBox = null!, _previewAmountBox = null!;
    private TextBox _propMasterSoundPath = null!, _propMasterVolume = null!;
    private Slider _propVolLSlider = null!, _propVolRSlider = null!, _propMasterVolSlider = null!;
    private readonly Dictionary<TextTransitionType, Canvas> _transitionTilePreviews = new();
    private TextTransitionType? _hoverPreviewTransition;
    private DispatcherQueueTimer? _transitionPreviewTimer;
    private double _transitionPreviewTime;
    private TextTransitionType? _draggingTransitionType;
    // Keyframe property editor
    private TextBox _kfX = null!, _kfY = null!, _kfW = null!, _kfH = null!,
                    _kfOpacity = null!, _kfScaleX = null!, _kfScaleY = null!, _kfRot = null!, _kfFillColor = null!;
    private Border _kfFillSwatch = null!;

    // Timeline controls
    private Slider   _tlSlider       = null!;
    private TextBlock _tlTimeLabel   = null!;
    private Canvas   _tlCanvas       = null!;
    private Canvas   _tlRulerCanvas  = null!;
    private Canvas   _tlHeaderCanvas = null!;
    private ScrollViewer _tlHeaderScroll = null!;
    private ScrollViewer _tlTrackScroll = null!;
    private ScrollViewer _tlRulerScroll = null!;
    private Line _timelinePlayheadLine = null!;
    private Line _rulerPlayheadLine = null!;
    private Rectangle _rulerPlayheadHead = null!;
    private readonly Dictionary<string, (float[] Peaks, double DurationSec)> _clipWaveforms = new();
    private readonly List<(AlertElement Element, double RowY, double RowH, double ClipX, double ClipEndX, double FadeInEndX, double FadeOutStartX)> _audioRowInfos = new();
    // Envelope polyline segments for click-on-line point creation (canvas coords)
    private readonly List<(AlertElement El, double X1, double Y1, double X2, double Y2)> _tlClipEnvSegs = new();
    private readonly List<(double X1, double Y1, double X2, double Y2)> _tlMasterEnvSegs = new();
    // Audio volume-keyframe panel controls
    private Slider _propClipVolSlider = null!;
    private TextBox _propClipVol = null!;
    private ListView _audioKfList = null!;
    private TextBox _audioKfTime = null!, _audioKfVol = null!;
    private sealed record AudioKfItem(AudioVolumeKeyframe Kf, string Label)
    {
        public override string ToString() => Label;
    }

    // Timeline drag state
    private enum TlDragMode { None, AudioMove, AudioFadeIn, AudioFadeOut, Keyframe, ClipMove, ClipTrimStart, ClipTrimEnd, MasterEnvPoint, ClipEnvPoint }
    private TlDragMode _tlDrag = TlDragMode.None;
    private AlertElement? _tlAudioDragEl;
    private double _tlAudioDragStartX;
    private float _tlAudioDragOrigStart, _tlAudioDragOrigFadeIn, _tlAudioDragOrigFadeOut;
    private double _tlPixPerSec;
    private double _tlZoom = 1.0;   // timeline horizontal zoom (1 = fit duration to viewport)

    // Timeline volume scales (1.0 = unity; >1 = boost). Matches WPF editor + wire format clamp (0–2).
    private const float MAX_ENVELOPE_VOL = 2.0f;
    private const float MAX_CLIP_VOL = 2.0f;

    // Timeline hit-test data — rebuilt on every DrawTimeline
    private readonly List<(AlertElement El, AlertKeyframe Kf, double X, double Y)> _tlKfHits = new();
    private readonly List<(AlertElement El, AlertKeyframe NextKf, double X, double Y)> _tlSpliceHits = new();
    private AlertElement? _tlDropSpliceEl;
    private AlertKeyframe? _tlDropSpliceKf;
    private TextTransitionType? _tlDropSpliceType;
    private readonly List<(AudioVolumeKeyframe Kf, double X, double Y)> _tlMasterEnvHits = new();
    private readonly List<(AlertElement El, AudioVolumeKeyframe Kf, double X, double Y)> _tlClipEnvHits = new();
    private readonly List<(AlertElement El, double RowY, double ClipX, double ClipEndX)> _tlVisualRowInfos = new();
    private double _tlLegacyRowY = -1, _tlLegacyRowH;

    // Keyframe / clip-bar / envelope drag state
    private AlertKeyframe? _tlDragKf;
    private float _tlDragKfOrigTime;
    private AudioVolumeKeyframe? _tlDragEnvKf;
    private float _tlDragEnvOrigVol;
    private readonly List<(AlertKeyframe Kf, float OrigTime)> _tlDragClipKfTimes = new();

    // ─── Drag state ─────────────────────────────────────────────────────────────
    private enum DragMode { None, Move, ResizeHandle, RotateHandle }
    private DragMode _dragMode = DragMode.None;
    private Point    _dragStart;
    private float    _dragOrigX, _dragOrigY, _dragOrigW, _dragOrigH;
    private bool     _hasDragged; // true only once movement exceeds 1px threshold
    private int      _resizeHandleIdx = -1;
    private Pointer? _capturedPointer;

    // ─── Playback ───────────────────────────────────────────────────────────────
    private DispatcherQueueTimer? _playTimer;
    private WaveOutEvent?    _waveOut;
    private AudioFileReader? _audioReader;
    private DockManager? _dockManager;
    private readonly List<DispatcherQueueTimer> _audioClipTimers = new();
    private readonly List<(WaveOutEvent Output, AudioFileReader Reader, AlertElement El)> _audioClipPlayers = new();
    private bool _renderingHooked;

    private bool _suppressProps;
    private bool _suppressSlider;
    private bool _isClosed;

    private sealed record GifFrameCacheEntry(
        WriteableBitmap[] Frames,
        double[] CumSec,
        double TotalSec,
        long LastWriteTicks);

    private sealed class TextRenderCacheEntry
    {
        public bool IsDualPass;
        public TextTransitionType TransitionType;
        public Grid? SingleGrid;
        public Grid? FromGrid;
        public Grid? ToGrid;
        public string? SingleSignature;
        public string? FromSignature;
        public string? ToSignature;
    }

    private enum NumericInputMode
    {
        UnsignedInteger,
        SignedInteger,
        UnsignedDecimal,
        SignedDecimal,
    }

    // ────────────────────────────────────────────────────────────────────────────
    public AlertEditorWindow(
        AlertLayout layout, float duration = 5f,
        string? soundFile = null, string? eventKey = null,
        MainViewModel? mainVm = null, string? labelGoalKey = null)
    {
        _vm           = new AlertEditorViewModel(layout, duration, soundFile, eventKey);
        _mainVm       = mainVm;
        _eventKey     = eventKey;
        _labelGoalKey = labelGoalKey;

        try
        {
            Log($"ctor start eventKey={eventKey ?? "<null>"} labelGoalKey={labelGoalKey ?? "<null>"} duration={duration:F2}");

            Log("InitializeComponent");
            InitializeComponent();
            AttachNumericFilter(CanvasW, NumericInputMode.UnsignedInteger);
            AttachNumericFilter(CanvasH, NumericInputMode.UnsignedInteger);
            AttachNumericFilter(DurationBox, NumericInputMode.UnsignedDecimal);
            CanvasW.TextChanged += (_, _) => ApplyCanvasSizeBoxes();
            CanvasH.TextChanged += (_, _) => ApplyCanvasSizeBoxes();
            DurationBox.TextChanged += (_, _) => ApplyDurationBox();

        // Build panel content after InitializeComponent — WinUI requires component alive before Children.Add
        Log("BuildLayersContent");
        _layersContent   = BuildLayersContent();
        Log("BuildCanvasContent");
        _canvasContent   = BuildCanvasContent();
        Log("BuildPropertiesContent");
        _propsContent    = BuildPropertiesContent();
        Log("BuildTimelineContent");
        _timelineContent = BuildTimelineContent();
        Log("BuildTransitionsContent");
        _transitionsContent = BuildTransitionsContent();

        // Build the DockManager hierarchy in code (avoids XAML compiler issues with third-party control)
        Log("SetupDockManager");
        SetupDockManager();

        Log("Configure AppWindow");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 900));
        string titleSuffix = eventKey ?? labelGoalKey ?? "";
        AppWindow.Title = titleSuffix.Length > 0 ? $"Alert Layout Editor — {titleSuffix}" : "Alert Layout Editor";

        Log("Populate toolbar fields");
        CanvasW.Text     = _vm.Layout.Width.ToString();
        CanvasH.Text     = _vm.Layout.Height.ToString();
        DurationBox.Text = duration.ToString("F1");
        _propMasterSoundPath.Text = _vm.SoundFile ?? "";
        _propMasterVolume.Text   = layout.Volume.ToString("F2");
        _previewUserBox.Text = _vm.PreviewUser;
        _previewMessageBox.Text = _vm.PreviewMessage;
        _previewAmountBox.Text = _vm.PreviewAmount;
        if (!string.IsNullOrWhiteSpace(_vm.SoundFile) && File.Exists(_vm.SoundFile))
            _vm.LoadSoundDuration(_vm.SoundFile);

        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.LayoutMutated  += UpdateUndoRedoButtons;
        // Wire Ctrl+Z/Y on the root content grid — Window has no KeyDown in WinUI 3
        ((Grid)this.Content).KeyDown += (_, e) =>
        {
            bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (!ctrl) return;
            if (e.Key == Windows.System.VirtualKey.Z) { DoUndo(); e.Handled = true; }
            else if (e.Key == Windows.System.VirtualKey.Y) { DoRedo(); e.Handled = true; }
        };
        OpenEditors.Add(this);
        Closed += (_, _) =>
        {
            _isClosed = true;
            OpenEditors.Remove(this);
            _tcs.TrySetResult(null);
            CleanupAudio();
            ClearVideoCaches();
        };

        DockHost.Loaded += (_, _) =>
        {
            Log("DockHost.Loaded");
            RebuildCanvas();
            RefreshLayerList();
            DrawTimeline();
            UpdatePropertiesPanel();
        };
        
        Log("ctor complete");
        }
        catch (Exception ex)
        {
            LogException("constructor failed", ex);
            if (Debugger.IsAttached) Debugger.Break();
            throw;
        }
    }

    public static async Task<AlertEditorResult?> OpenAsync(
        AlertLayout layout, float duration, string? soundFile, string? eventKey,
        MainViewModel? mainVm, string? labelGoalKey = null)
    {
        try
        {
            LogStatic($"OpenAsync start eventKey={eventKey ?? "<null>"} labelGoalKey={labelGoalKey ?? "<null>"}");
            var win = new AlertEditorWindow(layout, duration, soundFile, eventKey, mainVm, labelGoalKey);
            LogStatic("Activate");
            win.Activate();
            return await win._tcs.Task;
        }
        catch (Exception ex)
        {
            LogStatic($"OpenAsync failed: {ex}");
            if (Debugger.IsAttached) Debugger.Break();
            throw;
        }
    }

    private void Log(string message) => DebugLogFile.Append($"{LogPrefix} {message}");

    private void LogException(string message, Exception ex) => DebugLogFile.Append($"{LogPrefix} {message}: {ex}");

    private static void LogStatic(string message) => DebugLogFile.Append($"{LogPrefix} {message}");

    public static void CloseAllOpenEditors()
    {
        foreach (var editor in OpenEditors.ToArray())
        {
            try { editor.Close(); } catch { }
        }
    }


    // ─── ViewModel property changes ─────────────────────────────────────────────
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (e.PropertyName == nameof(AlertEditorViewModel.PreviewTime))
            {
                // During playback the CompositionTarget.Rendering loop updates the visual directly
                // (synchronously with the frame) — running it again here via the deferred dispatcher
                // path is what made playback stutter. Only handle the non-playback (scrub) case here.
                if (_renderingHooked) return;
                TimeLabel.Text = $"{_vm.PreviewTime:F2}s";
                if (!_suppressSlider) { _suppressSlider = true; _tlSlider.Value = _vm.PreviewTime; _suppressSlider = false; }
                _tlTimeLabel.Text = $"{_vm.PreviewTime:F2}s";
                UpdatePreviewState();
                UpdateTimelinePlayhead();
            }
            else if (e.PropertyName == nameof(AlertEditorViewModel.IsPlaying))
            {
                PlayBtn.Content = _vm.IsPlaying ? "⏸ Pause" : "▶ Play";
                if (!_vm.IsPlaying)
                    UpdatePropertiesPanel();
            }
            else if (e.PropertyName == nameof(AlertEditorViewModel.PreviewUser)
                  || e.PropertyName == nameof(AlertEditorViewModel.PreviewMessage)
                  || e.PropertyName == nameof(AlertEditorViewModel.PreviewAmount))
            {
                RebuildCanvas();
            }
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PANEL BUILDERS
    // ═══════════════════════════════════════════════════════════════════════════

    private UIElement BuildLayersContent()
    {
        _layersList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _layersList.SelectionChanged += LayerList_SelectionChanged;

        _layerHideBtn = MakeBtn("Hide", (s, e) =>
        {
            if (_vm.SelectedElement == null) return;
            _vm.SetSelectedHidden(!_vm.SelectedElement.Hidden);
            if (_dragMode == DragMode.Move) EndDrag();
            RebuildCanvas();
            RefreshLayerList();
            UpdatePropertiesPanel();
            DrawTimeline();
        });
        _layerLockBtn = MakeBtn("Lock", (s, e) =>
        {
            if (_vm.SelectedElement == null) return;
            _vm.SetSelectedLocked(!_vm.SelectedElement.Locked);
            if (_dragMode == DragMode.Move) EndDrag();
            RebuildCanvas();
            RefreshLayerList();
            UpdatePropertiesPanel();
            DrawTimeline();
        });

        var btnDelete = MakeBtn("Delete", (s, e) =>
        {
            _vm.DeleteSelected();
            RebuildCanvas();
            RefreshLayerList();
            UpdatePropertiesPanel();
            DrawTimeline();
        });
        var btnUp = MakeBtn("↑ Up", (s, e) =>
        {
            _vm.MoveSelectedUp();
            RebuildCanvas();
            RefreshLayerList();
        });
        var btnDown = MakeBtn("↓ Down", (s, e) =>
        {
            _vm.MoveSelectedDown();
            RebuildCanvas();
            RefreshLayerList();
        });

        var btnBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Padding = new Thickness(4),
        };
        btnBar.Children.Add(_layerHideBtn);
        btnBar.Children.Add(_layerLockBtn);
        btnBar.Children.Add(btnUp);
        btnBar.Children.Add(btnDown);
        btnBar.Children.Add(btnDelete);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_layersList, 0);
        Grid.SetRow(btnBar, 1);
        root.Children.Add(_layersList);
        root.Children.Add(btnBar);
        return root;
    }

    private UIElement BuildCanvasContent()
    {
        _innerCanvas = new Canvas
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 18, 18, 18)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _innerCanvas.PointerPressed   += Canvas_PointerPressed;
        _innerCanvas.PointerMoved     += Canvas_PointerMoved;
        _innerCanvas.PointerReleased  += Canvas_PointerReleased;
        _innerCanvas.PointerCaptureLost += (_, _) => EndDrag();
        _innerCanvas.SizeChanged += (_, _) => ApplyCheckerBackground();

        // Selection overlay (initially hidden)
        _selBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0, 160, 255)),
            BorderThickness = new Thickness(2),
            Background = new SolidColorBrush(Color.FromArgb(30, 0, 160, 255)),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        Canvas.SetZIndex(_selBorder, 9000);
        _innerCanvas.Children.Add(_selBorder);

        for (int i = 0; i < 8; i++)
        {
            var h = new Border
            {
                Width = 10, Height = 10,
                Background = new SolidColorBrush(Colors.White),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0, 120, 200)),
                BorderThickness = new Thickness(1.5),
                Visibility = Visibility.Collapsed,
                Tag = i,
            };
            int idx = i;
            h.PointerPressed += (s, e) =>
            {
                if (_vm.SelectedElement == null) return;
                _resizeHandleIdx = idx;
                _dragMode = DragMode.ResizeHandle;
                _vm.BeginGeometryGesture(); // one undo snapshot per resize gesture
                var el = _vm.SelectedElement;
                // Use the displayed (keyframe-evaluated) geometry as the drag origin
                var st0 = _vm.EvalAnimated(el, _vm.PreviewTime);
                _dragOrigX = st0.x; _dragOrigY = st0.y;
                _dragOrigW = st0.w; _dragOrigH = st0.h;
                _dragStart = e.GetCurrentPoint(_innerCanvas).Position;
                ((Border)s).CapturePointer(e.Pointer);
                _capturedPointer = e.Pointer;
                e.Handled = true;
            };
            h.PointerMoved    += (s, e) => ApplyHandleDrag(e.GetCurrentPoint(_innerCanvas).Position);
            h.PointerReleased += (s, e) => { CommitDrag(); ((Border)s).ReleasePointerCapture(e.Pointer); };
            Canvas.SetZIndex(h, 9001);
            _resizeHandles[i] = h;
            _innerCanvas.Children.Add(h);
        }

        _rotHandle = new Ellipse
        {
            Width = 12, Height = 12,
            Fill = new SolidColorBrush(Colors.LimeGreen),
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 1.5,
            Visibility = Visibility.Collapsed,
        };
        _rotHandle.PointerPressed += (s, e) =>
        {
            if (_vm.SelectedElement == null) return;
            _dragMode = DragMode.RotateHandle;
            _vm.BeginGeometryGesture(); // one undo snapshot per rotate gesture
            ((Ellipse)s).CapturePointer(e.Pointer);
            _capturedPointer = e.Pointer;
            e.Handled = true;
        };
        _rotHandle.PointerMoved += (s, e) => ApplyRotateDrag(e.GetCurrentPoint(_innerCanvas).Position);
        _rotHandle.PointerReleased += (s, e) => { CommitDrag(); ((Ellipse)s).ReleasePointerCapture(e.Pointer); };
        Canvas.SetZIndex(_rotHandle, 9001);
        _innerCanvas.Children.Add(_rotHandle);

        var viewbox = new Viewbox
        {
            Stretch = Stretch.Uniform,
            Child = _innerCanvas,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        var outer = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 40, 40, 40)),
        };
        outer.PointerPressed += CanvasOuter_PointerPressed;
        outer.Children.Add(viewbox);
        return outer;
    }

    private UIElement BuildPropertiesContent()
    {
        var sp = new StackPanel { Spacing = 0 };

        var previewPanel = BuildSection("Preview Variables", new UIElement[]
        {
            MakePropRow("User", _previewUserBox = MakeTextBox("TestUser")),
            MakePropRow("Message", _previewMessageBox = MakeTextBox("Test message!")),
            MakePropRow("Amount", _previewAmountBox = MakeNumericTextBox("100", NumericInputMode.UnsignedDecimal)),
        });
        _previewUserBox.LostFocus += (_, _) => ApplyPreviewVariables();
        _previewMessageBox.LostFocus += (_, _) => ApplyPreviewVariables();
        _previewAmountBox.LostFocus += (_, _) => ApplyPreviewVariables();
        _previewUserBox.TextChanged += (_, _) => ApplyPreviewVariables();
        _previewMessageBox.TextChanged += (_, _) => ApplyPreviewVariables();
        _previewAmountBox.TextChanged += (_, _) => ApplyPreviewVariables();
        sp.Children.Add(previewPanel);

        // Alert Sound section (master sound for the alert)
        var masterSoundBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        masterSoundBtns.Children.Add(MakeBtn("Browse...", BrowseSound_Click));
        masterSoundBtns.Children.Add(MakeBtn("▶ Test", (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_propMasterSoundPath.Text) && File.Exists(_propMasterSoundPath.Text))
            {
                float.TryParse(_propMasterVolume.Text, out var v);
                if (v <= 0) v = 1f;
                PlayAudio(_propMasterSoundPath.Text, v);
            }
        }));
        masterSoundBtns.Children.Add(MakeBtn("■ Stop", (_, _) => StopAudio()));
        var masterVolRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _propMasterVolSlider = new Slider { Minimum = 0, Maximum = 1, StepFrequency = 0.01, Width = 140, Value = 1 };
        masterVolRow.Children.Add(_propMasterVolSlider);
        masterVolRow.Children.Add(_propMasterVolume = MakeNumericTextBox("1.0", NumericInputMode.UnsignedDecimal));
        _propMasterVolume.Width = 56;
        _propMasterVolSlider.ValueChanged += (s, e) =>
        {
            if (_suppressProps) return;
            _suppressProps = true; _propMasterVolume.Text = e.NewValue.ToString("F2"); _suppressProps = false;
            ApplyMasterVolume();
        };
        _propMasterVolume.TextChanged += (s, e) => ApplyIfNotSuppressed(ApplyMasterVolume);
        var masterSoundPanel = BuildSection("Alert Sound", new UIElement[]
        {
            MakePropRow("Sound File", _propMasterSoundPath = MakeTextBox("")),
            masterSoundBtns,
            MakePropRow("Volume", masterVolRow),
        });
        sp.Children.Add(masterSoundPanel);

        _noSelLabel = new TextBlock
        {
            Text = "Select an element to edit its properties.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromArgb(180, 200, 200, 200)),
            Margin = new Thickness(12),
            FontSize = 12,
        };
        sp.Children.Add(_noSelLabel);

        // Flip buttons — negate width/height = mirror the element in place.
        var flipRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        flipRow.Children.Add(MakeBtn("⇄ Flip H", (_, _) => FlipSelected(horizontal: true)));
        flipRow.Children.Add(MakeBtn("⇅ Flip V", (_, _) => FlipSelected(horizontal: false)));

        // Geometry section
        _geomPanel = BuildSection("Position & Size", new UIElement[]
        {
            MakePropRow("X",        _propX       = MakeNumericTextBox("0", NumericInputMode.SignedDecimal)),
            MakePropRow("Y",        _propY       = MakeNumericTextBox("0", NumericInputMode.SignedDecimal)),
            MakePropRow("Width",    _propW       = MakeNumericTextBox("100", NumericInputMode.SignedDecimal)),
            MakePropRow("Height",   _propH       = MakeNumericTextBox("100", NumericInputMode.SignedDecimal)),
            flipRow,
            MakePropRow("Opacity",  _propOpacity = new Slider
            {
                Minimum = 0, Maximum = 1, StepFrequency = 0.01, Value = 1,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            }),
            MakePropRow("Rotation", _propRot     = MakeNumericTextBox("0", NumericInputMode.SignedDecimal)),
        });
        _propOpacity.ValueChanged += (s, e) => ApplyIfNotSuppressed(() =>
        {
            if (_vm.SelectedElement == null) return;
            _vm.WriteOpacityKf((float)_propOpacity.Value);
            UpdatePreviewState();
            DrawTimeline();
        });
        WireGeomBoxes();
        sp.Children.Add(_geomPanel);

        // Rect section — shape type, fill colour picker + opacity, corner radius
        _propShapeType = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        _propShapeType.Items.Add("Rectangle");
        _propShapeType.Items.Add("Ellipse");
        _fillSwatch = MakeSwatch(() => _vm.SelectedElement?.FillColor,
            c => { _suppressProps = false; _propFillColor.Text = $"{c.R:X2}{c.G:X2}{c.B:X2}"; });
        var fillRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        fillRow.Children.Add(_propFillColor = MakeTextBox("2196F3"));
        _propFillColor.Width = 90;
        fillRow.Children.Add(_fillSwatch);
        _propFillOpacity = new Slider { Minimum = 0, Maximum = 100, Value = 100, HorizontalAlignment = HorizontalAlignment.Stretch };
        _rectPanel = BuildSection("Shape", new UIElement[]
        {
            MakePropRow("Shape",               _propShapeType),
            MakePropRow("Fill Color (RRGGBB)", fillRow),
            MakePropRow("Fill Opacity %",      _propFillOpacity),
            _cornerRow = MakePropRow("Corner Radius", _propCornerRad = MakeNumericTextBox("0", NumericInputMode.UnsignedInteger)),
        });
        void ApplyFill() {
            if (_vm.SelectedElement == null) return;
            var argb = _vm.UpdateSelectedFillColor(_propFillColor.Text.TrimStart('#'), _propFillOpacity.Value);
            if (argb == null) return;
            SetSwatch(_fillSwatch, argb);
            _vm.WriteFillColorKf(argb);
            DrawTimeline();
            RebuildCanvas();
        }
        _propFillColor.TextChanged    += (s, e) => ApplyIfNotSuppressed(ApplyFill);
        _propFillOpacity.ValueChanged += (s, e) => ApplyIfNotSuppressed(ApplyFill);
        _propShapeType.SelectionChanged += (s, e) => ApplyIfNotSuppressed(() => {
            if (_vm.SelectedElement?.Type != AlertElementType.Rect) return;
            bool ellipse = _propShapeType.SelectedIndex == 1;
            _vm.UpdateSelectedCornerRadius(ellipse ? 9999 : (int.TryParse(_propCornerRad.Text, out var cr) && cr < 9999 ? cr : 0));
            _cornerRow.Visibility = ellipse ? Visibility.Collapsed : Visibility.Visible;
            RebuildCanvas();
        });
        _propCornerRad.TextChanged += (s, e) => ApplyIfNotSuppressed(() => {
            if (_vm.SelectedElement != null && int.TryParse(_propCornerRad.Text, out var v))
            { _vm.UpdateSelectedCornerRadius(v); _vm.WriteCornerRadiusKf(v); DrawTimeline(); RebuildCanvas(); }
        });
        sp.Children.Add(_rectPanel);

        // Text section
        _propBold   = new CheckBox { Content = "Bold",   MinWidth = 0 };
        _propItalic = new CheckBox { Content = "Italic", MinWidth = 0 };
        _propBold.Click   += (_, _) => ApplyIfNotSuppressed(ApplyBoldToSelection);
        _propItalic.Click += (_, _) => ApplyIfNotSuppressed(ApplyItalicToSelection);

        // Alignment
        _propAlignH = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var a in new[] { "Left", "Center", "Right" }) _propAlignH.Items.Add(a);
        _propAlignV = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var a in new[] { "Top", "Middle", "Bottom" }) _propAlignV.Items.Add(a);
        _propAlignH.SelectionChanged += (s, e) => ApplyIfNotSuppressed(() => {
            if (_vm.UpdateSelectedAlign(_propAlignH.SelectedIndex)) {
                _vm.WriteTextAlignKf(_propAlignH.SelectedIndex, _propAlignV.SelectedIndex >= 0 ? _propAlignV.SelectedIndex : 1);
                DrawTimeline(); RebuildCanvas();
            }
        });
        _propAlignV.SelectionChanged += (s, e) => ApplyIfNotSuppressed(() => {
            if (_vm.UpdateSelectedVertAlign(_propAlignV.SelectedIndex)) {
                _vm.WriteTextAlignKf(_propAlignH.SelectedIndex >= 0 ? _propAlignH.SelectedIndex : 1, _propAlignV.SelectedIndex);
                DrawTimeline(); RebuildCanvas();
            }
        });

        // Text colour (all spans) — hex box + picker swatch.
        // Call ApplyColorToSelection() DIRECTLY: _propTextColor is not in the visual tree, so its
        // TextChanged never fires — relying on it (the old path) silently dropped every colour pick.
        _propTextColor = MakeTextBox("FFFFFF");
        _propTextColor.Width = 90;
        _textSwatch = MakeSwatch(() => CurrentTextArgb(),
            c => { _propTextColor.Text = $"{c.R:X2}{c.G:X2}{c.B:X2}"; ApplyColorToSelection(); });
        _propTextColor.TextChanged += (s, e) => ApplyIfNotSuppressed(ApplyColorToSelection);

        // Drop shadow (Photoshop-style controls; preview already renders it)
        _propShadowOn = new CheckBox { Content = "Drop Shadow", MinWidth = 0 };
        _shadowSwatch = MakeSwatch(() => _vm.SelectedElement?.ShadowColor,
            c => { _suppressProps = false; _propShadowColor.Text = $"{c.R:X2}{c.G:X2}{c.B:X2}"; });
        var shadowColorRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        shadowColorRow.Children.Add(_propShadowColor = MakeTextBox("000000"));
        _propShadowColor.Width = 90;
        shadowColorRow.Children.Add(_shadowSwatch);
        _propShadowOpacity = new Slider { Minimum = 0, Maximum = 100, Value = 66 };
        _propShadowAngle   = new Slider { Minimum = 0, Maximum = 360, Value = 135 };
        _propShadowDist    = new Slider { Minimum = 0, Maximum = 30,  Value = 3 };
        _propShadowBlur    = new Slider { Minimum = 0, Maximum = 20,  Value = 4 };
        _shadowOpts = new StackPanel { Spacing = 2, Visibility = Visibility.Collapsed };
        _shadowOpts.Children.Add(MakePropRow("Color (RRGGBB)", shadowColorRow));
        _shadowOpts.Children.Add(MakePropRow("Opacity %",  _propShadowOpacity));
        _shadowOpts.Children.Add(MakePropRow("Angle °",    _propShadowAngle));
        _shadowOpts.Children.Add(MakePropRow("Distance px",_propShadowDist));
        _shadowOpts.Children.Add(MakePropRow("Blur px",    _propShadowBlur));
        void ApplyShadowColor() {
            if (_vm.SelectedElement == null) return;
            var argb = _vm.UpdateSelectedShadowColor(_propShadowColor.Text.TrimStart('#'), _propShadowOpacity.Value);
            if (argb != null)
            {
                SetSwatch(_shadowSwatch, argb);
                if (_vm.SelectedElement.Type == AlertElementType.Text)
                    _vm.WriteTextShadowKf(_propShadowOn.IsChecked == true, argb);
                RebuildCanvas();
            }
        }
        _propShadowColor.TextChanged      += (s, e) => ApplyIfNotSuppressed(ApplyShadowColor);
        _propShadowOpacity.ValueChanged   += (s, e) => ApplyIfNotSuppressed(ApplyShadowColor);
        void ApplyShadowGeometry() {
            if (_vm.SelectedElement == null) return;
            float a = (float)_propShadowAngle.Value, d = (float)_propShadowDist.Value, bl = (float)_propShadowBlur.Value;
            _vm.UpdateSelectedShadowAngle(a); _vm.UpdateSelectedShadowDistance(d); _vm.UpdateSelectedShadowBlur(bl);
            _vm.WriteShadowGeometryKf(a, d, bl);
            DrawTimeline(); RebuildCanvas();
        }
        _propShadowAngle.ValueChanged += (s, e) => ApplyIfNotSuppressed(ApplyShadowGeometry);
        _propShadowDist.ValueChanged  += (s, e) => ApplyIfNotSuppressed(ApplyShadowGeometry);
        _propShadowBlur.ValueChanged  += (s, e) => ApplyIfNotSuppressed(ApplyShadowGeometry);

        // Outline
        _propOutlineOn = new CheckBox { Content = "Outline", MinWidth = 0 };
        _outlineSwatch = MakeSwatch(() => _vm.SelectedElement?.OutlineColor,
            c => { _suppressProps = false; _propOutlineColor.Text = $"{c.R:X2}{c.G:X2}{c.B:X2}"; });
        var outlineColorRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        outlineColorRow.Children.Add(_propOutlineColor = MakeTextBox("000000"));
        _propOutlineColor.Width = 90;
        outlineColorRow.Children.Add(_outlineSwatch);
        _outlineOpts = new StackPanel { Spacing = 2, Visibility = Visibility.Collapsed };
        _outlineOpts.Children.Add(MakePropRow("Color (RRGGBB)", outlineColorRow));
        _outlineOpts.Children.Add(MakePropRow("Width px", _propOutlineW = MakeTextBox("1")));
        _propOutlineColor.TextChanged += (s, e) => ApplyIfNotSuppressed(() => {
            if (_vm.SelectedElement == null) return;
            var argb = _vm.UpdateSelectedOutlineColor(_propOutlineColor.Text.TrimStart('#'));
            if (argb != null)
            {
                SetSwatch(_outlineSwatch, argb);
                if (_vm.SelectedElement.Type == AlertElementType.Text)
                    _vm.WriteTextOutlineKf(_propOutlineOn.IsChecked == true, argb, int.TryParse(_propOutlineW.Text, out var ow) ? ow : 1);
                RebuildCanvas();
            }
        });
        _propOutlineW.TextChanged += (s, e) => ApplyIfNotSuppressed(() => {
            if (_vm.SelectedElement == null) return;
            if (int.TryParse(_propOutlineW.Text, out var v))
            {
                _vm.UpdateSelectedOutlineWidth(v);
                if (_vm.SelectedElement.Type == AlertElementType.Text)
                    _vm.WriteTextOutlineKf(_propOutlineOn.IsChecked == true, _vm.SelectedElement.OutlineColor, v);
                RebuildCanvas();
            }
        });
        void ApplyEffectFlags() {
            if (_vm.SelectedElement == null) return;
            bool shadowOn  = _propShadowOn.IsChecked  == true;
            bool outlineOn = _propOutlineOn.IsChecked == true;
            _vm.UpdateSelectedTextFlags(shadowOn, outlineOn);
            if (_vm.SelectedElement.Type == AlertElementType.Text)
            {
                _vm.WriteTextShadowKf(shadowOn, _vm.SelectedElement.ShadowColor);
                _vm.WriteTextOutlineKf(outlineOn, _vm.SelectedElement.OutlineColor, _vm.SelectedElement.OutlineWidth);
            }
            _shadowOpts.Visibility  = shadowOn  ? Visibility.Visible : Visibility.Collapsed;
            _outlineOpts.Visibility = outlineOn ? Visibility.Visible : Visibility.Collapsed;
            RebuildCanvas();
        }
        _propShadowOn.Click  += (_, _) => ApplyIfNotSuppressed(ApplyEffectFlags);
        _propOutlineOn.Click += (_, _) => ApplyIfNotSuppressed(ApplyEffectFlags);

        // RichEditBox — select text, apply bold/italic/colour/font to selection
        _propFontFamily = MakeFontCombo();
        _propFontSize   = MakeNumericTextBox("24", NumericInputMode.UnsignedDecimal);
        _propFontSize.Width = 52;

        // Formatting toolbar — Bold | Italic | Color | FontFamily | FontSize — pinned directly above
        // the text editing box it controls.
        var formatToolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(4, 4, 4, 2) };
        formatToolbar.Children.Add(_propBold);
        formatToolbar.Children.Add(_propItalic);
        formatToolbar.Children.Add(_textSwatch);
        formatToolbar.Children.Add(_propFontFamily);
        formatToolbar.Children.Add(_propFontSize);

        _richBox = new RichEditBox
        {
            Height = 160,
            AcceptsReturn = false,
            IsSpellCheckEnabled = false,
            FontSize = 14,
            Margin = new Thickness(4, 0, 4, 4),
        };
        // Kill the built-in floating Bold/Italic/Underline popup that appears over selected text —
        // it gets in the way; formatting is done from the toolbar above the box.
        _richBox.SelectionFlyout = null;
        // Keep the selection visibly highlighted after focus leaves the box. The toolbar controls
        // (font family/size, colour picker) steal focus the moment you click them, and by default a
        // RichEditBox hides its selection highlight when unfocused — so the user could no longer see
        // which text they were about to restyle. The formatting still targets the captured range
        // (_richSelStart/_richSelEnd); this only restores the visual cue.
        Color accent = Color.FromArgb(255, 0x33, 0x99, 0xFF);
        if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("SystemAccentColor", out var ac) && ac is Color c)
            accent = c;
        var selBrush = new SolidColorBrush(accent);
        _richBox.SelectionHighlightColor = selBrush;
        _richBox.SelectionHighlightColorWhenNotFocused = selBrush;
        _richBox.TextChanged      += (s, e) =>
        {
            if (_suppressRich) return;
            if (_pendingProgrammaticRichSpans != null)
            {
                var currentSpans = ExtractSpansFromRichBox();
                if (SpansEqual(currentSpans, _pendingProgrammaticRichSpans))
                {
                    _pendingProgrammaticRichSpans = null;
                    return;
                }
                _pendingProgrammaticRichSpans = null;
            }
            CommitRichSpans();
        };
        _richBox.SelectionChanged += (s, e) =>
        {
            if (_suppressRich) return;
            var sel = _richBox.Document.Selection;
            // Only react to a REAL (non-empty) selection. When the colour-picker flyout opens it
            // steals focus and collapses the selection to a caret; reacting to that collapse would
            // both lose the captured range AND re-arm _suppressProps (via the deferred reset in
            // UpdateRichToolbarFromSelection), which then blocks the colour from ever applying.
            if (sel.EndPosition > sel.StartPosition)
            {
                _richSelStart = sel.StartPosition;
                _richSelEnd   = sel.EndPosition;
                UpdateRichToolbarFromSelection();
            }
        };

        _textPanel = BuildSection("Text", new UIElement[]
        {
            formatToolbar,
            _richBox,
            MakePropRow("Align",       _propAlignH),
            MakePropRow("Vert Align",  _propAlignV),
            _propShadowOn,
            _shadowOpts,
            _propOutlineOn,
            _outlineOpts,
        });
        _propFontFamily.SelectionChanged += (s, e) => ApplyIfNotSuppressed(ApplyFontFamilyToSelection);
        _propFontSize.LostFocus          += (s, e) => ApplyIfNotSuppressed(ApplyFontSizeToSelection);
        _propFontSize.TextChanged        += (s, e) => ApplyIfNotSuppressed(ApplyFontSizeToSelection);
        sp.Children.Add(_textPanel);

        // Image / GIF section
        var browseImgBtn = MakeBtn("Browse...", async (s, e) =>
        {
            var path = await PickFileAsync(new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" });
            if (path != null)
            {
                _propFilePath.Text = path;
                _vm.UpdateSelectedFilePath(path, true);
                RebuildCanvas();
            }
        });
        _imagePanel = BuildSection("Image / GIF", new UIElement[]
        {
            MakePropRow("File Path", _propFilePath = MakeTextBox("")),
            browseImgBtn,
        });
        sp.Children.Add(_imagePanel);

        // Video section
        var browseVideoBtn = MakeBtn("Browse...", async (s, e) =>
        {
            var path = await PickFileAsync(new[] { ".mp4", ".mov", ".m4v" });
            if (path != null && _vm.SelectedElement is { Type: AlertElementType.Video } ve)
            {
                _propVideoPath.Text = path;
                ve.FilePath = path;
                RebuildCanvas();
            }
        });
        _propVideoEnd = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        _propVideoEnd.Items.Add("Loop");
        _propVideoEnd.Items.Add("Hold last frame");
        _propVideoEnd.Items.Add("Hold first frame");
        _propVideoEnd.Items.Add("End (hide)");
        _propVideoEnd.Items.Add("End (fade out)");
        _propVideoEnd.SelectionChanged += (s, e) =>
        {
            if (_suppressProps) return;
            if (_vm.SelectedElement is { Type: AlertElementType.Video } v && _propVideoEnd.SelectedIndex >= 0)
            {
                v.VideoEnd = VideoEndBehaviorFromComboIndex(_propVideoEnd.SelectedIndex);
                UpdatePreviewState();   // reflect end-behaviour (hide/fade past clip end) in the preview
            }
        };
        _propVideoMute = new CheckBox { Content = "Mute audio (in OBS)" };
        _propVideoMute.Checked   += (s, e) => { if (!_suppressProps && _vm.SelectedElement is { Type: AlertElementType.Video } v) v.VideoMuted = true; };
        _propVideoMute.Unchecked += (s, e) => { if (!_suppressProps && _vm.SelectedElement is { Type: AlertElementType.Video } v) v.VideoMuted = false; };
        _propVideoVolume = new Slider { Minimum = 0, Maximum = 2, StepFrequency = 0.05, Width = 160 };
        _propVideoVolume.ValueChanged += (s, e) =>
        {
            if (_suppressProps) return;
            if (_vm.SelectedElement is { Type: AlertElementType.Video } v) v.VideoVolume = (float)e.NewValue;
        };
        _videoPanel = BuildSection("Video", new UIElement[]
        {
            MakePropRow("File Path", _propVideoPath = MakeTextBox("")),
            browseVideoBtn,
            MakePropRow("When done", _propVideoEnd),
            _propVideoMute,
            MakePropRow("Volume", _propVideoVolume),
        });
        sp.Children.Add(_videoPanel);

        // Audio section
        var browseAudioBtn = MakeBtn("Browse...", async (s, e) =>
        {
            var path = await PickFileAsync(new[] { ".mp3", ".wav", ".ogg" });
            if (path != null)
            {
                _propAudioPath.Text = path;
                _vm.UpdateSelectedAudioFilePath(path);
                DrawTimeline();
            }
        });
        // Keyframed volume: dragging the slider writes/updates an envelope point at the
        // playhead (Premiere-style write automation). The slider always shows the
        // envelope value at the current preview time.
        var clipVolRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _propClipVolSlider = new Slider { Minimum = 0, Maximum = MAX_CLIP_VOL, StepFrequency = 0.01, Width = 140, Value = 1 };
        _propClipVol = MakeNumericTextBox("1.0", NumericInputMode.UnsignedDecimal);
        _propClipVol.Width = 56;
        clipVolRow.Children.Add(_propClipVolSlider);
        clipVolRow.Children.Add(_propClipVol);
        _propClipVolSlider.ValueChanged += (_, e) =>
        {
            if (_suppressProps) return;
            _suppressProps = true; _propClipVol.Text = e.NewValue.ToString("F2"); _suppressProps = false;
            WriteClipVolumeAtPlayhead((float)e.NewValue);
        };
        _propClipVol.TextChanged += (_, _) =>
        {
            if (_suppressProps || !float.TryParse(_propClipVol.Text, out var v)) return;
            v = Math.Clamp(v, 0, MAX_CLIP_VOL);
            _suppressProps = true; _propClipVolSlider.Value = v; _suppressProps = false;
            WriteClipVolumeAtPlayhead(v);
        };
        _propClipVol.LostFocus += (_, _) =>
        {
            if (_suppressProps || !float.TryParse(_propClipVol.Text, out var v)) return;
            v = Math.Clamp(v, 0, MAX_CLIP_VOL);
            _suppressProps = true; _propClipVolSlider.Value = v; _suppressProps = false;
            WriteClipVolumeAtPlayhead(v);
        };

        // Volume keyframe list — these ARE the points on the timeline rubber band
        _audioKfList = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 120 };
        _audioKfList.SelectionChanged += (_, _) =>
        {
            if (_suppressProps || _audioKfList.SelectedItem is not AudioKfItem item) return;
            _suppressProps = true;
            _audioKfTime.Text = item.Kf.Time.ToString("F2");
            _audioKfVol.Text  = (item.Kf.Volume * 100f).ToString("F0");
            _suppressProps = false;
            _vm.SetPreviewTime(item.Kf.Time);
            DrawTimeline();
        };
        var audioKfAdd = MakeBtn("◆ Add at Playhead", (_, _) =>
        {
            var el = _vm.SelectedElement;
            if (el?.Type != AlertElementType.Audio) return;
            var kf = AlertEditorViewModel.WriteClipVolumeEnvelopePoint(
                el, _vm.PreviewTime, (float)_propClipVolSlider.Value, MAX_CLIP_VOL);
            RefreshAudioKfPanel(kf);
            DrawTimeline();
        });
        var audioKfRem = MakeBtn("✕ Remove", (_, _) =>
        {
            var el = _vm.SelectedElement;
            if (el?.Type != AlertElementType.Audio || _audioKfList.SelectedItem is not AudioKfItem item) return;
            AlertEditorViewModel.RemoveClipVolumeEnvelopePoint(el, item.Kf);
            RefreshAudioKfPanel();
            DrawTimeline();
        });
        var audioKfBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(4, 4, 4, 0) };
        audioKfBtns.Children.Add(audioKfAdd);
        audioKfBtns.Children.Add(audioKfRem);
        _audioKfTime = MakeNumericTextBox("", NumericInputMode.UnsignedDecimal);
        _audioKfVol  = MakeNumericTextBox("", NumericInputMode.UnsignedDecimal);
        _audioKfTime.LostFocus += (_, _) => ApplyAudioKfFields();
        _audioKfVol.LostFocus  += (_, _) => ApplyAudioKfFields();
        _audioKfTime.TextChanged += (_, _) => ApplyAudioKfFields();
        _audioKfVol.TextChanged  += (_, _) => ApplyAudioKfFields();
        var audioKfHint = new TextBlock
        {
            Text = "Click the yellow volume line on the timeline to add a point; drag points to shape; right-click a point to delete.",
            FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 6, 4, 0),
            Foreground = new SolidColorBrush(Color.FromArgb(180, 200, 200, 200)),
        };

        _audioPanel = BuildSection("Audio Clip", new UIElement[]
        {
            MakePropRow("File Path",  _propAudioPath  = MakeTextBox("")),
            browseAudioBtn,
            MakePropRow("Start Time", _propStartTime  = MakeNumericTextBox("0", NumericInputMode.UnsignedDecimal)),
            MakePropRow("Fade In",    _propFadeIn     = MakeNumericTextBox("0", NumericInputMode.UnsignedDecimal)),
            MakePropRow("Fade Out",   _propFadeOut    = MakeNumericTextBox("0", NumericInputMode.UnsignedDecimal)),
            MakePropRow("Volume ◆",   clipVolRow),
            MakePropRow("Channel L",  MakeVolRow(out _propVolLSlider, out _propVolL)),
            MakePropRow("Channel R",  MakeVolRow(out _propVolRSlider, out _propVolR)),
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    MakeBtn("Test", (_, _) => PlaySelectedAudioClip()),
                    MakeBtn("Stop", (_, _) => StopAudio()),
                }
            },
            new TextBlock
            {
                Text = "Volume Keyframes", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(4, 10, 4, 0),
            },
            _audioKfList,
            audioKfBtns,
            MakePropRow("Time (s)",   _audioKfTime),
            MakePropRow("Volume %",   _audioKfVol),
            audioKfHint,
        });
        _propStartTime.LostFocus += (s, e) => ApplyAudioProps();
        _propFadeIn.LostFocus    += (s, e) => ApplyAudioProps();
        _propFadeOut.LostFocus   += (s, e) => ApplyAudioProps();
        _propVolL.LostFocus      += (s, e) => ApplyAudioProps();
        _propVolR.LostFocus      += (s, e) => ApplyAudioProps();
        _propStartTime.TextChanged += (s, e) => ApplyAudioProps();
        _propFadeIn.TextChanged    += (s, e) => ApplyAudioProps();
        _propFadeOut.TextChanged   += (s, e) => ApplyAudioProps();
        _propVolL.TextChanged      += (s, e) => ApplyAudioProps();
        _propVolR.TextChanged      += (s, e) => ApplyAudioProps();
        sp.Children.Add(_audioPanel);

        // Keyframes section
        _kfList = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 140 };
        var kfAddBtn = MakeBtn("Add KF at Time", (s, e) =>
        {
            var kf = _vm.AddSelectedKeyframeAtPreview();
            if (kf != null) { RefreshKfList(); DrawTimeline(); }
        });
        var kfRemBtn = MakeBtn("Remove KF", (s, e) =>
        {
            if (_kfList.SelectedItem is KfItem item)
            {
                _vm.RemoveSelectedKeyframe(item.Keyframe);
                RefreshKfList();
                DrawTimeline();
            }
        });
        var kfBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(4, 4, 4, 0) };
        kfBtns.Children.Add(kfAddBtn);
        kfBtns.Children.Add(kfRemBtn);

        // Per-keyframe easing (parity with WPF KfEasing combo)
        _kfEasing = new ComboBox { MinWidth = 130 };
        foreach (var name in new[] { "Linear", "Ease In", "Ease Out", "Ease In/Out", "Bounce" })
            _kfEasing.Items.Add(name);
        _kfEasing.SelectionChanged += (s, e) =>
        {
            if (_suppressProps || _kfEasing.SelectedIndex < 0) return;
            if (_kfList.SelectedItem is KfItem item)
            {
                AlertEditorViewModel.UpdateKeyframeEasing(item.Keyframe, _kfEasing.SelectedIndex);
                DrawTimeline();
            }
        };
        _kfList.SelectionChanged += (s, e) =>
        {
            if (_kfList.SelectedItem is not KfItem item) return;
            _suppressProps = true;
            _kfEasing.SelectedIndex = (int)item.Keyframe.Easing;
            _suppressProps = false;
            LoadKfFields(item.Keyframe);
        };
        _kfList.DoubleTapped += (s, e) =>
        {
            if (_kfList.SelectedItem is KfItem item)
            {
                _vm.SetPreviewTime(item.Keyframe.Time);
                UpdatePropertiesPanel();
            }
        };
        var kfEasingRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(4, 4, 4, 0) };
        kfEasingRow.Children.Add(new TextBlock { Text = "Easing", FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        kfEasingRow.Children.Add(_kfEasing);

        // Per-keyframe property grid (blank = property not animated at this keyframe)
        _kfTimeInput = MakeNumericTextBox("", NumericInputMode.UnsignedDecimal);
        _kfX = MakeNumericTextBox("", NumericInputMode.SignedDecimal); _kfY = MakeNumericTextBox("", NumericInputMode.SignedDecimal); _kfW = MakeNumericTextBox("", NumericInputMode.SignedDecimal); _kfH = MakeNumericTextBox("", NumericInputMode.SignedDecimal);
        _kfOpacity = MakeNumericTextBox("", NumericInputMode.UnsignedDecimal); _kfScaleX = MakeNumericTextBox("", NumericInputMode.SignedDecimal); _kfScaleY = MakeNumericTextBox("", NumericInputMode.SignedDecimal); _kfRot = MakeNumericTextBox("", NumericInputMode.SignedDecimal);
        _kfFillSwatch = MakeSwatch(() => (_kfList.SelectedItem as KfItem)?.Keyframe.FillColor,
            c => { _suppressProps = false; _kfFillColor.Text = $"{c.R:X2}{c.G:X2}{c.B:X2}"; });
        var kfFillRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        kfFillRow.Children.Add(_kfFillColor = MakeTextBox(""));
        _kfFillColor.Width = 80;
        kfFillRow.Children.Add(_kfFillSwatch);
        var kfProps = new StackPanel { Spacing = 2 };
        kfProps.Children.Add(new TextBlock
        {
            Text = "Selected keyframe — blank = not animated", FontSize = 11,
            Margin = new Thickness(4, 6, 4, 0),
            Foreground = new SolidColorBrush(Color.FromArgb(180, 200, 200, 200)),
        });
        kfProps.Children.Add(MakePropRow("Time (s)",  _kfTimeInput));
        kfProps.Children.Add(MakePropRow("X",         _kfX));
        kfProps.Children.Add(MakePropRow("Y",         _kfY));
        kfProps.Children.Add(MakePropRow("Width",     _kfW));
        kfProps.Children.Add(MakePropRow("Height",    _kfH));
        kfProps.Children.Add(MakePropRow("Opacity",   _kfOpacity));
        kfProps.Children.Add(MakePropRow("Scale X",   _kfScaleX));
        kfProps.Children.Add(MakePropRow("Scale Y",   _kfScaleY));
        kfProps.Children.Add(MakePropRow("Rotation",  _kfRot));
        kfProps.Children.Add(MakePropRow("Fill Color (Rect)", kfFillRow));
        foreach (var kfBox in new[] { _kfTimeInput, _kfX, _kfY, _kfW, _kfH, _kfOpacity, _kfScaleX, _kfScaleY, _kfRot })
            kfBox.LostFocus += (s, e) => ApplyIfNotSuppressed(ApplyKfFields);
        foreach (var kfBox in new[] { _kfTimeInput, _kfX, _kfY, _kfW, _kfH, _kfOpacity, _kfScaleX, _kfScaleY, _kfRot })
            kfBox.TextChanged += (s, e) => ApplyIfNotSuppressed(ApplyKfFields);
        _kfFillColor.TextChanged += (s, e) => ApplyIfNotSuppressed(ApplyKfFields);

        _kfPanel = BuildSection("Keyframes", new UIElement[] { _kfList, kfBtns, kfEasingRow, kfProps });
        sp.Children.Add(_kfPanel);

        var scroll = new ScrollViewer
        {
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            Content = sp,
        };
        return scroll;
    }

    private UIElement BuildTimelineContent()
    {
        _tlSlider = new Slider
        {
            Minimum = 0,
            Maximum = _vm.Duration > 0 ? _vm.Duration : 5,
            SmallChange = 0.1,
            LargeChange = 1.0,
            StepFrequency = 0.05,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(4, 0, 4, 0),
        };
        _tlSlider.ValueChanged += (s, e) =>
        {
            if (_suppressSlider) return;
            _suppressSlider = true;
            _vm.SetPreviewTime((float)_tlSlider.Value);
            _suppressSlider = false;
        };
        _tlSlider.PointerReleased += (s, e) => UpdatePropertiesPanel();

        _tlTimeLabel = new TextBlock
        {
            Text = "0.00s",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0),
            MinWidth = 50,
            FontSize = 12,
        };

        var sliderRowGrid = new Grid();
        sliderRowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sliderRowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_tlTimeLabel, 0);
        Grid.SetColumn(_tlSlider, 1);
        sliderRowGrid.Children.Add(_tlTimeLabel);
        sliderRowGrid.Children.Add(_tlSlider);

        _tlRulerCanvas = new Canvas
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 37, 37, 37)),
            Height = 20,
        };
        _tlRulerCanvas.PointerPressed += TlCanvas_PointerPressed;
        _tlRulerScroll = new ScrollViewer
        {
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollMode = ScrollMode.Disabled,      // scrolled programmatically in sync with the track
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = _tlRulerCanvas,
            Height = 20,
        };

        _tlHeaderCanvas = new Canvas
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 14, 14, 28)),
        };

        _tlCanvas = new Canvas
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 13, 13, 26)),
            AllowDrop = true,
        };
        _tlCanvas.PointerPressed  += TlCanvas_PointerPressed;
        _tlCanvas.PointerMoved    += TlCanvas_PointerMoved;
        _tlCanvas.PointerReleased += TlCanvas_PointerReleased;
        _tlCanvas.DragOver += TlCanvas_DragOver;
        _tlCanvas.Drop += TlCanvas_Drop;
        _tlCanvas.DragLeave += TlCanvas_DragLeave;

        _tlHeaderScroll = new ScrollViewer
        {
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = _tlHeaderCanvas,
        };

        _tlTrackScroll = new ScrollViewer
        {
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _tlCanvas,
        };
        _tlTrackScroll.ViewChanged += (_, _) =>
        {
            _tlHeaderScroll.ChangeView(null, _tlTrackScroll.VerticalOffset, null, true);
            _tlRulerScroll.ChangeView(_tlTrackScroll.HorizontalOffset, null, null, true);
        };
        // Ctrl + mouse wheel = zoom the timeline horizontally around the cursor.
        _tlCanvas.PointerWheelChanged += TimelineZoomWheel;
        _tlRulerCanvas.PointerWheelChanged += TimelineZoomWheel;

        var timelineGrid = new Grid();
        timelineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        timelineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        timelineGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
        timelineGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var corner = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 14, 14, 28)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 42, 42, 74)),
            BorderThickness = new Thickness(0, 0, 1, 1),
        };

        Grid.SetColumn(corner, 0);
        Grid.SetRow(corner, 0);
        Grid.SetColumn(_tlRulerScroll, 1);
        Grid.SetRow(_tlRulerScroll, 0);
        Grid.SetColumn(_tlHeaderScroll, 0);
        Grid.SetRow(_tlHeaderScroll, 1);
        Grid.SetColumn(_tlTrackScroll, 1);
        Grid.SetRow(_tlTrackScroll, 1);
        timelineGrid.Children.Add(corner);
        timelineGrid.Children.Add(_tlRulerScroll);
        timelineGrid.Children.Add(_tlHeaderScroll);
        timelineGrid.Children.Add(_tlTrackScroll);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(sliderRowGrid, 0);
        Grid.SetRow(timelineGrid, 1);
        root.Children.Add(sliderRowGrid);
        root.Children.Add(timelineGrid);
        root.SizeChanged += (s, e) => DrawTimeline();
        return root;
    }

    private UIElement BuildTransitionsContent()
    {
        var instruction = new TextBlock
        {
            Text = "Drag a transition tile onto a text splice in the timeline.",
            FontSize = 11,
            Margin = new Thickness(10, 8, 10, 6),
            Opacity = 0.78,
        };
        var palette = BuildTransitionPaletteHost();
        Grid.SetRow(palette, 1);

        var grid = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
            },
        };
        grid.Children.Add(instruction);
        grid.Children.Add(palette);
        return grid;
    }

    private FrameworkElement BuildTransitionPaletteHost()
    {
        var tiles = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 8,
            Margin = new Thickness(8, 4, 8, 8),
        };

        foreach (var type in _transitionTypes)
            tiles.Children.Add(BuildTransitionTile(type));

        return new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = tiles,
        };
    }

    private FrameworkElement BuildTransitionTile(TextTransitionType type)
    {
        var preview = new Canvas
        {
            Width = 100,
            Height = 42,
            Background = new SolidColorBrush(Color.FromArgb(255, 24, 24, 38)),
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, 100, 42) },
        };
        _transitionTilePreviews[type] = preview;
        RenderTransitionTilePreview(type, 1f);

        var label = new TextBlock
        {
            Text = AlertEditorViewModel.TransitionDisplayName(type),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var content = new StackPanel
        {
            Spacing = 6,
            IsHitTestVisible = false,
            Children =
            {
                preview,
                label
            }
        };

        var tile = new Border
        {
            Width = 150,
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 72, 72, 104)),
            Background = new SolidColorBrush(Color.FromArgb(255, 18, 18, 30)),
            CanDrag = true,
            Margin = new Thickness(0, 0, 0, 2),
            Child = content
        };

        WireTransitionTileDrag(tile, type);

        return tile;
    }

    private void WireTransitionTileDrag(Border element, TextTransitionType type)
    {
        element.CanDrag = true;
        element.PointerEntered += (_, _) => StartTransitionTilePreview(type);
        element.PointerMoved += (_, e) => UpdateTransitionTilePreview(type, e.GetCurrentPoint(element).Position.X, element.ActualWidth);
        element.PointerExited += (_, _) => StopTransitionTilePreview(type);
        element.DragStarting += (_, e) =>
        {
            _draggingTransitionType = type;
            e.Data.SetText(type.ToString());
            e.AllowedOperations = DataPackageOperation.Copy;
        };
        element.DropCompleted += (_, _) =>
        {
            _draggingTransitionType = null;
            ClearTimelineDropTarget();
        };
    }

    private void StartTransitionTilePreview(TextTransitionType type)
    {
        _hoverPreviewTransition = type;
        _transitionPreviewTime = 0;
        EnsureTransitionPreviewTimer();
        RenderTransitionTilePreview(type, 0f);
    }

    private void StopTransitionTilePreview(TextTransitionType type)
    {
        if (_hoverPreviewTransition == type)
            _hoverPreviewTransition = null;
        RenderTransitionTilePreview(type, 1f);
        if (_hoverPreviewTransition == null)
            StopTransitionPreviewTimer();
    }

    private void UpdateTransitionTilePreview(TextTransitionType type, double pointerX, double width)
    {
        if (width <= 1) return;
        float frac = (float)Math.Clamp(pointerX / width, 0.0, 1.0);
        _transitionPreviewTime = frac;
        RenderTransitionTilePreview(type, frac);
    }

    private void EnsureTransitionPreviewTimer()
    {
        if (_transitionPreviewTimer != null) return;
        _transitionPreviewTimer = DispatcherQueue.CreateTimer();
        _transitionPreviewTimer.Interval = TimeSpan.FromMilliseconds(33);
        _transitionPreviewTimer.Tick += (_, _) =>
        {
            if (_hoverPreviewTransition is not TextTransitionType type)
            {
                StopTransitionPreviewTimer();
                return;
            }

            _transitionPreviewTime = (_transitionPreviewTime + 0.05) % 1.0;
            RenderTransitionTilePreview(type, (float)_transitionPreviewTime);
        };
        _transitionPreviewTimer.Start();
    }

    private void StopTransitionPreviewTimer()
    {
        if (_transitionPreviewTimer == null) return;
        _transitionPreviewTimer.Stop();
        _transitionPreviewTimer = null;
    }

    private void RenderTransitionTilePreview(TextTransitionType type, float frac)
    {
        if (!_transitionTilePreviews.TryGetValue(type, out var canvas)) return;

        canvas.Children.Clear();
        var previewElement = CreateTransitionPreviewElement(type);

        if (type == TextTransitionType.Cut)
        {
            var spans = frac < 0.5f ? previewElement.Spans : previewElement.Keyframes[1].Spans!;
            canvas.Children.Add(MakeSpanGrid(previewElement, spans, null));
            return;
        }

        var trans = _vm.EvalTextTransitionState(previewElement, frac);
        if (!trans.InTransition)
        {
            canvas.Children.Add(MakeSpanGrid(previewElement, _vm.EvalSpansAt(previewElement, frac), null));
            return;
        }

        var fromGrid = MakeSpanGrid(previewElement, trans.FromSpans, null);
        var toGrid = MakeSpanGrid(previewElement, trans.ToSpans, null);

        switch (type)
        {
            case TextTransitionType.Fade:
            case TextTransitionType.Morph:
                fromGrid.Opacity = 1.0 - trans.Frac;
                toGrid.Opacity = trans.Frac;
                canvas.Children.Add(fromGrid);
                canvas.Children.Add(toGrid);
                break;

            case TextTransitionType.SlideLeft:
                fromGrid.RenderTransform = new TranslateTransform { X = -trans.Frac * previewElement.Width };
                toGrid.RenderTransform = new TranslateTransform { X = (1 - trans.Frac) * previewElement.Width };
                canvas.Children.Add(fromGrid);
                canvas.Children.Add(toGrid);
                break;

            case TextTransitionType.SlideRight:
                fromGrid.RenderTransform = new TranslateTransform { X = trans.Frac * previewElement.Width };
                toGrid.RenderTransform = new TranslateTransform { X = -(1 - trans.Frac) * previewElement.Width };
                canvas.Children.Add(fromGrid);
                canvas.Children.Add(toGrid);
                break;
        }
    }

    private static AlertElement CreateTransitionPreviewElement(TextTransitionType type)
    {
        var from = new List<TextSpan>
        {
            new() { Text = "ALERT", FontFamily = "Segoe UI", FontSize = 16, Bold = true, Color = "#FFFFFFFF" }
        };
        var to = new List<TextSpan>
        {
            new() { Text = "THANKS!", FontFamily = "Segoe UI", FontSize = 16, Bold = true, Color = "#FFFFC857" }
        };

        return new AlertElement
        {
            Type = AlertElementType.Text,
            Width = 100,
            Height = 42,
            Align = AlertTextAlign.Center,
            VertAlign = 1,
            Spans = from,
            Keyframes = new()
            {
                new AlertKeyframe { Time = 0f, Spans = from },
                new AlertKeyframe { Time = 1f, Spans = to, SpanTransition = type == TextTransitionType.Cut ? null : type }
            }
        };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // DOCK MANAGER SETUP
    // ═══════════════════════════════════════════════════════════════════════════

    private void SetupDockManager()
    {
        DockHost.Children.Clear();

        var layersDocument = new Document
        {
            Title = "Layers",
            CanClose = false,
            CanPin = true,
            Content = _layersContent,
        };

        var canvasDocument = new Document
        {
            Title = "Canvas",
            CanClose = false,
            Content = _canvasContent,
        };

        var propertiesDocument = new Document
        {
            Title = "Properties",
            CanClose = false,
            CanPin = true,
            Content = _propsContent,
        };

        var timelineDocument = new Document
        {
            Title = "Bottom##Timeline",
            CanClose = false,
            CanPin = true,
            Content = _timelineContent,
        };

        var transitionsDocument = new Document
        {
            Title = "Transitions",
            CanClose = false,
            CanPin = true,
            Content = _transitionsContent,
        };

        var topRow = new LayoutPanel
        {
            Orientation = Orientation.Horizontal,
        };

        topRow.Children.Add(new DocumentGroup
        {
            Width = 220,
            CompactTabs = true,
            Children = { layersDocument },
        });
        topRow.Children.Add(new DocumentGroup
        {
            ShowWhenEmpty = true,
            Children = { canvasDocument },
        });
        topRow.Children.Add(new DocumentGroup
        {
            Width = 320,
            CompactTabs = true,
            Children = { propertiesDocument },
        });

        var bottomRow = new LayoutPanel
        {
            Height = 220,
            Orientation = Orientation.Horizontal,
        };
        bottomRow.Children.Add(new DocumentGroup
        {
            Width = 190,
            CompactTabs = true,
            Children = { transitionsDocument },
        });
        bottomRow.Children.Add(new DocumentGroup
        {
            TabPosition = TabPosition.Bottom,
            Children = { timelineDocument },
        });

        var root = new LayoutPanel
        {
            Orientation = Orientation.Vertical,
        };
        root.Children.Add(topRow);
        root.Children.Add(bottomRow);

        _dockManager = new DockManager
        {
            Adapter = this,
            Behavior = this,
            Panel = root,
        };

        DockHost.Children.Add(_dockManager);
    }

    void IDockAdapter.OnCreated(Document document)
    {
        if (document.Content != null)
        {
            return;
        }

        document.Content = new TextBlock
        {
            Text = document.ActualTitle,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    void IDockAdapter.OnCreated(DocumentGroup group, Document? draggedDocument)
    {
        if (draggedDocument?.Title.Contains("Bottom##", StringComparison.Ordinal) is true)
        {
            group.TabPosition = TabPosition.Bottom;
        }
    }

    object? IDockAdapter.GetFloatingWindowTitleBar(Document? draggedDocument)
        => new TextBlock
        {
            IsHitTestVisible = false,
            Text = draggedDocument?.ActualTitle ?? "Alert Layout Editor",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };

    void IDockBehavior.ActivateMainWindow() => Activate();

    void IDockBehavior.OnDocked(Document src, DockManager dest, DockTarget target)
        => Log($"Docked '{src.ActualTitle}' to DockManager target={target}");

    void IDockBehavior.OnDocked(Document src, DocumentGroup dest, DockTarget target)
        => Log($"Docked '{src.ActualTitle}' to DocumentGroup target={target}");

    void IDockBehavior.OnFloating(Document document)
        => Log($"Floating '{document.ActualTitle}'");

    // ═══════════════════════════════════════════════════════════════════════════
    // CANVAS RENDERING
    // ═══════════════════════════════════════════════════════════════════════════

    private void RebuildCanvas()
    {
        _innerCanvas.Width  = _vm.Layout.Width;
        _innerCanvas.Height = _vm.Layout.Height;

        // Remove all element controls (keep handles and selection border)
        var toRemove = _innerCanvas.Children
            .OfType<FrameworkElement>()
            .Where(e => e.Tag is string)
            .ToList();
        foreach (var c in toRemove) _innerCanvas.Children.Remove(c);
        _elemControls.Clear();
        _textRenderCache.Clear();
        _gifLastFrameIndex.Clear();
        // New controls must re-fetch their frame; cached compositions are kept (reused, not reopened).
        ResetVideoFrameState();

        foreach (var el in _vm.Layout.Elements.OrderBy(e => e.ZOrder))
        {
            var ctrl = CreateElementControl(el);
            if (ctrl == null) continue;
            ctrl.Tag = el.Id;
            ctrl.IsHitTestVisible = !el.Hidden && !el.Locked;
            ctrl.PointerPressed += ElemCtrl_PointerPressed;
            ctrl.PointerMoved += Canvas_PointerMoved;
            ctrl.PointerReleased += Canvas_PointerReleased;
            ctrl.PointerCaptureLost += (_, _) =>
            {
                if (_dragMode == DragMode.Move)
                    EndDrag();
            };
            _elemControls[el.Id] = ctrl;
            Canvas.SetLeft(ctrl, el.X);
            Canvas.SetTop(ctrl, el.Y);
            Canvas.SetZIndex(ctrl, el.ZOrder);
            _innerCanvas.Children.Add(ctrl);
        }

        UpdatePreviewState();
        UpdateSelectionOverlay();
    }

    private FrameworkElement? CreateElementControl(AlertElement el)
    {
        switch (el.Type)
        {
            case AlertElementType.Rect:
            case AlertElementType.GoalBar:
            {
                var border = new Border
                {
                    Width  = Math.Abs(el.Width),
                    Height = Math.Abs(el.Height),
                    CornerRadius = new CornerRadius(Math.Min(el.CornerRadius, 999)),
                };
                if (!string.IsNullOrWhiteSpace(el.FillColor) && TryParseColor(el.FillColor, out var c))
                    border.Background = new SolidColorBrush(c);
                else
                    border.Background = new SolidColorBrush(Color.FromArgb(200, 33, 150, 243));
                return border;
            }

            case AlertElementType.Text:
                return MakeTextControl(el);

            case AlertElementType.Image:
                return MakeImageControl(el);

            case AlertElementType.Gif:
                return MakeGifControl(el);

            case AlertElementType.Video:
                return MakeVideoControl(el);

            case AlertElementType.Audio:
            {
                // Represent audio as a semi-transparent border with icon
                return new Border
                {
                    Width = Math.Max(40, el.Width),
                    Height = Math.Max(20, el.Height),
                    Background = new SolidColorBrush(Color.FromArgb(120, 100, 200, 100)),
                    CornerRadius = new CornerRadius(4),
                    Child = new TextBlock
                    {
                        Text = "♪ " + System.IO.Path.GetFileName(el.FilePath ?? "audio"),
                        Foreground = new SolidColorBrush(Colors.White),
                        FontSize = 11,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(4, 0, 4, 0),
                    },
                };
            }
        }
        return null;
    }

    private Canvas MakeTextControl(AlertElement el)
    {
        double aw = Math.Abs(el.Width), ah = Math.Abs(el.Height);
        var canvas = new Canvas
        {
            Width  = aw,
            Height = ah,
            Background = new SolidColorBrush(Colors.Transparent),
            Clip   = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, aw, ah) },
        };
        var grid = new Grid
        {
            Width = aw,
            Height = ah,
            IsHitTestVisible = false,
        };
        RebuildTextGrid(grid, el);
        canvas.Children.Add(grid);
        _textRenderCache[el.Id] = new TextRenderCacheEntry
        {
            IsDualPass = false,
            SingleGrid = grid,
            SingleSignature = BuildTextRenderSignature(el, _vm.EvalSpansAt(el, _vm.PreviewTime), null),
        };
        return canvas;
    }

    private void RebuildTextGrid(Grid grid, AlertElement el, AlertEditorViewModel.AnimState? state = null)
        => RebuildTextGridFromSpans(grid, el, _vm.EvalSpansAt(el, _vm.PreviewTime), state);

    private TextBlock MakeLayerTextBlock(AlertElement el, VerticalAlignment vertAlign, TextAlignment textAlign, Brush? forceBrush)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None,
            TextAlignment = textAlign,
            VerticalAlignment = vertAlign,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        foreach (var run in BuildRuns(el, forceBrush))
            tb.Inlines.Add(run);
        return tb;
    }

    private void RebuildTextTransitionCanvas(Canvas canvas, AlertElement el, float t, AlertEditorViewModel.AnimState? st)
    {
        if (!_textRenderCache.TryGetValue(el.Id, out var cache))
        {
            cache = new TextRenderCacheEntry();
            _textRenderCache[el.Id] = cache;
        }

        var trans = _vm.EvalTextTransitionState(el, t);
        if (!trans.InTransition)
        {
            // Single pass: EvalSpansAt handles TypeOn and Cut. Keep the grid and only rebuild when
            // the rendered span/alignment/shadow signature actually changes.
            var spans = _vm.EvalSpansAt(el, t);
            var signature = BuildTextRenderSignature(el, spans, st);

            if (cache.IsDualPass)
            {
                canvas.Children.Clear();
                cache.IsDualPass = false;
                cache.SingleGrid = null;
                cache.FromGrid = null;
                cache.ToGrid = null;
            }

            if (cache.SingleGrid == null)
            {
                cache.SingleGrid = MakeSpanGrid(el, spans, st);
                cache.SingleSignature = signature;
                canvas.Children.Clear();
                canvas.Children.Add(cache.SingleGrid);
            }
            else
            {
                SyncTextGridSize(cache.SingleGrid, el, st);
                if (!string.Equals(cache.SingleSignature, signature, StringComparison.Ordinal))
                {
                    RebuildTextGridFromSpans(cache.SingleGrid, el, spans, st);
                    cache.SingleSignature = signature;
                }
            }

            if (canvas.Children.Count != 1 || !ReferenceEquals(canvas.Children[0], cache.SingleGrid))
            {
                canvas.Children.Clear();
                canvas.Children.Add(cache.SingleGrid);
            }

            return;
        }

        // Dual pass (Fade / SlideLeft / SlideRight / Morph). The text content is static across the
        // transition; only opacity/translate changes every frame, so reuse the same two grids.
        double w = AnimatedTextWidth(el, st), h = AnimatedTextHeight(el, st);
        var fromSignature = BuildTextRenderSignature(el, trans.FromSpans, st);
        var toSignature = BuildTextRenderSignature(el, trans.ToSpans, st);

        if (!cache.IsDualPass || cache.TransitionType != trans.Type ||
            cache.FromGrid == null || cache.ToGrid == null)
        {
            cache.IsDualPass = true;
            cache.TransitionType = trans.Type;
            cache.FromGrid = MakeSpanGrid(el, trans.FromSpans, st);
            cache.ToGrid = MakeSpanGrid(el, trans.ToSpans, st);
            cache.FromSignature = fromSignature;
            cache.ToSignature = toSignature;
            canvas.Children.Clear();
            canvas.Children.Add(cache.FromGrid);
            canvas.Children.Add(cache.ToGrid);
        }
        else
        {
            SyncTextGridSize(cache.FromGrid, el, st);
            SyncTextGridSize(cache.ToGrid, el, st);
            if (!string.Equals(cache.FromSignature, fromSignature, StringComparison.Ordinal))
            {
                RebuildTextGridFromSpans(cache.FromGrid, el, trans.FromSpans, st);
                cache.FromSignature = fromSignature;
            }

            if (!string.Equals(cache.ToSignature, toSignature, StringComparison.Ordinal))
            {
                RebuildTextGridFromSpans(cache.ToGrid, el, trans.ToSpans, st);
                cache.ToSignature = toSignature;
            }
        }

        var fromGrid = cache.FromGrid;
        var toGrid = cache.ToGrid;
        float frac = trans.Frac;
        canvas.Children.Clear();
        switch (trans.Type)
        {
            case TextTransitionType.Fade:
            case TextTransitionType.Morph: // approximate Morph as Fade in C# preview
                fromGrid.Opacity = 1.0 - frac;
                toGrid.Opacity   = frac;
                canvas.Children.Add(fromGrid);
                canvas.Children.Add(toGrid);
                break;
            case TextTransitionType.SlideLeft:
                fromGrid.RenderTransform = new TranslateTransform { X = -frac * w };
                toGrid.RenderTransform   = new TranslateTransform { X = (1 - frac) * w };
                canvas.Children.Add(fromGrid);
                canvas.Children.Add(toGrid);
                break;
            case TextTransitionType.SlideRight:
                fromGrid.RenderTransform = new TranslateTransform { X = frac * w };
                toGrid.RenderTransform   = new TranslateTransform { X = -(1 - frac) * w };
                canvas.Children.Add(fromGrid);
                canvas.Children.Add(toGrid);
                break;
        }
    }

    private static void SyncTextGridSize(Grid grid, AlertElement el, AlertEditorViewModel.AnimState? state)
    {
        grid.Width = AnimatedTextWidth(el, state);
        grid.Height = AnimatedTextHeight(el, state);
    }

    private string BuildTextRenderSignature(
        AlertElement el,
        IList<Steaming.Core.Models.TextSpan> spans,
        AlertEditorViewModel.AnimState? state)
    {
        var sb = new StringBuilder();
        sb.Append(state?.vertAlign ?? el.VertAlign).Append('|');
        sb.Append(state?.align ?? (int)el.Align).Append('|');
        sb.Append(state?.shadowOffX ?? int.MinValue).Append('|');
        sb.Append(state?.shadowOffY ?? int.MinValue).Append('|');
        sb.Append(spans.Count).Append('|');
        foreach (var span in spans)
        {
            sb.Append(span.Text).Append('\u001f');
            sb.Append(span.Color).Append('\u001f');
            sb.Append(span.FontFamily).Append('\u001f');
            sb.Append(span.FontSize).Append('\u001f');
            sb.Append(span.Bold ? '1' : '0').Append(span.Italic ? '1' : '0').Append('|');
        }

        return sb.ToString();
    }

    private Grid MakeSpanGrid(AlertElement el, IList<Steaming.Core.Models.TextSpan> spans, AlertEditorViewModel.AnimState? state)
    {
        var grid = new Grid
        {
            Width = AnimatedTextWidth(el, state),
            Height = AnimatedTextHeight(el, state),
            IsHitTestVisible = false,
        };
        RebuildTextGridFromSpans(grid, el, spans, state);
        return grid;
    }

    private static double AnimatedTextWidth(AlertElement el, AlertEditorViewModel.AnimState? state)
        => Math.Max(1, Math.Abs((state?.w ?? el.Width) * (state?.scaleX ?? 1f)));

    private static double AnimatedTextHeight(AlertElement el, AlertEditorViewModel.AnimState? state)
        => Math.Max(1, Math.Abs((state?.h ?? el.Height) * (state?.scaleY ?? 1f)));

    private void RebuildTextGridFromSpans(Grid grid, AlertElement el,
        IList<Steaming.Core.Models.TextSpan> spans, AlertEditorViewModel.AnimState? state)
    {
        grid.Children.Clear();
        int evalVA = state?.vertAlign ?? el.VertAlign;
        int evalHA = state?.align     ?? (int)el.Align;
        var vertAlign = evalVA == 0 ? VerticalAlignment.Top
            : evalVA == 2 ? VerticalAlignment.Bottom
            : VerticalAlignment.Center;
        var textAlign = evalHA == 1 ? TextAlignment.Center
            : evalHA == 2 ? TextAlignment.Right
            : TextAlignment.Left;

        if (el.Shadow)
        {
            int offsetX = state?.shadowOffX ?? (int)Math.Round(Math.Cos(el.ShadowAngle * Math.PI / 180.0) * el.ShadowDistance);
            int offsetY = state?.shadowOffY ?? (int)Math.Round(Math.Sin(el.ShadowAngle * Math.PI / 180.0) * el.ShadowDistance);
            var shadow = MakeLayerTextBlockFromSpans(spans, vertAlign, textAlign, ParseBrushOrFallback(el.ShadowColor, Colors.Black));
            shadow.Margin = new Thickness(offsetX, offsetY, -offsetX, -offsetY);
            grid.Children.Add(shadow);
        }
        if (el.Outline && el.OutlineWidth > 0)
        {
            var outlineBrush = ParseBrushOrFallback(el.OutlineColor, Colors.Black);
            int ow = Math.Min(el.OutlineWidth, 3);
            for (int dy = -ow; dy <= ow; dy++)
                for (int dx = -ow; dx <= ow; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var outline = MakeLayerTextBlockFromSpans(spans, vertAlign, textAlign, outlineBrush);
                    outline.Margin = new Thickness(dx, dy, -dx, -dy);
                    grid.Children.Add(outline);
                }
        }
        grid.Children.Add(MakeLayerTextBlockFromSpans(spans, vertAlign, textAlign, null));
    }

    private TextBlock MakeLayerTextBlockFromSpans(IList<Steaming.Core.Models.TextSpan> spans,
        VerticalAlignment vertAlign, TextAlignment textAlign, Brush? forceBrush)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None,
            TextAlignment = textAlign,
            VerticalAlignment = vertAlign,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        foreach (var span in spans)
        {
            string preview = span.Text
                .Replace("{user}", _vm.PreviewUser)
                .Replace("{message}", _vm.PreviewMessage)
                .Replace("{amount}", _vm.PreviewAmount)
                .Replace("{months}", _vm.PreviewAmount)
                .Replace("{target}", _vm.PreviewTarget)
                .Replace("{reward}", _vm.PreviewReward)
                .Replace("{input}", _vm.PreviewInput);
            tb.Inlines.Add(new Run
            {
                Text = preview,
                Foreground = forceBrush ?? ParseBrushOrFallback(span.Color, Colors.White),
                FontFamily = SafeFontFamily(span.FontFamily),
                FontSize = Math.Max(8, span.FontSize),
                FontWeight = span.Bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                FontStyle = span.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            });
        }
        return tb;
    }

    private IEnumerable<Run> BuildRuns(AlertElement el, Brush? forceBrush)
    {
        foreach (var span in _vm.EvalSpansAt(el, _vm.PreviewTime))
        {
            string preview = span.Text
                .Replace("{user}", _vm.PreviewUser)
                .Replace("{message}", _vm.PreviewMessage)
                .Replace("{amount}", _vm.PreviewAmount)
                .Replace("{months}", _vm.PreviewAmount)
                .Replace("{target}", _vm.PreviewTarget)
                .Replace("{reward}", _vm.PreviewReward)
                .Replace("{input}", _vm.PreviewInput);

            yield return new Run
            {
                Text = preview,
                Foreground = forceBrush ?? ParseBrushOrFallback(span.Color, Colors.White),
                FontFamily = SafeFontFamily(span.FontFamily),
                FontSize = Math.Max(8, span.FontSize),
                FontWeight = span.Bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                FontStyle = span.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            };
        }
    }

    private FrameworkElement MakeImageControl(AlertElement el)
    {
        var img = new Microsoft.UI.Xaml.Controls.Image
        {
            Width = Math.Abs(el.Width),
            Height = Math.Abs(el.Height),
            Stretch = Stretch.Fill,
        };
        if (!string.IsNullOrWhiteSpace(el.FilePath) && System.IO.File.Exists(el.FilePath))
        {
            try { img.Source = new BitmapImage(new Uri(el.FilePath)); } catch { }
        }
        return img;
    }

    private FrameworkElement MakeGifControl(AlertElement el)
    {
        var img = new Microsoft.UI.Xaml.Controls.Image
        {
            Width = Math.Abs(el.Width),
            Height = Math.Abs(el.Height),
            Stretch = Stretch.Fill,
        };
        if (!string.IsNullOrWhiteSpace(el.FilePath) && System.IO.File.Exists(el.FilePath))
        {
            try
            {
                if (TryGetCachedGif(el.FilePath, out var gif))
                {
                    img.Source = gif.Frames[0];
                }
                else
                {
                    QueueGifLoad(el.FilePath);
                }
            }
            catch { }
        }
        return img;
    }

    // WYSIWYG video preview: a MediaPlayerElement positioned/animated by the same keyframe code as
    // every other element. The MediaPlayer is muted in the editor (we only want to *see* it) and is
    // fetched on demand per scrub position (no live media pipeline).
    private FrameworkElement MakeVideoControl(AlertElement el)
    {
        // A video element is just an Image showing the decoded frame at the current timeline position
        // (like images/GIFs — drags fine, no live media pipeline). The frame is fetched on demand.
        var img = new Microsoft.UI.Xaml.Controls.Image
        {
            Width  = Math.Abs(el.Width),
            Height = Math.Abs(el.Height),
            Stretch = Stretch.Fill,
        };
        if (!string.IsNullOrWhiteSpace(el.FilePath) && System.IO.File.Exists(el.FilePath))
        {
            float start = el.Keyframes.Count > 0 ? el.Keyframes.Min(k => k.Time) : 0f;
            RequestVideoFrame(el.Id, el.FilePath, Math.Max(0.0, _vm.PreviewTime - start));
        }
        return img;
    }

    // Request the frame at clip time t for an element. Never throttles playback: it decodes every
    // distinct requested time at full rate, only skipping a re-decode of the *identical* frame (so a
    // static timeline never re-decodes). Coalesces while a decode is in flight.
    private void RequestVideoFrame(string elId, string path, double t)
    {
        if (_videoDecoding.Contains(elId)) { _videoPendingTime[elId] = t; return; }
        if (_videoLastFrameTime.TryGetValue(elId, out var last) && last == t) return;
        _ = DecodeVideoLoopAsync(elId, path, t);
    }

    private async Task DecodeVideoLoopAsync(string elId, string path, double t)
    {
        _videoDecoding.Add(elId);
        try
        {
            while (true)
            {
                _videoLastFrameTime[elId] = t;
                var src = await DecodeVideoFrameAsync(elId, path, t);
                if (_isClosed) return;
                if (src != null && _elemControls.TryGetValue(elId, out var ctrl)
                    && ctrl is Microsoft.UI.Xaml.Controls.Image img)
                    img.Source = src;

                if (_videoPendingTime.TryGetValue(elId, out var pend))
                {
                    _videoPendingTime.Remove(elId);
                    if (pend != t) { t = pend; continue; }
                }
                break;
            }
        }
        catch { }
        finally { _videoDecoding.Remove(elId); }
    }

    private async Task<ImageSource?> DecodeVideoFrameAsync(string elId, string path, double t)
    {
        var comp = await GetVideoCompositionAsync(path);
        if (comp == null || _isClosed) return null;
        double durSec = comp.Duration.TotalSeconds;
        double clamped = durSec > 0 ? Math.Clamp(t, 0, Math.Max(0, durSec - 0.05)) : Math.Max(0, t);
        var stream = await comp.GetThumbnailAsync(
            TimeSpan.FromSeconds(clamped), 0, 0, Windows.Media.Editing.VideoFramePrecision.NearestFrame);
        if (stream == null || _isClosed) return null;
        var bmp = new BitmapImage();
        await bmp.SetSourceAsync(stream);

        // Fit to the clip's true aspect exactly once (resize the control in place — no full rebuild).
        if (_videoAspectFitted.Add(elId) && bmp.PixelWidth > 0 && bmp.PixelHeight > 0)
        {
            var target = _vm.Layout.Elements.FirstOrDefault(x => x.Id == elId);
            if (target != null)
            {
                FitVideoAspect(target, bmp.PixelWidth, bmp.PixelHeight);
                if (_elemControls.TryGetValue(elId, out var c))
                {
                    c.Width = Math.Abs(target.Width);
                    c.Height = Math.Abs(target.Height);
                }
                UpdateSelectionOverlay();
            }
        }
        return bmp;
    }

    private async Task<Windows.Media.Editing.MediaComposition?> GetVideoCompositionAsync(string path)
    {
        if (_videoCompositions.TryGetValue(path, out var c)) return c;
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(file);
            var comp = new Windows.Media.Editing.MediaComposition();
            comp.Clips.Add(clip);
            _videoCompositions[path] = comp;
            return comp;
        }
        catch { return null; }
    }

    // Forget which frame each control is showing so freshly-rebuilt controls re-fetch their frame.
    private void ResetVideoFrameState()
    {
        _videoLastFrameTime.Clear();
        _videoPendingTime.Clear();
        _gifLastFrameIndex.Clear();
    }

    // Full teardown (window close): also drop the cached compositions.
    private void ClearVideoCaches()
    {
        ResetVideoFrameState();
        _videoCompositions.Clear();
    }

    private bool TryGetCachedGif(string path, out GifFrameCacheEntry entry)
    {
        if (_gifCache.TryGetValue(path, out entry!))
        {
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path).Ticks == entry.LastWriteTicks)
                return true;

            _gifCache.Remove(path);
        }

        entry = null!;
        return false;
    }

    private void QueueGifLoad(string path)
    {
        if (_isClosed || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        if (TryGetCachedGif(path, out _) || _gifLoadTasks.ContainsKey(path))
            return;

        var lastWriteTicks = File.GetLastWriteTimeUtc(path).Ticks;
        var loadTask = LoadGifFramesAsync(path, lastWriteTicks);
        _gifLoadTasks[path] = loadTask;
    }

    private async Task LoadGifFramesAsync(string path, long lastWriteTicks)
    {
        try
        {
            var decoded = await Task.Run(() => DecodeGifFrames(path));
            if (decoded.Frames.Length == 0)
                return;

            if (_isClosed)
                return;

            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (_isClosed)
                        return;

                    if (!File.Exists(path) || File.GetLastWriteTimeUtc(path).Ticks != lastWriteTicks)
                    {
                        _gifCache.Remove(path);
                        return;
                    }

                    // WriteableBitmap is a XAML object with UI-thread affinity — it must be
                    // constructed here, never on the decode worker thread (COMException otherwise).
                    var frames = new WriteableBitmap[decoded.Frames.Length];
                    for (int i = 0; i < decoded.Frames.Length; i++)
                    {
                        var raw = decoded.Frames[i];
                        var wb = new WriteableBitmap(raw.Width, raw.Height);
                        using (var pixelStream = wb.PixelBuffer.AsStream())
                            pixelStream.Write(raw.Pixels, 0, raw.Pixels.Length);
                        wb.Invalidate();
                        frames[i] = wb;
                    }

                    _gifCache[path] = new GifFrameCacheEntry(frames, decoded.CumSec, decoded.TotalSec, lastWriteTicks);
                    ApplyLoadedGif(path);
                }
                catch { }
            });
        }
        catch { }
        finally
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _gifLoadTasks.Remove(path);
            });
        }
    }

    private void ApplyLoadedGif(string path)
    {
        foreach (var el in _vm.Layout.Elements.Where(e =>
                     e.Type == AlertElementType.Gif &&
                     string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase)))
        {
            if (_elemControls.TryGetValue(el.Id, out var ctrl) && ctrl is Microsoft.UI.Xaml.Controls.Image gifImage)
            {
                if (TryGetCachedGif(path, out var gif) && gif.Frames.Length > 0)
                    gifImage.Source = gif.Frames[0];
            }
        }

        UpdatePreviewState();
    }

    private sealed record RawGifFrame(byte[] Pixels, int Width, int Height);

    // Runs on a worker thread — must not touch any XAML/WinRT UI types.
    private static (RawGifFrame[] Frames, double[] CumSec, double TotalSec) DecodeGifFrames(string path)
    {
        using var image = DrawingImage.FromFile(path);
        var frameDimension = new DrawingFrameDimension(image.FrameDimensionsList[0]);
        int frameCount = Math.Max(1, image.GetFrameCount(frameDimension));
        var frames = new RawGifFrame[frameCount];
        var cumSec = new double[frameCount];
        double totalSec = 0;
        int[] delays = GetGifFrameDelays(image, frameCount);

        for (int i = 0; i < frameCount; i++)
        {
            image.SelectActiveFrame(frameDimension, i);
            using var bitmap = new DrawingBitmap(image.Width, image.Height);
            using (var g = DrawingGraphics.FromImage(bitmap))
                g.DrawImage(image, 0, 0, image.Width, image.Height);

            frames[i] = CopyFramePixels(bitmap);
            cumSec[i] = totalSec;
            totalSec += delays[i] / 1000.0;
        }

        return (frames, cumSec, totalSec);
    }

    private static int[] GetGifFrameDelays(DrawingImage image, int frameCount)
    {
        try
        {
            var item = image.GetPropertyItem(0x5100);
            if (item?.Value == null || item.Value.Length < frameCount * 4)
                return Enumerable.Repeat(100, frameCount).ToArray();
            var delays = new int[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                int raw = BitConverter.ToInt32(item.Value, i * 4);
                delays[i] = Math.Max(10, raw * 10);
            }
            return delays;
        }
        catch
        {
            return Enumerable.Repeat(100, frameCount).ToArray();
        }
    }

    // Runs on a worker thread — converts a GDI+ frame to a raw BGRA premultiplied pixel buffer.
    // WriteableBitmap construction happens later on the UI thread (see LoadGifFramesAsync).
    private static RawGifFrame CopyFramePixels(DrawingBitmap source)
    {
        using var bitmap = new DrawingBitmap(source.Width, source.Height, DrawingPixelFormat.Format32bppPArgb);
        using (var g = DrawingGraphics.FromImage(bitmap))
        {
            g.DrawImage(source, 0, 0, source.Width, source.Height);
        }

        var rect = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, DrawingPixelFormat.Format32bppPArgb);
        try
        {
            int byteCount = Math.Abs(data.Stride) * data.Height;
            byte[] pixels = new byte[byteCount];
            Marshal.Copy(data.Scan0, pixels, 0, byteCount);
            return new RawGifFrame(pixels, bitmap.Width, bitmap.Height);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static Brush ParseBrushOrFallback(string? hex, Color fallback)
        => !string.IsNullOrWhiteSpace(hex) && TryParseColor(hex, out var parsed)
            ? new SolidColorBrush(parsed)
            : new SolidColorBrush(fallback);

    private void UpdatePreviewState()
    {
        float t = _vm.PreviewTime;
        foreach (var el in _vm.Layout.Elements)
        {
            if (!_elemControls.TryGetValue(el.Id, out var ctrl)) continue;
            ApplyElementStateToControl(el, ctrl, t, skipTextRebuild: false);
        }

        UpdateSelectionOverlay();
    }

    private void ApplyElementStateToControl(AlertElement el, FrameworkElement ctrl, float t, bool skipTextRebuild)
    {
        ctrl.IsHitTestVisible = !el.Hidden && !el.Locked;
        if (el.Hidden)
        {
            ctrl.Visibility = Visibility.Collapsed;
            return;
        }

        if (el.Keyframes.Count > 0)
        {
            float startTime = el.Keyframes.Min(k => k.Time);
            float endTime = el.Keyframes.Max(k => k.Time);
            if (t < startTime || t > endTime)
            {
                ctrl.Visibility = Visibility.Collapsed;
                return;
            }
        }

        ctrl.Visibility = Visibility.Visible;
        var st = _vm.EvalAnimated(el, t);
        double effW = st.w * st.scaleX;
        double effH = st.h * st.scaleY;
        double absW = Math.Max(1, Math.Abs(effW));
        double absH = Math.Max(1, Math.Abs(effH));
        Canvas.SetLeft(ctrl, st.x);
        Canvas.SetTop(ctrl, st.y);
        ctrl.Width = absW;
        ctrl.Height = absH;
        ctrl.Opacity = Math.Clamp(st.opacity, 0, 1);
        if (ctrl is Canvas clipCanvas && clipCanvas.Clip is RectangleGeometry rg)
            rg.Rect = new Windows.Foundation.Rect(0, 0, absW, absH);
        // Negative width/height = mirror the element in place. WinUI controls can't take a
        // negative size, so render at abs(size) and flip via a ScaleTransform, combined with rotation.
        var tg = new TransformGroup();
        tg.Children.Add(new ScaleTransform
        {
            ScaleX = effW < 0 ? -1 : 1,
            ScaleY = effH < 0 ? -1 : 1,
            CenterX = absW / 2,
            CenterY = absH / 2,
        });
        tg.Children.Add(new RotateTransform { Angle = st.rotation, CenterX = absW / 2, CenterY = absH / 2 });
        ctrl.RenderTransform = tg;

        if (el.Type == AlertElementType.Rect || el.Type == AlertElementType.GoalBar)
        {
            if (ctrl is Border b)
            {
                if (st.fillColor != null && TryParseColor(st.fillColor, out var fc))
                    b.Background = new SolidColorBrush(fc);
                b.CornerRadius = new CornerRadius(Math.Min(st.cornerRadius, 999));
            }
        }

        if (!skipTextRebuild && el.Type == AlertElementType.Text && ctrl is Canvas textCanvas)
            RebuildTextTransitionCanvas(textCanvas, el, t, st);

        if (el.Type == AlertElementType.Gif
            && ctrl is Microsoft.UI.Xaml.Controls.Image gifImage
            && !string.IsNullOrWhiteSpace(el.FilePath))
        {
            if (TryGetCachedGif(el.FilePath, out var gif) && gif.TotalSec > 0)
            {
                float elementStart = el.Keyframes.Count > 0 ? el.Keyframes.Min(k => k.Time) : 0f;
                double relativeTime = Math.Max(0.0, t - elementStart);
                double looped = relativeTime % gif.TotalSec;
                int frameIndex = gif.Frames.Length - 1;
                for (int i = 0; i < gif.CumSec.Length - 1; i++)
                {
                    if (looped < gif.CumSec[i + 1])
                    {
                        frameIndex = i;
                        break;
                    }
                }

                if (!_gifLastFrameIndex.TryGetValue(el.Id, out var lastFrameIndex) || lastFrameIndex != frameIndex)
                {
                    gifImage.Source = gif.Frames[frameIndex];
                    _gifLastFrameIndex[el.Id] = frameIndex;
                }
            }
            else
            {
                QueueGifLoad(el.FilePath);
            }
        }

        if (el.Type == AlertElementType.Video
            && ctrl is Microsoft.UI.Xaml.Controls.Image
            && !string.IsNullOrWhiteSpace(el.FilePath))
        {
            float elementStart = el.Keyframes.Count > 0 ? el.Keyframes.Min(k => k.Time) : 0f;
            double rel = Math.Max(0.0, t - elementStart);
            double dur = _videoCompositions.TryGetValue(el.FilePath, out var comp) ? comp.Duration.TotalSeconds : 0.0;

            // End behaviour: hide / fade out once the clip finishes (Loop / Hold / HoldFirst keep showing).
            if (dur > 0 && rel >= dur &&
                el.VideoEnd is VideoEndBehavior.EndHide or VideoEndBehavior.EndFade)
            {
                if (el.VideoEnd == VideoEndBehavior.EndFade)
                {
                    double k = 1.0 - (rel - dur) / 0.5;
                    if (k <= 0) { ctrl.Visibility = Visibility.Collapsed; return; }
                    ctrl.Opacity = Math.Clamp(st.opacity * k, 0, 1);
                }
                else
                {
                    ctrl.Visibility = Visibility.Collapsed;
                    return;
                }
            }

            double pos = rel;
            if (dur > 0) pos = VideoPlaybackTime(el.VideoEnd, rel, dur);
            // Decodes only if the frame time actually changed — nothing happens when idle.
            RequestVideoFrame(el.Id, el.FilePath, pos);
        }
    }

    private static int VideoEndBehaviorToComboIndex(VideoEndBehavior endBehavior) => endBehavior switch
    {
        VideoEndBehavior.Loop => 0,
        VideoEndBehavior.Hold => 1,
        VideoEndBehavior.HoldFirst => 2,
        VideoEndBehavior.EndHide => 3,
        VideoEndBehavior.EndFade => 4,
        _ => 0,
    };

    private static VideoEndBehavior VideoEndBehaviorFromComboIndex(int index) => index switch
    {
        0 => VideoEndBehavior.Loop,
        1 => VideoEndBehavior.Hold,
        2 => VideoEndBehavior.HoldFirst,
        3 => VideoEndBehavior.EndHide,
        4 => VideoEndBehavior.EndFade,
        _ => VideoEndBehavior.Loop,
    };

    private static double VideoPlaybackTime(VideoEndBehavior endBehavior, double rel, double dur)
    {
        if (dur <= 0) return rel;
        return endBehavior switch
        {
            VideoEndBehavior.Loop => rel % dur,
            VideoEndBehavior.HoldFirst => rel >= dur ? 0.0 : rel,
            _ => Math.Min(rel, dur),
        };
    }

    private void UpdateSelectionOverlay()
    {
        var el = _vm.SelectedElement;
        if (el == null || el.Hidden)
        {
            _selBorder.Visibility = Visibility.Collapsed;
            foreach (var h in _resizeHandles) h.Visibility = Visibility.Collapsed;
            _rotHandle.Visibility = Visibility.Collapsed;
            return;
        }

        var st = _vm.EvalAnimated(el, _vm.PreviewTime);
        // Mirror (negative w/h) keeps the element in place, so the selection box uses abs size.
        float x = st.x, y = st.y, w = Math.Abs(st.w * st.scaleX), elH = Math.Abs(st.h * st.scaleY);

        _selBorder.Visibility = Visibility.Visible;
        Canvas.SetLeft(_selBorder, x);
        Canvas.SetTop(_selBorder, y);
        _selBorder.Width  = w;
        _selBorder.Height = elH;

        // 8 handle positions: TL,T,TR,R,BR,B,BL,L
        (float hx, float hy)[] hpos =
        {
            (x,      y),          // 0 TL
            (x+w/2,  y),          // 1 T
            (x+w,    y),          // 2 TR
            (x+w,    y+elH/2),    // 3 R
            (x+w,    y+elH),      // 4 BR
            (x+w/2,  y+elH),      // 5 B
            (x,      y+elH),      // 6 BL
            (x,      y+elH/2),    // 7 L
        };

        const float HS = 5f; // half-size of handle (10x10 / 2)
        for (int i = 0; i < 8; i++)
        {
            _resizeHandles[i].Visibility = el.Locked ? Visibility.Collapsed : Visibility.Visible;
            Canvas.SetLeft(_resizeHandles[i], hpos[i].hx - HS);
            Canvas.SetTop(_resizeHandles[i],  hpos[i].hy - HS);
        }

        _rotHandle.Visibility = el.Locked ? Visibility.Collapsed : Visibility.Visible;
        if (!el.Locked)
        {
            Canvas.SetLeft(_rotHandle, x + w / 2 - 6);
            Canvas.SetTop(_rotHandle, y - 24);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // CANVAS POINTER EVENTS
    // ═══════════════════════════════════════════════════════════════════════════

    private void ElemCtrl_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string id) return;
        var el = _vm.Layout.Elements.FirstOrDefault(x => x.Id == id);
        if (el == null || el.Hidden || el.Locked) return;

        _vm.SetSelectedElement(el);
        UpdatePropertiesPanel();
        RefreshLayerList();
        UpdateSelectionOverlay();

        _dragMode   = DragMode.Move;
        _vm.BeginGeometryGesture(); // one undo snapshot per move gesture (taken on first actual write)
        _hasDragged = false;
        _dragStart  = e.GetCurrentPoint(_innerCanvas).Position;
        // Origin must be the DISPLAYED (keyframe-evaluated) position, not el.X/Y base values —
        // otherwise the element jumps to base+delta on the first pointer move.
        var st0 = _vm.EvalAnimated(el, _vm.PreviewTime);
        _dragOrigX = st0.x; _dragOrigY = st0.y;
        _innerCanvas.CapturePointer(e.Pointer);
        _capturedPointer = e.Pointer;
        e.Handled = true;
    }

    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        DeselectCurrentElement();
        e.Handled = true;
    }

    private void CanvasOuter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Handled) return;
        if (e.OriginalSource == sender)
        {
            DeselectCurrentElement();
            e.Handled = true;
        }
    }

    private void DeselectCurrentElement()
    {
        _vm.SetSelectedElement(null);
        UpdateSelectionOverlay();
        UpdatePropertiesPanel();
        RefreshLayerList();
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragMode != DragMode.Move) return;
        var pos = e.GetCurrentPoint(_innerCanvas).Position;
        var el = _vm.SelectedElement;
        if (el == null) return;

        float dx = (float)(pos.X - _dragStart.X);
        float dy = (float)(pos.Y - _dragStart.Y);

        // Don't write until movement exceeds 1px — a plain click must not create a keyframe.
        if (!_hasDragged && Math.Abs(dx) < 1f && Math.Abs(dy) < 1f) return;
        _hasDragged = true;

        // Shift: lock to the dominant axis (parity with WPF)
        if (IsShiftDown())
        {
            if (Math.Abs(dx) >= Math.Abs(dy)) dy = 0;
            else dx = 0;
        }
        float newX = _dragOrigX + dx;
        float newY = _dragOrigY + dy;

        // Write via the keyframe-aware path — writing el.X/el.Y directly is ignored by
        // EvalAnimated for keyframed elements, which left the selection handles stale.
        _vm.WritePositionToBestTarget(newX, newY, null, null, null);
        // Moving an element does not change its text layout or transition content. Rebuilding every
        // text run/shadow/outline tree on every pointer move made text drags stick and redraw badly.
        // Update only the dragged control's transform/position here; full preview rebuilds still
        // happen for scrub/playback/property edits.
        if (_elemControls.TryGetValue(el.Id, out var ctrl))
            ApplyElementStateToControl(el, ctrl, _vm.PreviewTime, skipTextRebuild: true);
        UpdateSelectionOverlay();
        if (!_suppressProps)
        {
            _suppressProps = true;
            _propX.Text = newX.ToString("F0");
            _propY.Text = newY.ToString("F0");
            _suppressProps = false;
        }
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs e) => CommitDrag();

    private void ApplyHandleDrag(Point pos)
    {
        if (_dragMode != DragMode.ResizeHandle) return;
        var el = _vm.SelectedElement;
        if (el == null) return;

        float dx = (float)(pos.X - _dragStart.X);
        float dy = (float)(pos.Y - _dragStart.Y);

        float x = _dragOrigX, y = _dragOrigY, w = _dragOrigW, h = _dragOrigH;
        switch (_resizeHandleIdx)
        {
            case 0: x += dx; y += dy; w -= dx; h -= dy; break; // TL
            case 1:          y += dy;           h -= dy; break; // T
            case 2:          y += dy; w += dx;  h -= dy; break; // TR
            case 3:                   w += dx;           break; // R
            case 4:                   w += dx;  h += dy; break; // BR
            case 5:                             h += dy; break; // B
            case 6: x += dx;          w -= dx;  h += dy; break; // BL
            case 7: x += dx;          w -= dx;           break; // L
        }

        // Shift on a corner handle: constrain to the original aspect ratio (parity with WPF)
        bool isCorner = _resizeHandleIdx is 0 or 2 or 4 or 6;
        if (isCorner && IsShiftDown() && _dragOrigW > 0 && _dragOrigH > 0)
        {
            float aspect = _dragOrigW / _dragOrigH;
            if (Math.Abs(w - _dragOrigW) >= Math.Abs(h - _dragOrigH)) h = w / aspect;
            else w = h * aspect;
        }
        w = Math.Max(4, w); h = Math.Max(4, h);

        _hasDragged = true;
        _vm.WritePositionToBestTarget(x, y, w, h, null);
        UpdatePreviewState();
        UpdateSelectionOverlay();
    }

    private void ApplyRotateDrag(Point pos)
    {
        if (_dragMode != DragMode.RotateHandle) return;
        var el = _vm.SelectedElement;
        if (el == null) return;

        // Angle from element centre to the pointer; handle sits above the element so +90°
        var st = _vm.EvalAnimated(el, _vm.PreviewTime);
        double cx = st.x + st.w * st.scaleX / 2;
        double cy = st.y + st.h * st.scaleY / 2;
        double angle = Math.Atan2(pos.Y - cy, pos.X - cx) * 180.0 / Math.PI + 90.0;
        float newRot = (float)(angle % 360.0);
        if (newRot < 0) newRot += 360f;

        _hasDragged = true;
        _vm.WritePositionToBestTarget(null, null, null, null, newRot);
        UpdatePreviewState();
        UpdateSelectionOverlay();
        if (!_suppressProps)
        {
            _suppressProps = true;
            _propRot.Text = newRot.ToString("F1");
            _suppressProps = false;
        }
    }

    private void CommitDrag()
    {
        if (_dragMode == DragMode.None) return;
        bool geometryDrag = _dragMode is DragMode.Move or DragMode.ResizeHandle or DragMode.RotateHandle;
        if (_hasDragged)
        {
            // Per-move writes already landed in the right keyframe; just finish the gesture.
            _vm.ClearActiveDragKeyframe();
            RefreshKfList();
            DrawTimeline();
        }
        else if (geometryDrag)
        {
            // A previous gesture may have left an active drag keyframe behind; never carry it into
            // the next resize/rotate/move gesture or later edits will silently keep mutating the
            // old keyframe instead of creating/updating the correct one at the current time.
            _vm.ClearActiveDragKeyframe();
        }
        _hasDragged = false;
        if (_capturedPointer != null)
            _innerCanvas.ReleasePointerCapture(_capturedPointer);
        EndDrag();
        UpdateSelectionOverlay();
    }

    private void EndDrag()
    {
        _dragMode = DragMode.None;
        _capturedPointer = null;
        if (_vm.SelectedElement != null) UpdatePropertiesPanel();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // LAYERS LIST
    // ═══════════════════════════════════════════════════════════════════════════

    private void RefreshLayerList()
    {
        var items = _vm.Layout.Elements
            .OrderByDescending(e => e.ZOrder)
            .Select(e => new LayerItem(e.Id, AlertEditorViewModel.ElemLayerLabel(e), e == _vm.SelectedElement))
            .ToList();
        _layersList.ItemsSource = null;
        _layersList.ItemsSource = items;

        // Re-select
        if (_vm.SelectedElement != null)
        {
            var match = items.FirstOrDefault(i => i.Id == _vm.SelectedElement.Id);
            if (match != null) _layersList.SelectedItem = match;
        }
        UpdateLayerButtons();
    }

    private void LayerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_layersList.SelectedItem is not LayerItem item) return;
        var el = _vm.Layout.Elements.FirstOrDefault(x => x.Id == item.Id);
        if (el == null) return;
        _vm.SetSelectedElement(el);
        UpdateSelectionOverlay();
        UpdatePropertiesPanel();
        UpdateLayerButtons();
    }

    private record LayerItem(string Id, string Name, bool IsSelected)
    {
        public override string ToString() => Name;
    }

    private record KfItem(AlertKeyframe Keyframe, string Label)
    {
        public override string ToString() => Label;
    }

    private void UpdateLayerButtons()
    {
        bool hasSel = _vm.SelectedElement != null;
        if (_layerHideBtn != null)
        {
            _layerHideBtn.IsEnabled = hasSel;
            _layerHideBtn.Content = _vm.SelectedElement?.Hidden == true ? "Show" : "Hide";
        }
        if (_layerLockBtn != null)
        {
            _layerLockBtn.IsEnabled = hasSel;
            _layerLockBtn.Content = _vm.SelectedElement?.Locked == true ? "Unlock" : "Lock";
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PROPERTIES PANEL
    // ═══════════════════════════════════════════════════════════════════════════

    private void UpdatePropertiesPanel()
    {
        var el = _vm.SelectedElement;
        _noSelLabel.Visibility = el == null ? Visibility.Visible : Visibility.Collapsed;
        // Geometry and visual keyframes mean nothing for a sound — audio gets its own
        // volume-keyframe UI instead (the wire format has no visual keyframes for audio).
        bool isAudio = el?.Type == AlertElementType.Audio;
        _geomPanel.Visibility  = el != null && !isAudio ? Visibility.Visible : Visibility.Collapsed;
        _rectPanel.Visibility  = el?.Type is AlertElementType.Rect or AlertElementType.GoalBar ? Visibility.Visible : Visibility.Collapsed;
        _textPanel.Visibility  = el?.Type == AlertElementType.Text   ? Visibility.Visible : Visibility.Collapsed;
        _imagePanel.Visibility = el?.Type is AlertElementType.Image or AlertElementType.Gif ? Visibility.Visible : Visibility.Collapsed;
        _videoPanel.Visibility = el?.Type == AlertElementType.Video ? Visibility.Visible : Visibility.Collapsed;
        _audioPanel.Visibility = isAudio ? Visibility.Visible : Visibility.Collapsed;
        _kfPanel.Visibility    = el != null && !isAudio ? Visibility.Visible : Visibility.Collapsed;

        if (el == null) return;

        int propsVersion = ++_programmaticPropsVersion;
        _suppressProps = true;
        var st = _vm.EvalAnimated(el, _vm.PreviewTime);
        _propX.Text        = st.x.ToString("F0");
        _propY.Text        = st.y.ToString("F0");
        _propW.Text        = st.w.ToString("F0");
        _propH.Text        = st.h.ToString("F0");
        _propOpacity.Value = Math.Clamp(st.opacity, 0, 1);
        _propRot.Text      = st.rotation.ToString("F1");

        if (el.Type is AlertElementType.Rect or AlertElementType.GoalBar)
        {
            var (fillRgb, fillOp) = AlertEditorViewModel.ArgbToRgbAndOpacity(el.FillColor ?? "#FF2196F3");
            _propFillColor.Text    = fillRgb;
            _propFillOpacity.Value = fillOp;
            SetSwatch(_fillSwatch, el.FillColor);
            bool isEllipse = el.Type == AlertElementType.Rect && el.CornerRadius >= 9999;
            _propShapeType.Visibility    = el.Type == AlertElementType.GoalBar ? Visibility.Collapsed : Visibility.Visible;
            _propShapeType.SelectedIndex = isEllipse ? 1 : 0;
            _propCornerRad.Text   = isEllipse ? "" : el.CornerRadius.ToString();
            _cornerRow.Visibility = isEllipse ? Visibility.Collapsed : Visibility.Visible;
        }

        if (el.Type == AlertElementType.Text)
        {
            var spans = AlertEditorViewModel.GetEditableSpansAt(el, _vm.PreviewTime);
            LoadSpansIntoRichBoxFromList(spans);
            var sp0 = spans.Count > 0 ? spans[0] : null;
            SelectFontInCombo(sp0?.FontFamily ?? el.FontFamily ?? "Segoe UI");
            _propFontSize.Text    = (sp0?.FontSize ?? el.FontSize).ToString("F0");
            _propBold.IsChecked   = sp0?.Bold   ?? el.Bold;
            _propItalic.IsChecked = sp0?.Italic ?? el.Italic;

            _propAlignH.SelectedIndex = Math.Clamp((int)el.Align, 0, 2);
            _propAlignV.SelectedIndex = Math.Clamp(el.VertAlign, 0, 2);
            _propTextColor.Text = AlertEditorViewModel.ArgbToRgbAndOpacity(CurrentTextArgb()).rgb;
            SetSwatch(_textSwatch, CurrentTextArgb());

            _propShadowOn.IsChecked = el.Shadow;
            var (shRgb, shOp) = AlertEditorViewModel.ArgbToRgbAndOpacity(el.ShadowColor);
            _propShadowColor.Text     = shRgb;
            _propShadowOpacity.Value  = shOp;
            _propShadowAngle.Value    = Math.Clamp(el.ShadowAngle, 0, 360);
            _propShadowDist.Value     = Math.Clamp(el.ShadowDistance, 0, 30);
            _propShadowBlur.Value     = Math.Clamp(el.ShadowBlur, 0, 20);
            SetSwatch(_shadowSwatch, el.ShadowColor);
            _shadowOpts.Visibility = el.Shadow ? Visibility.Visible : Visibility.Collapsed;

            _propOutlineOn.IsChecked = el.Outline;
            _propOutlineColor.Text = AlertEditorViewModel.ArgbToRgbAndOpacity(el.OutlineColor).rgb;
            _propOutlineW.Text     = el.OutlineWidth.ToString();
            SetSwatch(_outlineSwatch, el.OutlineColor);
            _outlineOpts.Visibility = el.Outline ? Visibility.Visible : Visibility.Collapsed;

        }

        if (el.Type is AlertElementType.Image or AlertElementType.Gif)
            _propFilePath.Text = el.FilePath ?? "";

        if (el.Type == AlertElementType.Video)
        {
            _propVideoPath.Text         = el.FilePath ?? "";
            _propVideoEnd.SelectedIndex = VideoEndBehaviorToComboIndex(el.VideoEnd);
            _propVideoMute.IsChecked    = el.VideoMuted;
            _propVideoVolume.Value      = Math.Clamp(el.VideoVolume, 0, 2);
        }

        if (el.Type == AlertElementType.Audio)
        {
            _propAudioPath.Text  = el.FilePath ?? "";
            _propStartTime.Text  = el.StartTime.ToString("F2");
            _propFadeIn.Text     = el.FadeIn.ToString("F2");
            _propFadeOut.Text    = el.FadeOut.ToString("F2");
            _propVolL.Text       = el.VolumeL.ToString("F2");
            _propVolR.Text       = el.VolumeR.ToString("F2");
            _propVolLSlider.Value = Math.Clamp(el.VolumeL, 0, 2);
            _propVolRSlider.Value = Math.Clamp(el.VolumeR, 0, 2);
            RefreshAudioKfPanel(_audioKfList.SelectedItem is AudioKfItem cur ? cur.Kf : null);
        }

        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (propsVersion != _programmaticPropsVersion) return;
                _suppressProps = false;
            });
        RefreshKfList();
    }

    private void WireGeomBoxes()
    {
        void Apply()
        {
            if (_suppressProps || _vm.SelectedElement == null) return;
            var st = _vm.EvalAnimated(_vm.SelectedElement, _vm.PreviewTime);
            float x = float.TryParse(_propX.Text, out var parsedX) ? parsedX : st.x;
            float y = float.TryParse(_propY.Text, out var parsedY) ? parsedY : st.y;
            float w = float.TryParse(_propW.Text, out var parsedW) ? parsedW : st.w;
            float h = float.TryParse(_propH.Text, out var parsedH) ? parsedH : st.h;
            float rot = float.TryParse(_propRot.Text, out var parsedRot) ? parsedRot : st.rotation;
            // Negative width/height = mirror/flip; only 0 is invalid (invisible).
            if (w == 0) w = 1;
            if (h == 0) h = 1;
            _vm.WritePositionToBestTarget(x, y, w, h, rot);
            UpdatePreviewState();
            UpdateSelectionOverlay();
            RefreshKfList();
            DrawTimeline();
        }
        _propX.LostFocus += (_, _) => Apply();
        _propY.LostFocus += (_, _) => Apply();
        _propW.LostFocus += (_, _) => Apply();
        _propH.LostFocus += (_, _) => Apply();
        _propRot.LostFocus += (_, _) => Apply();
        _propX.TextChanged += (_, _) => Apply();
        _propY.TextChanged += (_, _) => Apply();
        _propW.TextChanged += (_, _) => Apply();
        _propH.TextChanged += (_, _) => Apply();
        _propRot.TextChanged += (_, _) => Apply();
    }


    // Slider + synced textbox pair for volume fields (parity with WPF's slider rows)
    private FrameworkElement MakeVolRow(out Slider slider, out TextBox box)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var s = new Slider { Minimum = 0, Maximum = 2, StepFrequency = 0.01, Width = 140, Value = 1 };
        var b = MakeNumericTextBox("1.0", NumericInputMode.UnsignedDecimal);
        b.Width = 56;
        row.Children.Add(s);
        row.Children.Add(b);
        s.ValueChanged += (_, e) =>
        {
            if (_suppressProps) return;
            _suppressProps = true; b.Text = e.NewValue.ToString("F2"); _suppressProps = false;
            ApplyAudioProps();
        };
        b.TextChanged += (_, _) =>
        {
            if (_suppressProps) return;
            if (float.TryParse(b.Text, out var v))
            {
                _suppressProps = true; s.Value = Math.Clamp(v, 0, 2); _suppressProps = false;
                ApplyAudioProps();
            }
        };
        slider = s;
        box = b;
        return row;
    }

    private static bool IsShiftDown()
        => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    // ── Keyframe property editor ───────────────────────────────────────────────
    private void LoadKfFields(AlertKeyframe kf)
    {
        int propsVersion = ++_programmaticPropsVersion;
        _suppressProps = true;
        _kfTimeInput.Text = kf.Time.ToString("F2");
        _kfX.Text       = kf.X?.ToString("F0") ?? "";
        _kfY.Text       = kf.Y?.ToString("F0") ?? "";
        _kfW.Text       = kf.Width?.ToString("F0") ?? "";
        _kfH.Text       = kf.Height?.ToString("F0") ?? "";
        _kfOpacity.Text = kf.Opacity?.ToString("F2") ?? "";
        _kfScaleX.Text  = kf.ScaleX?.ToString("F2") ?? "";
        _kfScaleY.Text  = kf.ScaleY?.ToString("F2") ?? "";
        _kfRot.Text     = kf.Rotation?.ToString("F1") ?? "";
        _kfFillColor.Text = string.IsNullOrEmpty(kf.FillColor)
            ? "" : AlertEditorViewModel.ArgbToRgbAndOpacity(kf.FillColor).rgb;
        SetSwatch(_kfFillSwatch, kf.FillColor);
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (propsVersion != _programmaticPropsVersion) return;
                _suppressProps = false;
            });
    }

    private void ApplyKfFields()
    {
        if (_kfList.SelectedItem is not KfItem item) return;
        var kf = item.Keyframe;
        if (float.TryParse(_kfTimeInput.Text, out var t)) kf.Time = Math.Max(0, t);

        kf.X = string.IsNullOrWhiteSpace(_kfX.Text)
            ? (float?)null
            : float.TryParse(_kfX.Text, out var x) ? x : kf.X;
        kf.Y = string.IsNullOrWhiteSpace(_kfY.Text)
            ? (float?)null
            : float.TryParse(_kfY.Text, out var y) ? y : kf.Y;
        kf.Width = string.IsNullOrWhiteSpace(_kfW.Text)
            ? (float?)null
            : float.TryParse(_kfW.Text, out var w) ? w : kf.Width;
        kf.Height = string.IsNullOrWhiteSpace(_kfH.Text)
            ? (float?)null
            : float.TryParse(_kfH.Text, out var h) ? h : kf.Height;
        kf.Opacity = string.IsNullOrWhiteSpace(_kfOpacity.Text)
            ? (float?)null
            : float.TryParse(_kfOpacity.Text, out var op) ? Math.Clamp(op, 0f, 1f) : kf.Opacity;
        kf.ScaleX = string.IsNullOrWhiteSpace(_kfScaleX.Text)
            ? (float?)null
            : float.TryParse(_kfScaleX.Text, out var sx) ? sx : kf.ScaleX;
        kf.ScaleY = string.IsNullOrWhiteSpace(_kfScaleY.Text)
            ? (float?)null
            : float.TryParse(_kfScaleY.Text, out var sy) ? sy : kf.ScaleY;
        kf.Rotation = string.IsNullOrWhiteSpace(_kfRot.Text)
            ? (float?)null
            : float.TryParse(_kfRot.Text, out var rt) ? rt : kf.Rotation;
        var kfRgb = _kfFillColor.Text.TrimStart('#').Trim();
        if (kfRgb.Length == 6) kf.FillColor = "#FF" + kfRgb.ToUpperInvariant();
        else if (string.IsNullOrWhiteSpace(kfRgb)) kf.FillColor = null;

        RefreshKfList();
        foreach (var it in _kfList.Items)
            if (it is KfItem ki && ReferenceEquals(ki.Keyframe, kf))
            { _suppressProps = true; _kfList.SelectedItem = it; _suppressProps = false; break; }
        DrawTimeline();
        UpdatePreviewState();
        UpdateSelectionOverlay();
    }

    // Checkerboard canvas background (parity with WPF) — regenerated on canvas resize
    private void ApplyCheckerBackground()
    {
        int w = (int)_innerCanvas.Width, h = (int)_innerCanvas.Height;
        if (w <= 0 || h <= 0 || w > 8192 || h > 8192) return;
        const int sq = 16;
        var wb = new WriteableBitmap(w, h);
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte v = ((x / sq) + (y / sq)) % 2 == 0 ? (byte)22 : (byte)30;
                int i = (y * w + x) * 4;
                px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 255;
            }
        using (var stream = wb.PixelBuffer.AsStream())
            stream.Write(px, 0, px.Length);
        wb.Invalidate();
        _innerCanvas.Background = new ImageBrush
        {
            ImageSource = wb, Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
        };
    }

    // ── Colour swatches (click → ColorPicker flyout, Photoshop-style) ─────────
    private Border MakeSwatch(Func<string?> getArgb, Action<Color> onPicked)
    {
        var swatch = new Border
        {
            Width = 30, Height = 26, CornerRadius = new CornerRadius(4),
            BorderBrush = new SolidColorBrush(Colors.White), BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Colors.Transparent),
        };
        var picker = new ColorPicker { IsAlphaEnabled = false };
        var flyout = new Flyout { Content = picker };
        bool settingInitial = false;
        // ColorChanged also fires when we seed picker.Color on open — ignore that so we don't
        // apply (and keyframe) the current colour; only genuine user picks call onPicked.
        picker.ColorChanged += (s, e) => { if (!settingInitial) onPicked(e.NewColor); };
        swatch.Tapped += (s, e) =>
        {
            var cur = getArgb();
            settingInitial = true;
            if (!string.IsNullOrWhiteSpace(cur) && TryParseColor(cur, out var c)) picker.Color = c;
            settingInitial = false;
            flyout.ShowAt(swatch);
        };
        return swatch;
    }

    private static void SetSwatch(Border swatch, string? argb)
    {
        if (!string.IsNullOrWhiteSpace(argb) && TryParseColor(argb, out var c))
            swatch.Background = new SolidColorBrush(c);
    }

    // ── Whole-element text colour ──────────────────────────────────────────────
    // ── RichEditBox helpers ────────────────────────────────────────────────────

    private void LoadSpansIntoRichBox(AlertElement el) => LoadSpansIntoRichBoxFromList(AlertEditorViewModel.GetEditableSpansAt(el, _vm.PreviewTime));

    private void LoadSpansIntoRichBoxFromList(List<TextSpan> spans)
    {
        if (_richBox == null) return;
        int loadVersion = ++_richProgrammaticLoadVersion;
        _suppressRich = true;
        _pendingProgrammaticRichSpans = spans.Select(s => s.Clone()).ToList();
        _richBox.Document.SetText(TextSetOptions.None, "");
        int pos = 0;
        foreach (var sp in spans)
        {
            if (string.IsNullOrEmpty(sp.Text)) continue;
            var insertRange = _richBox.Document.GetRange(pos, pos);
            insertRange.SetText(TextSetOptions.None, sp.Text);
            var fmtRange = _richBox.Document.GetRange(pos, pos + sp.Text.Length);
            fmtRange.CharacterFormat.ForegroundColor = ParseWinUiColor(sp.Color ?? "#FFFFFFFF");
            fmtRange.CharacterFormat.Name = string.IsNullOrWhiteSpace(sp.FontFamily) ? "Segoe UI" : sp.FontFamily;
            fmtRange.CharacterFormat.Size = Math.Max(6f, sp.FontSize);
            fmtRange.CharacterFormat.Bold   = sp.Bold   ? FormatEffect.On : FormatEffect.Off;
            fmtRange.CharacterFormat.Italic = sp.Italic ? FormatEffect.On : FormatEffect.Off;
            pos += sp.Text.Length;
        }
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (loadVersion != _richProgrammaticLoadVersion) return;
                _pendingProgrammaticRichSpans = null;
                _suppressRich = false;
            });
    }

    private List<TextSpan> ExtractSpansFromRichBox()
    {
        _richBox.Document.GetText(TextGetOptions.None, out string fullText);
        fullText = fullText.TrimEnd('\r', '\n', '\0');
        if (string.IsNullOrEmpty(fullText))
            return new List<TextSpan> { new() { Text = "", FontFamily = "Segoe UI", FontSize = 24, Color = "#FFFFFFFF" } };

        var result = new List<TextSpan>();
        TextSpan? current = null;
        for (int i = 0; i < fullText.Length; i++)
        {
            var range = _richBox.Document.GetRange(i, i + 1);
            var fmt = range.CharacterFormat;
            var c = fmt.ForegroundColor;
            string colorHex = $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
            string fontName = string.IsNullOrWhiteSpace(fmt.Name) ? "Segoe UI" : fmt.Name;
            int fontSize = Math.Max(6, (int)Math.Round(fmt.Size));
            bool bold   = fmt.Bold   == FormatEffect.On;
            bool italic = fmt.Italic == FormatEffect.On;

            bool same = current != null
                && string.Equals(current.Color,      colorHex, StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.FontFamily, fontName, StringComparison.OrdinalIgnoreCase)
                && current.FontSize == fontSize && current.Bold == bold && current.Italic == italic;

            if (same) current!.Text += fullText[i];
            else
            {
                current = new TextSpan { Text = fullText[i].ToString(), FontFamily = fontName, FontSize = fontSize, Bold = bold, Italic = italic, Color = colorHex };
                result.Add(current);
            }
        }
        return result.Count > 0 ? result : new List<TextSpan> { new() { Text = "", FontFamily = "Segoe UI", FontSize = 24, Color = "#FFFFFFFF" } };
    }

    private void CommitRichSpans()
    {
        if (_vm.SelectedElement?.Type != AlertElementType.Text || _suppressRich) return;
        _vm.WriteTextSpansKf(ExtractSpansFromRichBox());
        RebuildCanvas();
        RefreshKfList();
        DrawTimeline();
    }

    private static bool SpansEqual(IReadOnlyList<TextSpan> a, IReadOnlyList<TextSpan> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            if (!string.Equals(x.Text, y.Text, StringComparison.Ordinal)) return false;
            if (!string.Equals(x.FontFamily, y.FontFamily, StringComparison.OrdinalIgnoreCase)) return false;
            if (x.FontSize != y.FontSize) return false;
            if (x.Bold != y.Bold || x.Italic != y.Italic) return false;
            if (!string.Equals(x.Color, y.Color, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private void UpdateRichToolbarFromSelection()
    {
        if (_richBox == null || _vm.SelectedElement?.Type != AlertElementType.Text) return;
        var fmt = _richBox.Document.Selection.CharacterFormat;
        _suppressProps = true;
        SelectFontInCombo(string.IsNullOrWhiteSpace(fmt.Name) ? "Segoe UI" : fmt.Name);
        if (fmt.Size > 0) _propFontSize.Text = ((int)Math.Round(fmt.Size)).ToString();
        // FormatEffect.Undefined means mixed selection — don't flip the checkbox to false.
        if (fmt.Bold   != FormatEffect.Undefined) _propBold.IsChecked   = fmt.Bold   == FormatEffect.On;
        if (fmt.Italic != FormatEffect.Undefined) _propItalic.IsChecked = fmt.Italic == FormatEffect.On;
        // Selection.CharacterFormat.ForegroundColor returns Colors.Black for mixed-colour
        // selections — indistinguishable from actual black text. Read from the first
        // character in the selection range instead to get the real colour.
        int selStart = _richBox.Document.Selection.StartPosition;
        int selEnd   = _richBox.Document.Selection.EndPosition;
        var firstRange = _richBox.Document.GetRange(selStart, Math.Max(selStart + 1, selEnd));
        var c = firstRange.CharacterFormat.ForegroundColor;
        if (c.A > 0 || c.R > 0 || c.G > 0 || c.B > 0)
        {
            _propTextColor.Text = $"{c.R:X2}{c.G:X2}{c.B:X2}";
            SetSwatch(_textSwatch, $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}");
        }
        // ComboBox.SelectionChanged can fire asynchronously in WinUI after the call stack
        // returns — defer the reset so any queued handler still sees _suppressProps=true.
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => _suppressProps = false);
    }

    private void ApplyBoldToSelection()
    {
        if (_richBox == null || _vm.SelectedElement?.Type != AlertElementType.Text) return;
        FormattingRange().CharacterFormat.Bold =
            _propBold.IsChecked == true ? FormatEffect.On : FormatEffect.Off;
        CommitRichSpans();
    }

    private void ApplyItalicToSelection()
    {
        if (_richBox == null || _vm.SelectedElement?.Type != AlertElementType.Text) return;
        FormattingRange().CharacterFormat.Italic =
            _propItalic.IsChecked == true ? FormatEffect.On : FormatEffect.Off;
        CommitRichSpans();
    }

    private void ApplyFontFamilyToSelection()
    {
        if (_richBox == null || _vm.SelectedElement?.Type != AlertElementType.Text) return;
        if ((_propFontFamily.SelectedItem as ComboBoxItem)?.Content is string font && !string.IsNullOrWhiteSpace(font))
            FormattingRange().CharacterFormat.Name = font;
        CommitRichSpans();
    }

    private void ApplyFontSizeToSelection()
    {
        if (_richBox == null || _vm.SelectedElement?.Type != AlertElementType.Text) return;
        if (float.TryParse(_propFontSize.Text, out var fs) && fs >= 6)
            FormattingRange().CharacterFormat.Size = fs;
        CommitRichSpans();
    }

    // The range that selection-formatting should apply to. The colour-picker flyout (and the font
    // dropdown) move focus off the RichEditBox and collapse its live selection, so fall back to the
    // last captured non-empty selection when the live one is empty.
    private Microsoft.UI.Text.ITextRange FormattingRange()
    {
        var sel = _richBox.Document.Selection;
        if (sel.EndPosition > sel.StartPosition) return sel;
        if (_richSelEnd > _richSelStart) return _richBox.Document.GetRange(_richSelStart, _richSelEnd);
        return sel;
    }

    private void ApplyColorToSelection()
    {
        if (_richBox == null || _vm.SelectedElement?.Type != AlertElementType.Text) return;
        var rgb = _propTextColor.Text.TrimStart('#').Trim();
        if (rgb.Length != 6) return;
        try
        {
            var r = Convert.ToByte(rgb[0..2], 16);
            var g = Convert.ToByte(rgb[2..4], 16);
            var b = Convert.ToByte(rgb[4..6], 16);
            _richBox.Document.Selection.CharacterFormat.ForegroundColor =
                Windows.UI.Color.FromArgb(255, r, g, b);
            SetSwatch(_textSwatch, $"#FF{rgb.ToUpperInvariant()}");
        }
        catch { }
        CommitRichSpans();
    }

    private static Windows.UI.Color ParseWinUiColor(string? argbHex)
    {
        var hex = (argbHex ?? "").TrimStart('#');
        try
        {
            if (hex.Length == 8)
                return Windows.UI.Color.FromArgb(
                    Convert.ToByte(hex[0..2], 16), Convert.ToByte(hex[2..4], 16),
                    Convert.ToByte(hex[4..6], 16), Convert.ToByte(hex[6..8], 16));
            if (hex.Length == 6)
                return Windows.UI.Color.FromArgb(255,
                    Convert.ToByte(hex[0..2], 16), Convert.ToByte(hex[2..4], 16),
                    Convert.ToByte(hex[4..6], 16));
        }
        catch { }
        return Windows.UI.Color.FromArgb(255, 255, 255, 255);
    }

    private string CurrentTextArgb()
    {
        var el = _vm.SelectedElement;
        if (el == null) return "#FFFFFFFF";
        var spans = AlertEditorViewModel.EffectiveSpans(el);
        return spans.Count > 0 ? spans[0].Color ?? el.Color : el.Color;
    }

    // ── Fonts ────────────────────────────────────────────────────────────────
    private static List<string>? _systemFonts;

    private static List<string> EnumerateSystemFonts()
    {
        try
        {
            using var fonts = new System.Drawing.Text.InstalledFontCollection();
            return fonts.Families.Select(f => f.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return ["Segoe UI"]; }
    }

    // FontFamily's ctor throws ArgumentException on empty/malformed names — never construct
    // one directly from layout data.
    private static FontFamily SafeFontFamily(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            try { return new FontFamily(name); } catch { }
        }
        return new FontFamily("Segoe UI");
    }

    private ComboBox MakeFontCombo()
    {
        _systemFonts ??= EnumerateSystemFonts();
        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxDropDownHeight = 420,
        };
        foreach (var name in _systemFonts)
            combo.Items.Add(new ComboBoxItem
            {
                Content = name,
                FontFamily = SafeFontFamily(name),   // each entry previews its own face
                FontSize = 14,
            });
        return combo;
    }

    private void SelectFontInCombo(string name) => SelectFontIn(_propFontFamily, name);

    private static void SelectFontIn(ComboBox combo, string name)
    {
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem cbi &&
                string.Equals(cbi.Content as string, name, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = cbi;
                return;
            }
        }
        // Layout references a font that isn't installed — keep it selectable instead of dropping it
        var extra = new ComboBoxItem { Content = name, FontFamily = SafeFontFamily(name), FontSize = 14 };
        combo.Items.Insert(0, extra);
        combo.SelectedItem = extra;
    }


    private void ApplyAudioProps()
    {
        // Skip during programmatic panel load (fields are being set, not edited) so the async
        // TextChanged that WinUI raises after the load can't write the loaded values back / churn the timeline.
        if (_suppressProps) return;
        if (_vm.SelectedElement?.Type != AlertElementType.Audio) return;
        var el = _vm.SelectedElement;
        float st = float.TryParse(_propStartTime.Text, out var parsedStart) ? parsedStart : el.StartTime;
        float fi = float.TryParse(_propFadeIn.Text, out var parsedFadeIn) ? parsedFadeIn : el.FadeIn;
        float fo = float.TryParse(_propFadeOut.Text, out var parsedFadeOut) ? parsedFadeOut : el.FadeOut;
        float vl = float.TryParse(_propVolL.Text, out var parsedVolL) ? parsedVolL : el.VolumeL;
        float vr = float.TryParse(_propVolR.Text, out var parsedVolR) ? parsedVolR : el.VolumeR;
        _vm.UpdateSelectedAudioProps(st, fi, fo, vl, vr);
        DrawTimeline();
    }

    private void WriteClipVolumeAtPlayhead(float volume)
    {
        var el = _vm.SelectedElement;
        if (el?.Type != AlertElementType.Audio) return;
        var kf = AlertEditorViewModel.WriteClipVolumeEnvelopePoint(el, _vm.PreviewTime, volume, MAX_CLIP_VOL);
        RefreshAudioKfPanel(kf);
        DrawTimeline();
    }

    private void RefreshAudioKfPanel(AudioVolumeKeyframe? select = null)
    {
        var el = _vm.SelectedElement;
        if (el?.Type != AlertElementType.Audio)
        {
            _audioKfList.ItemsSource = null;
            return;
        }
        var items = el.VolumeEnvelope.OrderBy(k => k.Time)
            .Select(k => new AudioKfItem(k, $"{k.Time:F2}s   ·   {k.Volume * 100f:F0}%"))
            .ToList();
        var prev = _suppressProps;
        _suppressProps = true;
        _audioKfList.ItemsSource = items;
        if (select != null)
        {
            var match = items.FirstOrDefault(i => ReferenceEquals(i.Kf, select));
            if (match != null)
            {
                _audioKfList.SelectedItem = match;
                _audioKfTime.Text = match.Kf.Time.ToString("F2");
                _audioKfVol.Text  = (match.Kf.Volume * 100f).ToString("F0");
            }
        }
        _propClipVolSlider.Value = Math.Clamp(
            AlertEditorViewModel.EvalEnvelope(el.VolumeEnvelope, el.VolumeL, _vm.PreviewTime), 0, MAX_CLIP_VOL);
        _propClipVol.Text = _propClipVolSlider.Value.ToString("F2");
        _suppressProps = prev;
    }

    private void ApplyAudioKfFields()
    {
        if (_suppressProps) return;
        var el = _vm.SelectedElement;
        if (el?.Type != AlertElementType.Audio || _audioKfList.SelectedItem is not AudioKfItem item) return;
        float t = float.TryParse(_audioKfTime.Text, out var pt) ? pt : item.Kf.Time;
        float v = float.TryParse(_audioKfVol.Text, out var pv) ? pv / 100f : item.Kf.Volume;
        AlertEditorViewModel.UpdateClipVolumeEnvelopePoint(item.Kf, t, v, MAX_CLIP_VOL);
        el.VolumeEnvelope.Sort((a, b) => a.Time.CompareTo(b.Time));
        RefreshAudioKfPanel(item.Kf);
        DrawTimeline();
    }

    private void ApplyPreviewVariables()
    {
        if (_suppressProps) return;
        _vm.UpdatePreviewVariables(_previewUserBox.Text, _previewMessageBox.Text, _previewAmountBox.Text);
        RebuildCanvas();
    }

    private void ApplyMasterVolume()
    {
        if (_suppressProps || !float.TryParse(_propMasterVolume.Text, out var mv)) return;
        mv = Math.Clamp(mv, 0f, 1f);
        _suppressProps = true;
        _propMasterVolSlider.Value = mv;
        _suppressProps = false;
        _vm.SetMasterVolume(mv);
        if (_audioReader != null)
            _audioReader.Volume = Math.Clamp(
                AlertEditorViewModel.EvalEnvelope(_vm.VolumeEnvelope, _vm.Volume, _vm.PreviewTime), 0f, 1f);
    }

    private void ApplyCanvasSizeBoxes()
    {
        if (!int.TryParse(CanvasW.Text, out var w) || !int.TryParse(CanvasH.Text, out var h) || w <= 0 || h <= 0)
            return;
        if (_vm.Layout.Width == w && _vm.Layout.Height == h)
            return;
        _vm.ResizeCanvas(w, h);
        RebuildCanvas();
    }

    private void PlaySelectedAudioClip()
    {
        var el = _vm.SelectedElement;
        if (el?.Type != AlertElementType.Audio || string.IsNullOrWhiteSpace(el.FilePath) || !File.Exists(el.FilePath))
            return;

        float volume = Math.Clamp((el.VolumeL + el.VolumeR) / 2f, 0f, 1f);
        PlayAudio(el.FilePath, volume);
    }

    private void RefreshKfList()
    {
        var el = _vm.SelectedElement;
        if (el == null) { _kfList.ItemsSource = null; return; }
        var prevKf = (_kfList.SelectedItem as KfItem)?.Keyframe;
        var items = el.Keyframes
            .OrderBy(k => k.Time)
            .Select(k => new KfItem(k, AlertEditorViewModel.KfListItemLabel(k)))
            .ToList();
        _kfList.ItemsSource = items;
        if (prevKf != null)
        {
            var match = items.FirstOrDefault(i => ReferenceEquals(i.Keyframe, prevKf));
            if (match != null)
            {
                _kfList.SelectedItem = match;
                DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () => _kfList.ScrollIntoView(_kfList.SelectedItem));
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // TIMELINE
    // ═══════════════════════════════════════════════════════════════════════════

    private float TimeAtX(double x, float duration)
        => (float)Math.Clamp(_tlPixPerSec > 0 ? x / _tlPixPerSec : 0, 0, duration);

    private void SelectTimelineElement(AlertElement el)
    {
        if (_vm.SelectedElement == el) return;
        _vm.SetSelectedElement(el);
        UpdatePropertiesPanel();
        DrawTimeline();
    }

    // Selection during a press must NOT rebuild the timeline canvas — clearing all
    // children mid-press can drop the pointer capture and kill the drag before it
    // starts. The drag's own PointerMoved redraws every frame anyway.
    private void SelectTimelineElementLight(AlertElement el)
    {
        if (_vm.SelectedElement == el) return;
        _vm.SetSelectedElement(el);
        UpdatePropertiesPanel();
    }

    private void TlCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        var pt = e.GetCurrentPoint(fe);
        var pos = pt.Position;
        bool right = pt.Properties.IsRightButtonPressed;
        float duration = _vm.Duration > 0 ? _vm.Duration : 5f;
        const double HIT_R = 10.0;

        // Ruler clicks only ever seek — its Y coordinates must not hit-test track rows.
        if (ReferenceEquals(fe, _tlRulerCanvas))
        {
            if (!right) { _vm.SetPreviewTime(TimeAtX(pos.X, duration)); UpdatePropertiesPanel(); }
            return;
        }

        // ── Clip volume-envelope points: drag to move, right-click to delete ──
        foreach (var hit in _tlClipEnvHits)
        {
            if (Math.Abs(pos.X - hit.X) > HIT_R || Math.Abs(pos.Y - hit.Y) > HIT_R) continue;
            if (right)
            {
                AlertEditorViewModel.RemoveClipVolumeEnvelopePoint(hit.El, hit.Kf);
                DrawTimeline();
                return;
            }
            fe.CapturePointer(e.Pointer);
            SelectTimelineElementLight(hit.El);
            _tlDrag = TlDragMode.ClipEnvPoint;
            _tlAudioDragEl = hit.El;
            _tlDragEnvKf = hit.Kf;
            _tlDragEnvOrigVol = hit.Kf.Volume;
            _tlAudioDragStartX = pos.X;
            return;
        }

        // ── Master volume-envelope points: drag to move, right-click to delete ──
        foreach (var hit in _tlMasterEnvHits)
        {
            if (Math.Abs(pos.X - hit.X) > HIT_R || Math.Abs(pos.Y - hit.Y) > HIT_R) continue;
            if (right)
            {
                _vm.RemoveVolumeEnvelopePoint(hit.Kf);
                DrawTimeline();
                return;
            }
            fe.CapturePointer(e.Pointer);
            _tlDrag = TlDragMode.MasterEnvPoint;
            _tlDragEnvKf = hit.Kf;
            _tlDragEnvOrigVol = hit.Kf.Volume;
            _tlAudioDragStartX = pos.X;
            return;
        }

        // ── Splice indicators: click to pick transition type ──
        foreach (var hit in _tlSpliceHits)
        {
            if (Math.Abs(pos.X - hit.X) > 10 || Math.Abs(pos.Y - hit.Y) > 8) continue;
            ShowSpliceFlyout(hit.El, hit.NextKf, fe, pos);
            return;
        }

        // ── Keyframe diamonds: drag to retime ──
        foreach (var hit in _tlKfHits)
        {
            if (Math.Abs(pos.X - hit.X) > HIT_R || Math.Abs(pos.Y - hit.Y) > HIT_R) continue;
            if (right) return;
            fe.CapturePointer(e.Pointer);
            SelectTimelineElementLight(hit.El);
            _tlDrag = TlDragMode.Keyframe;
            _tlDragKf = hit.Kf;
            _tlDragKfOrigTime = hit.Kf.Time;
            _tlAudioDragStartX = pos.X;
            return;
        }

        // ── Click directly ON an envelope line: create a point there and drag it ──
        // (standard NLE rubber-band behavior — no right-click needed)
        if (!right)
        {
            foreach (var seg in _tlClipEnvSegs)
            {
                if (pos.X < seg.X1 - 2 || pos.X > seg.X2 + 2) continue;
                double frac = seg.X2 - seg.X1 > 0.5 ? (pos.X - seg.X1) / (seg.X2 - seg.X1) : 0;
                double yAt = seg.Y1 + frac * (seg.Y2 - seg.Y1);
                if (Math.Abs(pos.Y - yAt) > 7) continue;
                var rowEl = seg.El;
                var row = _audioRowInfos.FirstOrDefault(r => r.Element == rowEl);
                if (row.Element == null) break;
                double envBottom = row.RowY + row.RowH - 5;
                double envTop    = row.RowY + 5;
                float t = TimeAtX(pos.X, duration);
                float v = (float)Math.Clamp((envBottom - pos.Y) / Math.Max(1, envBottom - envTop) * MAX_CLIP_VOL, 0, MAX_CLIP_VOL);
                var kf = AlertEditorViewModel.WriteClipVolumeEnvelopePoint(rowEl, t, v, MAX_CLIP_VOL);
                fe.CapturePointer(e.Pointer);
                SelectTimelineElementLight(rowEl);
                _tlDrag = TlDragMode.ClipEnvPoint;
                _tlAudioDragEl = rowEl;
                _tlDragEnvKf = kf;
                _tlDragEnvOrigVol = kf.Volume;
                _tlAudioDragStartX = pos.X;
                return;
            }

            foreach (var seg in _tlMasterEnvSegs)
            {
                if (pos.X < seg.X1 - 2 || pos.X > seg.X2 + 2) continue;
                double frac = seg.X2 - seg.X1 > 0.5 ? (pos.X - seg.X1) / (seg.X2 - seg.X1) : 0;
                double yAt = seg.Y1 + frac * (seg.Y2 - seg.Y1);
                if (Math.Abs(pos.Y - yAt) > 7) continue;
                double envBottom = _tlLegacyRowY + _tlLegacyRowH - 4;
                double envTop    = _tlLegacyRowY + 4;
                float t = TimeAtX(pos.X, duration);
                float v = (float)Math.Clamp((envBottom - pos.Y) / Math.Max(1, envBottom - envTop) * MAX_ENVELOPE_VOL, 0, MAX_ENVELOPE_VOL);
                var kf = _vm.WriteMasterVolumeEnvelopePoint(t, v, MAX_ENVELOPE_VOL);
                if (kf == null) break;
                fe.CapturePointer(e.Pointer);
                _tlDrag = TlDragMode.MasterEnvPoint;
                _tlDragEnvKf = kf;
                _tlDragEnvOrigVol = kf.Volume;
                _tlAudioDragStartX = pos.X;
                return;
            }
        }

        // ── Audio clip rows: fade handles, clip move, right-click adds envelope point ──
        const double HANDLE_HIT = 14.0;
        foreach (var row in _audioRowInfos)
        {
            if (pos.Y < row.RowY || pos.Y > row.RowY + row.RowH) continue;
            if (pos.X < row.ClipX - HANDLE_HIT || pos.X > row.ClipEndX + HANDLE_HIT) continue;

            if (right)
            {
                if (pos.X >= row.ClipX && pos.X <= row.ClipEndX)
                {
                    double envBottom = row.RowY + row.RowH - 5;
                    double envTop    = row.RowY + 5;
                    float t = TimeAtX(pos.X, duration);
                    float v = (float)Math.Clamp((envBottom - pos.Y) / Math.Max(1, envBottom - envTop) * MAX_CLIP_VOL, 0, MAX_CLIP_VOL);
                    if (AlertEditorViewModel.AddClipVolumeEnvelopePoint(row.Element, t, v, MAX_CLIP_VOL))
                        DrawTimeline();
                }
                return;
            }

            _tlAudioDragEl        = row.Element;
            _tlAudioDragStartX    = pos.X;
            _tlAudioDragOrigStart   = row.Element.StartTime;
            _tlAudioDragOrigFadeIn  = row.Element.FadeIn;
            _tlAudioDragOrigFadeOut = row.Element.FadeOut;

            double distFiHandle = Math.Abs(pos.X - row.FadeInEndX);
            double distFoHandle = Math.Abs(pos.X - row.FadeOutStartX);
            if (distFiHandle < HANDLE_HIT && distFiHandle <= distFoHandle)
                _tlDrag = TlDragMode.AudioFadeIn;
            else if (distFoHandle < HANDLE_HIT)
                _tlDrag = TlDragMode.AudioFadeOut;
            else if (pos.X >= row.ClipX && pos.X <= row.ClipEndX)
                _tlDrag = TlDragMode.AudioMove;
            else
                _tlDrag = TlDragMode.None;

            if (_tlDrag != TlDragMode.None)
            {
                fe.CapturePointer(e.Pointer);
                SelectTimelineElementLight(row.Element);
                return;
            }
            SelectTimelineElement(row.Element);
            break;
        }

        // ── Legacy master sound row: right-click adds an envelope point ──
        if (right && _tlLegacyRowY >= 0 && pos.Y >= _tlLegacyRowY && pos.Y <= _tlLegacyRowY + _tlLegacyRowH)
        {
            double envBottom = _tlLegacyRowY + _tlLegacyRowH - 4;
            double envTop    = _tlLegacyRowY + 4;
            float t = TimeAtX(pos.X, duration);
            float v = (float)Math.Clamp((envBottom - pos.Y) / Math.Max(1, envBottom - envTop) * MAX_ENVELOPE_VOL, 0, MAX_ENVELOPE_VOL);
            if (_vm.AddVolumeEnvelopePoint(t, v))
                DrawTimeline();
            return;
        }

        // ── Visual layer clip bars: drag edges to trim, body to move all keyframes ──
        const double EDGE_HIT = 6.0;
        const double VISUAL_ROW_HIT_H = 22.0;
        foreach (var rowInfo in _tlVisualRowInfos)
        {
            if (pos.Y < rowInfo.RowY || pos.Y > rowInfo.RowY + VISUAL_ROW_HIT_H) continue;
            bool nearStart = Math.Abs(pos.X - rowInfo.ClipX)    <= EDGE_HIT;
            bool nearEnd   = Math.Abs(pos.X - rowInfo.ClipEndX) <= EDGE_HIT;
            bool inBody    = pos.X > rowInfo.ClipX + EDGE_HIT && pos.X < rowInfo.ClipEndX - EDGE_HIT;
            if (!nearStart && !nearEnd && !inBody) break; // empty part of the row → seek
            if (right) return;

            fe.CapturePointer(e.Pointer);
            SelectTimelineElementLight(rowInfo.El);
            _tlDragClipKfTimes.Clear();
            foreach (var kf in rowInfo.El.Keyframes) _tlDragClipKfTimes.Add((kf, kf.Time));
            _tlAudioDragStartX = pos.X;
            _tlDrag = nearStart ? TlDragMode.ClipTrimStart
                    : nearEnd   ? TlDragMode.ClipTrimEnd
                    :             TlDragMode.ClipMove;
            return;
        }

        if (right) return;

        // No interactive hit — seek timeline
        _vm.SetPreviewTime(TimeAtX(pos.X, duration));
    }

    private void TlCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_tlDrag == TlDragMode.None) return;
        if (sender is not FrameworkElement fe) return;
        var pos  = e.GetCurrentPoint(fe).Position;
        double dx = pos.X - _tlAudioDragStartX;
        double dt = _tlPixPerSec > 0 ? dx / _tlPixPerSec : 0;
        float duration = _vm.Duration > 0 ? _vm.Duration : 5f;

        switch (_tlDrag)
        {
            case TlDragMode.AudioMove when _tlAudioDragEl != null:
                AlertEditorViewModel.UpdateAudioElementStartTime(_tlAudioDragEl, _tlAudioDragOrigStart + (float)dt);
                break;

            case TlDragMode.AudioFadeIn when _tlAudioDragEl != null:
                AlertEditorViewModel.UpdateAudioElementFadeIn(_tlAudioDragEl, _tlAudioDragOrigFadeIn + (float)dt);
                break;

            case TlDragMode.AudioFadeOut when _tlAudioDragEl != null:
                AlertEditorViewModel.UpdateAudioElementFadeOut(_tlAudioDragEl, _tlAudioDragOrigFadeOut - (float)dt);
                break;

            case TlDragMode.Keyframe when _tlDragKf != null:
                _tlDragKf.Time = Math.Clamp(_tlDragKfOrigTime + (float)dt, 0f, duration);
                UpdatePreviewState();
                break;

            case TlDragMode.ClipMove when _tlDragClipKfTimes.Count > 0:
            {
                float minOrig = _tlDragClipKfTimes.Min(k => k.OrigTime);
                float maxOrig = _tlDragClipKfTimes.Max(k => k.OrigTime);
                // If a keyframe already sits past the duration, (duration - maxOrig) can be less than
                // (-minOrig) — Math.Clamp throws when min > max. Skip the move in that case.
                float lo = -minOrig, hi = duration - maxOrig;
                float d = lo <= hi ? Math.Clamp((float)dt, lo, hi) : 0f;
                foreach (var (kf, orig) in _tlDragClipKfTimes) kf.Time = orig + d;
                UpdatePreviewState();
                break;
            }

            case TlDragMode.ClipTrimStart when _tlDragClipKfTimes.Count > 0:
            {
                var first = _tlDragClipKfTimes.OrderBy(k => k.OrigTime).First();
                // Single keyframe: trimming degenerates to moving it freely
                float limit = _tlDragClipKfTimes.Count > 1 ? _tlDragClipKfTimes.Max(k => k.OrigTime) : duration;
                first.Kf.Time = Math.Clamp(first.OrigTime + (float)dt, 0f, limit);
                UpdatePreviewState();
                break;
            }

            case TlDragMode.ClipTrimEnd when _tlDragClipKfTimes.Count > 0:
            {
                var last = _tlDragClipKfTimes.OrderBy(k => k.OrigTime).Last();
                float limit = _tlDragClipKfTimes.Count > 1 ? _tlDragClipKfTimes.Min(k => k.OrigTime) : 0f;
                last.Kf.Time = Math.Clamp(last.OrigTime + (float)dt, limit, duration);
                UpdatePreviewState();
                break;
            }

            case TlDragMode.MasterEnvPoint when _tlDragEnvKf != null:
            {
                double envBottom = _tlLegacyRowY + _tlLegacyRowH - 4;
                double envTop    = _tlLegacyRowY + 4;
                float t = TimeAtX(pos.X, duration);
                float v = (float)Math.Clamp((envBottom - pos.Y) / Math.Max(1, envBottom - envTop) * MAX_ENVELOPE_VOL, 0, MAX_ENVELOPE_VOL);
                _vm.UpdateVolumeEnvelopePoint(_tlDragEnvKf, t, v, MAX_ENVELOPE_VOL);
                break;
            }

            case TlDragMode.ClipEnvPoint when _tlDragEnvKf != null && _tlAudioDragEl != null:
            {
                float t = TimeAtX(pos.X, duration);
                float v = _tlDragEnvOrigVol;
                var row = _audioRowInfos.FirstOrDefault(r => r.Element == _tlAudioDragEl);
                if (row.Element != null)
                {
                    double envBottom = row.RowY + row.RowH - 5;
                    double envTop    = row.RowY + 5;
                    v = (float)Math.Clamp((envBottom - pos.Y) / Math.Max(1, envBottom - envTop) * MAX_CLIP_VOL, 0, MAX_CLIP_VOL);
                }
                AlertEditorViewModel.UpdateClipVolumeEnvelopePoint(_tlDragEnvKf, t, v, MAX_CLIP_VOL);
                break;
            }
        }

        DrawTimeline();
        UpdatePropertiesPanel();
    }

    private void TlCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_tlDrag == TlDragMode.None) return;
        bool keyframesChanged = _tlDrag is TlDragMode.Keyframe or TlDragMode.ClipMove
                              or TlDragMode.ClipTrimStart or TlDragMode.ClipTrimEnd;
        var draggedClipEnvKf = _tlDrag == TlDragMode.ClipEnvPoint ? _tlDragEnvKf : null;
        _tlDrag = TlDragMode.None;
        _tlAudioDragEl = null;
        _tlDragKf = null;
        _tlDragEnvKf = null;
        _tlDragClipKfTimes.Clear();
        if (sender is FrameworkElement fe) fe.ReleasePointerCapture(e.Pointer);
        if (keyframesChanged) RefreshKfList();
        if (draggedClipEnvKf != null) RefreshAudioKfPanel(draggedClipEnvKf);
        DrawTimeline();
        UpdatePropertiesPanel();
    }

    private void DrawTimeline()
    {
        _audioRowInfos.Clear();
        _tlKfHits.Clear();
        _tlSpliceHits.Clear();
        _tlMasterEnvHits.Clear();
        _tlClipEnvHits.Clear();
        _tlClipEnvSegs.Clear();
        _tlMasterEnvSegs.Clear();
        _tlVisualRowInfos.Clear();
        _tlLegacyRowY = -1;
        _tlRulerCanvas.Children.Clear();
        _tlHeaderCanvas.Children.Clear();
        _tlCanvas.Children.Clear();

        double w = _tlTrackScroll?.ActualWidth ?? 0;
        if (w > 0) w -= 16;
        if (w < 10) return;

        float duration = _vm.Duration > 0 ? _vm.Duration : 5f;
        const double RULER_H = 20.0;
        const double VISUAL_ROW_H = 22.0;
        const double AUDIO_ROW_H = 72.0;
        const double LEGACY_AUDIO_H = 48.0;

        // Zoom: _tlZoom==1 fits the whole duration in the viewport; higher zoom widens the content
        // so the horizontal scrollbar reveals fine detail. (w becomes the content width.)
        double pixPerSec = (w / duration) * _tlZoom;
        w = duration * pixPerSec;
        _tlPixPerSec = pixPerSec;
        for (float t = 0; t <= duration + 0.001f; t += 0.5f)
        {
            double x = t * pixPerSec;
            bool major = Math.Abs(t % 1.0f) < 0.01f;
            double tickH = major ? RULER_H : RULER_H / 2;
            _tlRulerCanvas.Children.Add(MakeLine(x, RULER_H - tickH, x, RULER_H,
                major ? Colors.LightGray : Color.FromArgb(180, 120, 120, 120)));
            if (major)
            {
                var lbl = new TextBlock
                {
                    Text = $"{t:F0}s",
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Colors.LightGray),
                };
                Canvas.SetLeft(lbl, x + 2);
                Canvas.SetTop(lbl, 2);
                _tlRulerCanvas.Children.Add(lbl);
            }
        }

        double rowCursor = 0;
        var visualElems = _vm.Layout.Elements.Where(e => e.Type != AlertElementType.Audio).OrderByDescending(e => e.ZOrder).ToList();
        var audioElems = _vm.Layout.Elements.Where(e => e.Type == AlertElementType.Audio).OrderBy(e => e.StartTime).ToList();

        foreach (var el in visualElems)
        {
            double rowY = rowCursor;
            rowCursor += VISUAL_ROW_H;
            bool isSelected = el == _vm.SelectedElement;

            var rowBg = Color.FromArgb(255, isSelected ? (byte)50 : (byte)35, 35, 40);
            _tlCanvas.Children.Add(MakeRect(0, rowY, w, VISUAL_ROW_H, rowBg));

            var hdrBg = MakeRect(0, rowY, 120, VISUAL_ROW_H, isSelected ? Color.FromArgb(60, 100, 100, 220) : Color.FromArgb(255, 18, 18, 30));
            _tlHeaderCanvas.Children.Add(hdrBg);

            var rowLbl = new TextBlock
            {
                Text = AlertEditorViewModel.ElemDisplayName(el),
                FontSize = 10,
                Foreground = new SolidColorBrush(isSelected ? Color.FromArgb(255, 200, 200, 255) : Colors.LightGray),
                Width = 110,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Canvas.SetLeft(rowLbl, 4);
            Canvas.SetTop(rowLbl, rowY + 4);
            _tlHeaderCanvas.Children.Add(rowLbl);

            // Clip bar spanning the keyframe range — drag body to move, edges to trim
            if (el.Keyframes.Count > 0)
            {
                float clipStartT = el.Keyframes.Min(k => k.Time);
                float clipEndT   = el.Keyframes.Max(k => k.Time);
                double barX = clipStartT * pixPerSec;
                double barW = Math.Max(4, (clipEndT - clipStartT) * pixPerSec);
                var clipBar = new Rectangle
                {
                    Width = barW, Height = VISUAL_ROW_H - 6,
                    Fill   = new SolidColorBrush(isSelected ? Color.FromArgb(120, 100, 100, 255) : Color.FromArgb(70, 100, 100, 200)),
                    Stroke = new SolidColorBrush(isSelected ? Color.FromArgb(255, 180, 180, 255) : Color.FromArgb(255, 100, 100, 180)),
                    StrokeThickness = 1, RadiusX = 3, RadiusY = 3,
                };
                Canvas.SetLeft(clipBar, barX);
                Canvas.SetTop(clipBar, rowY + 3);
                _tlCanvas.Children.Add(clipBar);

                // Trim grips at the bar edges
                _tlCanvas.Children.Add(MakeRect(barX, rowY + 3, 3, VISUAL_ROW_H - 6,
                    isSelected ? Color.FromArgb(255, 220, 220, 255) : Color.FromArgb(255, 140, 140, 220)));
                _tlCanvas.Children.Add(MakeRect(barX + barW - 3, rowY + 3, 3, VISUAL_ROW_H - 6,
                    isSelected ? Color.FromArgb(255, 220, 220, 255) : Color.FromArgb(255, 140, 140, 220)));

                _tlVisualRowInfos.Add((el, rowY, barX, barX + barW));
            }

            foreach (var kf in el.Keyframes)
            {
                double kx = kf.Time * pixPerSec;
                if (kx < 0 || kx > w) continue;
                var diamond = new Polygon
                {
                    Points = new PointCollection
                    {
                        new Point(kx,     rowY + VISUAL_ROW_H / 2 - 5),
                        new Point(kx + 5, rowY + VISUAL_ROW_H / 2),
                        new Point(kx,     rowY + VISUAL_ROW_H / 2 + 5),
                        new Point(kx - 5, rowY + VISUAL_ROW_H / 2),
                    },
                    Fill   = new SolidColorBrush(Color.FromArgb(255, 255, 200, 0)),
                    Stroke = new SolidColorBrush(Colors.White),
                    StrokeThickness = 0.5,
                };
                _tlCanvas.Children.Add(diamond);
                _tlKfHits.Add((el, kf, kx, rowY + VISUAL_ROW_H / 2));
            }

            // Splice indicators between consecutive span KFs (text elements only)
            if (el.Type == AlertElementType.Text)
            {
                var spanKfs = el.Keyframes
                    .Where(k => k.Spans?.Count > 0)
                    .OrderBy(k => k.Time)
                    .ToList();
                for (int si = 1; si < spanKfs.Count; si++)
                {
                    var prevKf = spanKfs[si - 1];
                    var nextKf = spanKfs[si];
                    double midX = ((prevKf.Time + nextKf.Time) / 2.0) * pixPerSec;
                    if (midX < 0 || midX > w) continue;
                    bool isDropTarget = ReferenceEquals(_tlDropSpliceEl, el) && ReferenceEquals(_tlDropSpliceKf, nextKf);
                    var type = isDropTarget ? (_tlDropSpliceType ?? (nextKf.SpanTransition ?? TextTransitionType.Cut))
                                            : (nextKf.SpanTransition ?? TextTransitionType.Cut);
                    string abbr = TransitionAbbrev(type);
                    if (isDropTarget)
                    {
                        var chipBg = new Border
                        {
                            Width = 22,
                            Height = 14,
                            CornerRadius = new CornerRadius(7),
                            Background = new SolidColorBrush(Color.FromArgb(220, 46, 138, 255)),
                            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 170, 220, 255)),
                            BorderThickness = new Thickness(1),
                        };
                        Canvas.SetLeft(chipBg, midX - 11);
                        Canvas.SetTop(chipBg, rowY);
                        _tlCanvas.Children.Add(chipBg);
                    }
                    var lbl = new TextBlock
                    {
                        Text = abbr,
                        FontSize = 8,
                        Foreground = new SolidColorBrush(isDropTarget
                            ? Colors.White
                            : Color.FromArgb(255, 255, 220, 60)),
                    };
                    Canvas.SetLeft(lbl, midX - 5);
                    Canvas.SetTop(lbl, rowY + 2);
                    _tlCanvas.Children.Add(lbl);
                    // Hit aligns with the visible badge (top of the row), not the row centre, so a
                    // click on the "C"/"F"/… actually opens the transition menu.
                    _tlSpliceHits.Add((el, nextKf, midX, rowY + 6));
                }
            }

            _tlCanvas.Children.Add(MakeLine(0, rowY + VISUAL_ROW_H - 1, w, rowY + VISUAL_ROW_H - 1,
                Color.FromArgb(80, 255, 255, 255)));
            _tlHeaderCanvas.Children.Add(MakeLine(0, rowY + VISUAL_ROW_H - 1, 120, rowY + VISUAL_ROW_H - 1,
                Color.FromArgb(80, 255, 255, 255)));
        }

        foreach (var el in audioElems)
        {
            double rowY = rowCursor;
            rowCursor += AUDIO_ROW_H;
            bool isSelected = el == _vm.SelectedElement;

            _tlCanvas.Children.Add(MakeRect(0, rowY, w, AUDIO_ROW_H, isSelected ? Color.FromArgb(50, 80, 120, 80) : Color.FromArgb(12, 80, 255, 80)));
            _tlHeaderCanvas.Children.Add(MakeRect(0, rowY, 120, AUDIO_ROW_H, isSelected ? Color.FromArgb(60, 40, 80, 40) : Color.FromArgb(255, 14, 20, 14)));

            var audioLbl = new TextBlock
            {
                Text = AlertEditorViewModel.ElemDisplayName(el),
                FontSize = 9,
                Foreground = new SolidColorBrush(isSelected ? Color.FromArgb(255, 160, 255, 160) : Color.FromArgb(255, 100, 170, 100)),
                Width = 110,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Canvas.SetLeft(audioLbl, 4);
            Canvas.SetTop(audioLbl, rowY + 22);
            _tlHeaderCanvas.Children.Add(audioLbl);

            DrawAudioClipRow(el, isSelected, rowY, AUDIO_ROW_H, w, duration);
            _tlCanvas.Children.Add(MakeLine(0, rowY + AUDIO_ROW_H - 1, w, rowY + AUDIO_ROW_H - 1, Color.FromArgb(80, 80, 200, 80)));
            _tlHeaderCanvas.Children.Add(MakeLine(0, rowY + AUDIO_ROW_H - 1, 120, rowY + AUDIO_ROW_H - 1, Color.FromArgb(80, 80, 200, 80)));
        }

        if (_vm.WaveformSamples.Length > 0 && _vm.SoundDurationSec > 0)
        {
            double rowY = rowCursor;
            rowCursor += LEGACY_AUDIO_H;
            _tlLegacyRowY = rowY;
            _tlLegacyRowH = LEGACY_AUDIO_H;
            _tlCanvas.Children.Add(MakeRect(0, rowY, w, LEGACY_AUDIO_H, Color.FromArgb(40, 20, 80, 20)));
            _tlHeaderCanvas.Children.Add(MakeRect(0, rowY, 120, LEGACY_AUDIO_H, Color.FromArgb(255, 12, 18, 12)));

            double soundEndX = Math.Min(w, _vm.SoundDurationSec / duration * w);
            double midY = rowY + LEGACY_AUDIO_H / 2;
            double halfH = (LEGACY_AUDIO_H - 8) / 2;
            for (int i = 0; i < _vm.WaveformSamples.Length; i++)
            {
                double x = i / (double)_vm.WaveformSamples.Length * soundEndX;
                double amp = _vm.WaveformSamples[i] * halfH;
                _tlCanvas.Children.Add(MakeLine(x, midY - amp, x, midY + amp, Color.FromArgb(160, 60, 200, 80)));
            }

            // Master volume envelope — right-click row to add a point, drag points to shape, right-click point to delete
            double envBottom = rowY + LEGACY_AUDIO_H - 4;
            double envTop    = rowY + 4;
            double VToY(float v) => envBottom - Math.Clamp(v / MAX_ENVELOPE_VOL, 0, 1) * (envBottom - envTop);

            var masterEnv = _vm.VolumeEnvelope.OrderBy(k => k.Time).ToList();
            float baseVol = _vm.Volume > 0 ? _vm.Volume : 1f;
            float vol0 = masterEnv.Count > 0 && masterEnv[0].Time <= 0.001f ? masterEnv[0].Volume : baseVol;
            var envPts = new List<(double X, double Y)> { (0, VToY(vol0)) };
            foreach (var ekf in masterEnv)
                envPts.Add((Math.Clamp(ekf.Time * pixPerSec, 0, w), VToY(ekf.Volume)));
            float volLast = masterEnv.Count > 0 ? masterEnv[^1].Volume : baseVol;
            envPts.Add((w, VToY(volLast)));

            for (int i = 1; i < envPts.Count; i++)
            {
                _tlCanvas.Children.Add(MakeLine(envPts[i - 1].X, envPts[i - 1].Y, envPts[i].X, envPts[i].Y,
                    Color.FromArgb(255, 80, 220, 80)));
                _tlMasterEnvSegs.Add((envPts[i - 1].X, envPts[i - 1].Y, envPts[i].X, envPts[i].Y));
            }

            foreach (var ekf in masterEnv)
            {
                double ex = Math.Clamp(ekf.Time * pixPerSec, 0, w);
                double ey = VToY(ekf.Volume);
                var dot = new Ellipse
                {
                    Width = 10, Height = 10,
                    Fill = new SolidColorBrush(Color.FromArgb(255, 80, 220, 80)),
                    Stroke = new SolidColorBrush(Colors.White), StrokeThickness = 1,
                };
                Canvas.SetLeft(dot, ex - 5);
                Canvas.SetTop(dot, ey - 5);
                _tlCanvas.Children.Add(dot);
                _tlMasterEnvHits.Add((ekf, ex, ey));
            }
        }

        _tlCanvas.Width = w;
        _tlCanvas.Height = Math.Max(rowCursor + 4, _tlTrackScroll?.ActualHeight ?? 0);
        _tlHeaderCanvas.Width = 120;
        _tlHeaderCanvas.Height = _tlCanvas.Height;
        _tlRulerCanvas.Width = w;

        double curX = _vm.PreviewTime * pixPerSec;
        _timelinePlayheadLine = MakeLine(curX, 0, curX, _tlCanvas.Height, Color.FromArgb(220, 255, 80, 80));
        _rulerPlayheadLine = MakeLine(curX, 0, curX, RULER_H, Color.FromArgb(220, 255, 80, 80));
        _rulerPlayheadHead = MakeRect(curX - 5, 0, 10, 10, Color.FromArgb(220, 255, 80, 80));
        _tlCanvas.Children.Add(_timelinePlayheadLine);
        _tlRulerCanvas.Children.Add(_rulerPlayheadLine);
        _tlRulerCanvas.Children.Add(_rulerPlayheadHead);
    }

    private void UpdateTimelinePlayhead()
    {
        if (_timelinePlayheadLine == null || _rulerPlayheadLine == null || _rulerPlayheadHead == null || _tlCanvas == null) return;
        float duration = _vm.Duration > 0 ? _vm.Duration : 5f;
        if (duration <= 0 || _tlCanvas.Width <= 0) return;
        double curX = _vm.PreviewTime / duration * _tlCanvas.Width;
        _timelinePlayheadLine.X1 = curX;
        _timelinePlayheadLine.X2 = curX;
        _timelinePlayheadLine.Y2 = _tlCanvas.Height;
        _rulerPlayheadLine.X1 = curX;
        _rulerPlayheadLine.X2 = curX;
        Canvas.SetLeft(_rulerPlayheadHead, curX - 5);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // TOOLBAR HANDLERS
    // ═══════════════════════════════════════════════════════════════════════════

    private void PlayBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsPlaying)
        {
            _vm.StopPlayback();
            StopPlaybackLoop();
            StopAudio();
            StopAllAudioClips();
            return;
        }

        if (_vm.Duration <= 0) return;
        _tlSlider.Maximum = _vm.Duration;
        // Start every playback from clean cached render state so the second play is identical to the
        // first (no stale text-render-cache / frame-index state carried over → no replay stutter).
        _textRenderCache.Clear();
        ResetVideoFrameState();
        _vm.StartPlayback();

        if (!string.IsNullOrWhiteSpace(_propMasterSoundPath.Text) && System.IO.File.Exists(_propMasterSoundPath.Text))
            PlayAudio(_propMasterSoundPath.Text, float.TryParse(_propMasterVolume.Text, out var v) ? v : 1f);

        StopAllAudioClips();
        foreach (var el in _vm.Layout.Elements.Where(e2 => e2.Type == AlertElementType.Audio
                                                      && !string.IsNullOrWhiteSpace(e2.FilePath)
                                                      && File.Exists(e2.FilePath)))
        {
            var capturedEl = el;
            if (el.StartTime <= 0.05f)
            {
                StartAudioClipPlayer(capturedEl);
            }
            else
            {
                var timer = DispatcherQueue.CreateTimer();
                timer.Interval = TimeSpan.FromSeconds(el.StartTime);
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    _audioClipTimers.Remove(timer);
                    StartAudioClipPlayer(capturedEl);
                };
                _audioClipTimers.Add(timer);
                timer.Start();
            }
        }

        StartPlaybackLoop();
    }

    private void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        _vm.StopPlayback();
        StopPlaybackLoop();
        StopAudio();
        StopAllAudioClips();
    }

    private void StartPlaybackLoop()
    {
        StopPlaybackLoop();
        CompositionTarget.Rendering += OnCompositionTargetRendering;
        _renderingHooked = true;
    }

    private void StopPlaybackLoop()
    {
        _playTimer?.Stop();
        _playTimer = null;

        if (!_renderingHooked) return;
        CompositionTarget.Rendering -= OnCompositionTargetRendering;
        _renderingHooked = false;
    }

    private void OnCompositionTargetRendering(object? sender, object e)
    {
        if (!_vm.IsPlaying)
        {
            StopPlaybackLoop();
            return;
        }

        bool ended = _vm.OnPlayTick();

        // Update the visual on the render frame (synchronous, not via the deferred PropertyChanged
        // path) so playback is smooth.
        float t = _vm.PreviewTime;
        TimeLabel.Text   = $"{t:F2}s";
        _tlTimeLabel.Text = $"{t:F2}s";
        if (!_suppressSlider) { _suppressSlider = true; _tlSlider.Value = t; _suppressSlider = false; }
        UpdatePreviewState();
        UpdateTimelinePlayhead();

        // Apply volume envelopes each frame to match OBS behaviour
        if (_audioReader != null)
            _audioReader.Volume = Math.Clamp(
                AlertEditorViewModel.EvalEnvelope(_vm.VolumeEnvelope, _vm.Volume, t), 0f, 1f);
        foreach (var clip in _audioClipPlayers)
            clip.Reader.Volume = Math.Clamp(
                AlertEditorViewModel.EvalEnvelope(
                    clip.El.VolumeEnvelope, (clip.El.VolumeL + clip.El.VolumeR) / 2f, t), 0f, 1f);

        if (!ended) return;

        StopPlaybackLoop();
        StopAudio();
        StopAllAudioClips();
    }

    private void AddText_Click(object sender, RoutedEventArgs e)
    {
        var el = _vm.CreateTextElement();
        _vm.AddElement(el);
        _vm.SetSelectedElement(el);
        RebuildCanvas(); RefreshLayerList(); UpdatePropertiesPanel(); DrawTimeline();
    }

    private void AddRect_Click(object sender, RoutedEventArgs e)
    {
        var el = _vm.CreateRectElement();
        _vm.AddElement(el);
        _vm.SetSelectedElement(el);
        RebuildCanvas(); RefreshLayerList(); UpdatePropertiesPanel(); DrawTimeline();
    }

    private void AddGoalBar_Click(object sender, RoutedEventArgs e)
    {
        var el = _vm.CreateGoalBarElement();
        _vm.AddElement(el);
        _vm.SetSelectedElement(el);
        RebuildCanvas(); RefreshLayerList(); UpdatePropertiesPanel(); DrawTimeline();
    }

    private async void AddImage_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync(new[] { ".png", ".jpg", ".jpeg", ".bmp", ".webp" });
        if (path == null) return;
        var el = _vm.CreateMediaElement(AlertElementType.Image, path);
        SetMediaAspectSize(el, path);
        _vm.AddElement(el);
        _vm.SetSelectedElement(el);
        RebuildCanvas(); RefreshLayerList(); UpdatePropertiesPanel(); DrawTimeline();
    }

    private async void AddGif_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync(new[] { ".gif" });
        if (path == null) return;
        var el = _vm.CreateMediaElement(AlertElementType.Gif, path);
        SetMediaAspectSize(el, path);
        _vm.AddElement(el);
        _vm.SetSelectedElement(el);
        RebuildCanvas(); RefreshLayerList(); UpdatePropertiesPanel(); DrawTimeline();
    }

    private async void AddVideo_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync(new[] { ".mp4", ".mov", ".m4v" });
        if (path == null) return;
        var el = _vm.CreateMediaElement(AlertElementType.Video, path);
        FitVideoAspect(el, 16, 9);  // default 16:9; corrected to true aspect on MediaOpened
        _vm.AddElement(el);
        _vm.SetSelectedElement(el);
        RebuildCanvas(); RefreshLayerList(); UpdatePropertiesPanel(); DrawTimeline();
    }

    // Fit a video element to the given aspect within the canvas, centered.
    private void FitVideoAspect(AlertElement el, int vw, int vh)
    {
        if (vw <= 0 || vh <= 0) return;
        double cw = _vm.Layout.Width, ch = _vm.Layout.Height;
        double scale = Math.Min(cw / vw, ch / vh);
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale)) scale = 1;
        el.Width  = Math.Max(1, (int)Math.Round(vw * scale));
        el.Height = Math.Max(1, (int)Math.Round(vh * scale));
        el.X = (int)Math.Round((cw - el.Width) / 2);
        el.Y = (int)Math.Round((ch - el.Height) / 2);
    }

    // Mirrors the selected element by negating its width (H) or height (V).
    private void FlipSelected(bool horizontal)
    {
        if (_vm.SelectedElement == null) return;
        var st = _vm.EvalAnimated(_vm.SelectedElement, _vm.PreviewTime);
        _vm.WritePositionToBestTarget(
            null,
            null,
            horizontal ? -st.w : null,
            horizontal ? null : -st.h,
            null);
        UpdatePreviewState();
        UpdateSelectionOverlay();
        UpdatePropertiesPanel();
        RefreshKfList();
        DrawTimeline();
    }

    // Sizes a newly added image/GIF to its real aspect ratio, scaled to fit within the canvas
    // (CreateMediaElement defaults to a 100x100 square, which distorts non-square images).
    private void SetMediaAspectSize(AlertElement el, string path)
    {
        try
        {
            int iw, ih;
            using (var img = System.Drawing.Image.FromFile(path)) { iw = img.Width; ih = img.Height; }
            if (iw <= 0 || ih <= 0) return;
            double cw = _vm.Layout.Width, ch = _vm.Layout.Height;
            double scale = Math.Min(cw / iw, ch / ih);
            if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale)) scale = 1;
            el.Width  = Math.Max(1, (int)Math.Round(iw * scale));
            el.Height = Math.Max(1, (int)Math.Round(ih * scale));
            el.X = (int)Math.Round((cw - el.Width) / 2);
            el.Y = (int)Math.Round((ch - el.Height) / 2);
        }
        catch { /* keep the default size if the image can't be read */ }
    }

    private async void AddAudio_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync(new[] { ".mp3", ".wav", ".ogg" });
        if (path == null) return;
        var el = _vm.CreateAudioElement(path);
        _vm.AddElement(el);
        _vm.SetSelectedElement(el);
        RebuildCanvas(); RefreshLayerList(); UpdatePropertiesPanel(); DrawTimeline();
    }

    private async void BrowseSound_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync(new[] { ".mp3", ".wav", ".ogg" });
        if (path != null)
        {
            _propMasterSoundPath.Text = path;
            _vm.SetSoundFile(path);
            _vm.LoadSoundDuration(path);
            if (float.TryParse(DurationBox.Text, out var dur) && dur <= 0 && _vm.SoundDurationSec > 0)
                DurationBox.Text = _vm.SoundDurationSec.ToString("F1");
            DrawTimeline();
        }
    }

    private void DeleteElem_Click(object sender, RoutedEventArgs e)
    {
        _vm.DeleteSelected();
        RebuildCanvas(); RefreshLayerList(); UpdatePropertiesPanel(); DrawTimeline();
    }

    private void ResizeCanvas_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(CanvasW.Text, out var w) && int.TryParse(CanvasH.Text, out var h) && w > 0 && h > 0)
        {
            _vm.ResizeCanvas(w, h);
            RebuildCanvas();
        }
    }

    // ─── Undo / Redo ─────────────────────────────────────────────────────────────
    private void UndoBtn_Click(object sender, RoutedEventArgs e) => DoUndo();
    private void RedoBtn_Click(object sender, RoutedEventArgs e) => DoRedo();

    private void DoUndo()
    {
        if (!_vm.Undo()) return;
        OnUndoRedoApplied();
    }

    private void DoRedo()
    {
        if (!_vm.Redo()) return;
        OnUndoRedoApplied();
    }

    private void OnUndoRedoApplied()
    {
        _vm.SetSelectedElement(null);
        RebuildCanvas();
        RefreshLayerList();
        DrawTimeline();
        UpdatePropertiesPanel();
        UpdateUndoRedoButtons();
    }

    private void UpdateUndoRedoButtons()
    {
        UndoBtn.IsEnabled = _vm.CanUndo;
        RedoBtn.IsEnabled = _vm.CanRedo;
    }

    private void ApplyDuration_Click(object sender, RoutedEventArgs e) => ApplyDurationBox();
    private void DurationBox_LostFocus(object sender, RoutedEventArgs e) => ApplyDurationBox();
    private void DurationBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) ApplyDurationBox();
    }

    private void ApplyDurationBox()
    {
        if (!float.TryParse(DurationBox.Text, out var dur) || dur <= 0) return;
        if (Math.Abs(_vm.Duration - dur) < 0.0001f) return;
        _vm.Duration = dur;
        if (_tlSlider != null) _tlSlider.Maximum = dur;
        DrawTimeline();
    }

    // ─── Template import / export (same folder + format as WPF version) ─────────

    private async void ImportLayout_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        AlertLayout? loaded;
        try
        {
            var json = await Windows.Storage.FileIO.ReadTextAsync(file);
            loaded = AlertLayout.FromJson(json);
        }
        catch (Exception ex) { Log($"Import read failed: {ex.Message}"); return; }

        if (loaded == null) { Log("Import: file is not a valid alert layout"); return; }

        var dlg = new ContentDialog
        {
            Title             = "Load Template",
            Content           = "Replace — replaces this layout entirely.\nMerge — adds the template's elements to the current layout.",
            PrimaryButtonText = "Replace",
            SecondaryButtonText = "Merge",
            CloseButtonText   = "Cancel",
            XamlRoot          = Content.XamlRoot,
        };
        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.None) return;

        if (result == ContentDialogResult.Primary)
        {
            _vm.ReplaceLayout(loaded);
            CanvasW.Text = _vm.Layout.Width.ToString();
            CanvasH.Text = _vm.Layout.Height.ToString();
            _propMasterSoundPath.Text = _vm.SoundFile ?? "";
        }
        else
        {
            _vm.MergeLayout(loaded);
        }

        RebuildCanvas();
        RefreshLayerList();
        DrawTimeline();
        UpdatePropertiesPanel();
    }

    private async void ExportLayout_Click(object sender, RoutedEventArgs e)
    {
        float.TryParse(_propMasterVolume.Text, out var vol);
        _vm.PrepareTemplateSave(_propMasterSoundPath.Text, vol);

        AlertEditorViewModel.EnsureTemplateFolder();

        var picker = new FileSavePicker();
        picker.DefaultFileExtension = ".json";
        picker.FileTypeChoices.Add("Alert Template", new List<string> { ".json" });
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = $"layout_{_eventKey ?? _labelGoalKey ?? "custom"}";
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        try
        {
            _vm.SaveTemplateToFile(file.Path);
            Log($"Template exported to {file.Path}");
        }
        catch (Exception ex) { Log($"Export failed: {ex.Message}"); }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SAVE / CANCEL
    // ═══════════════════════════════════════════════════════════════════════════

    private async void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        float.TryParse(DurationBox.Text, out var dur);
        float.TryParse(_propMasterVolume.Text, out var vol);
        if (dur <= 0) dur = 5f;
        if (vol <= 0) vol = 1f;

        _vm.CommitSave(_propMasterSoundPath.Text, vol, dur);

        if (_vm.Result != null)
        {
            var result = new AlertEditorResult(
                _vm.Result.ToJson(),
                _vm.ResultSoundFile,
                _vm.Result.Volume,
                _vm.ResultDuration);

            _tcs.TrySetResult(result);
        }

        Close();
        await Task.CompletedTask;
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        _tcs.TrySetResult(null);
        Close();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // AUDIO
    // ═══════════════════════════════════════════════════════════════════════════

    private void PlayAudio(string path, float volume)
    {
        CleanupAudio();
        try
        {
            _audioReader = new AudioFileReader(path) { Volume = Math.Clamp(volume, 0f, 1f) };
            _waveOut = new WaveOutEvent();
            _waveOut.Init(_audioReader);
            _waveOut.Play();
        }
        catch { }
    }

    private void StopAudio()
    {
        _waveOut?.Stop();
    }

    private void StartAudioClipPlayer(AlertElement el)
    {
        if (string.IsNullOrWhiteSpace(el.FilePath) || !File.Exists(el.FilePath)) return;
        try
        {
            var reader = new AudioFileReader(el.FilePath) { Volume = Math.Clamp((el.VolumeL + el.VolumeR) / 2f, 0f, 1f) };
            var output = new WaveOutEvent();
            output.Init(reader);
            output.Play();
            _audioClipPlayers.Add((output, reader, el));
        }
        catch { }
    }

    private void StopAllAudioClips()
    {
        foreach (var timer in _audioClipTimers)
            timer.Stop();
        _audioClipTimers.Clear();

        foreach (var clip in _audioClipPlayers)
        {
            try { clip.Output.Stop(); clip.Output.Dispose(); } catch { }
            try { clip.Reader.Dispose(); } catch { }
        }
        _audioClipPlayers.Clear();
    }

    private void CleanupAudio()
    {
        StopPlaybackLoop();
        try { _waveOut?.Stop(); _waveOut?.Dispose(); } catch { }
        try { _audioReader?.Dispose(); } catch { }
        _waveOut = null;
        _audioReader = null;
        StopAllAudioClips();
    }

    private (float[] Peaks, double DurationSec) GetOrLoadClipWaveform(string path)
    {
        if (_clipWaveforms.TryGetValue(path, out var cached)) return cached;
        _clipWaveforms[path] = (Array.Empty<float>(), 0);
        _ = Task.Run(() =>
        {
            try
            {
                using var reader = new AudioFileReader(path);
                double durationSec = reader.TotalTime.TotalSeconds;
                int channels = reader.WaveFormat.Channels;
                long total = reader.Length / (reader.WaveFormat.BitsPerSample / 8 * channels);
                int target = 1200;
                float step = Math.Max(1f, (float)total / target);
                var pts = new List<float>(target);
                float[] buf = new float[(int)(step * channels + channels)];
                int read;
                while ((read = reader.Read(buf, 0, buf.Length)) > 0)
                {
                    float peak = 0;
                    for (int i = 0; i < read; i++) peak = Math.Max(peak, Math.Abs(buf[i]));
                    pts.Add(peak);
                }

                float max = pts.Count > 0 ? pts.Max() : 1f;
                if (max < 0.001f) max = 1f;
                var peaks = pts.Select(v => v / max).ToArray();
                DispatcherQueue.TryEnqueue(() =>
                {
                    _clipWaveforms[path] = (peaks, durationSec);
                    DrawTimeline();
                });
            }
            catch { }
        });
        return (Array.Empty<float>(), 0);
    }

    private void DrawAudioClipRow(AlertElement el, bool isSelected, double rowY, double rowH, double width, double duration)
    {
        double startX = Math.Clamp(el.StartTime / duration * width, 0, width);
        float[] waveform = Array.Empty<float>();
        double audioDurSec = 0;
        if (!string.IsNullOrWhiteSpace(el.FilePath))
        {
            var loaded = GetOrLoadClipWaveform(el.FilePath);
            waveform = loaded.Peaks;
            audioDurSec = loaded.DurationSec;
        }

        double clipW = audioDurSec > 0
            ? Math.Clamp(audioDurSec / duration * width, 6, Math.Max(6, width - startX))
            : Math.Max(6, width - startX);
        double clipEnd = startX + clipW;

        double fadeInPx  = audioDurSec > 0 ? Math.Clamp(el.FadeIn  / (float)audioDurSec * clipW, 0, clipW) : 0;
        double fadeOutPx = audioDurSec > 0 ? Math.Clamp(el.FadeOut / (float)audioDurSec * clipW, 0, clipW) : 0;
        double fadeInEndX     = startX + fadeInPx;
        double fadeOutStartX  = clipEnd - fadeOutPx;
        _audioRowInfos.Add((el, rowY, rowH, startX, clipEnd, fadeInEndX, fadeOutStartX));

        double clipTop    = rowY + 1;
        double clipBottom = rowY + rowH - 1;

        var fill = isSelected ? Color.FromArgb(160, 20, 80, 40) : Color.FromArgb(100, 15, 60, 30);
        var border = isSelected ? Color.FromArgb(255, 60, 220, 80) : Color.FromArgb(255, 40, 140, 60);
        var rect = new Rectangle
        {
            Width = clipW,
            Height = rowH - 2,
            Fill = new SolidColorBrush(fill),
            Stroke = new SolidColorBrush(border),
            StrokeThickness = 1,
            RadiusX = 3,
            RadiusY = 3,
        };
        Canvas.SetLeft(rect, startX);
        Canvas.SetTop(rect, clipTop);
        _tlCanvas.Children.Add(rect);

        if (waveform.Length > 0)
        {
            double midY = rowY + rowH / 2;
            double halfH = (rowH - 10) / 2;
            for (int i = 0; i < waveform.Length; i++)
            {
                double x = startX + i / (double)waveform.Length * clipW;
                double amp = waveform[i] * halfH;
                _tlCanvas.Children.Add(MakeLine(x, midY - amp, x, midY + amp, Color.FromArgb(160, 80, 220, 120)));
            }
        }

        // Premiere-style fade handles: diagonal overlay + draggable circle handle
        var handleBrush = new SolidColorBrush(Color.FromArgb(255, 80, 240, 120));
        if (fadeInPx > 1)
        {
            var fiPoly = new Polygon
            {
                Points = new PointCollection
                {
                    new Point(startX,        clipTop),
                    new Point(fadeInEndX,     clipTop),
                    new Point(startX,         clipBottom),
                },
                Fill = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)),
            };
            _tlCanvas.Children.Add(fiPoly);
            _tlCanvas.Children.Add(MakeLine(startX, clipBottom, fadeInEndX, clipTop, Color.FromArgb(200, 80, 240, 120)));
        }
        var fiHandle = new Ellipse { Width = 10, Height = 10, Fill = handleBrush };
        Canvas.SetLeft(fiHandle, fadeInEndX - 5);
        Canvas.SetTop(fiHandle,  clipTop - 4);
        _tlCanvas.Children.Add(fiHandle);

        if (fadeOutPx > 1)
        {
            var foPoly = new Polygon
            {
                Points = new PointCollection
                {
                    new Point(fadeOutStartX, clipTop),
                    new Point(clipEnd,        clipTop),
                    new Point(clipEnd,        clipBottom),
                },
                Fill = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)),
            };
            _tlCanvas.Children.Add(foPoly);
            _tlCanvas.Children.Add(MakeLine(fadeOutStartX, clipTop, clipEnd, clipBottom, Color.FromArgb(200, 80, 240, 120)));
        }
        var foHandle = new Ellipse { Width = 10, Height = 10, Fill = handleBrush };
        Canvas.SetLeft(foHandle, fadeOutStartX - 5);
        Canvas.SetTop(foHandle,  clipTop - 4);
        _tlCanvas.Children.Add(foHandle);

        // ── Volume rubber-band envelope (Premiere-style) ──────────────────────
        // Y maps volume 0–MAX_CLIP_VOL within the row; 1.0 (unity) sits mid-row on a dashed
        // reference line — above = boost, below = cut. Right-click clip adds a point.
        double envBottom = rowY + rowH - 5;
        double envTop    = rowY + 5;
        double envRange  = envBottom - envTop;
        double VolToY(float v) => envBottom - Math.Clamp(v / MAX_CLIP_VOL, 0, 1) * envRange;

        double unityY = VolToY(1f);
        for (double ux = startX; ux < clipEnd; ux += 8)
            _tlCanvas.Children.Add(MakeLine(ux, unityY, Math.Min(ux + 4, clipEnd), unityY,
                Color.FromArgb(70, 255, 255, 255)));

        var clipEnv = el.VolumeEnvelope.OrderBy(k => k.Time).ToList();
        float vol0 = clipEnv.Count > 0 && clipEnv[0].Time <= 0.001f ? clipEnv[0].Volume : el.VolumeL;
        var envPts = new List<(double X, double Y)> { (startX, VolToY(vol0)) };
        foreach (var ekf in clipEnv)
            envPts.Add((Math.Clamp(ekf.Time * _tlPixPerSec, startX, clipEnd), VolToY(ekf.Volume)));
        float volLast = clipEnv.Count > 0 ? clipEnv[^1].Volume : el.VolumeL;
        envPts.Add((clipEnd, VolToY(volLast)));

        for (int i = 1; i < envPts.Count; i++)
        {
            _tlCanvas.Children.Add(MakeLine(envPts[i - 1].X, envPts[i - 1].Y, envPts[i].X, envPts[i].Y,
                Color.FromArgb(255, 255, 220, 0)));
            _tlClipEnvSegs.Add((el, envPts[i - 1].X, envPts[i - 1].Y, envPts[i].X, envPts[i].Y));
        }

        foreach (var ekf in clipEnv)
        {
            double ex = Math.Clamp(ekf.Time * _tlPixPerSec, startX, clipEnd);
            double ey = VolToY(ekf.Volume);
            var dot = new Ellipse
            {
                Width = 10, Height = 10,
                Fill = new SolidColorBrush(Color.FromArgb(255, 255, 220, 0)),
                Stroke = new SolidColorBrush(Colors.White), StrokeThickness = 1,
            };
            Canvas.SetLeft(dot, ex - 5);
            Canvas.SetTop(dot, ey - 5);
            _tlCanvas.Children.Add(dot);
            _tlClipEnvHits.Add((el, ekf, ex, ey));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // FILE PICKERS
    // ═══════════════════════════════════════════════════════════════════════════

    private async Task<string?> PickFileAsync(string[] extensions)
    {
        var picker = new FileOpenPicker();
        foreach (var ext in extensions) picker.FileTypeFilter.Add(ext);
        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════════════

    private static StackPanel BuildSection(string title, IEnumerable<UIElement> children)
    {
        var header = new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 12,
            Margin = new Thickness(8, 10, 8, 4),
        };
        var inner = new StackPanel { Spacing = 2, Margin = new Thickness(8, 0, 8, 6) };
        foreach (var c in children) inner.Children.Add(c);
        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(inner);
        return panel;
    }

    private static Grid MakePropRow(string label, FrameworkElement input)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(180, 210, 210, 210)),
        };
        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(input, 1);
        g.Children.Add(lbl);
        g.Children.Add(input);
        return g;
    }

    private static TextBox MakeTextBox(string placeholder)
        => new TextBox
        {
            PlaceholderText = placeholder,
            FontSize = 12,
            Padding = new Thickness(4, 2, 4, 2),
        };

    private static TextBox MakeNumericTextBox(string placeholder, NumericInputMode mode)
    {
        var box = MakeTextBox(placeholder);
        AttachNumericFilter(box, mode);
        return box;
    }

    private static void AttachNumericFilter(TextBox box, NumericInputMode mode)
    {
        box.BeforeTextChanging += (_, e) =>
        {
            if (!IsValidNumericInput(e.NewText, mode))
                e.Cancel = true;
        };
    }

    private static bool IsValidNumericInput(string text, NumericInputMode mode)
    {
        if (string.IsNullOrEmpty(text))
            return true;

        bool allowDecimal = mode is NumericInputMode.UnsignedDecimal or NumericInputMode.SignedDecimal;
        bool allowNegative = mode is NumericInputMode.SignedInteger or NumericInputMode.SignedDecimal;
        int start = 0;
        int dots = 0;

        if (allowNegative && text[0] == '-')
            start = 1;

        for (int i = start; i < text.Length; i++)
        {
            char ch = text[i];
            if (char.IsDigit(ch))
                continue;
            if (allowDecimal && ch == '.' && dots++ == 0)
                continue;
            return false;
        }

        return true;
    }

    private static Button MakeBtn(string content, RoutedEventHandler? handler)
    {
        var btn = new Button { Content = content, FontSize = 12, Padding = new Thickness(8, 4, 8, 4) };
        if (handler != null) btn.Click += handler;
        return btn;
    }

    private static Rectangle MakeRect(double x, double y, double w, double h, Color c)
    {
        var r = new Rectangle { Width = w, Height = h, Fill = new SolidColorBrush(c) };
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        return r;
    }

    private static Line MakeLine(double x1, double y1, double x2, double y2, Color c)
    {
        var l = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = new SolidColorBrush(c), StrokeThickness = 1 };
        return l;
    }

    private void ApplyIfNotSuppressed(Action action)
    {
        if (_suppressProps) return;
        action();
    }

    private static string TransitionAbbrev(TextTransitionType t) => t switch
    {
        TextTransitionType.TypeOn     => "T",
        TextTransitionType.Fade       => "F",
        TextTransitionType.SlideLeft  => "SL",
        TextTransitionType.SlideRight => "SR",
        TextTransitionType.Morph      => "M",
        _                             => "C",
    };

    private void ShowSpliceFlyout(AlertElement el, AlertKeyframe nextKf, FrameworkElement anchor, Windows.Foundation.Point pos)
    {
        var flyout = new MenuFlyout();
        foreach (TextTransitionType t in Enum.GetValues<TextTransitionType>())
        {
            var item = new MenuFlyoutItem { Text = AlertEditorViewModel.TransitionDisplayName(t) };
            var captured = t;
            item.Click += (_, _) =>
            {
                _vm.ApplySpanTransition(el, nextKf, captured);
                DrawTimeline();
                RebuildCanvas();
            };
            flyout.Items.Add(item);
        }
        flyout.ShowAt(anchor, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = pos });
    }

    private async void TlCanvas_DragOver(object sender, DragEventArgs e)
    {
        if (_draggingTransitionType is not TextTransitionType type || sender is not FrameworkElement fe)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            ClearTimelineDropTarget();
            return;
        }

        var pos = e.GetPosition(fe);
        var target = FindTimelineSpliceTarget(pos);
        if (target == null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            ClearTimelineDropTarget();
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.Caption = $"Drop {AlertEditorViewModel.TransitionDisplayName(type)}";
        SetTimelineDropTarget(target.Value.El, target.Value.NextKf, type);
        await Task.CompletedTask;
    }

    private async void TlCanvas_Drop(object sender, DragEventArgs e)
    {
        if (_draggingTransitionType is not TextTransitionType type || _tlDropSpliceEl == null || _tlDropSpliceKf == null)
        {
            ClearTimelineDropTarget();
            await Task.CompletedTask;
            return;
        }

        _vm.ApplySpanTransition(_tlDropSpliceEl, _tlDropSpliceKf, type);
        ClearTimelineDropTarget();
        _draggingTransitionType = null;
        DrawTimeline();
        RebuildCanvas();
        await Task.CompletedTask;
    }

    private void TlCanvas_DragLeave(object sender, DragEventArgs e)
        => ClearTimelineDropTarget();

    private (AlertElement El, AlertKeyframe NextKf, double X, double Y)? FindTimelineSpliceTarget(Point pos)
    {
        const double hitX = 24.0;
        const double hitY = 12.0;
        (AlertElement El, AlertKeyframe NextKf, double X, double Y)? best = null;
        double bestScore = double.MaxValue;

        foreach (var hit in _tlSpliceHits)
        {
            double dx = Math.Abs(pos.X - hit.X);
            double dy = Math.Abs(pos.Y - hit.Y);
            if (dx > hitX || dy > hitY) continue;
            double score = dx + dy * 2;
            if (score >= bestScore) continue;
            best = hit;
            bestScore = score;
        }

        return best;
    }

    private void SetTimelineDropTarget(AlertElement el, AlertKeyframe nextKf, TextTransitionType type)
    {
        if (ReferenceEquals(_tlDropSpliceEl, el) &&
            ReferenceEquals(_tlDropSpliceKf, nextKf) &&
            _tlDropSpliceType == type)
            return;

        _tlDropSpliceEl = el;
        _tlDropSpliceKf = nextKf;
        _tlDropSpliceType = type;
        DrawTimeline();
    }

    private void ClearTimelineDropTarget()
    {
        if (_tlDropSpliceEl == null && _tlDropSpliceKf == null && _tlDropSpliceType == null)
            return;

        _tlDropSpliceEl = null;
        _tlDropSpliceKf = null;
        _tlDropSpliceType = null;
        DrawTimeline();
    }

    // Ctrl + wheel zooms the timeline horizontally, keeping the time under the cursor in place.
    private void TimelineZoomWheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!ctrl) return; // let normal scrolling happen

        var pt = e.GetCurrentPoint((UIElement)sender);
        int delta = pt.Properties.MouseWheelDelta;
        if (delta == 0) { return; }
        e.Handled = true;

        double cursorContentX = pt.Position.X;                 // content-space X under cursor
        double timeAtCursor   = _tlPixPerSec > 0 ? cursorContentX / _tlPixPerSec : 0;
        double oldOffset      = _tlTrackScroll.HorizontalOffset;

        double oldZoom = _tlZoom;
        _tlZoom = Math.Clamp(_tlZoom * (delta > 0 ? 1.2 : 1.0 / 1.2), 1.0, 30.0);
        if (Math.Abs(_tlZoom - oldZoom) < 0.0001) return;

        DrawTimeline(); // recomputes _tlPixPerSec at the new zoom

        // Restore the cursor's time to the same on-screen position.
        double viewportX = cursorContentX - oldOffset;
        double target    = Math.Max(0, timeAtCursor * _tlPixPerSec - viewportX);
        DispatcherQueue.TryEnqueue(() => _tlTrackScroll.ChangeView(target, null, null, true));
    }

    private static bool TryParseColor(string hex, out Color color)
    {
        color = Colors.White;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        hex = hex.TrimStart('#');
        try
        {
            if (hex.Length == 6)
            {
                byte r = Convert.ToByte(hex[0..2], 16);
                byte g = Convert.ToByte(hex[2..4], 16);
                byte b = Convert.ToByte(hex[4..6], 16);
                color = Color.FromArgb(255, r, g, b);
                return true;
            }
            if (hex.Length == 8)
            {
                byte a = Convert.ToByte(hex[0..2], 16);
                byte r = Convert.ToByte(hex[2..4], 16);
                byte g = Convert.ToByte(hex[4..6], 16);
                byte b = Convert.ToByte(hex[6..8], 16);
                color = Color.FromArgb(a, r, g, b);
                return true;
            }
        }
        catch { }
        return false;
    }
}
