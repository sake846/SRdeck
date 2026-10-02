using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SRdeck.Models;

namespace SRdeck.Views;

public partial class BandPlanEditorDialog : Window
{
    internal BandPlanEditorDialog(BandPlanItem? initialBand = null)
    {
        InitializeComponent();
        ModeComboBox.ItemsSource = FrequencyCatalogDialog.Modes;
        BandPlan = initialBand is null
            ? new BandPlanItem
            {
                StartHz = 7_000_000,
                EndHz = 7_200_000,
                Label = "New Band",
                Color = "#3C708CA0",
                DefaultStepHz = 1_000,
                Mode = "不明"
            }
            : new BandPlanItem
            {
                StartHz = initialBand.StartHz,
                EndHz = initialBand.EndHz,
                Label = initialBand.Label,
                Color = initialBand.Color,
                DefaultStepHz = initialBand.DefaultStepHz,
                Mode = initialBand.Mode
            };

        StartFrequencyTextBox.Text = BandPlan.StartHz.ToString(CultureInfo.InvariantCulture);
        EndFrequencyTextBox.Text = BandPlan.EndHz.ToString(CultureInfo.InvariantCulture);
        LabelTextBox.Text = BandPlan.Label;
        ColorNumberTextBox.Text = ColorToNumber(BandPlan.Color).ToString(CultureInfo.InvariantCulture);
        StepTextBox.Text = BandPlan.DefaultStepHz.ToString(CultureInfo.InvariantCulture);
        ModeComboBox.SelectedItem = FrequencyCatalogDialog.NormalizeMode(BandPlan.Mode);
    }

    public BandPlanItem BandPlan { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowTheme.ApplyDarkTitleBar(this);
    }

    internal static string ColorNumberToHex(double colorNumber)
    {
        double hue = colorNumber / 60 * 360;
        double section = (hue == 360 ? 0 : hue) / 60;
        int index = (int)Math.Floor(section);
        double fraction = section - index;
        const double saturation = 0.5;
        const double value = 0.62745;
        double p = value * (1 - saturation);
        double q = value * (1 - saturation * fraction);
        double t = value * (1 - saturation * (1 - fraction));
        (double red, double green, double blue) = index switch
        {
            0 => (value, t, p),
            1 => (q, value, p),
            2 => (p, value, t),
            3 => (p, q, value),
            4 => (t, p, value),
            _ => (value, p, q)
        };
        return $"#3C{ToByte(red):X2}{ToByte(green):X2}{ToByte(blue):X2}";
    }

    private static int ColorToNumber(string colorText)
    {
        try
        {
            if (ColorConverter.ConvertFromString(colorText) is not Color color) return 0;
            double red = color.R / 255d;
            double green = color.G / 255d;
            double blue = color.B / 255d;
            double maximum = Math.Max(red, Math.Max(green, blue));
            double minimum = Math.Min(red, Math.Min(green, blue));
            double delta = maximum - minimum;
            double hue = delta == 0 ? 0
                : maximum == red ? 60 * (((green - blue) / delta) % 6)
                : maximum == green ? 60 * (((blue - red) / delta) + 2)
                : 60 * (((red - green) / delta) + 4);
            if (hue < 0) hue += 360;
            return (int)Math.Clamp(Math.Round(hue / 360 * 60), 0, 60);
        }
        catch (FormatException)
        {
            return 0;
        }
    }

    private static int ToByte(double value) => (int)Math.Clamp(Math.Round(value * 255), 0, 255);

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        long startHz = 0;
        long endHz = 0;
        int stepHz = 0;
        double colorNumber = 0;
        bool valid = long.TryParse(StartFrequencyTextBox.Text.Replace(",", ""), out startHz) &&
            long.TryParse(EndFrequencyTextBox.Text.Replace(",", ""), out endHz) &&
            int.TryParse(StepTextBox.Text.Replace(",", ""), out stepHz) &&
            double.TryParse(ColorNumberTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out colorNumber) &&
            startHz >= 1 && endHz <= int.MaxValue && startHz < endHz &&
            stepHz is >= 1 and <= 1_000_000 && double.IsFinite(colorNumber) &&
            colorNumber is >= 0 and <= 60 &&
            !string.IsNullOrWhiteSpace(LabelTextBox.Text);
        if (!valid)
        {
            ShowError("周波数範囲、バンド名、カラー番号（0～60）、ステップを確認してください。");
            return;
        }

        BandPlan.StartHz = startHz;
        BandPlan.EndHz = endHz;
        BandPlan.Label = LabelTextBox.Text.Trim();
        BandPlan.Color = ColorNumberToHex(colorNumber);
        BandPlan.DefaultStepHz = stepHz;
        BandPlan.Mode = ModeComboBox.SelectedItem?.ToString() ?? "不明";
        DialogResult = true;
    }

    private void ColorNumberTextBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (ColorPreviewBorder is null) return;
        if (double.TryParse(ColorNumberTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double colorNumber) &&
            colorNumber is >= 0 and <= 60)
            ColorPreviewBorder.Background = (Brush)new BrushConverter().ConvertFromString(ColorNumberToHex(colorNumber))!;
        else
            ColorPreviewBorder.Background = Brushes.Transparent;
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
