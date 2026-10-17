using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Streamline.Services;

/// <summary>
/// Picks the trailers to embed for an item from the YouTube links its metadata (TMDb) carries.
/// Each link is checked through YouTube's public oEmbed endpoint, which needs no API key and
/// says whether the video still exists and may be embedded, and gives its title. That drops dead
/// or embed-blocked links and puts real trailers ("Official Trailer") ahead of clips,
/// featurettes and reviews.
/// </summary>
public sealed partial class TrailerService
{
    private const int MaxTrailers = 6;
    private static readonly TimeSpan KnownLifetime = TimeSpan.FromDays(1);
    private static readonly TimeSpan UnknownLifetime = TimeSpan.FromHours(1);
    private static readonly string[] Unwanted = ["clip", "featurette", "behind the scenes", "interview", "review", "reaction", "recap", "explained", "making of", "b-roll", "bloopers", "deleted scene", "first look", "sneak peek"];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TrailerService> _logger;
    private readonly ConcurrentDictionary<string, (DateTime CheckedAt, VideoCheck Check)> _checks = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="TrailerService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public TrailerService(IHttpClientFactory httpClientFactory, ILogger<TrailerService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets the item's playable trailers, best first.
    /// </summary>
    /// <param name="item">A movie or show.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<TrailerInfo>> GetTrailersAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var keys = (item.RemoteTrailers ?? [])
            .Select(trailer => YouTubeKey(trailer.Url))
            .OfType<string>()
            .Distinct()
            .Take(MaxTrailers * 2)
            .ToList();

        var checks = await Task.WhenAll(keys.Select(key => CheckAsync(key, cancellationToken))).ConfigureAwait(false);
        var playable = checks.Where(check => check.Playable).ToList();

        // TMDb's video list mixes trailers with TV spots and ads ("OWN NOW", "Now Playing"); when
        // there are real trailers or teasers, only show those.
        if (playable.Any(check => Score(check.Title) > 0))
        {
            playable = playable.Where(check => Score(check.Title) > 0).ToList();
        }

        return playable
            .Select((check, index) => (check, index))
            .OrderByDescending(entry => Score(entry.check.Title))
            .ThenBy(entry => entry.index)
            .Take(MaxTrailers)
            .Select(entry => new TrailerInfo { Key = entry.check.Key, Title = entry.check.Title })
            .ToList();
    }

    /// <summary>
    /// Checks videos found elsewhere (season trailers, an admin's choice): the playable ones, with
    /// their YouTube titles, in the order given.
    /// </summary>
    /// <param name="videos">Video ids and the titles they came with.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<List<TrailerInfo>> CheckVideosAsync(IEnumerable<(string Key, string? Title, int? Season)> videos, CancellationToken cancellationToken)
    {
        var list = videos.DistinctBy(video => video.Key).Take(MaxTrailers * 2).ToList();
        var checks = await Task.WhenAll(list.Select(video => CheckAsync(video.Key, cancellationToken))).ConfigureAwait(false);
        return list.Zip(checks)
            .Where(pair => pair.Second.Playable)
            .Select(pair => new TrailerInfo { Key = pair.First.Key, Title = pair.Second.Title ?? pair.First.Title, Season = pair.First.Season })
            .ToList();
    }

    /// <summary>
    /// Extracts the video id from a YouTube link, or null if it isn't one.
    /// </summary>
    /// <param name="url">The link.</param>
    public static string? YouTubeKey(string? url)
    {
        var match = YouTubeRegex().Match(url ?? string.Empty);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static int Score(string? title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return 0;
        }

        var lower = title.ToLowerInvariant();
        var score = 0;
        if (lower.Contains("official trailer", StringComparison.Ordinal))
        {
            score += 4;
        }
        else if (lower.Contains("trailer", StringComparison.Ordinal))
        {
            score += 3;
        }
        else if (lower.Contains("teaser", StringComparison.Ordinal))
        {
            score += 1;
        }

        if (Unwanted.Any(word => lower.Contains(word, StringComparison.Ordinal)))
        {
            score -= 5;
        }

        return score;
    }

    private async Task<VideoCheck> CheckAsync(string key, CancellationToken cancellationToken)
    {
        if (_checks.TryGetValue(key, out var cached)
            && DateTime.UtcNow - cached.CheckedAt < (cached.Check.Known ? KnownLifetime : UnknownLifetime))
        {
            return cached.Check;
        }

        VideoCheck check;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            var client = _httpClientFactory.CreateClient();
            var url = "https://www.youtube.com/oembed?format=json&url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=" + key);
            using var response = await client.GetAsync(url, timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
                var title = json.RootElement.TryGetProperty("title", out var value) ? value.GetString() : null;
                check = new VideoCheck(key, title, Playable: true, Known: true);
            }
            else if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                // Removed, private, or the owner doesn't allow embedding.
                check = new VideoCheck(key, null, Playable: false, Known: true);
            }
            else
            {
                check = new VideoCheck(key, null, Playable: true, Known: false);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Can't reach YouTube from the server: keep the link rather than hide every trailer.
            _logger.LogDebug(ex, "Could not check trailer {Key}.", key);
            check = new VideoCheck(key, null, Playable: true, Known: false);
        }

        _checks[key] = (DateTime.UtcNow, check);
        return check;
    }

    [GeneratedRegex(@"(?:youtube(?:-nocookie)?\.com/(?:watch\?(?:.*&)?v=|embed/|shorts/)|youtu\.be/)([\w-]{11})")]
    private static partial Regex YouTubeRegex();

    private sealed record VideoCheck(string Key, string? Title, bool Playable, bool Known);
}

/// <summary>
/// A trailer to embed.
/// </summary>
public sealed class TrailerInfo
{
    /// <summary>
    /// Gets or sets the YouTube video id.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the video's title on YouTube, if known.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the season it's the trailer of, for a show's season trailers.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? Season { get; set; }
}
