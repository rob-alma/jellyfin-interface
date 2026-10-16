using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Streamline.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Streamline.ScheduledTasks;

/// <summary>
/// Nightly: prepares the Collections page for everyone in one go (every movie's directors and
/// writers looked up again), after the nightly metadata and title fixes, so nobody waits for
/// it during the day.
/// </summary>
public sealed class CollectionsTask : IScheduledTask
{
    private readonly CollectionService _collections;

    /// <summary>
    /// Initializes a new instance of the <see cref="CollectionsTask"/> class.
    /// </summary>
    /// <param name="collections">The collections.</param>
    public CollectionsTask(CollectionService collections)
    {
        _collections = collections;
    }

    /// <inheritdoc />
    public string Name => "Prepare collections";

    /// <inheritdoc />
    public string Key => "Jellyfin.Plugin.Streamline.Collections";

    /// <inheritdoc />
    public string Description => "Works out every collection (franchises, movie series, themes, decades, directors) for the Collections page, so it's ready before anyone opens it.";

    /// <inheritdoc />
    public string Category => "Streamline";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _collections.RebuildAsync(true, cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // After "Fill in missing genres and trailers" (4:00) and "Fix wrong titles" (4:30).
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(5).Ticks };
    }
}
