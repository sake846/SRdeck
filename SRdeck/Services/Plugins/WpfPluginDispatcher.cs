using System.Windows;
using System.Windows.Threading;
using SRdeckPlugin.Contracts;

namespace SRdeck.Services.Plugins;

public sealed class WpfPluginDispatcher : IPluginDispatcher
{
    public bool CheckAccess() =>
        Application.Current?.Dispatcher.CheckAccess() ?? true;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }
        // Plugins can publish a message for every received frame.  Keeping those updates
        // below Render prevents a busy plugin pane from starving the spectrum/waterfall.
        _ = dispatcher.InvokeAsync(action, DispatcherPriority.Background);
    }
}
