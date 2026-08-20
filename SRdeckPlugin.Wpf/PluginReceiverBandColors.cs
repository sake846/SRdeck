using System.Windows.Media;

namespace SRdeckPlugin.Wpf;

/// <summary>
/// Colors for receive-band overlays drawn on the host spectrum view.
/// These colors are intentionally independent from the common plugin display
/// accent colors used by WPF views.
/// </summary>
public static class PluginReceiverBandColors
{
    public static readonly Color Primary = Color.FromRgb(0x1E, 0x88, 0xE5); // Blue
    public static readonly Color Secondary = Color.FromRgb(0x00, 0x96, 0x88); // Blue-green

    public static string WithAlpha(byte alpha, Color color) =>
        $"#{alpha:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
}
