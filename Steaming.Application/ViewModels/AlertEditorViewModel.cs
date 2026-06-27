using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Steaming.Core.Models;

namespace Steaming.Application.ViewModels;

public class AlertEditorViewModel : ViewModelBase
{
    // ── Layout model ──────────────────────────────────────────────────────────
    private AlertLayout _layout;
    public AlertLayout Layout
    {
        get => _layout;
        set => Set(ref _layout, value);
    }

    private AlertElement? _selectedElement;
    public AlertElement? SelectedElement
    {
        get => _selectedElement;
        set => Set(ref _selectedElement, value);
    }

    // ── Preview state ─────────────────────────────────────────────────────────
    private float _previewTime;
    public float PreviewTime
    {
        get => _previewTime;
        set => Set(ref _previewTime, value);
    }

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        set => Set(ref _isPlaying, value);
    }

    private float _duration;
    public float Duration
    {
        get => _duration;
        set => Set(ref _duration, value);
    }

    // ── Preview substitution variables ────────────────────────────────────────
    private string _previewUser = "TestUser";
    public string PreviewUser { get => _previewUser; set => Set(ref _previewUser, value); }

    private string _previewMessage = "Test message!";
    public string PreviewMessage { get => _previewMessage; set => Set(ref _previewMessage, value); }

    private string _previewAmount = "100";
    public string PreviewAmount { get => _previewAmount; set => Set(ref _previewAmount, value); }

    private string _previewTarget = "SomeViewer";
    public string PreviewTarget { get => _previewTarget; set => Set(ref _previewTarget, value); }

    private string _previewReward = "Hydrate";
    public string PreviewReward { get => _previewReward; set => Set(ref _previewReward, value); }

    private string _previewInput = "Big sip";
    public string PreviewInput { get => _previewInput; set => Set(ref _previewInput, value); }

    // ── Sound ─────────────────────────────────────────────────────────────────
    private string? _soundFile;
    public string? SoundFile
    {
        get => _soundFile;
        set => Set(ref _soundFile, value);
    }

    private float _volume = 1f;
    public float Volume
    {
        get => _volume;
        set => Set(ref _volume, value);
    }

    private double _soundDurationSec;
    public double SoundDurationSec
    {
        get => _soundDurationSec;
        set => Set(ref _soundDurationSec, value);
    }

    private float[] _waveformSamples = Array.Empty<float>();
    public float[] WaveformSamples
    {
        get => _waveformSamples;
        set => Set(ref _waveformSamples, value);
    }

    // ── Volume envelope ───────────────────────────────────────────────────────
    private List<AudioVolumeKeyframe> _volumeEnvelope = new();
    public List<AudioVolumeKeyframe> VolumeEnvelope
    {
        get => _volumeEnvelope;
        set => Set(ref _volumeEnvelope, value);
    }

    // ── Save result ───────────────────────────────────────────────────────────
    public AlertLayout? Result { get; private set; }
    public string? ResultSoundFile { get; private set; }
    public float ResultDuration { get; private set; }

    // ── Drag state (keyframe during current drag gesture) ─────────────────────
    public AlertKeyframe? ActiveDragKf { get; set; }

    // ── Undo / Redo ────────────────────────────────────────────────────────────
    private const int MaxUndoDepth = 50;
    private readonly List<string> _undoList = new();
    private readonly List<string> _redoList = new();
    public bool CanUndo => _undoList.Count > 0;
    public bool CanRedo => _redoList.Count > 0;

    // Raised whenever a mutation is captured — lets the UI keep Undo/Redo button state current.
    public event Action? LayoutMutated;

    public void CaptureUndoSnapshot()
    {
        _undoList.Add(_layout.ToJson());
        if (_undoList.Count > MaxUndoDepth) _undoList.RemoveAt(0);
        _redoList.Clear();
        LayoutMutated?.Invoke();
    }

    public bool Undo()
    {
        if (_undoList.Count == 0) return false;
        _redoList.Add(_layout.ToJson());
        RestoreLayoutSnapshot(_undoList[^1]);
        _undoList.RemoveAt(_undoList.Count - 1);
        return true;
    }

    public bool Redo()
    {
        if (_redoList.Count == 0) return false;
        _undoList.Add(_layout.ToJson());
        RestoreLayoutSnapshot(_redoList[^1]);
        _redoList.RemoveAt(_redoList.Count - 1);
        return true;
    }

    private void RestoreLayoutSnapshot(string json)
    {
        var restored = AlertLayout.FromJson(json);
        if (restored == null) return;
        _layout = restored;
        _selectedElement = null;
        Notify(nameof(Layout));
        Notify(nameof(SelectedElement));
    }

    // ── Playback (Stopwatch owned here; DispatcherTimer mechanism is in code-behind) ──
    private readonly Stopwatch _playStopwatch = new();

    public void StartPlayback()
    {
        PreviewTime = 0f;
        IsPlaying   = true;
        _playStopwatch.Restart();
    }

    public void StopPlayback()
    {
        _playStopwatch.Stop();
        IsPlaying = false;
    }

    // Called every ~16ms by the DispatcherTimer in code-behind.
    // Returns true when playback just ended (so caller can stop the timer and reset audio).
    public bool OnPlayTick()
    {
        float elapsed = (float)_playStopwatch.Elapsed.TotalSeconds;
        if (elapsed >= _duration)
        {
            _playStopwatch.Stop();
            IsPlaying   = false;
            PreviewTime = 0f;
            return true;   // playback ended
        }
        PreviewTime = elapsed;
        return false;
    }

    // ── Constructor ───────────────────────────────────────────────────────────
    public AlertEditorViewModel(AlertLayout layout, float duration, string? soundFile, string? eventKey)
    {
        _layout   = DeepClone(layout);
        _duration = duration;
        _soundFile = soundFile ?? layout.SoundFile;
        _volumeEnvelope = layout.VolumeEnvelope?
            .Select(k => new AudioVolumeKeyframe { Time = k.Time, Volume = k.Volume }).ToList() ?? new();

        if (eventKey != null)
        {
            (_previewUser, _previewMessage, _previewAmount, _previewTarget, _previewReward, _previewInput) = eventKey switch
            {
                "Subscribe"        => ("TestUser", "just subscribed for 6 months!", "6", "SomeViewer", "Hydrate", "Big sip"),
                "GiftSubscribe"    => ("GiftGiver", "gifted a sub!", "1", "SomeViewer", "Hydrate", "Big sip"),
                "Bits"             => ("TestUser", "Cheer100 Thanks for the stream!", "100", "SomeViewer", "Hydrate", "Big sip"),
                "Raid"             => ("OtherChannel", "is raiding with 42 viewers!", "42", "SomeViewer", "Hydrate", "Big sip"),
                "RewardRedemption" => ("TestUser", "redeemed a reward!", "1000", "SomeViewer", "Hydrate", "Big sip"),
                "Follow"           => ("TestUser", "is now following!", "0", "SomeViewer", "Hydrate", "Big sip"),
                _                  => ("TestUser", "Test message!", "100", "SomeViewer", "Hydrate", "Big sip"),
            };
        }
    }

    // ── Layout clone ──────────────────────────────────────────────────────────
    public static AlertLayout DeepClone(AlertLayout src)
    {
        var json = src.ToJson();
        return AlertLayout.FromJson(json) ?? src;
    }

    // ── ZOrder ────────────────────────────────────────────────────────────────
    public int NextZOrder()
        => _layout.Elements.Count == 0 ? 0 : _layout.Elements.Max(e => e.ZOrder) + 1;

    // ── Display helpers ───────────────────────────────────────────────────────
    public static string ElemDisplayName(AlertElement el) => el.Type switch
    {
        AlertElementType.Text    => $"Text: {(string.IsNullOrWhiteSpace(el.Content) ? "…" : el.Content.Length > 20 ? el.Content[..20] + "…" : el.Content)}",
        AlertElementType.Image   => $"Image: {System.IO.Path.GetFileName(el.FilePath ?? "")}",
        AlertElementType.Gif     => $"GIF: {System.IO.Path.GetFileName(el.FilePath ?? "")}",
        AlertElementType.Audio   => $"Audio: {System.IO.Path.GetFileName(el.FilePath ?? "")}",
        AlertElementType.Rect    => "Rect",
        AlertElementType.GoalBar => "Goal Bar",
        _                        => el.Type.ToString(),
    };

    public static string ElemLayerLabel(AlertElement el)
    {
        string state = $"{(el.Hidden ? "[H] " : "")}{(el.Locked ? "[L] " : "")}";
        return state + ElemDisplayName(el);
    }

    public static string KfListItemLabel(AlertKeyframe kf)
    {
        var parts = new List<string> { $"t={kf.Time:F2}s" };
        if (kf.X.HasValue)       parts.Add($"x={kf.X:F0}");
        if (kf.Y.HasValue)       parts.Add($"y={kf.Y:F0}");
        if (kf.Width.HasValue)   parts.Add($"w={kf.Width:F0}");
        if (kf.Height.HasValue)  parts.Add($"h={kf.Height:F0}");
        if (kf.Opacity.HasValue) parts.Add($"op={kf.Opacity:F2}");
        if (kf.Rotation.HasValue)parts.Add($"rot={kf.Rotation:F1}°");
        if (!string.IsNullOrEmpty(kf.FillColor)) parts.Add("color");
        return string.Join("  ", parts);
    }

    // ── Text spans ────────────────────────────────────────────────────────────
    public static List<TextSpan> EffectiveSpans(AlertElement el)
    {
        if (el.Spans.Count > 0) return el.Spans;
        return new List<TextSpan> { new()
        {
            Text       = el.Content,
            FontFamily = el.FontFamily,
            FontSize   = el.FontSize,
            Bold       = el.Bold,
            Italic     = el.Italic,
            Color      = el.Color,
        }};
    }

    // ── Element factory methods ───────────────────────────────────────────────
    public AlertElement CreateTextElement() => new()
    {
        Type    = AlertElementType.Text, X = 50, Y = 50, Width = 300, Height = 50,
        Content = "New Text",
        Spans   = new() { new TextSpan { Text = "New Text", FontFamily = "Segoe UI", FontSize = 24, Color = "#FFFFFFFF" } },
        Align   = AlertTextAlign.Center, ZOrder = NextZOrder()
    };

    public AlertElement CreateRectElement() => new()
    {
        Type = AlertElementType.Rect, X = 50, Y = 50, Width = 200, Height = 100,
        FillColor = "#CC2244AA", CornerRadius = 8, ZOrder = NextZOrder()
    };

    public AlertElement CreateGoalBarElement() => new()
    {
        Type      = AlertElementType.GoalBar,
        X         = 0, Y = _layout.Height - 40,
        Width     = _layout.Width, Height = 30,
        FillColor = "#FF2196F3", CornerRadius = 4, ZOrder = NextZOrder()
    };

    public AlertElement CreateMediaElement(AlertElementType type, string path)
    {
        bool isGif = path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
        if (isGif) type = AlertElementType.Gif;
        return new AlertElement
        {
            Type = type, X = 20, Y = 20, Width = 100, Height = 100,
            FilePath = path, ZOrder = NextZOrder()
        };
    }

    public AlertElement CreateAudioElement(string path) => new()
    {
        Type      = AlertElementType.Audio,
        FilePath  = path,
        StartTime = 0f,
        VolumeL   = 1f,
        VolumeR   = 1f,
        FadeIn    = 0f,
        FadeOut   = 0f,
        ZOrder    = NextZOrder(),
    };

    // ── Element mutation ──────────────────────────────────────────────────────
    public void AddElement(AlertElement el)
    {
        CaptureUndoSnapshot();
        _layout.Elements.Add(el);
    }

    public void DeleteSelected()
    {
        if (_selectedElement == null) return;
        CaptureUndoSnapshot();
        _layout.Elements.Remove(_selectedElement);
        _selectedElement = null;
        ActiveDragKf = null;
    }

    public void MoveSelectedUp()
    {
        if (_selectedElement == null) return;
        CaptureUndoSnapshot();
        _selectedElement.ZOrder++;
    }

    public void MoveSelectedDown()
    {
        if (_selectedElement == null) return;
        CaptureUndoSnapshot();
        _selectedElement.ZOrder = Math.Max(0, _selectedElement.ZOrder - 1);
    }

    public void SetSelectedHidden(bool hidden)
    {
        if (_selectedElement == null || _selectedElement.Hidden == hidden) return;
        CaptureUndoSnapshot();
        _selectedElement.Hidden = hidden;
        if (hidden) ActiveDragKf = null;
    }

    public void SetSelectedLocked(bool locked)
    {
        if (_selectedElement == null || _selectedElement.Locked == locked) return;
        CaptureUndoSnapshot();
        _selectedElement.Locked = locked;
        if (locked) ActiveDragKf = null;
    }

    public void ResizeCanvas(int w, int h)
    {
        _layout.Width  = w;
        _layout.Height = h;
    }

    public void SetSelectedElement(AlertElement? element)
        => SelectedElement = element;

    public void ClearActiveDragKeyframe()
    {
        ActiveDragKf = null;
        _dragMovedBase = false;
        _gestureActive = false;
    }

    private bool _dragMovedBase;

    // One undo snapshot per drag/resize/rotate gesture. The gesture begins at pointer-press
    // (BeginGeometryGesture) and ends in ClearActiveDragKeyframe. The first geometry write of the
    // gesture captures the snapshot; later writes in the same gesture do not. This covers the case
    // the old per-keyframe logic missed: dragging an element parked on an EXISTING keyframe never
    // created a new keyframe, so it never snapshotted, so Ctrl+Z could not undo the move.
    private bool _gestureActive;
    private bool _gestureSnapshotTaken;

    // Called by the view at the start of a canvas drag/resize/rotate gesture.
    public void BeginGeometryGesture()
    {
        _gestureActive = true;
        _gestureSnapshotTaken = false;
    }

    // ── Animation evaluation ──────────────────────────────────────────────────
    // Preserves sign (negative = flip) but enforces a minimum magnitude of 1.
    private static float ClampSignedMin1(float v)
        => Math.Abs(v) < 1f ? (v < 0 ? -1f : 1f) : v;

    // Mirror the selected element by negating width (horizontal) or height (vertical), including any
    // width/height keyframes so the flip holds across the whole animation.
    public bool FlipSelectedHorizontal()
    {
        if (_selectedElement == null) return false;
        CaptureUndoSnapshot();
        _selectedElement.Width = -_selectedElement.Width;
        foreach (var kf in _selectedElement.Keyframes)
            if (kf.Width.HasValue) kf.Width = -kf.Width.Value;
        return true;
    }

    public bool FlipSelectedVertical()
    {
        if (_selectedElement == null) return false;
        CaptureUndoSnapshot();
        _selectedElement.Height = -_selectedElement.Height;
        foreach (var kf in _selectedElement.Keyframes)
            if (kf.Height.HasValue) kf.Height = -kf.Height.Value;
        return true;
    }

    public bool UpdateSelectedGeometry(float? x, float? y, float? w, float? h, float? rotation)
    {
        if (_selectedElement == null) return false;
        CaptureUndoSnapshot();
        if (x.HasValue) _selectedElement.X = x.Value;
        if (y.HasValue) _selectedElement.Y = y.Value;
        // Negative width/height is allowed = mirror/flip. Keep a 1px minimum magnitude so the
        // element never collapses to nothing (0 = invisible).
        if (w.HasValue) _selectedElement.Width = ClampSignedMin1(w.Value);
        if (h.HasValue) _selectedElement.Height = ClampSignedMin1(h.Value);
        if (rotation.HasValue) _selectedElement.Rotation = rotation.Value;
        return true;
    }

    public string? UpdateSelectedFillColor(string rgb, double opacity)
    {
        if (_selectedElement == null) return null;
        CaptureUndoSnapshot();
        _selectedElement.FillColor = RgbAndOpacityToArgb(rgb, opacity);
        return _selectedElement.FillColor;
    }

    public bool UpdateSelectedCornerRadius(int cornerRadius)
    {
        if (_selectedElement == null) return false;
        _selectedElement.CornerRadius = cornerRadius;
        return true;
    }

    public bool UpdateSelectedOutlineWidth(int outlineWidth)
    {
        if (_selectedElement == null) return false;
        _selectedElement.OutlineWidth = Math.Max(0, Math.Min(8, outlineWidth));
        return true;
    }

    public bool UpdateSelectedShadowAngle(float angle)
    {
        if (_selectedElement == null) return false;
        _selectedElement.ShadowAngle = angle;
        return true;
    }

    public bool UpdateSelectedShadowDistance(float distance)
    {
        if (_selectedElement == null) return false;
        _selectedElement.ShadowDistance = distance;
        return true;
    }

    public bool UpdateSelectedShadowBlur(float blur)
    {
        if (_selectedElement == null) return false;
        _selectedElement.ShadowBlur = blur;
        return true;
    }

    public string? UpdateSelectedShadowColor(string rgb, double opacity)
    {
        if (_selectedElement == null) return null;
        CaptureUndoSnapshot();
        _selectedElement.ShadowColor = RgbAndOpacityToArgb(rgb, opacity);
        return _selectedElement.ShadowColor;
    }

    public string? UpdateSelectedOutlineColor(string rgb)
    {
        if (_selectedElement == null) return null;
        CaptureUndoSnapshot();
        _selectedElement.OutlineColor = "#FF" + rgb.TrimStart('#').PadRight(6, '0')[..6].ToUpper();
        return _selectedElement.OutlineColor;
    }

    public int UpdateSelectedShapeType(bool ellipse)
    {
        if (_selectedElement == null) return 0;
        CaptureUndoSnapshot();
        if (ellipse)
            _selectedElement.CornerRadius = 9999;
        else if (_selectedElement.CornerRadius >= 9999)
            _selectedElement.CornerRadius = 0;
        return _selectedElement.CornerRadius;
    }

    public bool UpdateSelectedTextFlags(bool shadow, bool outline)
    {
        if (_selectedElement == null) return false;
        CaptureUndoSnapshot();
        _selectedElement.Shadow = shadow;
        _selectedElement.Outline = outline;
        return true;
    }

    public bool UpdateSelectedAlign(int alignIndex)
    {
        if (_selectedElement == null) return false;
        _selectedElement.Align = (AlertTextAlign)alignIndex;
        return true;
    }

    public bool UpdateSelectedVertAlign(int vertAlignIndex)
    {
        if (_selectedElement == null) return false;
        _selectedElement.VertAlign = vertAlignIndex;
        return true;
    }

    public AlertElement? UpdateSelectedFilePath(string path, bool treatGifAsGif)
    {
        if (_selectedElement == null) return null;
        if (treatGifAsGif && path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            _selectedElement.Type = AlertElementType.Gif;
        _selectedElement.FilePath = path;
        return _selectedElement;
    }

    public AlertElement? UpdateSelectedAudioFilePath(string path)
    {
        if (_selectedElement == null || _selectedElement.Type != AlertElementType.Audio) return null;
        _selectedElement.FilePath = path;
        return _selectedElement;
    }

    public (float volumeL, float volumeR)? UpdateSelectedAudioProps(
        float? startTime, float? fadeIn, float? fadeOut, float? volumeL, float? volumeR)
    {
        if (_selectedElement == null || _selectedElement.Type != AlertElementType.Audio) return null;
        if (startTime.HasValue) _selectedElement.StartTime = Math.Max(0f, startTime.Value);
        if (fadeIn.HasValue) _selectedElement.FadeIn = Math.Max(0f, fadeIn.Value);
        if (fadeOut.HasValue) _selectedElement.FadeOut = Math.Max(0f, fadeOut.Value);
        if (volumeL.HasValue) _selectedElement.VolumeL = (float)Math.Clamp(volumeL.Value, 0.0, 2.0);
        if (volumeR.HasValue) _selectedElement.VolumeR = (float)Math.Clamp(volumeR.Value, 0.0, 2.0);
        return (_selectedElement.VolumeL, _selectedElement.VolumeR);
    }

    public float? UpdateSelectedAudioVolumeL(float volume)
    {
        if (_selectedElement == null || _selectedElement.Type != AlertElementType.Audio) return null;
        _selectedElement.VolumeL = volume;
        return _selectedElement.VolumeL;
    }

    public float? UpdateSelectedAudioVolumeR(float volume)
    {
        if (_selectedElement == null || _selectedElement.Type != AlertElementType.Audio) return null;
        _selectedElement.VolumeR = volume;
        return _selectedElement.VolumeR;
    }

    public bool UpdateSelectedTextSpans(List<TextSpan> spans)
    {
        if (_selectedElement == null || _selectedElement.Type != AlertElementType.Text) return false;
        _selectedElement.Spans = spans;
        _selectedElement.Content = string.Join("", spans.Select(s => s.Text));
        return true;
    }

    public void UpdatePreviewVariables(string user, string message, string amount)
    {
        PreviewUser = user;
        PreviewMessage = message;
        PreviewAmount = amount;
    }

    public AlertKeyframe? AddSelectedKeyframeAtPreview()
    {
        // Audio elements have no visual keyframes — the wire format never serializes
        // them (AlertLayout writes 0). Creating them only lies to the user.
        if (_selectedElement == null || _selectedElement.Type == AlertElementType.Audio) return null;
        CaptureUndoSnapshot();
        var st = EvalAnimated(_selectedElement, _previewTime);
        var kf = new AlertKeyframe
        {
            Time      = _previewTime,
            X         = st.x,
            Y         = st.y,
            Width     = st.w,
            Height    = st.h,
            Opacity   = st.opacity,
            Rotation  = st.rotation,
            FillColor = _selectedElement.Type == AlertElementType.Rect ? _selectedElement.FillColor : null,
        };

        if (_selectedElement.Type == AlertElementType.Text)
        {
            var split = FindSplitSpanTransition(_selectedElement, _previewTime);
            kf.Spans = EvalSpansAt(_selectedElement, _previewTime)
                .Select(s => s.Clone())
                .ToList();

            // Transition type lives on the NEXT span keyframe. Inserting a new span KF inside an
            // existing transition therefore moves the old transition onto the inserted KF so the
            // original segment becomes prev -> inserted, while inserted -> old-next defaults to Cut
            // until the user chooses a new transition for that later gap.
            if (split.Prev != null && split.Next != null && split.Next.Time - split.Prev.Time > 0.05f)
            {
                kf.SpanTransition = split.Next.SpanTransition;
                split.Next.SpanTransition = null;
            }
        }

        _selectedElement.Keyframes.Add(kf);
        return kf;
    }

    public bool RemoveSelectedKeyframe(AlertKeyframe keyframe)
    {
        if (_selectedElement == null) return false;
        CaptureUndoSnapshot();
        return _selectedElement.Keyframes.Remove(keyframe);
    }

    public bool AddVolumeEnvelopePoint(float time, float volume)
    {
        if (_soundFile == null) return false;
        if (_volumeEnvelope.Any(k => Math.Abs(k.Time - time) < 0.05f)) return false;
        _volumeEnvelope.Add(new AudioVolumeKeyframe { Time = time, Volume = volume });
        _volumeEnvelope.Sort((a, b) => a.Time.CompareTo(b.Time));
        return true;
    }

    public bool RemoveVolumeEnvelopePoint(AudioVolumeKeyframe keyframe)
        => _volumeEnvelope.Remove(keyframe);

    public void UpdateVolumeEnvelopePoint(AudioVolumeKeyframe keyframe, float time, float volume, float maxVolume)
    {
        keyframe.Time = Math.Max(0f, time);
        keyframe.Volume = (float)Math.Clamp(volume, 0.0, maxVolume);
    }

    // Linear interpolation over a sorted envelope — mirrors the C++ EvalVolumeEnvelope
    // in layout_types.h so the editor shows exactly what OBS will play.
    public static float EvalEnvelope(List<AudioVolumeKeyframe> env, float baseVolume, float t)
    {
        if (env == null || env.Count == 0) return baseVolume;
        var sorted = env.OrderBy(k => k.Time).ToList();
        if (t <= sorted[0].Time) return sorted[0].Volume;
        if (t >= sorted[^1].Time) return sorted[^1].Volume;
        for (int i = 1; i < sorted.Count; i++)
        {
            if (t > sorted[i].Time) continue;
            float span = sorted[i].Time - sorted[i - 1].Time;
            float frac = span > 0.0001f ? (t - sorted[i - 1].Time) / span : 0f;
            return sorted[i - 1].Volume + frac * (sorted[i].Volume - sorted[i - 1].Volume);
        }
        return baseVolume;
    }

    // Insert a point, or update the existing one within the merge window — this is the
    // "keyframed volume slider" write path. Returns the affected keyframe.
    public static AudioVolumeKeyframe WriteClipVolumeEnvelopePoint(AlertElement element, float time, float volume, float maxVolume)
    {
        time = Math.Max(0f, time);
        volume = (float)Math.Clamp(volume, 0.0, maxVolume);
        var existing = element.VolumeEnvelope.FirstOrDefault(k => Math.Abs(k.Time - time) < 0.05f);
        if (existing != null) { existing.Volume = volume; return existing; }
        var kf = new AudioVolumeKeyframe { Time = time, Volume = volume };
        element.VolumeEnvelope.Add(kf);
        element.VolumeEnvelope.Sort((a, b) => a.Time.CompareTo(b.Time));
        return kf;
    }

    public AudioVolumeKeyframe? WriteMasterVolumeEnvelopePoint(float time, float volume, float maxVolume)
    {
        if (_soundFile == null) return null;
        time = Math.Max(0f, time);
        volume = (float)Math.Clamp(volume, 0.0, maxVolume);
        var existing = _volumeEnvelope.FirstOrDefault(k => Math.Abs(k.Time - time) < 0.05f);
        if (existing != null) { existing.Volume = volume; return existing; }
        var kf = new AudioVolumeKeyframe { Time = time, Volume = volume };
        _volumeEnvelope.Add(kf);
        _volumeEnvelope.Sort((a, b) => a.Time.CompareTo(b.Time));
        return kf;
    }

    public static bool AddClipVolumeEnvelopePoint(AlertElement element, float time, float volume, float maxVolume)
    {
        if (element.Type != AlertElementType.Audio) return false;
        if (element.VolumeEnvelope.Any(k => Math.Abs(k.Time - time) < 0.05f)) return false;
        element.VolumeEnvelope.Add(new AudioVolumeKeyframe
        {
            Time = time,
            Volume = (float)Math.Clamp(volume, 0.0, maxVolume),
        });
        element.VolumeEnvelope.Sort((a, b) => a.Time.CompareTo(b.Time));
        return true;
    }

    public static bool RemoveClipVolumeEnvelopePoint(AlertElement element, AudioVolumeKeyframe keyframe)
        => element.Type == AlertElementType.Audio && element.VolumeEnvelope.Remove(keyframe);

    public static void UpdateClipVolumeEnvelopePoint(AudioVolumeKeyframe keyframe, float time, float volume, float maxVolume)
    {
        keyframe.Time = Math.Max(0f, time);
        keyframe.Volume = (float)Math.Clamp(volume, 0.0, maxVolume);
    }

    public static float UpdateAudioElementStartTime(AlertElement element, float startTime)
    {
        element.StartTime = Math.Max(0f, startTime);
        return element.StartTime;
    }

    public static float UpdateAudioElementFadeIn(AlertElement element, float fadeIn)
    {
        element.FadeIn = Math.Max(0f, fadeIn);
        return element.FadeIn;
    }

    public static float UpdateAudioElementFadeOut(AlertElement element, float fadeOut)
    {
        element.FadeOut = Math.Max(0f, fadeOut);
        return element.FadeOut;
    }

    public void SetPreviewTime(float time) => PreviewTime = time;

    public void ClampPreviewTime(float maxTime)
    {
        if (PreviewTime > maxTime)
            PreviewTime = maxTime;
    }

    public void SetSoundFile(string? soundFile) => SoundFile = string.IsNullOrWhiteSpace(soundFile) ? null : soundFile.Trim();

    public void SetMasterVolume(float volume) => Volume = (float)Math.Clamp(volume, 0.0, 2.0);

    public void PrepareTemplateSave(string soundFilePath, double volume)
    {
        SetSoundFile(soundFilePath);
        SetMasterVolume((float)volume);
    }

    public static void UpdateKeyframeFromEditor(
        AlertKeyframe keyframe,
        float? time,
        float? x,
        float? y,
        float? width,
        float? height,
        float? opacity,
        float? scaleX,
        float? scaleY,
        float? rotation,
        string? fillColor)
    {
        if (time.HasValue) keyframe.Time = time.Value;
        keyframe.X = x;
        keyframe.Y = y;
        keyframe.Width = width;
        keyframe.Height = height;
        keyframe.Opacity = opacity;
        keyframe.ScaleX = scaleX;
        keyframe.ScaleY = scaleY;
        keyframe.Rotation = rotation;
        keyframe.FillColor = string.IsNullOrWhiteSpace(fillColor) ? null : fillColor.Trim();
    }

    public static string UpdateKeyframeFillColor(AlertKeyframe keyframe, string hex)
    {
        keyframe.FillColor = "#" + hex.TrimStart('#');
        return keyframe.FillColor;
    }

    public static void UpdateKeyframeEasing(AlertKeyframe keyframe, int easingIndex)
    {
        keyframe.Easing = (AlertEasing)easingIndex;
    }

    public record AnimState(float x, float y, float w, float h,
        float opacity, float scaleX, float scaleY, float rotation,
        string? fillColor = null,
        int cornerRadius = 0,
        int shadowOffX = 0, int shadowOffY = 0, int shadowBlur = 0,
        int align = 1, int vertAlign = 1);

    public AnimState EvalAnimated(AlertElement el, float t)
    {
        float x = el.X, y = el.Y, w = el.Width, h = el.Height, opacity = 1f, scaleX = 1f, scaleY = 1f, rotation = el.Rotation;
        if (el.Keyframes.Count == 0) return new(x, y, w, h, opacity, scaleX, scaleY, rotation);

        var sorted = el.Keyframes.OrderBy(k => k.Time).ToList();

        float Interp(Func<AlertKeyframe, float?> getter, float baseVal)
        {
            var withVal = sorted.Where(k => getter(k).HasValue).ToList();
            if (withVal.Count == 0) return baseVal;
            AlertKeyframe? prev = null, next = null;
            foreach (var kf in withVal)
            {
                if (kf.Time <= t) prev = kf;
                else if (next == null) { next = kf; break; }
            }
            if (prev == null) return getter(withVal[0])!.Value;
            if (next == null) return getter(prev)!.Value;
            float span = next.Time - prev.Time;
            float raw  = span > 0 ? (t - prev.Time) / span : 1f;
            float et   = ApplyEasing(next.Easing, raw);
            return getter(prev)!.Value + et * (getter(next)!.Value - getter(prev)!.Value);
        }

        x        = Interp(k => k.X,        el.X);
        y        = Interp(k => k.Y,        el.Y);
        w        = Interp(k => k.Width,    el.Width);
        h        = Interp(k => k.Height,   el.Height);
        opacity  = Interp(k => k.Opacity,  1f);
        scaleX   = Interp(k => k.ScaleX,   1f);
        scaleY   = Interp(k => k.ScaleY,   1f);
        rotation = Interp(k => k.Rotation, el.Rotation);

        string? fillColor = null;
        if (el.Type == AlertElementType.Rect || el.Type == AlertElementType.GoalBar)
        {
            var colorKfs = sorted.Where(k => !string.IsNullOrEmpty(k.FillColor)).ToList();
            if (colorKfs.Count > 0)
            {
                AlertKeyframe? prev = null, next = null;
                foreach (var kf in colorKfs)
                {
                    if (kf.Time <= t) prev = kf;
                    else if (next == null) { next = kf; break; }
                }
                if (prev == null) fillColor = next!.FillColor;
                else if (next == null) fillColor = prev.FillColor;
                else
                {
                    float span = next.Time - prev.Time;
                    float raw  = span > 0 ? (t - prev.Time) / span : 1f;
                    float et   = ApplyEasing(next.Easing, raw);
                    fillColor  = InterpolateArgbHex(prev.FillColor!, next.FillColor!, et);
                }
            }
        }

        int cornerRadius = el.CornerRadius;
        {
            var crKfs = sorted.Where(k => k.KfCornerRadius.HasValue).ToList();
            if (crKfs.Count > 0)
                cornerRadius = (int)Math.Round(Interp(k => k.KfCornerRadius.HasValue ? (float?)k.KfCornerRadius.Value : null, el.CornerRadius));
        }

        // Shadow geometry KFs — angle+dist stored; convert to offsets here
        int shadowOffX = (int)Math.Round(Math.Cos(el.ShadowAngle * Math.PI / 180.0) * el.ShadowDistance);
        int shadowOffY = (int)Math.Round(Math.Sin(el.ShadowAngle * Math.PI / 180.0) * el.ShadowDistance);
        int shadowBlur = (int)el.ShadowBlur;
        {
            // Interpolate angle+dist+blur independently then convert
            float angle = Interp(k => k.KfShadowAngle,    el.ShadowAngle);
            float dist  = Interp(k => k.KfShadowDistance, el.ShadowDistance);
            float blur  = Interp(k => k.KfShadowBlur,     el.ShadowBlur);
            bool hasGeomKfs = sorted.Any(k => k.KfShadowAngle.HasValue || k.KfShadowDistance.HasValue || k.KfShadowBlur.HasValue);
            if (hasGeomKfs) {
                shadowOffX = (int)Math.Round(Math.Cos(angle * Math.PI / 180.0) * dist);
                shadowOffY = (int)Math.Round(Math.Sin(angle * Math.PI / 180.0) * dist);
                shadowBlur = (int)Math.Round(blur);
            }
        }

        // Text alignment — step-change at KF time (lerp on ints makes no semantic sense)
        int align    = (int)el.Align;
        int vertAlign = el.VertAlign;
        {
            var alignKfs = sorted.Where(k => k.KfAlign.HasValue || k.KfVertAlign.HasValue).ToList();
            if (alignKfs.Count > 0)
            {
                AlertKeyframe? prev = null;
                foreach (var kf in alignKfs) { if (kf.Time <= t) prev = kf; }
                if (prev != null) { align = prev.KfAlign ?? align; vertAlign = prev.KfVertAlign ?? vertAlign; }
                else { align = alignKfs[0].KfAlign ?? align; vertAlign = alignKfs[0].KfVertAlign ?? vertAlign; }
            }
        }

        return new(x, y, w, h, opacity, scaleX, scaleY, rotation, fillColor, cornerRadius, shadowOffX, shadowOffY, shadowBlur, align, vertAlign);
    }

    public static float ApplyEasing(AlertEasing e, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return e switch
        {
            AlertEasing.EaseIn    => t * t,
            AlertEasing.EaseOut   => 1 - (1 - t) * (1 - t),
            AlertEasing.EaseInOut => t < .5f ? 2*t*t : 1 - 2*(1-t)*(1-t),
            AlertEasing.Bounce    => BounceEase(t),
            _                     => t,
        };
    }

    public static float BounceEase(float t)
    {
        float u = 1 - t;
        if (u < 1/2.75f)      return 1 - 7.5625f*u*u;
        if (u < 2/2.75f)      { u -= 1.5f/2.75f;  return 1-(7.5625f*u*u+.75f);   }
        if (u < 2.5f/2.75f)   { u -= 2.25f/2.75f; return 1-(7.5625f*u*u+.9375f); }
        u -= 2.625f/2.75f;    return 1-(7.5625f*u*u+.984375f);
    }

    // ── Keyframe writes ───────────────────────────────────────────────────────
    // Returns the keyframe modified/created (caller updates list UI).
    public AlertKeyframe? WritePositionToBestTarget(float? newX, float? newY, float? newW, float? newH, float? newRot)
    {
        if (_selectedElement == null) return null;

        // Drag/resize/rotate gesture: take exactly one snapshot, on the first write of the gesture,
        // BEFORE any mutation — regardless of whether the write lands on the base geometry, a new
        // keyframe, or an existing keyframe (the existing-keyframe case is the one Ctrl+Z missed).
        if (_gestureActive && !_gestureSnapshotTaken) { CaptureUndoSnapshot(); _gestureSnapshotTaken = true; }

        // No keyframes → the element is static and spans the whole alert. Move its BASE geometry
        // instead of creating a keyframe — a single keyframe would collapse its visible range to one
        // instant (which made a dragged video vanish until you keyframed it again).
        if (_selectedElement.Keyframes.Count == 0)
        {
            // Non-gesture writes (typed property boxes, flip) snapshot here; gesture writes already did above.
            if (!_gestureActive && !_dragMovedBase) { CaptureUndoSnapshot(); _dragMovedBase = true; }
            if (newX.HasValue)   _selectedElement.X        = newX.Value;
            if (newY.HasValue)   _selectedElement.Y        = newY.Value;
            if (newW.HasValue)   _selectedElement.Width    = ClampSignedMin1(newW.Value);
            if (newH.HasValue)   _selectedElement.Height   = ClampSignedMin1(newH.Value);
            if (newRot.HasValue) _selectedElement.Rotation = newRot.Value;
            return null;
        }

        if (ActiveDragKf == null
            || !_selectedElement.Keyframes.Contains(ActiveDragKf)
            || Math.Abs(ActiveDragKf.Time - _previewTime) >= 0.05f)
        {
            ActiveDragKf = _selectedElement.Keyframes
                .OrderBy(k => Math.Abs(k.Time - _previewTime))
                .FirstOrDefault(k => Math.Abs(k.Time - _previewTime) < 0.05f);

            if (ActiveDragKf == null)
            {
                if (!_gestureActive) CaptureUndoSnapshot(); // gesture writes already snapshotted above
                var st = EvalAnimated(_selectedElement, _previewTime);
                ActiveDragKf = new AlertKeyframe
                {
                    Time     = _previewTime,
                    X        = newX.HasValue    ? st.x        : null,
                    Y        = newY.HasValue    ? st.y        : null,
                    Width    = newW.HasValue    ? st.w        : null,
                    Height   = newH.HasValue    ? st.h        : null,
                    Rotation = newRot.HasValue  ? st.rotation : null,
                };
                _selectedElement.Keyframes.Add(ActiveDragKf);
            }
        }

        if (newX.HasValue)   ActiveDragKf.X        = newX;
        if (newY.HasValue)   ActiveDragKf.Y        = newY;
        if (newW.HasValue)   ActiveDragKf.Width    = newW;
        if (newH.HasValue)   ActiveDragKf.Height   = newH;
        if (newRot.HasValue) ActiveDragKf.Rotation = newRot;

        return ActiveDragKf;
    }

    // ── Text span / shadow / outline keyframe writes ──────────────────────────
    // Writes span data to the keyframe nearest the preview time (within 0.05s),
    // or creates a new keyframe. Also keeps element-level spans in sync as the
    // "fallback" (used when no span KF precedes the current time).
    public AlertKeyframe? WriteTextSpansKf(List<TextSpan> spans)
    {
        if (_selectedElement == null || _selectedElement.Type != AlertElementType.Text) return null;

        // Compare against the authored edit target, not the animated preview output. EvalSpansAt
        // can return transient TypeOn/interpolated spans that are not the persisted keyframe data.
        if (SpansEqual(GetEditableSpansAt(_selectedElement, _previewTime), spans)) return null;
        CaptureUndoSnapshot();

        var spanClones = spans.Select(s => s.Clone()).ToList();
        var previousElementSpans = _selectedElement.Spans.Select(s => s.Clone()).ToList();
        _selectedElement.Content = string.Join("", spans.Select(s => s.Text));

        // If this is the first span KF and it's not at T=0, auto-create a baseline KF at T=0
        // using el.Spans so that C# and C++ can interpolate between the baseline and this KF.
        // Without a T=0 anchor, EvalSpansAt returns EffectiveSpans(el) for all T < first KF —
        // a hard cut rather than a smooth transition.
        bool isFirstSpanKf = !_selectedElement.Keyframes.Any(k => k.Spans != null && k.Spans.Count > 0);
        if (isFirstSpanKf && _previewTime > 0.05f && previousElementSpans.Count > 0)
        {
            var baselineKf = new AlertKeyframe
            {
                Time = 0f,
                Spans = previousElementSpans
            };
            _selectedElement.Keyframes.Add(baselineKf);
        }

        // Keep the element-level spans in sync with the latest committed edit. EvalSpansAt and
        // serialization both use el.Spans as the fallback store outside explicit span KFs.
        _selectedElement.Spans = spanClones.Select(s => s.Clone()).ToList();

        var kf = _selectedElement.Keyframes
            .OrderBy(k => Math.Abs(k.Time - _previewTime))
            .FirstOrDefault(k => Math.Abs(k.Time - _previewTime) < 0.05f);
        if (kf == null)
        {
            kf = new AlertKeyframe { Time = _previewTime, Spans = spanClones.Select(s => s.Clone()).ToList() };
            _selectedElement.Keyframes.Add(kf);
        }
        else
        {
            kf.Spans = spanClones.Select(s => s.Clone()).ToList();
        }
        return kf;
    }

    public static List<TextSpan> GetEditableSpansAt(AlertElement el, float t)
    {
        var exactKf = el.Keyframes
            .Where(k => k.Spans != null && k.Spans.Count > 0)
            .OrderBy(k => Math.Abs(k.Time - t))
            .FirstOrDefault(k => Math.Abs(k.Time - t) < 0.05f);

        if (exactKf?.Spans != null && exactKf.Spans.Count > 0)
            return exactKf.Spans.Select(s => s.Clone()).ToList();

        return EffectiveSpans(el).Select(s => s.Clone()).ToList();
    }

    private static (AlertKeyframe? Prev, AlertKeyframe? Next) FindSplitSpanTransition(AlertElement el, float t)
    {
        AlertKeyframe? prev = null, next = null;
        foreach (var kf in el.Keyframes
                     .Where(k => k.Spans != null && k.Spans.Count > 0)
                     .OrderBy(k => k.Time))
        {
            if (kf.Time <= t) prev = kf;
            else { next = kf; break; }
        }

        return (prev, next);
    }

    private static bool SpansEqual(List<TextSpan> a, List<TextSpan> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var x = a[i]; var y = b[i];
            if (x.Text != y.Text || x.FontFamily != y.FontFamily || x.FontSize != y.FontSize
                || x.Bold != y.Bold || x.Italic != y.Italic
                || !string.Equals(x.Color, y.Color, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    public AlertKeyframe? WriteTextShadowKf(bool on, string argbColor)
    {
        if (_selectedElement == null || _selectedElement.Type != AlertElementType.Text) return null;
        CaptureUndoSnapshot();
        var kf = _selectedElement.Keyframes
            .OrderBy(k => Math.Abs(k.Time - _previewTime))
            .FirstOrDefault(k => Math.Abs(k.Time - _previewTime) < 0.05f);
        if (kf == null)
        {
            kf = new AlertKeyframe { Time = _previewTime, KfShadow = on, KfShadowColor = argbColor };
            _selectedElement.Keyframes.Add(kf);
        }
        else
        {
            kf.KfShadow = on;
            kf.KfShadowColor = argbColor;
        }
        return kf;
    }

    public AlertKeyframe? WriteTextOutlineKf(bool on, string argbColor, int width)
    {
        if (_selectedElement == null || _selectedElement.Type != AlertElementType.Text) return null;
        CaptureUndoSnapshot();
        var kf = _selectedElement.Keyframes
            .OrderBy(k => Math.Abs(k.Time - _previewTime))
            .FirstOrDefault(k => Math.Abs(k.Time - _previewTime) < 0.05f);
        if (kf == null)
        {
            kf = new AlertKeyframe { Time = _previewTime, KfOutline = on, KfOutlineColor = argbColor, KfOutlineWidth = width };
            _selectedElement.Keyframes.Add(kf);
        }
        else
        {
            kf.KfOutline = on;
            kf.KfOutlineColor = argbColor;
            kf.KfOutlineWidth = width;
        }
        return kf;
    }

    // Evaluate the effective spans for a text element at time t.
    // Colors are RGBA-interpolated between consecutive span KFs using character-position
    // matching (handles different span counts). el.Spans acts as an implicit T=0 anchor
    // so existing alerts interpolate correctly without needing a manual T=0 span KF.
    public List<TextSpan> EvalSpansAt(AlertElement el, float t)
    {
        var spanKfs = el.Keyframes
            .Where(k => k.Spans != null && k.Spans.Count > 0)
            .OrderBy(k => k.Time)
            .ToList();

        if (spanKfs.Count == 0) return EffectiveSpans(el);

        AlertKeyframe? prev = null, next = null;
        foreach (var k in spanKfs)
        {
            if (k.Time <= t) prev = k;
            else if (next == null) { next = k; break; }
        }

        if (prev == null)
        {
            // Before the first span KF — treat el.Spans as an implicit anchor at T=0
            // and interpolate toward the first span KF. This makes existing alerts that
            // only have one span KF animate from their baseline color rather than hard-cutting.
            var firstKf = spanKfs[0];
            if (firstKf.Time <= 0f) return firstKf.Spans!;
            float implFrac = Math.Clamp(t / firstKf.Time, 0f, 1f);
            return InterpolateSpanColors(EffectiveSpans(el), firstKf.Spans!, implFrac);
        }

        if (next == null) return prev.Spans!; // after last span KF: hold

        float span = next.Time - prev.Time;
        float frac = span > 0f ? (t - prev.Time) / span : 1f;
        frac = Math.Clamp(frac, 0f, 1f);

        // TypeOn: reveal next-KF text character by character — no color interpolation
        if (next.SpanTransition == TextTransitionType.TypeOn)
            return TruncateSpans(next.Spans!, frac);

        return InterpolateSpanColors(prev.Spans!, next.Spans!, frac);
    }

    // ── Text transition state ─────────────────────────────────────────────────

    public struct TextTransitionState
    {
        public bool InTransition;
        public TextTransitionType Type;
        public float Frac;
        public List<TextSpan> FromSpans;
        public List<TextSpan> ToSpans;
    }

    // Returns dual-pass transition info for Fade/Slide/Morph. Returns InTransition=false
    // for Cut, TypeOn (handled by EvalSpansAt), or outside a gap between span KFs.
    public TextTransitionState EvalTextTransitionState(AlertElement el, float t)
    {
        var spanKfs = el.Keyframes
            .Where(k => k.Spans != null && k.Spans.Count > 0)
            .OrderBy(k => k.Time)
            .ToList();

        if (spanKfs.Count < 2) return new TextTransitionState { ToSpans = EvalSpansAt(el, t) };

        AlertKeyframe? prev = null, next = null;
        foreach (var k in spanKfs)
        {
            if (k.Time <= t) prev = k;
            else if (next == null) { next = k; break; }
        }

        if (prev == null || next == null)
            return new TextTransitionState { ToSpans = EvalSpansAt(el, t) };

        float span = next.Time - prev.Time;
        float frac = span > 0f ? Math.Clamp((t - prev.Time) / span, 0f, 1f) : 1f;

        var type = next.SpanTransition ?? TextTransitionType.Cut;
        if (type == TextTransitionType.Cut || type == TextTransitionType.TypeOn || frac <= 0f || frac >= 1f)
            return new TextTransitionState { ToSpans = EvalSpansAt(el, t) };

        return new TextTransitionState
        {
            InTransition = true,
            Type = type,
            Frac = frac,
            FromSpans = prev.Spans!,
            ToSpans = next.Spans!,
        };
    }

    private static List<TextSpan> TruncateSpans(List<TextSpan> spans, float frac)
    {
        int total = spans.Sum(s => s.Text.Length);
        int visible = Math.Clamp((int)Math.Floor(total * frac), 0, total);
        var result = new List<TextSpan>();
        int remaining = visible;
        foreach (var sp in spans)
        {
            if (remaining <= 0) break;
            if (remaining >= sp.Text.Length)
            {
                result.Add(sp.Clone());
                remaining -= sp.Text.Length;
            }
            else
            {
                var partial = sp.Clone();
                partial.Text = sp.Text[..remaining];
                result.Add(partial);
                break;
            }
        }
        if (result.Count > 0) return result;
        var empty = spans[0].Clone(); empty.Text = ""; return new List<TextSpan> { empty };
    }

    // Interpolate span colors from 'from' to 'to' at fraction frac (0=from, 1=to).
    // Uses character-position matching so span count differences are handled:
    // each FROM span's color animates toward the color of whichever TO span covers
    // the same character position. Text/font/bold/italic are taken from FROM spans.
    private static List<TextSpan> InterpolateSpanColors(List<TextSpan> from, List<TextSpan> to, float frac)
    {
        static string ColorAtChar(List<TextSpan> spans, int charPos)
        {
            int pos = 0;
            foreach (var s in spans)
            {
                if (charPos < pos + s.Text.Length) return s.Color ?? "#FFFFFFFF";
                pos += s.Text.Length;
            }
            return spans.Count > 0 ? (spans[^1].Color ?? "#FFFFFFFF") : "#FFFFFFFF";
        }

        static byte Lerp(string ca, string cb, int shift, float f)
        {
            uint a = ParseArgbStatic(ca); uint b = ParseArgbStatic(cb);
            int av = (int)((a >> shift) & 0xFF); int bv = (int)((b >> shift) & 0xFF);
            return (byte)(av + f * (bv - av) + 0.5f);
        }

        var result = new List<TextSpan>(from.Count);
        int charOffset = 0;
        foreach (var p in from)
        {
            string toColor = ColorAtChar(to, charOffset);
            result.Add(new TextSpan
            {
                Text       = p.Text,
                FontFamily = p.FontFamily,
                FontSize   = p.FontSize,
                Bold       = p.Bold,
                Italic     = p.Italic,
                Color      = $"#{Lerp(p.Color ?? "#FFFFFFFF", toColor, 24, frac):X2}{Lerp(p.Color ?? "#FFFFFFFF", toColor, 16, frac):X2}{Lerp(p.Color ?? "#FFFFFFFF", toColor, 8, frac):X2}{Lerp(p.Color ?? "#FFFFFFFF", toColor, 0, frac):X2}",
            });
            charOffset += p.Text.Length;
        }
        return result;
    }

    private static uint ParseArgbStatic(string hex)
    {
        var h = hex.TrimStart('#');
        try
        {
            if (h.Length == 8) return Convert.ToUInt32(h, 16);
            if (h.Length == 6) return 0xFF000000u | Convert.ToUInt32(h, 16);
        }
        catch { }
        return 0xFFFFFFFFu;
    }

    // Returns the keyframe written (caller updates list UI).
    public AlertKeyframe? WriteOpacityKf(float opacity)
    {
        if (_selectedElement == null) return null;
        CaptureUndoSnapshot();
        var kf = _selectedElement.Keyframes
            .OrderBy(k => Math.Abs(k.Time - _previewTime))
            .FirstOrDefault(k => Math.Abs(k.Time - _previewTime) < 0.05f);
        if (kf == null)
        {
            kf = new AlertKeyframe { Time = _previewTime, Opacity = opacity };
            _selectedElement.Keyframes.Add(kf);
        }
        else
        {
            kf.Opacity = opacity;
        }
        return kf;
    }

    public AlertKeyframe? WriteFillColorKf(string argbHex)
    {
        if (_selectedElement == null) return null;
        CaptureUndoSnapshot();
        var kf = _selectedElement.Keyframes
            .OrderBy(k => Math.Abs(k.Time - _previewTime))
            .FirstOrDefault(k => Math.Abs(k.Time - _previewTime) < 0.05f);
        if (kf == null)
        {
            kf = new AlertKeyframe { Time = _previewTime, FillColor = argbHex };
            _selectedElement.Keyframes.Add(kf);
        }
        else
        {
            kf.FillColor = argbHex;
        }
        return kf;
    }

    public AlertKeyframe? WriteCornerRadiusKf(int radius)
    {
        if (_selectedElement == null) return null;
        CaptureUndoSnapshot();
        var kf = _selectedElement.Keyframes
            .OrderBy(k => Math.Abs(k.Time - _previewTime))
            .FirstOrDefault(k => Math.Abs(k.Time - _previewTime) < 0.05f);
        if (kf == null)
        {
            kf = new AlertKeyframe { Time = _previewTime, KfCornerRadius = radius };
            _selectedElement.Keyframes.Add(kf);
        }
        else
        {
            kf.KfCornerRadius = radius;
        }
        return kf;
    }

    public AlertKeyframe? WriteShadowGeometryKf(float angle, float distance, float blur)
    {
        if (_selectedElement == null) return null;
        CaptureUndoSnapshot();
        var kf = _selectedElement.Keyframes
            .OrderBy(k => Math.Abs(k.Time - _previewTime))
            .FirstOrDefault(k => Math.Abs(k.Time - _previewTime) < 0.05f);
        if (kf == null)
        {
            kf = new AlertKeyframe { Time = _previewTime, KfShadowAngle = angle, KfShadowDistance = distance, KfShadowBlur = blur };
            _selectedElement.Keyframes.Add(kf);
        }
        else
        {
            kf.KfShadowAngle    = angle;
            kf.KfShadowDistance = distance;
            kf.KfShadowBlur     = blur;
        }
        return kf;
    }

    public AlertKeyframe? WriteTextAlignKf(int hAlign, int vAlign)
    {
        if (_selectedElement == null) return null;
        CaptureUndoSnapshot();
        var kf = _selectedElement.Keyframes
            .OrderBy(k => Math.Abs(k.Time - _previewTime))
            .FirstOrDefault(k => Math.Abs(k.Time - _previewTime) < 0.05f);
        if (kf == null)
        {
            kf = new AlertKeyframe { Time = _previewTime, KfAlign = hAlign, KfVertAlign = vAlign };
            _selectedElement.Keyframes.Add(kf);
        }
        else
        {
            kf.KfAlign    = hAlign;
            kf.KfVertAlign = vAlign;
        }
        return kf;
    }

    // Sets the SpanTransition on the KF that immediately FOLLOWS prevKf in el.Keyframes (span KFs only).
    public static void SetSpanTransitionOnNextKf(AlertElement el, AlertKeyframe nextKf, TextTransitionType type)
    {
        nextKf.SpanTransition = type == TextTransitionType.Cut ? null : type;
    }

    public void ApplySpanTransition(AlertElement el, AlertKeyframe nextKf, TextTransitionType type)
    {
        if (el == null || nextKf == null) return;
        CaptureUndoSnapshot();
        SetSpanTransitionOnNextKf(el, nextKf, type);
    }

    // Sets the transition on EVERY span keyframe of the element in one go — the quick path so you
    // don't have to set each keyframe individually. Returns the number of keyframes changed.
    public int SetAllSpanTransitions(AlertElement el, TextTransitionType type)
    {
        if (el == null) return 0;
        var spanKfs = el.Keyframes.Where(k => k.Spans?.Count > 0).ToList();
        if (spanKfs.Count == 0) return 0;
        CaptureUndoSnapshot();
        foreach (var kf in spanKfs)
            kf.SpanTransition = type == TextTransitionType.Cut ? null : type;
        return spanKfs.Count;
    }

    public static string TransitionDisplayName(TextTransitionType t) => t switch
    {
        TextTransitionType.Cut       => "Cut",
        TextTransitionType.TypeOn    => "Type On",
        TextTransitionType.SlideLeft => "Slide Left",
        TextTransitionType.SlideRight=> "Slide Right",
        TextTransitionType.Fade      => "Fade",
        TextTransitionType.Morph     => "Morph",
        _                            => t.ToString(),
    };

    // ── Sound loading (no WPF — custom WAV parser + file IO) ─────────────────
    public void LoadSoundDuration(string path)
    {
        _soundDurationSec = 0;
        _waveformSamples  = Array.Empty<float>();
        if (!File.Exists(path)) return;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);
            var riff = new string(br.ReadChars(4));
            if (riff != "RIFF") return;
            br.ReadInt32();
            var wave = new string(br.ReadChars(4));
            if (wave != "WAVE") return;

            int sampleRate = 44100, channels = 1, bitsPerSample = 16;
            while (fs.Position < fs.Length - 8)
            {
                var chunkId   = new string(br.ReadChars(4));
                int chunkSize = br.ReadInt32();
                long chunkEnd = fs.Position + chunkSize;
                if (chunkId == "fmt ")
                {
                    br.ReadInt16();
                    channels      = br.ReadInt16();
                    sampleRate    = br.ReadInt32();
                    br.ReadInt32(); br.ReadInt16();
                    bitsPerSample = br.ReadInt16();
                }
                else if (chunkId == "data")
                {
                    int bytesPerSample = bitsPerSample / 8;
                    int totalFrames    = chunkSize / (bytesPerSample * channels);
                    _soundDurationSec  = (double)totalFrames / sampleRate;

                    int step = Math.Max(1, totalFrames / 800);
                    var pts  = new List<float>();
                    byte[] raw = br.ReadBytes(chunkSize);
                    for (int i = 0; i < totalFrames; i += step)
                    {
                        float peak = 0;
                        for (int ch = 0; ch < channels; ch++)
                        {
                            int idx = (i * channels + ch) * bytesPerSample;
                            if (idx + bytesPerSample > raw.Length) break;
                            float s = bitsPerSample switch
                            {
                                16 => BitConverter.ToInt16(raw, idx) / 32768f,
                                8  => (raw[idx] - 128) / 128f,
                                32 => BitConverter.ToSingle(raw, idx),
                                _  => 0f
                            };
                            peak = Math.Max(peak, Math.Abs(s));
                        }
                        pts.Add(peak);
                    }
                    _waveformSamples = pts.ToArray();
                    return;
                }
                fs.Seek(chunkEnd, SeekOrigin.Begin);
            }
        }
        catch { }
    }

    // ── Template folder ───────────────────────────────────────────────────────
    public static string TemplateFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "Steaming", "templates");

    public static string EnsureTemplateFolder()
    {
        var f = TemplateFolder;
        Directory.CreateDirectory(f);
        return f;
    }

    public void SaveTemplateToFile(string path)
    {
        _layout.SoundFile      = string.IsNullOrWhiteSpace(_soundFile) ? null : _soundFile;
        _layout.Volume         = (float)Math.Clamp((double)_volume, 0.0, 2.0);
        _layout.VolumeEnvelope = _volumeEnvelope.OrderBy(k => k.Time).ToList();
        File.WriteAllText(path, _layout.ToJson(), Encoding.UTF8);
    }

    public static AlertLayout? LoadTemplateFromFile(string path)
    {
        var json = File.ReadAllText(path, Encoding.UTF8);
        return AlertLayout.FromJson(json);
    }

    public void MergeLayout(AlertLayout other)
    {
        foreach (var el in other.Elements)
        {
            el.ZOrder = NextZOrder();
            _layout.Elements.Add(el);
        }
    }

    public void ReplaceLayout(AlertLayout other)
    {
        _layout = DeepClone(other);
        _soundFile = other.SoundFile;
        _volumeEnvelope = other.VolumeEnvelope?
            .Select(k => new AudioVolumeKeyframe { Time = k.Time, Volume = k.Volume }).ToList() ?? new();
        _selectedElement = null;
        ActiveDragKf = null;
    }

    // ── Colour utilities (no WPF — pure string/math) ──────────────────────────
    public static string InterpolateArgbHex(string a, string b, float t)
    {
        static (int A, int R, int G, int B) Parse(string hex)
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 6) hex = "FF" + hex;
            int v = int.Parse(hex, System.Globalization.NumberStyles.HexNumber);
            return ((v>>24)&0xFF, (v>>16)&0xFF, (v>>8)&0xFF, v&0xFF);
        }
        int Lerp(int x, int y) => (int)(x + (y - x) * t);
        var (aA,aR,aG,aB) = Parse(a);
        var (bA,bR,bG,bB) = Parse(b);
        return $"#{Lerp(aA,bA):X2}{Lerp(aR,bR):X2}{Lerp(aG,bG):X2}{Lerp(aB,bB):X2}";
    }

    // opacity scale is 0-100 (matches PropFillOpacity / PropShadowOpacity slider range)
    public static (string rgb, double opacity) ArgbToRgbAndOpacity(string argb)
    {
        argb = argb.TrimStart('#');
        if (argb.Length == 6) return ($"#{argb.ToUpper()}", 100.0);
        if (argb.Length != 8) return ("#FFFFFF", 100.0);
        int a = Convert.ToInt32(argb[..2], 16);
        return ($"#{argb[2..].ToUpper()}", Math.Round(a / 255.0 * 100.0, 1));
    }

    public static string RgbAndOpacityToArgb(string rgb, double opacity)
    {
        rgb = rgb.TrimStart('#').PadRight(6, '0')[..6].ToUpper();
        int a = Math.Clamp((int)Math.Round(opacity / 100.0 * 255), 0, 255);
        return $"#{a:X2}{rgb}";
    }

    // ── Save / commit ─────────────────────────────────────────────────────────
    public void CommitSave(string soundFilePath, double volume, double duration)
    {
        _layout.SoundFile      = string.IsNullOrWhiteSpace(soundFilePath) ? null : soundFilePath.Trim();
        _layout.Volume         = (float)Math.Clamp(volume, 0.0, 2.0);
        _layout.VolumeEnvelope = _volumeEnvelope.OrderBy(k => k.Time).ToList();
        Result         = _layout;
        ResultSoundFile = _layout.SoundFile;
        ResultDuration = (float)Math.Max(0.5, duration > 0 ? duration : _duration);
    }
}
