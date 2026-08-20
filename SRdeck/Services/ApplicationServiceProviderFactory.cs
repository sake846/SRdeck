using Microsoft.Extensions.DependencyInjection;
using SRdeckPlugin.Contracts;

namespace SRdeck.Services;

internal static class ApplicationServiceProviderFactory
{
    public static ServiceProvider Create(IReadOnlyList<IPluginModule> pluginModules) =>
        new ServiceCollection()
            .AddPluginServices(pluginModules)
            .AddRadioServices()
            .AddSignalProcessingServices()
            .AddPresentationServices()
            .AddApplicationStartupServices()
            .BuildServiceProvider();
}
