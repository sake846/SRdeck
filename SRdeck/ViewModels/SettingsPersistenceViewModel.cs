using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SRdeck.Configuration;
using SRdeck.Services;

namespace SRdeck.ViewModels;

/// <summary>Surfaces persistence failures without showing a dialog on every repeated save.</summary>
public partial class SettingsPersistenceViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsPersistenceNotifications? _settings;
    private readonly ISettingsPersistenceNotifications? _lastState;
    private readonly IDialogService _dialogs;
    private readonly HashSet<string> _shown = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIssue))]
    private string _message = "";
    public bool HasIssue => !string.IsNullOrEmpty(Message);

    public SettingsPersistenceViewModel(ISettingsService settings, ILastStateService lastState, IDialogService dialogs)
    {
        _settings = settings as ISettingsPersistenceNotifications;
        _lastState = lastState as ISettingsPersistenceNotifications;
        _dialogs = dialogs;
        if (_settings is not null) _settings.PersistenceIssue += OnIssue;
        if (_lastState is not null) _lastState.PersistenceIssue += OnIssue;
    }

    private void OnIssue(SettingsPersistenceIssue issue)
    {
        void Publish()
        {
            if (_disposed) return;
            Message = $"{Path.GetFileName(issue.FilePath)}: {issue.Message}";
            if (_shown.Add($"{issue.FilePath}|{issue.Operation}|{issue.IsError}"))
                _dialogs.ShowMessage($"{Message}\n\n{issue.FilePath}\n{issue.Exception?.Message}",
                    issue.IsError ? "設定ファイルのエラー" : "設定ファイルの復旧");
        }

        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            // Queue even on the UI thread: a notification may arrive while a file
            // lock is held or before the main window has finished constructing.
            if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke((Action)Publish);
        }
        else Publish();
    }

    public Task FlushNotificationsAsync()
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        return dispatcher is null || dispatcher.HasShutdownStarted
            ? Task.CompletedTask
            : dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background).Task;
    }

    public void Dispose()
    {
        _disposed = true;
        if (_settings is not null) _settings.PersistenceIssue -= OnIssue;
        if (_lastState is not null) _lastState.PersistenceIssue -= OnIssue;
    }
}
