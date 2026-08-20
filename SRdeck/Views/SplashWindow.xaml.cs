using System;
using System.Windows;
using System.Windows.Media;

namespace SRdeck.Views;

/// <summary>
/// SplashWindow.xaml の相互作用ロジック
/// </summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        SetVersionInfo(DateTime.Today.ToShortDateString(), "Ver.1.0.0");
    }

    public void SetVersionInfo(string dateText, string versionText = "Ver.1.0.0")
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetVersionInfo(dateText, versionText));
            return;
        }

        VersionInfoText.Text = $"{versionText} ({dateText})";
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
