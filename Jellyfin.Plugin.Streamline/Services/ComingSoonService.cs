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
/// What's coming to the server in the next month, for the "Coming Soon" row. The same for
/// everyone (only cut down to the libraries each person can see): the next episode of every
/// show on the server that airs within the month, and requested movies and new shows that are
/// on their way (never who asked). Air and release dates come from Seerr (which gets them
/// from TMDb): Jellyfin only knows episodes once they're downloaded. Worked out in the
/// background every few hours, so the row never waits for Seerr.
/// </summary>
public sealed class ComingSoonService
{
    private const int DaysAhead = 30;
    private const int MovieDaysAhead = 60;
    private const int OnItsWayDays = 120;
    private const int MaxParallel = 4;
    private static readonly TimeSpan RebuildEvery = TimeSpan.FromHours(3);
    private static readonly TimeSpan DetailsLifetime = TimeSpan.FromHours(12);
    private static readonly TimeSpan EndedLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan VisibleLifetime = TimeSpan.FromMinutes(15);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly PendingChoicesService _pending;
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
    /// <param name="pending">Watch List and Favorites choices for titles not on the server yet.</param>
    /// <param name="logger">Logger.</param>
    public ComingSoonService(IHttpClientFactory httpClientFactory, ILibraryManager libraryManager, PendingChoicesService pending, ILogger<ComingSoonService> logger)
    {
        _pending = pending;
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
        return items.Where(item => item.ItemId is null || visible.Contains(Guid.Parse(item.ItemId))).ToList();
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
            // Titles that arrived since: carry out what people chose for them here.
            _pending.ApplyArrived();
            var started = DateTime.UtcNow;
            var items = new List<ComingSoonItem>();
            items.AddRange(await NewEpisodesAsync(cancellationToken).ConfigureAwait(false));
            var seen = items.Select(item => item.TmdbId).ToHashSet();
            items.AddRange((await RequestedAsync(cancellationToken).ConfigureAwait(false)).Where(item => !seen.Contains(item.TmdbId)));

            // On its way (out already, downloading) first, then by date.
            _list = (DateTime.UtcNow, items
                .OrderBy(item => item.Date is null ? DateTime.MinValue : DateTime.Parse(item.Date, CultureInfo.InvariantCulture))
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

    // Every show on the server: its next episode, if that airs within the month.
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

            var premiere = season == 1 && number == 1;
            result.Add(new ComingSoonItem
            {
                Kind = premiere ? "show" : number == 1 ? "season" : "episode",
                MediaType = "tv",
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

    // Approved requests on their way: movies about to come out (or out and downloading), and
    // new shows not on the server yet. Never who asked: everyone sees this.
    private async Task<List<ComingSoonItem>> RequestedAsync(CancellationToken cancellationToken)
    {
        var result = new List<ComingSoonItem>();
        using var json = await GetJsonAsync("api/v1/request?take=200&skip=0&filter=approved&sort=added", cancellationToken).ConfigureAwait(false);
        if (json is null || !json.RootElement.TryGetProperty("results", out var requests))
        {
            return result;
        }

        var today = DateTime.UtcNow.Date;
        foreach (var request in requests.EnumerateArray())
        {
            if (!request.TryGetProperty("media", out var media))
            {
                continue;
            }

            // Seerr's media status: 2 pending, 3 processing (downloading or waiting for release).
            var mediaStatus = media.TryGetProperty("status", out var mediaStatusValue) && mediaStatusValue.ValueKind == JsonValueKind.Number ? mediaStatusValue.GetInt32() : 0;
            var tmdb = media.TryGetProperty("tmdbId", out var tmdbValue) && tmdbValue.ValueKind == JsonValueKind.Number ? tmdbValue.GetInt32().ToString(CultureInfo.InvariantCulture) : null;
            var type = media.TryGetProperty("mediaType", out var typeValue) ? typeValue.GetString() : null;
            if (mediaStatus is not (2 or 3) || tmdb is null || type is not ("movie" or "tv"))
            {
                continue;
            }

            if (InLibrary(type, tmdb) is not null || await DetailsAsync(type, tmdb, cancellationToken).ConfigureAwait(false) is not { } details)
            {
                continue;
            }

            // A movie: its digital (or disc) release, else its cinema date. A show: its premiere.
            var released = type == "movie" ? HomeRelease(details) ?? Date(details, "releaseDate") : Date(details, "firstAirDate");
            var requested = request.TryGetProperty("createdAt", out var createdValue) && DateTime.TryParse(createdValue.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var created) ? created : today;
            if (released is null
                || released > today.AddDays(type == "movie" ? MovieDaysAhead : DaysAhead)
                || (released <= today && requested < today.AddDays(-OnItsWayDays)))
            {
                continue;
            }

            result.Add(new ComingSoonItem
            {
                Kind = type == "tv" ? "show" : "movie",
                MediaType = type,
                Title = (type == "movie" ? Text(details, "title") : Text(details, "name")) ?? string.Empty,
                Subtitle = type == "tv" ? "New show" : released > today ? "New movie" : "On its way",
                // Out already: it's downloading, so no date to count down to.
                Date = released > today ? released.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
                TmdbId = type + ":" + tmdb,
                Backdrop = Text(details, "backdropPath"),
                Poster = Text(details, "posterPath")
            });
        }

        return result;
    }

    private BaseItem? InLibrary(string type, string tmdb) => _libraryManager.GetItemList(new InternalItemsQuery
    {
        IncludeItemTypes = [type == "tv" ? BaseItemKind.Series : BaseItemKind.Movie],
        Recursive = true,
        HasAnyProviderId = new Dictionary<string, string> { [MetadataProvider.Tmdb.ToString()] = tmdb }
    }).FirstOrDefault();

    // The first digital (4) or physical (5) release anywhere: when a movie can come to the server.
    private static DateTime? HomeRelease(JsonElement movie)
    {
        if (!movie.TryGetProperty("releases", out var releases) || !releases.TryGetProperty("results", out var countries) || countries.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return countries.EnumerateArray()
            .Where(country => country.TryGetProperty("release_dates", out var dates) && dates.ValueKind == JsonValueKind.Array)
            .SelectMany(country => country.GetProperty("release_dates").EnumerateArray())
            .Where(release => release.TryGetProperty("type", out var kind) && kind.ValueKind == JsonValueKind.Number && kind.GetInt32() is 4 or 5)
            .Select(release => Date(release, "release_date"))
            .Where(date => date is not null)
            .Min();
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
    /// <summary>Gets or sets what it is: episode, season (premiere), show (a new show) or movie.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets "tv" or "movie".</summary>
    public string MediaType { get; set; } = "tv";

    /// <summary>Gets or sets a value indicating whether the card opens the title's page (a show on the server, but not a brand-new one).</summary>
    public bool Opens { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the line under it (the episode).</summary>
    public string Subtitle { get; set; } = string.Empty;

    /// <summary>Gets or sets the date it airs/comes out (yyyy-MM-dd), or null when it's on its way now.</summary>
    public string? Date { get; set; }

    /// <summary>Gets or sets the Jellyfin item id, when it's (partly) in the library.</summary>
    public string? ItemId { get; set; }

    /// <summary>Gets or sets "movie:id"/"tv:id" for TMDb.</summary>
    public string? TmdbId { get; set; }

    /// <summary>Gets or sets the TMDb backdrop path, for titles not in the library.</summary>
    public string? Backdrop { get; set; }

    /// <summary>Gets or sets the TMDb poster path, for titles not in the library.</summary>
    public string? Poster { get; set; }
}
