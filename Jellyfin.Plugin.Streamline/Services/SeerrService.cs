using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Streamline.Services;

/// <summary>
/// Puts what someone requested in Seerr (Jellyseerr/Overseerr) on their Watch List once it's
/// in Jellyfin. Polls Seerr's request list (it has no way to call Jellyfin itself), matches each
/// request's TMDb id to a movie or show in the library, and the requester to a Jellyfin user
/// (Jellyseerr knows their Jellyfin account; otherwise by user name). Each request is handled
/// once, so removing it from the Watch List afterwards sticks. That includes requests from before
/// this was switched on: anything requested earlier that's already in the library goes on the
/// requester's list the first time it runs.
/// </summary>
public sealed class SeerrService : IHostedService, IDisposable
{
    private const int PageSize = 100;
    private const int MaxPages = 5;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly WatchlistService _watchlist;
    private readonly ILogger<SeerrService> _logger;
    private readonly SemaphoreSlim _running = new(1, 1);
    private Timer? _timer;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeerrService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="userManager">Jellyfin's user manager.</param>
    /// <param name="watchlist">The Watch List store.</param>
    /// <param name="logger">Logger.</param>
    public SeerrService(IHttpClientFactory httpClientFactory, ILibraryManager libraryManager, IUserManager userManager, WatchlistService watchlist, ILogger<SeerrService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _watchlist = watchlist;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(_ => _ = SyncAsync(CancellationToken.None), null, TimeSpan.FromMinutes(1), Interval);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer?.Dispose();
    }

    /// <summary>
    /// Checks Seerr's connection settings: whether it answers and how many requests it has.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<string> TestAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        if (string.IsNullOrWhiteSpace(config.SeerrUrl) || string.IsNullOrWhiteSpace(config.SeerrApiKey))
        {
            return "Fill in Seerr's address and API key first.";
        }

        try
        {
            using var json = await GetAsync($"api/v1/request?take=1&skip=0&filter=all&sort=added", cancellationToken).ConfigureAwait(false);
            var total = json.RootElement.TryGetProperty("pageInfo", out var info) && info.TryGetProperty("results", out var count) ? count.GetInt32() : 0;
            return $"Connected. Seerr has {total} request(s).";
        }
        catch (Exception ex)
        {
            return "Couldn't reach Seerr: " + ex.Message;
        }
    }

    /// <summary>
    /// Handles new requests: anything now in the library goes on its requester's Watch List.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.SeerrAddToWatchList || string.IsNullOrWhiteSpace(config.SeerrUrl) || string.IsNullOrWhiteSpace(config.SeerrApiKey))
        {
            return;
        }

        if (!await _running.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var state = LoadState();
            if (!state.Retroactive)
            {
                // Earlier versions skipped requests already in the library on the first run;
                // forget those so they're added now.
                state.Handled.Clear();
                state.Retroactive = true;
            }

            var added = 0;
            for (var page = 0; page < MaxPages; page++)
            {
                using var json = await GetAsync($"api/v1/request?take={PageSize}&skip={page * PageSize}&filter=all&sort=added", cancellationToken).ConfigureAwait(false);
                if (!json.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                {
                    break;
                }

                foreach (var request in results.EnumerateArray())
                {
                    var id = request.TryGetProperty("id", out var idValue) ? idValue.GetInt32() : 0;
                    if (id == 0 || state.Handled.Contains(id))
                    {
                        continue;
                    }

                    // Declined requests never arrive.
                    if (request.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Number && status.GetInt32() == 3)
                    {
                        state.Handled.Add(id);
                        continue;
                    }

                    var item = FindInLibrary(request);
                    if (item is null)
                    {
                        // Not downloaded yet: look again next time.
                        continue;
                    }

                    state.Handled.Add(id);
                    var user = FindUser(request);
                    if (user is null)
                    {
                        _logger.LogInformation("Seerr request {Id} ({Name}) is in the library, but its requester doesn't match a Jellyfin user.", id, item.Name);
                        continue;
                    }

                    _watchlist.Add(user.Id, item.Id);
                    added++;
                    _logger.LogInformation("Added \"{Name}\" to {User}'s Watch List (requested in Seerr).", item.Name, user.Username);
                }

                if (results.GetArrayLength() < PageSize)
                {
                    break;
                }
            }

            SaveState(state);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Checking Seerr requests failed.");
        }
        finally
        {
            _running.Release();
        }
    }

    private BaseItem? FindInLibrary(JsonElement request)
    {
        if (!request.TryGetProperty("media", out var media))
        {
            return null;
        }

        var tmdb = media.TryGetProperty("tmdbId", out var tmdbValue) && tmdbValue.ValueKind == JsonValueKind.Number ? tmdbValue.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        var type = media.TryGetProperty("mediaType", out var typeValue) ? typeValue.GetString() : null;
        if (string.IsNullOrEmpty(tmdb))
        {
            return null;
        }

        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [type == "tv" ? BaseItemKind.Series : BaseItemKind.Movie],
            Recursive = true,
            HasAnyProviderId = new Dictionary<string, string> { [MetadataProvider.Tmdb.ToString()] = tmdb }
        }).FirstOrDefault();
    }

    private Jellyfin.Database.Implementations.Entities.User? FindUser(JsonElement request)
    {
        if (!request.TryGetProperty("requestedBy", out var by))
        {
            return null;
        }

        // Jellyseerr stores the Jellyfin account id of users who sign in with Jellyfin.
        if (by.TryGetProperty("jellyfinUserId", out var jellyfinId) && Guid.TryParse(jellyfinId.GetString(), out var userId))
        {
            var byId = _userManager.GetUserById(userId);
            if (byId is not null)
            {
                return byId;
            }
        }

        foreach (var field in new[] { "jellyfinUsername", "username", "displayName", "plexUsername" })
        {
            if (by.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                var byName = _userManager.GetUserByName(value.GetString()!);
                if (byName is not null)
                {
                    return byName;
                }
            }
        }

        return null;
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var message = new HttpRequestMessage(HttpMethod.Get, config.SeerrUrl.TrimEnd('/') + "/" + path);
        message.Headers.Add("X-Api-Key", config.SeerrApiKey.Trim());
        using var response = await _httpClientFactory.CreateClient().SendAsync(message, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
    }

    private static string StateFile() => Path.Combine(Plugin.Instance!.DataFolderPath, "seerr.json");

    private SyncState LoadState()
    {
        try
        {
            return File.Exists(StateFile()) ? JsonSerializer.Deserialize<SyncState>(File.ReadAllText(StateFile())) ?? new SyncState() : new SyncState();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read Seerr sync state; starting over.");
            return new SyncState();
        }
    }

    private static void SaveState(SyncState state)
    {
        Directory.CreateDirectory(Plugin.Instance!.DataFolderPath);
        var temp = StateFile() + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state));
        File.Move(temp, StateFile(), true);
    }

    private sealed class SyncState
    {
        public bool Retroactive { get; set; }

        public HashSet<int> Handled { get; set; } = [];
    }
}
