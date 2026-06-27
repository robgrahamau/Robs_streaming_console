using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SkiaSharp;
using Steaming.Application.ViewModels;
using Steaming.Data;

namespace Steaming.WinUI.Pages;

public sealed partial class AnalyticsPage : Page
{
    private MainViewModel?       _vm;
    private AnalyticsRepository? _repo;

    private List<AnalyticsRepository.SessionTrend> _lastTrends = [];
    private string _trendMetric   = "Average Viewers";
    private string _sessionMetric = "Average Viewers";

    private List<ViewerSnapshot> _lastSnapshots        = [];
    private string               _lastSessionPlatform  = "";
    private int _loadDataVersion;
    private int _loadSessionChartVersion;

    // Date range — null means no filter (all time)
    private DateTimeOffset? _fromDate;
    private DateTimeOffset? _toDate;

    private static readonly SKColor Gray   = SKColor.Parse("#888888");
    private static readonly SKColor Purple = SKColor.Parse("#9146FF");
    private static readonly SKColor Green  = SKColor.Parse("#53FC18");
    private static readonly SKColor Blue   = SKColor.Parse("#2196F3");
    private static readonly SKColor Red    = SKColor.Parse("#FF0000"); // YouTube

    private static SKColor PlatformColor(string? platform) => platform switch
    {
        "Twitch"  => Purple,
        "Kick"    => Green,
        "YouTube" => Red,
        _         => Blue,
    };

    public AnalyticsPage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not MainViewModel vm) return;
        _vm   = vm;
        _repo = new AnalyticsRepository();
        LoadData();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Interlocked.Increment(ref _loadDataVersion);
        Interlocked.Increment(ref _loadSessionChartVersion);
        _repo?.Dispose();
        _repo = null;
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void PlatformFilter_Changed(object sender, SelectionChangedEventArgs e) => LoadData();

    private void TrendMetric_Changed(object sender, SelectionChangedEventArgs e)
    {
        _trendMetric = MetricFromIndex(TrendMetricPicker.SelectedIndex);
        if (_lastTrends.Count > 0) BuildOverviewChart(_lastTrends);
    }

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is SessionRow row)
            LoadSessionChart(row.Id, row.Platform, row.Session);
    }

    private void SessionMetric_Changed(object sender, SelectionChangedEventArgs e)
    {
        _sessionMetric = SessionMetricPicker.SelectedIndex switch
        {
            1 => "Chat Messages",
            _ => "Average Viewers",
        };
        if (_lastSnapshots.Count > 0)
            BuildSessionChart(_lastSnapshots, _lastSessionPlatform);
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        var days = int.Parse(tag);
        if (days == 0)
        {
            _fromDate = null;
            _toDate   = null;
            DateFrom.Date = null;
            DateTo.Date   = null;
        }
        else
        {
            _fromDate = DateTimeOffset.UtcNow.AddDays(-days);
            _toDate   = null;
            DateFrom.Date = _fromDate.Value.LocalDateTime;
            DateTo.Date   = null;
        }
        LoadData();
    }

    private void DateRange_Changed(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs e)
    {
        _fromDate = DateFrom.Date.HasValue
            ? new DateTimeOffset(DateFrom.Date.Value.Date, TimeSpan.Zero)
            : null;
        _toDate = DateTo.Date.HasValue
            ? new DateTimeOffset(DateTo.Date.Value.Date.AddDays(1).AddTicks(-1), TimeSpan.Zero)
            : null;
        LoadData();
    }

    // ── Data loading ──────────────────────────────────────────────────────────

    // Filter tokens: null = All (T+K+Y); a single name = Single; an "A+B" token = Dual.
    private string? SelectedPlatformFilter() =>
        PlatformFilter.SelectedIndex switch
        {
            1 => "Twitch",
            2 => "Kick",
            3 => "YouTube",
            4 => "Twitch+Kick",
            5 => "Twitch+YouTube",
            6 => "Kick+YouTube",
            _ => null,
        };

    // True when the current filter spans more than one platform (All or any Dual pair) — those views
    // get the grouped per-platform breakdown and the combined merged-snapshot chart.
    private static bool IsCombinedFilter(string? platform)
        => platform is null || platform.Contains('+');

    private static List<string> IncludedPlatforms(string? platform) => platform switch
    {
        null => new() { "Twitch", "Kick", "YouTube" },
        _    => platform.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
    };

    private static string MetricFromIndex(int idx) => idx switch
    {
        0 => "Average Viewers",
        1 => "Peak Viewers",
        2 => "Follows",
        3 => "Subs",
        4 => "Unique Chatters",
        5 => "Duration (min)",
        _ => "Average Viewers",
    };

    private void LoadData()
    {
        if (_repo == null) return;
        var platform = SelectedPlatformFilter();
        var repo = _repo;
        int requestVersion = Interlocked.Increment(ref _loadDataVersion);
        _ = Task.Run(() =>
        {
            try
            {
                var sessions = repo.GetSessions(200, platform, _fromDate, _toDate);
                var stats    = repo.GetAllTimeStats(platform, _fromDate, _toDate);
                var trends   = repo.GetSessionTrends(60, _fromDate, _toDate, platform);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_repo == null || requestVersion != _loadDataVersion)
                        return;

                    TotalStreamsLabel.Text  = stats.TotalStreams.ToString();
                    TotalTimeLabel.Text    = FormatTime(stats.TotalStreamTime);
                    TotalFollowsLabel.Text = stats.TotalFollows.ToString();
                    TotalSubsLabel.Text    = stats.TotalSubs.ToString();
                    PeakViewersLabel.Text  = stats.PeakViewers.ToString();
                    AvgViewersLabel.Text   = stats.AvgViewers > 0 ? stats.AvgViewers.ToString("N1") : "—";

                    SessionList.ItemsSource = sessions.Select(s => new SessionRow
                    {
                        Id             = s.Id,
                        Session        = s,
                        Date           = s.StartedAt.ToLocalTime().ToString("MM-dd HH:mm"),
                        Platform       = s.Platform,
                        Duration       = FormatTime(s.Duration),
                        PeakViewers    = s.PeakViewers.ToString(),
                        AvgViewers     = s.AvgViewers.ToString("F1"),
                        Follows        = s.TotalFollows,
                        Subs           = s.TotalSubs,
                        UniqueChatters = s.UniqueChatters,
                        Title          = s.Title,
                    }).ToList();

                    _lastTrends = trends;
                    BuildOverviewChart(trends);
                });
            }
            catch (Exception) when (_repo == null) { }
        });
    }

    private void BuildOverviewChart(List<AnalyticsRepository.SessionTrend> trends)
    {
        var platform = SelectedPlatformFilter();
        var labels   = trends.Select(t => t.Date.ToLocalTime().ToString("dd/MM")).ToList();
        var series   = new List<ISeries>();

        bool isViewerMetric = _trendMetric is "Average Viewers" or "Peak Viewers";

        if (isViewerMetric && IsCombinedFilter(platform))
        {
            // Combined view (All or a Dual pair): Total + a grouped bar per included platform.
            series.Add(new ColumnSeries<double>
            {
                Name   = "Total",
                Values = ExtractMetric(trends, _trendMetric),
                Fill   = new SolidColorPaint(Blue),
                Stroke = null,
            });
            foreach (var p in IncludedPlatforms(platform))
                series.Add(new ColumnSeries<double>
                {
                    Name   = p,
                    Values = ExtractPlatformMetric(trends, _trendMetric, p),
                    Fill   = new SolidColorPaint(PlatformColor(p)),
                    Stroke = null,
                });
        }
        else
        {
            series.Add(new ColumnSeries<double>
            {
                Name   = _trendMetric,
                Values = ExtractMetric(trends, _trendMetric),
                Fill   = new SolidColorPaint(PlatformColor(platform)),
                Stroke = null,
            });
        }

        TrendChart.Series = series.ToArray();
        TrendChart.XAxes = new Axis[]
        {
            new Axis { Labels = labels, LabelsPaint = new SolidColorPaint(Gray), TextSize = 10 }
        };
        TrendChart.YAxes = new Axis[]
        {
            new Axis { LabelsPaint = new SolidColorPaint(Gray), TextSize = 10, MinLimit = 0 }
        };
    }

    private void LoadSessionChart(long sessionId, string platform, StreamSession session)
    {
        StatDuration.Text    = FormatTime(session.Duration);
        StatAvgViewers.Text  = session.AvgViewers.ToString("F1");
        StatPeakViewers.Text = session.PeakViewers.ToString();
        StatChatters.Text    = session.UniqueChatters.ToString();
        StatFollows.Text     = session.TotalFollows.ToString();
        StatSubs.Text        = session.TotalSubs.ToString();

        var titlePart    = string.IsNullOrWhiteSpace(session.Title)    ? "" : $" — {session.Title}";
        var categoryPart = string.IsNullOrWhiteSpace(session.Category) ? "" : $" ({session.Category})";
        ViewerChartTitle.Text = $"{session.StartedAt.ToLocalTime():dd MMM yyyy HH:mm}{titlePart}{categoryPart}";

        SessionPlaceholder.Visibility = Visibility.Collapsed;
        SessionDetailPanel.Visibility = Visibility.Visible;

        if (_repo == null) return;
        var repo = _repo;
        int requestVersion = Interlocked.Increment(ref _loadSessionChartVersion);
        _ = Task.Run(() =>
        {
            try
            {
                var snapshots = session.MergedSessionIds is { Count: > 1 }
                    ? repo.GetMergedSnapshots(session.MergedSessionIds)
                    : repo.GetSnapshots(sessionId);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_repo == null || requestVersion != _loadSessionChartVersion)
                        return;

                    _lastSnapshots       = snapshots;
                    _lastSessionPlatform = platform;
                    if (snapshots.Count == 0)
                    {
                        ViewerChart.Series = Array.Empty<ISeries>();
                        return;
                    }
                    BuildSessionChart(snapshots, platform);
                });
            }
            catch (Exception) when (_repo == null) { }
        });
    }

    private void BuildSessionChart(List<ViewerSnapshot> snapshots, string platform)
    {
        var labels = snapshots.Select(s => s.Timestamp.ToLocalTime().ToString("HH:mm")).ToList();
        var series = new List<ISeries>();

        if (_sessionMetric == "Chat Messages")
        {
            series.Add(new ColumnSeries<double>
            {
                Name   = "Chat Messages",
                Values = snapshots.Select(s => (double)s.ChatCount).ToArray(),
                Fill   = new SolidColorPaint(Purple),
                Stroke = null,
            });
        }
        else
        {
            series.Add(new LineSeries<double>
            {
                Name         = "Total",
                Values       = snapshots.Select(s => (double)s.Total).ToArray(),
                Stroke       = new SolidColorPaint(Blue, 2),
                Fill         = new SolidColorPaint(SKColor.Parse("#202196F3")),
                GeometrySize = 0,
            });
            // Draw a per-platform line whenever that platform actually has data in the cluster, so
            // Single / Dual / All all render the right lines without special-casing the label.
            if (snapshots.Any(s => s.TwitchViewers > 0))
                series.Add(new LineSeries<double>
                {
                    Name         = "Twitch",
                    Values       = snapshots.Select(s => (double)s.TwitchViewers).ToArray(),
                    Stroke       = new SolidColorPaint(Purple, 1.5f),
                    Fill         = null,
                    GeometrySize = 0,
                });
            if (snapshots.Any(s => s.KickViewers > 0))
                series.Add(new LineSeries<double>
                {
                    Name         = "Kick",
                    Values       = snapshots.Select(s => (double)s.KickViewers).ToArray(),
                    Stroke       = new SolidColorPaint(Green, 1.5f),
                    Fill         = null,
                    GeometrySize = 0,
                });
            if (snapshots.Any(s => s.YouTubeViewers > 0))
                series.Add(new LineSeries<double>
                {
                    Name         = "YouTube",
                    Values       = snapshots.Select(s => (double)s.YouTubeViewers).ToArray(),
                    Stroke       = new SolidColorPaint(Red, 1.5f),
                    Fill         = null,
                    GeometrySize = 0,
                });
        }

        ViewerChart.Series = series.ToArray();
        ViewerChart.XAxes  = new Axis[] { new Axis { Labels = labels, LabelsPaint = new SolidColorPaint(Gray), TextSize = 10 } };
        ViewerChart.YAxes  = new Axis[] { new Axis { LabelsPaint = new SolidColorPaint(Gray), TextSize = 10, MinLimit = 0 } };
    }

    // ── Metric extraction ─────────────────────────────────────────────────────

    private static double[] ExtractMetric(List<AnalyticsRepository.SessionTrend> trends, string metric) =>
        metric switch
        {
            "Average Viewers"  => trends.Select(t => t.AvgViewers).ToArray(),
            "Peak Viewers"     => trends.Select(t => (double)t.PeakViewers).ToArray(),
            "Follows"          => trends.Select(t => (double)t.Follows).ToArray(),
            "Subs"             => trends.Select(t => (double)t.Subs).ToArray(),
            "Unique Chatters"  => trends.Select(t => (double)t.UniqueChatters).ToArray(),
            "Duration (min)"   => trends.Select(t => t.DurationSecs / 60.0).ToArray(),
            _                  => trends.Select(t => t.AvgViewers).ToArray(),
        };

    private static double[] ExtractPlatformMetric(List<AnalyticsRepository.SessionTrend> trends,
        string metric, string platform) =>
        metric switch
        {
            "Average Viewers" => platform switch
            {
                "Twitch"  => trends.Select(t => t.TwitchAvgViewers).ToArray(),
                "Kick"    => trends.Select(t => t.KickAvgViewers).ToArray(),
                "YouTube" => trends.Select(t => t.YouTubeAvgViewers).ToArray(),
                _         => trends.Select(_ => 0.0).ToArray(),
            },
            "Peak Viewers" => platform switch
            {
                "Twitch"  => trends.Select(t => (double)t.TwitchPeakViewers).ToArray(),
                "Kick"    => trends.Select(t => (double)t.KickPeakViewers).ToArray(),
                "YouTube" => trends.Select(t => (double)t.YouTubePeakViewers).ToArray(),
                _         => trends.Select(_ => 0.0).ToArray(),
            },
            _ => trends.Select(_ => 0.0).ToArray(),
        };

    private static string FormatTime(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:D2}m";
        return $"{t.Minutes}m";
    }

    private sealed class SessionRow
    {
        public long          Id             { get; init; }
        public StreamSession Session        { get; init; } = null!;
        public string        Date           { get; init; } = "";
        public string        Platform       { get; init; } = "";
        public string        Duration       { get; init; } = "";
        public string        PeakViewers    { get; init; } = "";
        public string        AvgViewers     { get; init; } = "";
        public int           Follows        { get; init; }
        public int           Subs           { get; init; }
        public int           UniqueChatters { get; init; }
        public string        Title          { get; init; } = "";
    }
}
