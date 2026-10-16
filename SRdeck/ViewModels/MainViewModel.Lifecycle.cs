using CommunityToolkit.Mvvm.ComponentModel;
using SRdeck.Models;

namespace SRdeck.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public async Task ClosingAsync()
    {
        _lifetimeEnded = true;
        try
        {
            await DetachLifetimeSubscriptionsAsync();
            await Diagnostics.StopLoadCaptureAsync();

            using (SRdeck.Services.ShutdownDiagnosticLog.Scope("PluginManager.ShutdownAsync"))
            {
                await _pluginManager.ShutdownAsync();
            }

            using (SRdeck.Services.ShutdownDiagnosticLog.Scope("SaveLastStateAndSettings"))
            {
                PersistFftBatchState(FftBatchMode);
                _statePersistence.SaveShutdownState(_engine.Control);
                await SettingsPersistence.FlushNotificationsAsync();
            }
        }
        finally
        {
            SettingsPersistence.Dispose();
        }
    }

    public void OnRendering()
    {
        if (_engine?.NeedsBackgroundRedraw == true) _engine.NeedsBackgroundRedraw = false;
    }
}
