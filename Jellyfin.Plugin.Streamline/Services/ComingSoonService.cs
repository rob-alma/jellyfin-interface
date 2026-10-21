using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Streamline.Services;

/// <summary>
/// What's about to air, for the "Coming Soon" row: the next episode of every show on the server
/// that airs within the month, when it's new: a new show, or a new season (the season started
/// recently, not a long-running one that keeps going). The same for everyone (only cut down to the libraries each
/// person can see); requests aren't part of it. Air dates come from Seerr (which gets them from
/// TMDb): Jellyfin only knows episodes once they're downloaded. Worked out in the background
/// every few hours, so the row never waits for Seerr.
/// </summary>
public sealed class ComingSoonService
{
    private const int DaysAhead = 30;
    private const int RecentSeasonDays = 90;
    private const int MaxParallel = 4;
    private static readonly TimeSpan RebuildEvery = TimeSpan.FromHours(3);
    private static readonly TimeSpan DetailsLifetime = TimeSpan.FromHours(12);
    private static readonly TimeSpan EndedLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan VisibleLifetime = TimeSpan.FromMinutes(15);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ComingSoonService> _logger;
    private readonly ConcurrentDictionary<string, (DateTime At, JsonElement? Details)> _details = new();
    private readonly ConcurrentDictionary<Guid, (DateTime At, HashSet<Guid> Ids)> _visible = new();
    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private (DateTime At, List<ComingSoonItem> Items) _list = (DateTime.MinValue, []);

    /// <summary>
    /// Initializes a new instance of the <see cref="ComingSoonService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="logger">Logger.</param>
    public ComingSoonService(IHttpClientFactory httpClientFactory, ILibraryManager libraryManager, ILogger<ComingSoonService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _libraryManager = libraryManager;
        _logger = logger;

        // Ready before anyone opens Home: a first build shortly after the server starts.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
            await RebuildAsync(CancellationToken.None).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Gets what's coming, soonest first, for what this user can see. Never waits for Seerr:
    /// an old list starts a new one in the background.
    /// </summary>
    /// <param name="user">The user.</param>
    public IReadOnlyList<ComingSoonItem> Get(User user)
    {
        var config = Plugin.Instance!.Configuration;
        if (string.IsNullOrWhiteSpace(config.SeerrUrl) || string.IsNullOrWhiteSpace(config.SeerrApiKey))
        {
            return [];
        }

        var (at, items) = _list;
        if (DateTime.UtcNow - at > RebuildEvery && _buildLock.CurrentCount > 0)
        {
            _ = Task.Run(() => RebuildAsync(CancellationToken.None));
        }

        var visible = VisibleIds(user);
        return items.Where(item => item.ItemId is not null && visible.Contains(Guid.Parse(item.ItemId))).ToList();
    }

    /// <summary>
    /// Works the list out again (in the background; also every few hours by itself).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task RebuildAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        if (string.IsNullOrWhiteSpace(config.SeerrUrl) || string.IsNullOrWhiteSpace(config.SeerrApiKey)
            || !await _buildLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var started = DateTime.UtcNow;
            var items = await NewEpisodesAsync(cancellationToken).ConfigureAwait(false);

            // Soonest first.
            _list = (DateTime.UtcNow, items
                .OrderBy(item => DateTime.Parse(item.Date!, CultureInfo.InvariantCulture))
                .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                .ToList());
            _logger.LogInformation("Coming Soon: {Count} titles in {Seconds:0}s.", items.Count, (DateTime.UtcNow - started).TotalSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Building Coming Soon failed.");
        }
        finally
        {
            _buildLock.Release();
        }
    }

    // What the user may see, as ids: one cheap id-only query per user every few minutes.
    private HashSet<Guid> VisibleIds(User user)
    {
        if (_visible.TryGetValue(user.Id, out var known) && DateTime.UtcNow - known.At < VisibleLifetime)
        {
            return known.Ids;
        }

        var ids = _libraryManager.GetItemIds(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Recursive = true
        }).ToHashSet();
        _visible[user.Id] = (DateTime.UtcNow, ids);
        return ids;
    }

    // Every show on the server: its next episode, if that airs within the month and its season
    // is new.
    private async Task<List<ComingSoonItem>> NewEpisodesAsync(CancellationToken cancellationToken)
    {
        var shows = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Series],
            Recursive = true,
            DtoOptions = new DtoOptions(false) { Fields = [MediaBrowser.Model.Querying.ItemFields.ProviderIds] }
        }).OfType<Series>()
            .Select(series => (Series: series, Tmdb: series.GetProviderId(MetadataProvider.Tmdb)))
            .Where(entry => !string.IsNullOrEmpty(entry.Tmdb))
            .ToList();

        var today = DateTime.UtcNow.Date;
        var result = new ConcurrentBag<ComingSoonItem>();
        await Parallel.ForEachAsync(shows, new ParallelOptions { MaxDegreeOfParallelism = MaxParallel, CancellationToken = cancellationToken }, async (entry, token) =>
        {
            var details = await DetailsAsync("tv", entry.Tmdb!, token).ConfigureAwait(false);
            if (details is not { } tv || !tv.TryGetProperty("nextEpisodeToAir", out var next) || next.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var airDate = Date(next, "airDate");
            var season = next.TryGetProperty("seasonNumber", out var seasonValue) && seasonValue.ValueKind == JsonValueKind.Number ? seasonValue.GetInt32() : 0;
            var number = next.TryGetProperty("episodeNumber", out var numberValue) && numberValue.ValueKind == JsonValueKind.Number ? numberValue.GetInt32() : 0;
            if (airDate is null || airDate < today || airDate > today.AddDays(DaysAhead))
            {
                return;
            }

            // A new show or a new season: the season started (or starts) recently.
            if ((SeasonStart(tv, season) ?? airDate) < today.AddDays(-RecentSeasonDays))
            {
                return;
            }

            var premiere = season == 1 && number == 1;
            result.Add(new ComingSoonItem
            {
                Kind = premiere ? "show" : number == 1 ? "season" : "episode",
                        Title = entry.Series.Name,
                Subtitle = $"S{season}:E{number}" + (next.TryGetProperty("name", out var name) && !string.IsNullOrWhiteSpace(name.GetString()) ? " " + name.GetString() : string.Empty),
                Date = airDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ItemId = entry.Series.Id.ToString("N"),
                // The show's own page; not for a brand-new show, which has nothing to show yet.
                Opens = !premiere,
                TmdbId = "tv:" + entry.Tmdb
            });
        }).ConfigureAwait(false);

        return result.ToList();
    }

    private static DateTime? SeasonStart(JsonElement tv, int seasonNumber)
    {
        if (!tv.TryGetProperty("seasons", out var seasons) || seasons.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return seasons.EnumerateArray()
            .Where(season => season.TryGetProperty("seasonNumber", out var number) && number.ValueKind == JsonValueKind.Number && number.GetInt32() == seasonNumber)
            .Select(season => Date(season, "airDate"))
            .FirstOrDefault();
    }

    private static DateTime? Date(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            && DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
            ? date.Date
            : null;
    }

    private static string? Text(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private async Task<JsonElement?> DetailsAsync(string type, string tmdb, CancellationToken cancellationToken)
    {
        var key = type + ":" + tmdb;
        // A show that has ended won't get a next episode: looked up again much less often.
        if (_details.TryGetValue(key, out var cached)
            && DateTime.UtcNow - cached.At < (cached.Details is { } known && Text(known, "status") is "Ended" or "Canceled" ? EndedLifetime : DetailsLifetime))
        {
            return cached.Details;
        }

        using var json = await GetJsonAsync($"api/v1/{type}/{tmdb}", cancellationToken).ConfigureAwait(false);
        JsonElement? details = json?.RootElement.Clone();
        _details[key] = (DateTime.UtcNow, details);
        return details;
    }

    private async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var message = new HttpRequestMessage(HttpMethod.Get, config.SeerrUrl.TrimEnd('/') + "/" + path);
            message.Headers.Add("X-Api-Key", config.SeerrApiKey.Trim());
            using var response = await _httpClientFactory.CreateClient().SendAsync(message, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false))
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogDebug(ex, "Seerr lookup failed: {Path}", path);
            return null;
        }
    }
}

/// <summary>
/// One thing in the Coming Soon row.
/// </summary>
public sealed class ComingSoonItem
{
    /// <summary>Gets or sets what it is: episode, season (premiere) or show (a new show's first episode).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the card opens the title's page (a show on the server, but not a brand-new one).</summary>
    public bool Opens { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the line under it (the episode).</summary>
    public string Subtitle { get; set; } = string.Empty;

    /// <summary>Gets or sets the date it airs (yyyy-MM-dd).</summary>
    public string? Date { get; set; }

    /// <summary>Gets or sets the show's Jellyfin item id.</summary>
    public string? ItemId { get; set; }

    /// <summary>Gets or sets "tv:id" for TMDb.</summary>
    public string? TmdbId { get; set; }
}
