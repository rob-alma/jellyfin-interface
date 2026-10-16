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
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Streamline.Services;

/// <summary>
/// What's coming in the next month, for the "Coming Soon" row: new episodes of shows the user
/// watches, and titles requested in Seerr that are on their way. Only new things: a show counts
/// when the season the next episode belongs to started recently (a new show or a new season),
/// not a long-running show that keeps going. Air dates come from Seerr (which gets them from
/// TMDb): Jellyfin only knows episodes once they're downloaded.
/// </summary>
public sealed class ComingSoonService
{
    private const int DaysAhead = 30;
    private const int RecentSeasonDays = 90;
    private const int MaxShows = 60;
    private static readonly TimeSpan ListLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DetailsLifetime = TimeSpan.FromHours(6);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly WatchlistService _watchlist;
    private readonly ILogger<ComingSoonService> _logger;
    private readonly ConcurrentDictionary<Guid, (DateTime At, List<ComingSoonItem> Items)> _lists = new();
    private readonly ConcurrentDictionary<string, (DateTime At, JsonElement? Details)> _details = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ComingSoonService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="userDataManager">Jellyfin's user data manager.</param>
    /// <param name="watchlist">The Watch List store.</param>
    /// <param name="logger">Logger.</param>
    public ComingSoonService(IHttpClientFactory httpClientFactory, ILibraryManager libraryManager, IUserDataManager userDataManager, WatchlistService watchlist, ILogger<ComingSoonService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _watchlist = watchlist;
        _logger = logger;
    }

    /// <summary>
    /// Gets what's coming for a user, soonest first.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<ComingSoonItem>> GetAsync(User user, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        if (string.IsNullOrWhiteSpace(config.SeerrUrl) || string.IsNullOrWhiteSpace(config.SeerrApiKey))
        {
            return [];
        }

        if (_lists.TryGetValue(user.Id, out var cached) && DateTime.UtcNow - cached.At < ListLifetime)
        {
            return cached.Items;
        }

        var items = new List<ComingSoonItem>();
        try
        {
            items.AddRange(await NewEpisodesAsync(user, cancellationToken).ConfigureAwait(false));
            var seen = items.Where(item => item.TmdbId is not null).Select(item => item.TmdbId).ToHashSet();
            items.AddRange((await RequestedAsync(cancellationToken).ConfigureAwait(false)).Where(item => !seen.Contains(item.TmdbId)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Building Coming Soon failed.");
        }

        // On its way (already out, downloading) first, then by date.
        var list = items
            .OrderBy(item => item.Date is null ? DateTime.MinValue : DateTime.Parse(item.Date, CultureInfo.InvariantCulture))
            .ToList();
        _lists[user.Id] = (DateTime.UtcNow, list);
        return list;
    }

    // Shows the user is watching (played something in the last half year), has on their Watch
    // List, or favorited: their next episode, if it airs within the month and belongs to a
    // season that started recently.
    private async Task<List<ComingSoonItem>> NewEpisodesAsync(User user, CancellationToken cancellationToken)
    {
        var since = DateTime.UtcNow.AddDays(-180);
        var seriesIds = new HashSet<Guid>();
        foreach (var episode in _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)],
            Limit = 300,
            DtoOptions = new DtoOptions(false)
        }).OfType<Episode>())
        {
            var played = _userDataManager.GetUserData(user, episode)?.LastPlayedDate;
            if (played is null || played < since)
            {
                break;
            }

            seriesIds.Add(episode.SeriesId);
        }

        foreach (var id in _watchlist.Get(user.Id))
        {
            seriesIds.Add(id);
        }

        foreach (var favorite in _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Series],
            Recursive = true,
            IsFavorite = true,
            DtoOptions = new DtoOptions(false)
        }))
        {
            seriesIds.Add(favorite.Id);
        }

        var today = DateTime.UtcNow.Date;
        var result = new List<ComingSoonItem>();
        foreach (var series in seriesIds.Select(_libraryManager.GetItemById).OfType<Series>().Take(MaxShows))
        {
            var tmdb = series.GetProviderId(MetadataProvider.Tmdb);
            if (string.IsNullOrEmpty(tmdb))
            {
                continue;
            }

            var details = await DetailsAsync("tv", tmdb, cancellationToken).ConfigureAwait(false);
            if (details is not { } tv || !tv.TryGetProperty("nextEpisodeToAir", out var next) || next.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var airDate = Date(next, "airDate");
            var season = next.TryGetProperty("seasonNumber", out var seasonValue) && seasonValue.ValueKind == JsonValueKind.Number ? seasonValue.GetInt32() : 0;
            var number = next.TryGetProperty("episodeNumber", out var numberValue) && numberValue.ValueKind == JsonValueKind.Number ? numberValue.GetInt32() : 0;
            if (airDate is null || airDate < today || airDate > today.AddDays(DaysAhead))
            {
                continue;
            }

            // A new show or a new season: the season started (or starts) recently. A show that
            // has been running its current season for ages doesn't count as new.
            var seasonStart = SeasonStart(tv, season) ?? airDate;
            if (seasonStart < today.AddDays(-RecentSeasonDays))
            {
                continue;
            }

            result.Add(new ComingSoonItem
            {
                Kind = number == 1 && season > 1 ? "season" : number == 1 ? "show" : "episode",
                Title = series.Name,
                Subtitle = $"S{season}:E{number}" + (next.TryGetProperty("name", out var name) && !string.IsNullOrWhiteSpace(name.GetString()) ? " " + name.GetString() : string.Empty),
                Date = airDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ItemId = series.Id.ToString("N"),
                TmdbId = "tv:" + tmdb
            });
        }

        return result;
    }

    // Requests that are approved but not (fully) in the library yet, if they're out already
    // (downloading) or come out within the month.
    private async Task<List<ComingSoonItem>> RequestedAsync(CancellationToken cancellationToken)
    {
        var result = new List<ComingSoonItem>();
        using var json = await GetJsonAsync("api/v1/request?take=100&skip=0&filter=all&sort=added", cancellationToken).ConfigureAwait(false);
        if (json is null || !json.RootElement.TryGetProperty("results", out var requests))
        {
            return result;
        }

        var today = DateTime.UtcNow.Date;
        foreach (var request in requests.EnumerateArray())
        {
            // Approved requests, for media Seerr has as pending, processing or partly there.
            var status = request.TryGetProperty("status", out var statusValue) && statusValue.ValueKind == JsonValueKind.Number ? statusValue.GetInt32() : 0;
            if (status != 2 || !request.TryGetProperty("media", out var media))
            {
                continue;
            }

            var mediaStatus = media.TryGetProperty("status", out var mediaStatusValue) && mediaStatusValue.ValueKind == JsonValueKind.Number ? mediaStatusValue.GetInt32() : 0;
            var tmdb = media.TryGetProperty("tmdbId", out var tmdbValue) && tmdbValue.ValueKind == JsonValueKind.Number ? tmdbValue.GetInt32().ToString(CultureInfo.InvariantCulture) : null;
            var type = media.TryGetProperty("mediaType", out var typeValue) ? typeValue.GetString() : null;
            if (mediaStatus is < 2 or > 4 || tmdb is null || type is not ("movie" or "tv"))
            {
                continue;
            }

            if (await DetailsAsync(type, tmdb, cancellationToken).ConfigureAwait(false) is not { } details)
            {
                continue;
            }

            // When it's out: a movie's release date; a show's premiere, or for a show that's
            // already running, its next episode if that's within the month.
            var released = type == "movie" ? Date(details, "releaseDate") : Date(details, "firstAirDate");
            if (type == "tv" && released < today
                && details.TryGetProperty("nextEpisodeToAir", out var next) && next.ValueKind == JsonValueKind.Object
                && Date(next, "airDate") is { } nextAir && nextAir >= today && nextAir <= today.AddDays(DaysAhead))
            {
                released = nextAir;
            }

            if (released is not null && released > today.AddDays(DaysAhead))
            {
                continue;
            }

            var inLibrary = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [type == "tv" ? BaseItemKind.Series : BaseItemKind.Movie],
                Recursive = true,
                HasAnyProviderId = new Dictionary<string, string> { [MetadataProvider.Tmdb.ToString()] = tmdb }
            }).FirstOrDefault();

            result.Add(new ComingSoonItem
            {
                Kind = type == "tv" ? "requested-show" : "requested-movie",
                Title = (type == "movie" ? Text(details, "title") : Text(details, "name")) ?? string.Empty,
                // Never who asked for it: everyone sees this part of the row.
                Subtitle = type == "tv" ? "Requested show" : "Requested movie",
                // Out already: it's downloading ("on its way"), so no date to count down to.
                Date = released is not null && released > today ? released.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
                ItemId = inLibrary?.Id.ToString("N"),
                TmdbId = (type == "tv" ? "tv:" : "movie:") + tmdb,
                Backdrop = Text(details, "backdropPath"),
                Poster = Text(details, "posterPath")
            });
        }

        return result;
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
        if (_details.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < DetailsLifetime)
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
    /// <summary>Gets or sets what it is: episode, season, show, requested-movie, requested-show.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the line under it (the episode, or "Requested movie" / "Requested show").</summary>
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
