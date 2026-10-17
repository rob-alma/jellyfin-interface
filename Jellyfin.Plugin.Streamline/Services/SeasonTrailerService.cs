using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Streamline.Services;

/// <summary>
/// Trailers per season, for shows with more than one season: "Season 2 Official Trailer",
/// "Stranger Things 4 | Trailer". Taken from the show's own trailer links, KinoCheck's videos for
/// the show, Seerr's TMDb videos (when Seerr is set up), and, for seasons still without one, a
/// YouTube search when an API key is set. The season is read from the video's title. Found
/// trailers are kept in the plugin's data folder and looked for again every few days.
/// </summary>
public sealed partial class SeasonTrailerService
{
    private const int MaxSearchesPerShow = 3;
    private static readonly TimeSpan RetryMissing = TimeSpan.FromDays(3);
    private static readonly TimeSpan RetryComplete = TimeSpan.FromDays(14);
    private static readonly JsonSerializerOptions FileJson = new() { WriteIndented = false };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<SeasonTrailerService> _logger;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly object _fileLock = new();
    private ConcurrentDictionary<Guid, ShowSeasons>? _known;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeasonTrailerService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="logger">Logger.</param>
    public SeasonTrailerService(IHttpClientFactory httpClientFactory, ILibraryManager libraryManager, ILogger<SeasonTrailerService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    private static string FilePath => Path.Combine(Plugin.Instance!.DataFolderPath, "season-trailers.json");

    /// <summary>
    /// Gets a show's trailers per season (empty for a show with one season).
    /// </summary>
    /// <param name="series">The show.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyDictionary<int, List<SeasonVideo>>> GetAsync(Series series, CancellationToken cancellationToken)
    {
        var seasons = SeasonNumbers(series);
        if (seasons.Count < 2)
        {
            return new Dictionary<int, List<SeasonVideo>>();
        }

        var known = Known();
        if (known.TryGetValue(series.Id, out var saved)
            && DateTime.UtcNow - saved.CheckedAt < (seasons.All(saved.Seasons.ContainsKey) ? RetryComplete : RetryMissing))
        {
            return saved.Seasons;
        }

        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var found = await FindAsync(series, seasons, cancellationToken).ConfigureAwait(false);
            known[series.Id] = new ShowSeasons { CheckedAt = DateTime.UtcNow, Seasons = found };
            Save(known);
            return found;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Looking for season trailers for {Name} failed.", series.Name);
            return saved?.Seasons ?? new Dictionary<int, List<SeasonVideo>>();
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>
    /// Reads the season a trailer is for from its title, if it says.
    /// </summary>
    /// <param name="title">The video's title.</param>
    /// <param name="showName">The show's name.</param>
    /// <param name="lastSeason">The show's last season here.</param>
    internal static int? SeasonOf(string? title, string showName, int lastSeason)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var lower = title.ToLowerInvariant();
        int? number = null;
        var match = SeasonWordRegex().Match(lower);
        if (match.Success)
        {
            number = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        else if (ShortSeasonRegex().Match(lower) is { Success: true } shortMatch)
        {
            number = int.Parse(shortMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        else if (lower.Contains("final season", StringComparison.Ordinal))
        {
            number = lastSeason;
        }
        else
        {
            // "Stranger Things 4 | Official Trailer": the show's name and then a number.
            var name = CollectionService.Normalize(showName);
            var words = CollectionService.Normalize(title);
            var at = words.IndexOf(name + " ", StringComparison.Ordinal);
            if (name.Length > 0 && at >= 0)
            {
                var next = words[(at + name.Length + 1)..].Split(' ').FirstOrDefault();
                if (int.TryParse(next, out var value) && value is > 0 and < 30)
                {
                    number = value;
                }
            }
        }

        return number is > 0 && number <= lastSeason ? number : null;
    }

    /// <summary>
    /// Gets the show's seasons here, by number.
    /// </summary>
    /// <param name="series">The show.</param>
    public List<int> SeasonNumbers(Series series) => _libraryManager.GetItemList(new InternalItemsQuery
    {
        AncestorIds = [series.Id],
        IncludeItemTypes = [BaseItemKind.Season],
        Recursive = true,
        DtoOptions = new DtoOptions(false)
    }).OfType<Season>()
        .Select(season => season.IndexNumber ?? 0)
        .Where(number => number > 0)
        .Distinct()
        .OrderBy(number => number)
        .ToList();

    private async Task<Dictionary<int, List<SeasonVideo>>> FindAsync(Series series, List<int> seasons, CancellationToken cancellationToken)
    {
        var last = seasons.Max();
        var candidates = new List<(string Key, string? Title)>();

        // 1. The show's own trailer links (TMDb's videos carry names like "Season 2 Trailer").
        candidates.AddRange((series.RemoteTrailers ?? [])
            .Select(trailer => (Key: TrailerService.YouTubeKey(trailer.Url), Title: trailer.Name))
            .Where(video => video.Key is not null)
            .Select(video => (Key: video.Key!, Title: (string?)video.Title)));

        var tmdb = series.GetProviderId(MetadataProvider.Tmdb);
        if (!string.IsNullOrEmpty(tmdb))
        {
            // 2. KinoCheck: every trailer it has for the show.
            using (var json = await GetJsonAsync($"https://api.kinocheck.com/shows?tmdb_id={Uri.EscapeDataString(tmdb)}&language=en", null, cancellationToken).ConfigureAwait(false))
            {
                if (json is not null)
                {
                    if (json.RootElement.TryGetProperty("trailer", out var main) && main.ValueKind == JsonValueKind.Object)
                    {
                        AddKinoCheck(candidates, main);
                    }

                    if (json.RootElement.TryGetProperty("videos", out var videos) && videos.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var video in videos.EnumerateArray())
                        {
                            AddKinoCheck(candidates, video);
                        }
                    }
                }
            }

            // 3. Seerr: TMDb's video list for the show.
            var config = Plugin.Instance!.Configuration;
            if (!string.IsNullOrWhiteSpace(config.SeerrUrl) && !string.IsNullOrWhiteSpace(config.SeerrApiKey))
            {
                using var json = await GetJsonAsync($"{config.SeerrUrl.TrimEnd('/')}/api/v1/tv/{Uri.EscapeDataString(tmdb)}", config.SeerrApiKey, cancellationToken).ConfigureAwait(false);
                if (json is not null && json.RootElement.TryGetProperty("relatedVideos", out var related) && related.ValueKind == JsonValueKind.Array)
                {
                    foreach (var video in related.EnumerateArray())
                    {
                        var type = video.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
                        var url = video.TryGetProperty("url", out var urlValue) ? urlValue.GetString() : null;
                        var key = TrailerService.YouTubeKey(url) ?? (video.TryGetProperty("key", out var keyValue) ? keyValue.GetString() : null);
                        if (key is { Length: 11 } && type is "Trailer" or "Teaser")
                        {
                            candidates.Add((key, video.TryGetProperty("name", out var name) ? name.GetString() : null));
                        }
                    }
                }
            }
        }

        var found = new Dictionary<int, List<SeasonVideo>>();
        foreach (var (key, title) in candidates.DistinctBy(candidate => candidate.Key))
        {
            if (SeasonOf(title, series.Name, last) is int season && seasons.Contains(season))
            {
                Add(found, season, key, title);
            }
        }

        // 4. YouTube search for seasons still without one (with an API key; a few per show).
        var apiKey = Plugin.Instance!.Configuration.YouTubeApiKey;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            foreach (var season in seasons.Where(number => !found.ContainsKey(number)).Take(MaxSearchesPerShow))
            {
                var query = $"{series.Name} season {season} official trailer";
                using var json = await GetJsonAsync(
                    "https://www.googleapis.com/youtube/v3/search?part=snippet&type=video&videoEmbeddable=true&maxResults=6&q=" + Uri.EscapeDataString(query) + "&key=" + Uri.EscapeDataString(apiKey.Trim()),
                    null,
                    cancellationToken).ConfigureAwait(false);
                if (json is null || !json.RootElement.TryGetProperty("items", out var items))
                {
                    continue;
                }

                var name = CollectionService.Normalize(series.Name);
                foreach (var result in items.EnumerateArray())
                {
                    var key = result.GetProperty("id").TryGetProperty("videoId", out var id) ? id.GetString() : null;
                    var title = System.Net.WebUtility.HtmlDecode(result.GetProperty("snippet").TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty);
                    if (key is not null && CollectionService.Normalize(title).Contains(name, StringComparison.Ordinal)
                        && title.Contains("trailer", StringComparison.OrdinalIgnoreCase)
                        && SeasonOf(title, series.Name, last) == season)
                    {
                        Add(found, season, key, title);
                        break;
                    }
                }
            }
        }

        _logger.LogDebug("Season trailers for {Name}: {Seasons}.", series.Name, string.Join(", ", found.Keys));
        return found;
    }

    private static void Add(Dictionary<int, List<SeasonVideo>> found, int season, string key, string? title)
    {
        if (!found.TryGetValue(season, out var list))
        {
            found[season] = list = [];
        }

        if (list.Count < 3 && list.All(video => video.Key != key))
        {
            list.Add(new SeasonVideo { Key = key, Title = title });
        }
    }

    private static void AddKinoCheck(List<(string Key, string? Title)> candidates, JsonElement video)
    {
        var key = video.TryGetProperty("youtube_video_id", out var id) ? id.GetString() : null;
        if (!string.IsNullOrEmpty(key))
        {
            candidates.Add((key, video.TryGetProperty("title", out var title) ? title.GetString() : null));
        }
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, string? seerrKey, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (seerrKey is not null)
            {
                request.Headers.Add("X-Api-Key", seerrKey);
            }

            using var response = await _httpClientFactory.CreateClient().SendAsync(request, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false))
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or UriFormatException)
        {
            _logger.LogDebug(ex, "Season trailer lookup failed: {Url}", url.Split("&key=")[0]);
            return null;
        }
    }

    private ConcurrentDictionary<Guid, ShowSeasons> Known()
    {
        if (_known is not null)
        {
            return _known;
        }

        lock (_fileLock)
        {
            try
            {
                var saved = File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<Guid, ShowSeasons>>(File.ReadAllText(FilePath), FileJson) : null;
                _known = new ConcurrentDictionary<Guid, ShowSeasons>(saved ?? []);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                _known = new ConcurrentDictionary<Guid, ShowSeasons>();
            }
        }

        return _known;
    }

    private void Save(ConcurrentDictionary<Guid, ShowSeasons> known)
    {
        lock (_fileLock)
        {
            try
            {
                Directory.CreateDirectory(Plugin.Instance!.DataFolderPath);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(known, FileJson));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Couldn't save season trailers.");
            }
        }
    }

    [GeneratedRegex(@"\b(?:season|seizoen|staffel|saison|temporada|series)\s*(\d{1,2})\b")]
    private static partial Regex SeasonWordRegex();

    [GeneratedRegex(@"\bs(\d{1,2})\b(?!\s*e\d)")]
    private static partial Regex ShortSeasonRegex();

    /// <summary>
    /// What's known about one show.
    /// </summary>
    internal sealed class ShowSeasons
    {
        public DateTime CheckedAt { get; set; }

        public Dictionary<int, List<SeasonVideo>> Seasons { get; set; } = [];
    }
}

/// <summary>
/// A season's trailer.
/// </summary>
public sealed class SeasonVideo
{
    /// <summary>Gets or sets the YouTube video id.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the title it came with.</summary>
    public string? Title { get; set; }
}
