using System.Reflection;
using System.Windows;
using System.Windows.Media;

namespace SRdeck.Views;

/// <summary>
/// SplashWindow.xaml の相互作用ロジック
/// </summary>
public partial class SplashWindow : Window
{
    public event EventHandler? ExitRequested;

    public SplashWindow()
    {
        InitializeComponent();
        SetVersionInfo(GetVersionText());
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private static string GetVersionText()
    {
        return typeof(SplashWindow).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "SRdeckVersion")?.Value
            ?? throw new InvalidOperationException("The SRdeck version metadata is missing.");
    }

    public void SetVersionInfo(string versionText)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetVersionInfo(versionText));
            return;
        }

        VersionInfoText.Text = versionText;
    }

    public void SetCalibrationStatus(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetCalibrationStatus(text));
            return;
        }

        CalibrationStatusText.Text = text;
        CalibrationStatusText.Foreground = new SolidColorBrush(Color.FromRgb(169, 182, 166));
    }
}
