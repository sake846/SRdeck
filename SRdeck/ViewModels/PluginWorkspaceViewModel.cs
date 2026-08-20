using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Wpf;
using SRdeck.Services.Plugins;

namespace SRdeck.ViewModels;

public sealed partial class PluginWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly IPluginManager _pluginManager;
    private readonly Dictionary<string, (FrameworkElement Main, FrameworkElement? Settings)> _views =
        new(StringComparer.Ordinal);

    [ObservableProperty] private FrameworkElement? _activeContent;
    [ObservableProperty] private FrameworkElement? _settingsContent;
    [ObservableProperty] private string _status = "プラグインが選択されていません。";
    [ObservableProperty] private bool _hasActiveContent;

    public PluginWorkspaceViewModel(IPluginManager pluginManager)
    {
        _pluginManager = pluginManager;
        _pluginManager.RuntimeChanged += HandleRuntimeChanged;
        Refresh();
    }

    public IReadOnlyList<PluginRuntimeInfo> Plugins => _pluginManager.Plugins;

    public async ValueTask<PluginOperationResult> ActivateAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        PluginOperationResult result = await _pluginManager.ActivateAsync(pluginId, cancellationToken);
        RefreshOnUiThread();
        return result;
    }

    public void Dispose()
    {
        _pluginManager.RuntimeChanged -= HandleRuntimeChanged;
        _views.Clear();
    }

    private void HandleRuntimeChanged(object? sender, PluginRuntimeChangedEventArgs e) => RefreshOnUiThread();

    private void RefreshOnUiThread()
    {
        System.Windows.Threading.Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && dispatcher.Thread.IsAlive && !dispatcher.HasShutdownStarted)
        {
            if (!dispatcher.CheckAccess())
            {
                try
                {
                    _ = dispatcher.InvokeAsync(Refresh);
                    return;
                }
                catch (InvalidOperationException)
                {
                    // Fallback to synchronous refresh if dispatcher post fails
                }
            }
        }
        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(Plugins));
        string? activeId = _pluginManager.ActivePluginId;
        if (activeId is null ||
            !_pluginManager.TryGetActiveCapability<IPluginViewProvider>(out IPluginViewProvider? provider) ||
            provider is null)
        {
            ActiveContent = null;
            SettingsContent = null;
            HasActiveContent = false;
            PluginRuntimeInfo? fault = _pluginManager.Plugins.FirstOrDefault(info => info.State == PluginLifecycleState.Faulted);
            PluginRuntimeInfo? rejected = _pluginManager.Plugins.FirstOrDefault(info =>
                info.State == PluginLifecycleState.Initialized && !string.IsNullOrWhiteSpace(info.LastError));
            PluginRuntimeInfo? active = activeId is null
                ? null
                : _pluginManager.Plugins.FirstOrDefault(info => info.Descriptor.Id == activeId);
            Status = fault?.LastError
                ?? rejected?.LastError
                ?? (active is null
                    ? "プラグインが選択されていません。"
                    : $"{active.Descriptor.DisplayName}（専用画面なし）");
            return;
        }

        if (!_views.TryGetValue(activeId, out (FrameworkElement Main, FrameworkElement? Settings) views))
        {
            views = (provider.CreateMainView(), provider.CreateSettingsView());
            _views.Add(activeId, views);
        }
        ActiveContent = views.Main;
        SettingsContent = views.Settings;
        HasActiveContent = true;
        Status = _pluginManager.Plugins.First(info => info.Descriptor.Id == activeId).Descriptor.DisplayName;
    }
}
