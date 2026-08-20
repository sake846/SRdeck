using System.Windows.Media;

namespace SRdeckPlugin.Wpf;

/// <summary>
/// Shared display accent colors for plugin-owned WPF views.
/// Receive-band overlays use <see cref="PluginReceiverBandColors"/> instead.
/// </summary>
public static class PluginDisplayColors
{
    public static readonly Color Primary = Color.FromRgb(0x55, 0xC8, 0xD8);
    public static readonly Color Secondary = Color.FromRgb(0xDD, 0x94, 0x4B);
    public static readonly Color Tertiary = Color.FromRgb(0xAA, 0x98, 0xE6);

    public static string WithAlpha(byte alpha, Color color) =>
        $"#{alpha:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
}
