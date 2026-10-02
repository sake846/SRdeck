using System.Windows;

namespace SRdeckPlugin.Wpf;

public static class PluginHost
{
    public static readonly DependencyProperty SdrControlProperty = DependencyProperty.RegisterAttached(
        "SdrControl",
        typeof(object),
        typeof(PluginHost),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    public static object? GetSdrControl(DependencyObject element) =>
        element.GetValue(SdrControlProperty);

    public static void SetSdrControl(DependencyObject element, object? value) =>
        element.SetValue(SdrControlProperty, value);
}
