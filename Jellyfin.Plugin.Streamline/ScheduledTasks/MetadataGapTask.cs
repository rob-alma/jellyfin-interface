using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Streamline.Services;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Streamline.ScheduledTasks;

/// <summary>
/// Daily: for movies and shows added recently that are still missing genres, keywords or a
/// trailer, asks Jellyfin to fetch them again (TMDb tends to fill these in over the weeks after
/// a release). Gap-filling only; nothing already there is replaced.
/// </summary>
public sealed class MetadataGapTask : IScheduledTask
{
    private const int MaxPerRun = 150;
    private const int MaxTrailerLookups = 200;

    private readonly ILibraryManager _libraryManager;
    private readonly MetadataGapService _gaps;
    private readonly TrailerFinder _trailerFinder;
    private readonly ILogger<MetadataGapTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataGapTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="gaps">The gap-filling service.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="trailerFinder">Finds trailers TMDb doesn't have.</param>
    public MetadataGapTask(ILibraryManager libraryManager, MetadataGapService gaps, ILogger<MetadataGapTask> logger, TrailerFinder trailerFinder)
    {
        _trailerFinder = trailerFinder;
        _libraryManager = libraryManager;
        _gaps = gaps;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Fill in missing genres and trailers";

    /// <inheritdoc />
    public string Key => "Jellyfin.Plugin.Streamline.MetadataGaps";

    /// <inheritdoc />
    public string Description => "Fetches genres, keywords and trailers again for recently added movies and shows that are still missing them, and finds trailers elsewhere (KinoCheck, YouTube) for anything TMDb has none for.";

    /// <inheritdoc />
    public string Category => "Streamline";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.AutoFillMetadata)
        {
            return;
        }

        var since = DateTime.UtcNow.AddDays(-Math.Clamp(config.MetadataLookbackDays, 1, 3650));
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Recursive = true,
            MinDateCreated = since,
            DtoOptions = new DtoOptions(true)
        });

        var queued = 0;
        var withGaps = items.Where(MetadataGapService.HasGaps).ToList();
        for (var i = 0; i < withGaps.Count && queued < MaxPerRun; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_gaps.RequestRefresh(withGaps[i]))
            {
                queued++;
            }

            progress.Report(50.0 * (i + 1) / withGaps.Count);
        }

        _logger.LogInformation(
            "{WithGaps} of {Total} titles added in the last {Days} days are missing genres, keywords or a trailer; asked for {Queued} of them again.",
            withGaps.Count,
            items.Count,
            config.MetadataLookbackDays,
            queued);

        // Trailers: anything in the whole library still without one gets looked up elsewhere
        // (KinoCheck, then YouTube search if configured). Spread over days for big libraries.
        var noTrailer = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Recursive = true,
            DtoOptions = new DtoOptions(true)
        }).Where(item => (item.RemoteTrailers?.Count ?? 0) == 0).Take(MaxTrailerLookups).ToList();

        var found = 0;
        for (var i = 0; i < noTrailer.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await _trailerFinder.FindAsync(noTrailer[i], cancellationToken).ConfigureAwait(false))
            {
                found++;
            }

            progress.Report(50 + (50.0 * (i + 1) / noTrailer.Count));
        }

        _logger.LogInformation("Looked for trailers for {Count} titles without one; found {Found}.", noTrailer.Count, found);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(4).Ticks };
    }
}
