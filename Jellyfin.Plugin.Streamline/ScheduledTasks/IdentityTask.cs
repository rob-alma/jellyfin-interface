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
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Streamline.ScheduledTasks;

/// <summary>
/// Daily: goes through every movie and show for wrong matches (name/year not matching the
/// folder, or not identified) and missing posters, and fixes what it can with confidence. The
/// rest shows up on the plugin's settings page.
/// </summary>
public sealed class IdentityTask : IScheduledTask
{
    // Each lookup is a TMDb search; a big library with many problems is worked through over days.
    private const int MaxLookupsPerRun = 40;

    private readonly ILibraryManager _libraryManager;
    private readonly IdentityService _identity;
    private readonly ILogger<IdentityTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IdentityTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="identity">The identity checker.</param>
    /// <param name="logger">Logger.</param>
    public IdentityTask(ILibraryManager libraryManager, IdentityService identity, ILogger<IdentityTask> logger)
    {
        _libraryManager = libraryManager;
        _identity = identity;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Fix wrong titles and posters";

    /// <inheritdoc />
    public string Key => "Jellyfin.Plugin.Streamline.Identity";

    /// <inheritdoc />
    public string Description => "Finds movies and shows matched to the wrong title (or not at all) or without a poster, and fixes them when the right match is clear.";

    /// <inheritdoc />
    public string Category => "Streamline";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.AutoFixIdentity == false)
        {
            return;
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Recursive = true,
            DtoOptions = new DtoOptions(true)
        });

        // Spotting problems is local and cheap; only the suspicious ones get looked up.
        var suspicious = items
            .Where(item => !item.IsLocked && (IdentityService.Diagnose(item, IdentityService.ExpectedTitle.FromPath(item)) is not null || !item.HasImage(ImageType.Primary, 0)))
            .Take(MaxLookupsPerRun)
            .ToList();

        var outcomes = new Dictionary<IdentityOutcome, int>();
        for (var i = 0; i < suspicious.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await _identity.CheckAsync(suspicious[i], cancellationToken).ConfigureAwait(false);
            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
            progress.Report(100.0 * (i + 1) / suspicious.Count);
        }

        _logger.LogInformation(
            "Checked {Total} titles; {Suspicious} looked off: {Outcomes}.",
            items.Count,
            suspicious.Count,
            string.Join(", ", outcomes.Select(pair => $"{pair.Key} {pair.Value}")));
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(4.5).Ticks };
    }
}
