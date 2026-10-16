using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Streamline.Services;

/// <summary>
/// Makes sure new movies and shows end up with genres, keywords and a trailer, however they
/// arrived (Radarr/Sonarr after a Seerr request, or copied in by hand: all of them come in
/// through Jellyfin's library scan). Jellyfin fetches that metadata itself while scanning, but
/// it can come up short: a brand-new release often has no trailer or keywords on TMDb yet, or a
/// lookup fails. A while after each new item is added, anything still missing is fetched again
/// (filling gaps only, never overwriting), and <see cref="ScheduledTasks.MetadataGapTask"/>
/// keeps retrying recent additions daily, since TMDb fills these in over the following weeks.
/// </summary>
public sealed class MetadataGapService : IHostedService, IDisposable
{
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(12);

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<MetadataGapService> _logger;
    private readonly TrailerFinder _trailerFinder;
    private readonly IdentityService _identity;
    private readonly SeerrService _seerr;
    private readonly ConcurrentDictionary<Guid, DateTime> _added = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _lastAttempt = new();
    private Timer? _timer;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataGapService"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="providerManager">Jellyfin's metadata provider manager.</param>
    /// <param name="fileSystem">Jellyfin's file system abstraction.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="trailerFinder">Finds trailers TMDb doesn't have.</param>
    /// <param name="identity">Fixes wrongly identified titles.</param>
    /// <param name="seerr">Seerr request sync.</param>
    public MetadataGapService(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILogger<MetadataGapService> logger,
        TrailerFinder trailerFinder,
        IdentityService identity,
        SeerrService seerr)
    {
        _trailerFinder = trailerFinder;
        _identity = identity;
        _seerr = seerr;
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Whether an item is missing anything the streamlined pages rely on.
    /// </summary>
    /// <param name="item">A movie or show.</param>
    public static bool HasGaps(BaseItem item)
    {
        return item.Genres.Length == 0
            || item.Tags.Length == 0
            || (item.RemoteTrailers?.Count ?? 0) == 0
            || string.IsNullOrEmpty(item.GetProviderId(MetadataProvider.Tmdb));
    }

    /// <summary>
    /// Asks Jellyfin to fetch an item's missing metadata again, unless it was asked recently.
    /// Fills gaps only: nothing already there (or locked) is replaced.
    /// </summary>
    /// <param name="item">A movie or show.</param>
    /// <returns>Whether a refresh was queued.</returns>
    public bool RequestRefresh(BaseItem item)
    {
        if (_lastAttempt.TryGetValue(item.Id, out var last) && DateTime.UtcNow - last < RetryAfter)
        {
            return false;
        }

        _lastAttempt[item.Id] = DateTime.UtcNow;
        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ImageRefreshMode = MetadataRefreshMode.Default,
            ReplaceAllMetadata = false,
            ReplaceAllImages = false
        };
        _providerManager.QueueRefresh(item.Id, options, RefreshPriority.Low);
        return true;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _timer = new Timer(_ => CheckSettled(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer?.Dispose();
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Movie or Series)
        {
            _added[e.Item.Id] = DateTime.UtcNow;
        }
    }

    private void CheckSettled()
    {
        try
        {
            _identity.VerifyPending();

            // Give the scan's own metadata fetch time to finish before judging what's missing.
            var due = _added.Where(pair => DateTime.UtcNow - pair.Value >= SettleDelay).Select(pair => pair.Key).ToList();
            if (due.Count == 0)
            {
                return;
            }

            var config = Plugin.Instance?.Configuration;
            var queued = 0;
            var items = new List<BaseItem>();
            foreach (var id in due)
            {
                _added.TryRemove(id, out _);
                var item = _libraryManager.GetItemById(id);
                if (item is null)
                {
                    continue;
                }

                items.Add(item);
                if (config?.AutoFillMetadata != false && HasGaps(item) && RequestRefresh(item))
                {
                    queued++;
                }
            }

            if (queued > 0)
            {
                _logger.LogInformation("Asked for missing genres/keywords/trailers on {Count} newly added titles.", queued);
            }

            // Then, one at a time in the background: the title itself (wrong match, no poster),
            // a trailer if there's still none, and any Seerr requests that just arrived.
            _ = Task.Run(async () =>
            {
                foreach (var item in items)
                {
                    try
                    {
                        if (config?.AutoFixIdentity != false)
                        {
                            await _identity.CheckAsync(item, CancellationToken.None).ConfigureAwait(false);
                        }

                        var fresh = _libraryManager.GetItemById(item.Id);
                        if (fresh is not null && config?.AutoFillMetadata != false && (fresh.RemoteTrailers?.Count ?? 0) == 0)
                        {
                            await _trailerFinder.FindAsync(fresh, CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Checking new title {Name} failed.", item.Name);
                    }
                }

                await _seerr.SyncAsync(CancellationToken.None).ConfigureAwait(false);
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Checking new titles for missing metadata failed.");
        }
    }
}
