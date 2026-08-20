using System.Windows;

namespace SRdeckPlugin.Wpf;

/// <summary>
/// Optional WPF presentation capability. DSP-only and headless plugins do not
/// need to reference this assembly.
/// </summary>
public interface IPluginViewProvider
{
    FrameworkElement CreateMainView();
    FrameworkElement? CreateSettingsView();
}
