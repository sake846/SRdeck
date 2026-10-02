using System.Globalization;
using System.Windows;
using SRdeck.Models;

namespace SRdeck.Views;

public partial class StationEditorDialog : Window
{
    internal StationEditorDialog(StationItem? initialStation = null)
    {
        InitializeComponent();
        ModeComboBox.ItemsSource = FrequencyCatalogDialog.Modes;
        Station = initialStation is null
            ? new StationItem { Comment = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss") }
            : new StationItem
            {
                FrequencyHz = initialStation.FrequencyHz,
                Mode = initialStation.Mode,
                Name = initialStation.Name,
                Comment = initialStation.Comment
            };
        FrequencyTextBox.Text = Station.FrequencyHz > 0
            ? Station.FrequencyHz.ToString(CultureInfo.InvariantCulture)
            : "";
        NameTextBox.Text = Station.Name;
        ModeComboBox.SelectedItem = FrequencyCatalogDialog.NormalizeMode(Station.Mode);
    }

    public StationItem Station { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowTheme.ApplyDarkTitleBar(this);
    }

    internal static bool TryParseFrequency(string input, out long frequencyHz)
    {
        frequencyHz = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;

        string value = input.Trim().ToLowerInvariant().Replace(",", "");
        double multiplier = 1;
        foreach ((string suffix, double factor) in new[]
                 {
                     ("mhz", 1_000_000d), ("khz", 1_000d), ("hz", 1d),
                     ("m", 1_000_000d), ("k", 1_000d)
                 })
        {
            if (!value.EndsWith(suffix, StringComparison.Ordinal)) continue;
            multiplier = factor;
            value = value[..^suffix.Length];
            break;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ||
            !double.IsFinite(parsed))
            return false;
        double hz = parsed * multiplier;
        if (hz is < 1 or > int.MaxValue) return false;
        frequencyHz = (long)hz;
        return true;
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryParseFrequency(FrequencyTextBox.Text, out long frequencyHz) ||
            string.IsNullOrWhiteSpace(NameTextBox.Text))
        {
            ShowError("周波数と局名を正しく入力してください。\n周波数は「10M」や「100k」等も入力可能です。");
            return;
        }

        Station.FrequencyHz = frequencyHz;
        Station.Name = NameTextBox.Text.Trim();
        Station.Mode = ModeComboBox.SelectedItem?.ToString() ?? "不明";
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorTextBlock.Text = message;
        ErrorOverlay.Visibility = Visibility.Visible;
        System.Media.SystemSounds.Hand.Play();
    }

    private void CloseError_OnClick(object sender, RoutedEventArgs e) => ErrorOverlay.Visibility = Visibility.Collapsed;
    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
