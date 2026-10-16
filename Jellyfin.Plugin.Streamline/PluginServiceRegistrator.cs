using Jellyfin.Plugin.Streamline.ScheduledTasks;
using Jellyfin.Plugin.Streamline.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Streamline;

/// <summary>
/// Registers plugin services with Jellyfin's dependency injection container.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<WatchlistService>();
        serviceCollection.AddSingleton<TrailerService>();
        serviceCollection.AddSingleton<TrailerFinder>();
        serviceCollection.AddSingleton<IdentityService>();
        serviceCollection.AddSingleton<ComingSoonService>();
        serviceCollection.AddSingleton<CollectionService>();
        serviceCollection.AddSingleton<IScheduledTask, CollectionsTask>();
        serviceCollection.AddSingleton<SeerrService>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<SeerrService>());
        serviceCollection.AddSingleton<IScheduledTask, IdentityTask>();
        serviceCollection.AddSingleton<MetadataGapService>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<MetadataGapService>());
        serviceCollection.AddSingleton<IScheduledTask, MetadataGapTask>();
        serviceCollection.AddSingleton<IScheduledTask, FrontendRegistrationStartupTask>();
    }
}
