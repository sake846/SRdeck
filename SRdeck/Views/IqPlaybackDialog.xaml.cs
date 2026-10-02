using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using SRdeck.Audio;

namespace SRdeck.Views;

public partial class IqPlaybackDialog : Window, INotifyPropertyChanged
{
    private readonly TimeSpan _duration;
    private string _errorText = string.Empty;

    internal IqPlaybackDialog(string filePath, IqPlaybackFileInfo fileInfo)
    {
        InitializeComponent();
        DataContext = this;
        FilePath = filePath;
        _duration = fileInfo.Duration;
        FormatText = $"16 bit PCM / I・Q 2チャンネル / {fileInfo.SampleRateHz / 1_000.0:0.###} kS/s / {fileInfo.Duration.TotalSeconds:0.###} 秒";
        IsCenterFrequencyEditable = fileInfo.CenterFrequencyHz is null;
        CenterFrequencyMhz = fileInfo.CenterFrequencyHz is int center
            ? (center / 1_000_000.0).ToString("0.######", CultureInfo.InvariantCulture)
            : string.Empty;
        CenterFrequencyHint = fileInfo.CenterFrequencyHz is null
            ? "付帯情報がないため、録音時のRF中心周波数を入力してください。現在のSDR周波数は流用しません。"
            : $"{fileInfo.CenterFrequencySource} から復元しました。";
    }

    public string FilePath { get; }
    public string FormatText { get; }
    public bool IsCenterFrequencyEditable { get; }
    public string CenterFrequencyHint { get; }
    public string CenterFrequencyMhz { get; set; }
    public string StartSeconds { get; set; } = "0";
    public int ResultCenterFrequencyHz { get; private set; }
    public double ResultStartSeconds { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowTheme.ApplyDarkTitleBar(this);
    }

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (_errorText == value) return;
            _errorText = value;
            OnPropertyChanged();
        }
    }

    private void Play_OnClick(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(CenterFrequencyMhz, NumberStyles.Float, CultureInfo.InvariantCulture,
                out double centerMhz) || centerMhz <= 0 || centerMhz * 1_000_000 > int.MaxValue)
        {
            ErrorText = "中心周波数を MHz 単位で入力してください。";
            return;
        }
        if (!double.TryParse(StartSeconds, NumberStyles.Float, CultureInfo.InvariantCulture,
                out double startSeconds) || startSeconds < 0 || startSeconds > _duration.TotalSeconds)
        {
            ErrorText = $"開始位置は 0～{_duration.TotalSeconds:0.###} 秒で入力してください。";
            return;
        }

        ResultCenterFrequencyHz = checked((int)Math.Round(centerMhz * 1_000_000));
        ResultStartSeconds = startSeconds;
        DialogResult = true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new(propertyName));
}
