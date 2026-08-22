using System.Windows.Media;

namespace SRdeckPlugin.Wpf;

/// <summary>
/// Shared display accent colors for plugin-owned WPF views.
/// Receive-band overlays use <see cref="PluginReceiverBandColors"/> instead.
/// </summary>
public static class PluginDisplayColors
{
    public static readonly Color Primary = Color.FromRgb(0x55, 0xC8, 0xD8);

    // Secondary and tertiary keep the primary accent's HSV saturation (about 60.6%)
    // while using pink and orange hues respectively.
    public static readonly Color Secondary = Color.FromRgb(0xFE, 0x64, 0x98);
    public static readonly Color Tertiary = Color.FromRgb(0xBB, 0x93, 0x4A);

    public static string WithAlpha(byte alpha, Color color) =>
        $"#{alpha:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
}
