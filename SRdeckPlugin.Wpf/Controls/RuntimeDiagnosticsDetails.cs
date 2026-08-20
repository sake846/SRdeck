using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.Wpf.Controls;

/// <summary>Expanded view of host-owned real-time processing diagnostics.</summary>
public sealed class RuntimeDiagnosticsDetails : Control
{
    private readonly DispatcherTimer refreshTimer;

    public static readonly DependencyProperty RuntimeDiagnosticsProperty = DependencyProperty.Register(
        nameof(RuntimeDiagnostics), typeof(IPluginRuntimeDiagnostics), typeof(RuntimeDiagnosticsDetails),
        new FrameworkPropertyMetadata(NullPluginRuntimeDiagnostics.Instance, OnSourceChanged));

    private static readonly DependencyPropertyKey LoadTextPropertyKey =
        RegisterReadOnly(nameof(LoadText), "現在 -- / 平均 -- / 最大 --");
    private static readonly DependencyPropertyKey ProcessingTextPropertyKey =
        RegisterReadOnly(nameof(ProcessingText), "--");
    private static readonly DependencyPropertyKey QueueTextPropertyKey =
        RegisterReadOnly(nameof(QueueText), "現在 0 / 最大 0 block");
    private static readonly DependencyPropertyKey DropTextPropertyKey =
        RegisterReadOnly(nameof(DropText), "0 block / 0 sample");
    private static readonly DependencyPropertyKey LastProcessingTextPropertyKey =
        RegisterReadOnly(nameof(LastProcessingText), "--");
    private static readonly DependencyPropertyKey ProcessingStagesTextPropertyKey =
        RegisterReadOnly(nameof(ProcessingStagesText), "入力開始後に処理経路を表示します。");
    private static readonly DependencyPropertyKey ProcessingStagesToolTipTextPropertyKey =
        RegisterReadOnly(nameof(ProcessingStagesToolTipText), string.Empty);

    public static readonly DependencyProperty LoadTextProperty = LoadTextPropertyKey.DependencyProperty;
    public static readonly DependencyProperty ProcessingTextProperty = ProcessingTextPropertyKey.DependencyProperty;
    public static readonly DependencyProperty QueueTextProperty = QueueTextPropertyKey.DependencyProperty;
    public static readonly DependencyProperty DropTextProperty = DropTextPropertyKey.DependencyProperty;
    public static readonly DependencyProperty LastProcessingTextProperty =
        LastProcessingTextPropertyKey.DependencyProperty;
    public static readonly DependencyProperty ProcessingStagesTextProperty =
        ProcessingStagesTextPropertyKey.DependencyProperty;
    public static readonly DependencyProperty ProcessingStagesToolTipTextProperty =
        ProcessingStagesToolTipTextPropertyKey.DependencyProperty;

    public IPluginRuntimeDiagnostics RuntimeDiagnostics
    {
        get => (IPluginRuntimeDiagnostics)GetValue(RuntimeDiagnosticsProperty);
        set => SetValue(RuntimeDiagnosticsProperty, value);
    }

    public string LoadText => (string)GetValue(LoadTextProperty);
    public string ProcessingText => (string)GetValue(ProcessingTextProperty);
    public string QueueText => (string)GetValue(QueueTextProperty);
    public string DropText => (string)GetValue(DropTextProperty);
    public string LastProcessingText => (string)GetValue(LastProcessingTextProperty);
    public string ProcessingStagesText => (string)GetValue(ProcessingStagesTextProperty);
    public string ProcessingStagesToolTipText =>
        (string)GetValue(ProcessingStagesToolTipTextProperty);

    static RuntimeDiagnosticsDetails()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(RuntimeDiagnosticsDetails),
            new FrameworkPropertyMetadata(typeof(RuntimeDiagnosticsDetails)));
    }

    public RuntimeDiagnosticsDetails()
    {
        refreshTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(500),
            DispatcherPriority.Background,
            (_, _) => Refresh(),
            Dispatcher)
        {
            IsEnabled = false
        };
        Loaded += (_, _) =>
        {
            Refresh();
            refreshTimer.Start();
        };
        Unloaded += (_, _) => refreshTimer.Stop();
    }

    private static DependencyPropertyKey RegisterReadOnly(string name, string defaultValue) =>
        DependencyProperty.RegisterReadOnly(
            name, typeof(string), typeof(RuntimeDiagnosticsDetails),
            new FrameworkPropertyMetadata(defaultValue));

    private static void OnSourceChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args) =>
        ((RuntimeDiagnosticsDetails)dependencyObject).Refresh();

    private void Refresh()
    {
        PluginRuntimeDiagnosticsSnapshot value;
        try
        {
            value = (RuntimeDiagnostics ?? NullPluginRuntimeDiagnostics.Instance).GetSnapshot();
        }
        catch
        {
            value = default;
        }

        double current = Load(value.CurrentProcessingTimeMs, value.CurrentBlockDurationMs);
        double average = Load(value.AverageProcessingTimeMs, value.CurrentBlockDurationMs);
        double maximum = Load(value.MaximumProcessingTimeMs, value.CurrentBlockDurationMs);
        SetValue(LoadTextPropertyKey,
            $"現在 {Percent(current)} / 平均 {Percent(average)} / 最大 {Percent(maximum)}");
        SetValue(ProcessingTextPropertyKey,
            $"{value.CurrentProcessingTimeMs:F1} ms / 入力時間枠 {value.CurrentBlockDurationMs:F1} ms");
        SetValue(QueueTextPropertyKey,
            $"現在 {value.QueueDepth:N0} / 最大 {value.MaximumQueueDepth:N0} block");
        SetValue(DropTextPropertyKey,
            $"{value.DroppedBlocks:N0} block / {value.DroppedSamples:N0} sample");
        ApplyProcessingStages(value.ProcessingStages, value.CurrentBlockDurationMs);

        string last = value.LastSuccessfulProcessingUtc is DateTimeOffset time
            ? time.ToLocalTime().ToString("HH:mm:ss")
            : "--";
        string error = string.IsNullOrWhiteSpace(value.LastError) ? "なし" : value.LastError;
        SetValue(LastProcessingTextPropertyKey,
            $"最終成功 {last} / 最終エラー {error}");
    }

    private void ApplyProcessingStages(
        IReadOnlyList<PluginProcessingStageSnapshot>? stages,
        double blockDurationMs)
    {
        if (stages is null || stages.Count == 0)
        {
            SetValue(ProcessingStagesTextPropertyKey, "入力開始後に処理経路を表示します。");
            SetValue(ProcessingStagesToolTipTextPropertyKey, string.Empty);
            return;
        }

        string[] lines = stages.Select(stage =>
        {
            string timing = stage.MeasurementCount == 0
                ? "測定待ち"
                : $"現在 {stage.CurrentProcessingTimeMs:F1} ms" +
                  $"（枠 {BudgetPercent(stage.CurrentProcessingTimeMs, blockDurationMs)}）" +
                  $" / 平均 {stage.AverageProcessingTimeMs:F1} ms";
            return $"[{DeviceLabel(stage.Device)}] {stage.Operation} ｜ {timing} ｜ {stage.Backend}";
        }).ToArray();
        SetValue(ProcessingStagesTextPropertyKey, string.Join(Environment.NewLine, lines));

        string[] details = stages
            .Where(stage => !string.IsNullOrWhiteSpace(stage.Detail))
            .Select(stage => $"{stage.Operation}: {stage.Detail}")
            .ToArray();
        SetValue(ProcessingStagesToolTipTextPropertyKey, string.Join(Environment.NewLine, details));
    }

    private static double Load(double processingTimeMs, double blockDurationMs) =>
        blockDurationMs > 0 ? processingTimeMs * 100.0 / blockDurationMs : double.NaN;

    private static string Percent(double value) =>
        double.IsFinite(value) ? $"{value:F1}%" : "--";

    private static string BudgetPercent(double processingTimeMs, double blockDurationMs) =>
        blockDurationMs > 0 ? $"{processingTimeMs * 100.0 / blockDurationMs:F0}%" : "--";

    private static string DeviceLabel(PluginComputeDevice device) => device switch
    {
        PluginComputeDevice.Cpu => "CPU",
        PluginComputeDevice.Gpu => "GPU",
        PluginComputeDevice.Mixed => "CPU+GPU",
        _ => "不明"
    };
}
