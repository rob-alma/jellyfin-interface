using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Streamline.Services;

/// <summary>
/// Finds a trailer for a movie or show that has none, and saves it into the item's own Jellyfin
/// metadata (its trailer links), so it shows up like any other: nobody has to (or can) pick one
/// by hand. Sources, in order: KinoCheck (a free trailer database built on YouTube, looked up by
/// the item's TMDb id, no key needed), then YouTube's own search if an API key is configured.
/// </summary>
public sealed class TrailerFinder
{
    private const int MaxNew = 4;
    private static readonly TimeSpan RetryAfter = TimeSpan.FromDays(3);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TrailerFinder> _logger;
    private readonly ConcurrentDictionary<Guid, DateTime> _attempted = new();
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="TrailerFinder"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public TrailerFinder(IHttpClientFactory httpClientFactory, ILogger<TrailerFinder> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Looks for trailers for the item and saves any it finds. Does nothing if it already looked
    /// recently.
    /// </summary>
    /// <param name="item">A movie or show.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether trailers were added.</returns>
    public async Task<bool> FindAsync(BaseItem item, CancellationToken cancellationToken)
    {
        if (item is not (Movie or Series) || item.IsLocked
            || (_attempted.TryGetValue(item.Id, out var last) && DateTime.UtcNow - last < RetryAfter))
        {
            return false;
        }

        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _attempted[item.Id] = DateTime.UtcNow;
            var found = await FromKinoCheckAsync(item, cancellationToken).ConfigureAwait(false);
            if (found.Count == 0)
            {
                found = await FromYouTubeSearchAsync(item, cancellationToken).ConfigureAwait(false);
            }

            var existing = (item.RemoteTrailers ?? []).ToList();
            var known = existing.Select(trailer => TrailerService.YouTubeKey(trailer.Url)).ToHashSet();
            var added = found.Where(trailer => known.Add(TrailerService.YouTubeKey(trailer.Url))).Take(MaxNew).ToList();
            if (added.Count == 0)
            {
                return false;
            }

            item.RemoteTrailers = existing.Concat(added).ToArray();
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Added {Count} trailer(s) for {Name}.", added.Count, item.Name);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Looking for a trailer for {Name} failed.", item.Name);
            return false;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private async Task<List<MediaUrl>> FromKinoCheckAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var tmdb = item.GetProviderId(MetadataProvider.Tmdb);
        if (string.IsNullOrEmpty(tmdb))
        {
            return [];
        }

        var kind = item is Movie ? "movies" : "shows";
        var languages = new[] { item.GetPreferredMetadataLanguage(), "en" }
            .Where(language => !string.IsNullOrWhiteSpace(language))
            .Select(language => language!.Split('-')[0].ToLowerInvariant())
            .Distinct();

        foreach (var language in languages)
        {
            using var json = await GetJsonAsync($"https://api.kinocheck.com/{kind}?tmdb_id={Uri.EscapeDataString(tmdb)}&language={language}", cancellationToken).ConfigureAwait(false);
            if (json is null)
            {
                continue;
            }

            var videos = new List<(string Key, string Title, bool IsTrailer)>();
            if (json.RootElement.TryGetProperty("trailer", out var main) && main.ValueKind == JsonValueKind.Object)
            {
                AddVideo(videos, main, true);
            }

            if (json.RootElement.TryGetProperty("videos", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var video in list.EnumerateArray())
                {
                    var isTrailer = video.TryGetProperty("categories", out var categories)
                        && categories.ValueKind == JsonValueKind.Array
                        && categories.EnumerateArray().Any(category => string.Equals(category.GetString(), "Trailer", StringComparison.OrdinalIgnoreCase));
                    AddVideo(videos, video, isTrailer);
                }
            }

            var trailers = videos.Where(video => video.IsTrailer).DistinctBy(video => video.Key).ToList();
            if (trailers.Count > 0)
            {
                return trailers.Select(video => new MediaUrl { Url = "https://www.youtube.com/watch?v=" + video.Key, Name = video.Title }).ToList();
            }
        }

        return [];
    }

    private static void AddVideo(List<(string Key, string Title, bool IsTrailer)> videos, JsonElement video, bool isTrailer)
    {
        var key = video.TryGetProperty("youtube_video_id", out var id) ? id.GetString() : null;
        if (!string.IsNullOrEmpty(key))
        {
            var title = video.TryGetProperty("title", out var name) ? name.GetString() ?? string.Empty : string.Empty;
            videos.Add((key, title, isTrailer));
        }
    }

    private async Task<List<MediaUrl>> FromYouTubeSearchAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var key = Plugin.Instance?.Configuration.YouTubeApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            return [];
        }

        var query = $"{item.Name} {item.ProductionYear} official trailer".Replace("  ", " ", StringComparison.Ordinal);
        using var json = await GetJsonAsync(
            "https://www.googleapis.com/youtube/v3/search?part=snippet&type=video&videoEmbeddable=true&maxResults=8&q=" + Uri.EscapeDataString(query) + "&key=" + Uri.EscapeDataString(key.Trim()),
            cancellationToken).ConfigureAwait(false);
        if (json is null || !json.RootElement.TryGetProperty("items", out var results))
        {
            return [];
        }

        // Only videos whose title names the movie/show and says "trailer": the top search hit is
        // too often a reaction video or a different film with a similar name.
        var name = IdentityService.Normalize(item.Name);
        return results.EnumerateArray()
            .Select(result => (
                Key: result.GetProperty("id").TryGetProperty("videoId", out var id) ? id.GetString() : null,
                Title: result.GetProperty("snippet").TryGetProperty("title", out var title) ? WebUtilityDecode(title.GetString()) : string.Empty))
            .Where(result => !string.IsNullOrEmpty(result.Key)
                && IdentityService.Normalize(result.Title).Contains(name, StringComparison.Ordinal)
                && result.Title.Contains("trailer", StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .Select(result => new MediaUrl { Url = "https://www.youtube.com/watch?v=" + result.Key, Name = result.Title })
            .ToList();
    }

    private static string WebUtilityDecode(string? text) => System.Net.WebUtility.HtmlDecode(text ?? string.Empty);

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _httpClientFactory.CreateClient().GetAsync(url, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogDebug(ex, "Trailer lookup failed: {Url}", url.Split("&key=")[0]);
            return null;
        }
    }
}
