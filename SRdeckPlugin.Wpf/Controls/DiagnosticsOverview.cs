using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.Wpf.Controls;

/// <summary>
/// Compact, fixed diagnostic summary shared by all plugin views. The control
/// polls host-owned runtime diagnostics so plugins do not duplicate queue and
/// processing-load presentation logic.
/// </summary>
public sealed class DiagnosticsOverview : Control
{
    private readonly DispatcherTimer refreshTimer;

    public static readonly DependencyProperty StatusKindProperty = DependencyProperty.Register(
        nameof(StatusKind), typeof(OverallStatusKind), typeof(DiagnosticsOverview),
        new FrameworkPropertyMetadata(OverallStatusKind.Idle, OnPresentationStatusChanged));

    public static readonly DependencyProperty StatusTextProperty = DependencyProperty.Register(
        nameof(StatusText), typeof(string), typeof(DiagnosticsOverview),
        new FrameworkPropertyMetadata("入力待ち", OnPresentationStatusChanged));

    public static readonly DependencyProperty PhaseTextProperty = DependencyProperty.Register(
        nameof(PhaseText), typeof(string), typeof(DiagnosticsOverview),
        new FrameworkPropertyMetadata(string.Empty));

    public static readonly DependencyProperty LastUpdatedTextProperty = DependencyProperty.Register(
        nameof(LastUpdatedText), typeof(string), typeof(DiagnosticsOverview),
        new FrameworkPropertyMetadata(string.Empty));

    public static readonly DependencyProperty SummaryTextProperty = DependencyProperty.Register(
        nameof(SummaryText), typeof(string), typeof(DiagnosticsOverview),
        new FrameworkPropertyMetadata(string.Empty));

    public static readonly DependencyProperty RecommendationTextProperty = DependencyProperty.Register(
        nameof(RecommendationText), typeof(string), typeof(DiagnosticsOverview),
        new FrameworkPropertyMetadata(string.Empty));

    public static readonly DependencyProperty ChannelTextProperty = DependencyProperty.Register(
        nameof(ChannelText), typeof(string), typeof(DiagnosticsOverview),
        new FrameworkPropertyMetadata(string.Empty));

    public static readonly DependencyProperty RuntimeDiagnosticsProperty = DependencyProperty.Register(
        nameof(RuntimeDiagnostics), typeof(IPluginRuntimeDiagnostics), typeof(DiagnosticsOverview),
        new FrameworkPropertyMetadata(NullPluginRuntimeDiagnostics.Instance, OnRuntimeDiagnosticsChanged));

    private static readonly DependencyPropertyKey RuntimeSummaryTextPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(RuntimeSummaryText), typeof(string), typeof(DiagnosticsOverview),
            new FrameworkPropertyMetadata("負荷 測定待ち ｜ 実行 -- ｜ 待ち 0 ｜ ドロップ 0"));

    public static readonly DependencyProperty RuntimeSummaryTextProperty =
        RuntimeSummaryTextPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey RuntimeToolTipTextPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(RuntimeToolTipText), typeof(string), typeof(DiagnosticsOverview),
            new FrameworkPropertyMetadata(string.Empty));

    public static readonly DependencyProperty RuntimeToolTipTextProperty =
        RuntimeToolTipTextPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey EffectiveStatusKindPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(EffectiveStatusKind), typeof(OverallStatusKind), typeof(DiagnosticsOverview),
            new FrameworkPropertyMetadata(OverallStatusKind.Idle));

    public static readonly DependencyProperty EffectiveStatusKindProperty =
        EffectiveStatusKindPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey EffectiveStatusTextPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(EffectiveStatusText), typeof(string), typeof(DiagnosticsOverview),
            new FrameworkPropertyMetadata("入力待ち"));

    public static readonly DependencyProperty EffectiveStatusTextProperty =
        EffectiveStatusTextPropertyKey.DependencyProperty;

    public OverallStatusKind StatusKind
    {
        get => (OverallStatusKind)GetValue(StatusKindProperty);
        set => SetValue(StatusKindProperty, value);
    }

    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public string PhaseText
    {
        get => (string)GetValue(PhaseTextProperty);
        set => SetValue(PhaseTextProperty, value);
    }

    public string LastUpdatedText
    {
        get => (string)GetValue(LastUpdatedTextProperty);
        set => SetValue(LastUpdatedTextProperty, value);
    }

    public string SummaryText
    {
        get => (string)GetValue(SummaryTextProperty);
        set => SetValue(SummaryTextProperty, value);
    }

    public string RecommendationText
    {
        get => (string)GetValue(RecommendationTextProperty);
        set => SetValue(RecommendationTextProperty, value);
    }

    public string ChannelText
    {
        get => (string)GetValue(ChannelTextProperty);
        set => SetValue(ChannelTextProperty, value);
    }

    public IPluginRuntimeDiagnostics RuntimeDiagnostics
    {
        get => (IPluginRuntimeDiagnostics)GetValue(RuntimeDiagnosticsProperty);
        set => SetValue(RuntimeDiagnosticsProperty, value);
    }

    public string RuntimeSummaryText => (string)GetValue(RuntimeSummaryTextProperty);
    public string RuntimeToolTipText => (string)GetValue(RuntimeToolTipTextProperty);
    public OverallStatusKind EffectiveStatusKind =>
        (OverallStatusKind)GetValue(EffectiveStatusKindProperty);
    public string EffectiveStatusText => (string)GetValue(EffectiveStatusTextProperty);

    static DiagnosticsOverview()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(DiagnosticsOverview),
            new FrameworkPropertyMetadata(typeof(DiagnosticsOverview)));
    }

    public DiagnosticsOverview()
    {
        refreshTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(500),
            DispatcherPriority.Background,
            (_, _) => RefreshRuntimeDiagnostics(),
            Dispatcher)
        {
            IsEnabled = false
        };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshRuntimeDiagnostics();
        refreshTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => refreshTimer.Stop();

    private static void OnRuntimeDiagnosticsChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        ((DiagnosticsOverview)dependencyObject).RefreshRuntimeDiagnostics();
    }

    private static void OnPresentationStatusChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        ((DiagnosticsOverview)dependencyObject).RefreshRuntimeDiagnostics();
    }

    private void RefreshRuntimeDiagnostics()
    {
        IPluginRuntimeDiagnostics source =
            RuntimeDiagnostics ?? NullPluginRuntimeDiagnostics.Instance;
        PluginRuntimeDiagnosticsSnapshot value;
        try
        {
            value = source.GetSnapshot();
        }
        catch
        {
            value = default;
        }
        string execution = ExecutionSummary(value.ProcessingStages);

        if (value.SubmittedBlocks == 0 && value.ProcessedBlocks == 0 &&
            value.DroppedBlocks == 0)
        {
            ApplyEffectiveStatus(StatusKind, StatusText);
            SetValue(RuntimeSummaryTextPropertyKey,
                $"負荷 測定待ち ｜ 実行 {execution} ｜ 待ち 0 ｜ ドロップ 0");
            SetValue(RuntimeToolTipTextPropertyKey, "IQ入力開始後にリアルタイム処理負荷を測定します。");
            return;
        }

        double currentLoad = LoadPercent(
            value.CurrentProcessingTimeMs, value.CurrentBlockDurationMs);
        double averageLoad = LoadPercent(
            value.AverageProcessingTimeMs, value.CurrentBlockDurationMs);
        double maximumLoad = LoadPercent(
            value.MaximumProcessingTimeMs, value.CurrentBlockDurationMs);
        string current = double.IsFinite(currentLoad) ? $"{currentLoad:F0}%" : "--";

        OverallStatusKind runtimeKind = StatusKind;
        string runtimeStatus = StatusText;
        if (StatusKind is not (OverallStatusKind.Error or OverallStatusKind.Critical))
        {
            if (!string.IsNullOrWhiteSpace(value.LastError))
            {
                runtimeKind = OverallStatusKind.Error;
                runtimeStatus = "エラー";
            }
            else if (value.DroppedBlocks > 0 || currentLoad >= 100 || averageLoad >= 100)
            {
                runtimeKind = OverallStatusKind.Critical;
                runtimeStatus = "過負荷";
            }
            else if (value.QueueDepth > 0 || currentLoad >= 75 || averageLoad >= 75)
            {
                runtimeKind = OverallStatusKind.Warning;
                runtimeStatus = "注意";
            }
        }
        ApplyEffectiveStatus(runtimeKind, runtimeStatus);

        SetValue(RuntimeSummaryTextPropertyKey,
            $"負荷 {current} ｜ 実行 {execution} ｜ 待ち {value.QueueDepth:N0} ｜ ドロップ {value.DroppedBlocks:N0}");

        string lastSuccess = value.LastSuccessfulProcessingUtc is DateTimeOffset time
            ? time.ToLocalTime().ToString("HH:mm:ss")
            : "--";
        string error = string.IsNullOrWhiteSpace(value.LastError) ? "なし" : value.LastError;
        SetValue(RuntimeToolTipTextPropertyKey,
            $"リアルタイム処理負荷（処理時間÷入力時間枠）\n" +
            $"現在 {FormatPercent(currentLoad)} / 平均 {FormatPercent(averageLoad)} / 最大 {FormatPercent(maximumLoad)}\n" +
            $"処理時間 {value.CurrentProcessingTimeMs:F1} ms / 入力時間枠 {value.CurrentBlockDurationMs:F1} ms\n" +
            $"処理待ち 現在 {value.QueueDepth:N0} / 最大 {value.MaximumQueueDepth:N0} block\n" +
            $"ドロップ {value.DroppedBlocks:N0} block / {value.DroppedSamples:N0} sample\n" +
            $"最終成功 {lastSuccess} / 最終エラー {error}");
    }

    private static double LoadPercent(double processingTimeMs, double blockDurationMs) =>
        blockDurationMs > 0 ? processingTimeMs * 100.0 / blockDurationMs : double.NaN;

    private static string FormatPercent(double value) =>
        double.IsFinite(value) ? $"{value:F1}%" : "--";

    private static string ExecutionSummary(
        IReadOnlyList<PluginProcessingStageSnapshot>? stages)
    {
        if (stages is null || stages.Count == 0) return "--";
        bool cpu = stages.Any(stage => stage.Device is PluginComputeDevice.Cpu or PluginComputeDevice.Mixed);
        bool gpu = stages.Any(stage => stage.Device is PluginComputeDevice.Gpu or PluginComputeDevice.Mixed);
        bool automatic = stages.Any(stage => stage.Device == PluginComputeDevice.Unknown);
        string known = (cpu, gpu) switch
        {
            (true, true) => "CPU+GPU",
            (true, false) => "CPU",
            (false, true) => "GPU",
            _ => string.Empty
        };
        if (automatic)
            return string.IsNullOrEmpty(known) ? "自動" : $"{known}+自動";
        return string.IsNullOrEmpty(known) ? "不明" : known;
    }

    private void ApplyEffectiveStatus(OverallStatusKind kind, string? text)
    {
        SetValue(EffectiveStatusKindPropertyKey, kind);
        SetValue(EffectiveStatusTextPropertyKey,
            string.IsNullOrWhiteSpace(text) ? "入力待ち" : text);
    }
}
