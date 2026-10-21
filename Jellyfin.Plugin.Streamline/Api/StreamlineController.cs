using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Plugin.Streamline.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Streamline.Api;

/// <summary>
/// Serves the home screen's client script and stylesheet, the client-facing subset of the
/// plugin configuration, and the per-user Watch List, trailers and Coming Soon endpoints.
/// </summary>
[ApiController]
[Route("Streamline")]
public sealed class StreamlineController : ControllerBase
{
    private const string ResourcePrefix = "Jellyfin.Plugin.Streamline.Web.";
    private const int MaxListedIds = 200;

    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly WatchlistService _watchlist;
    private readonly TrailerService _trailers;
    private readonly TrailerFinder _trailerFinder;
    private readonly IdentityService _identity;
    private readonly SeerrService _seerr;
    private readonly ComingSoonService _comingSoon;
    private readonly TrailerOverrides _trailerOverrides;
    private readonly SeasonTrailerService _seasonTrailers;
    private readonly CollectionService _collections;
    private readonly GenreTasteService _genreTaste;
    private readonly ContinueWatchingService _continueWatching;
    private readonly PendingChoicesService _pending;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamlineController"/> class.
    /// </summary>
    /// <param name="userManager">Jellyfin's user manager.</param>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="userDataManager">Jellyfin's user data manager.</param>
    /// <param name="watchlist">The per-user Watch List store.</param>
    /// <param name="trailers">Trailer picking.</param>
    /// <param name="trailerFinder">Finds trailers TMDb doesn't have.</param>
    /// <param name="identity">Wrong-title detection.</param>
    /// <param name="seerr">Seerr request sync.</param>
    /// <param name="comingSoon">The Coming Soon row.</param>
    /// <param name="collections">The Collections page.</param>
    /// <param name="genreTaste">Genre-row order and choices.</param>
    /// <param name="trailerOverrides">Admins' trailer choices.</param>
    /// <param name="seasonTrailers">Trailers per season.</param>
    /// <param name="continueWatching">Titles taken out of Continue Watching.</param>
    /// <param name="pending">Watch List and Favorites choices for titles not on the server yet.</param>
    public StreamlineController(
        IUserManager userManager,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        WatchlistService watchlist,
        TrailerService trailers,
        TrailerFinder trailerFinder,
        IdentityService identity,
        SeerrService seerr,
        ComingSoonService comingSoon,
        CollectionService collections,
        GenreTasteService genreTaste,
        TrailerOverrides trailerOverrides,
        SeasonTrailerService seasonTrailers,
        ContinueWatchingService continueWatching,
        PendingChoicesService pending)
    {
        _pending = pending;
        _continueWatching = continueWatching;
        _trailerOverrides = trailerOverrides;
        _seasonTrailers = seasonTrailers;
        _genreTaste = genreTaste;
        _collections = collections;
        _comingSoon = comingSoon;
        _trailerFinder = trailerFinder;
        _identity = identity;
        _seerr = seerr;
        _watchlist = watchlist;
        _trailers = trailers;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
    }

    /// <summary>
    /// Gets the client script. Deliberately not <c>[Authorize]</c>: it's loaded via a bare
    /// <c>&lt;script src&gt;</c> tag before any user session exists in the page. Everything it
    /// goes on to fetch is authenticated as normal.
    /// </summary>
    [HttpGet("script")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Produces("application/javascript")]
    public ActionResult GetScript() => ServeResource("home.js", "application/javascript");

    /// <summary>
    /// Gets the small script that runs before Jellyfin's own code (for the admin's User view).
    /// Not <c>[Authorize]</c>, for the same reason as the script.
    /// </summary>
    [HttpGet("early")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Produces("application/javascript")]
    public ActionResult GetEarlyScript() => ServeResource("early.js", "application/javascript");

    /// <summary>
    /// Gets the client stylesheet. Not <c>[Authorize]</c>, for the same reason as the script.
    /// </summary>
    [HttpGet("style")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Produces("text/css")]
    public ActionResult GetStyle() => ServeResource("home.css", "text/css");

    /// <summary>
    /// Gets the settings the client needs. Separate from Jellyfin's own plugin configuration
    /// endpoint so non-admin users only ever see this subset.
    /// </summary>
    [HttpGet("Config")]
    [Authorize]
    public ActionResult<object> GetClientConfig()
    {
        var config = Plugin.Instance!.Configuration;
        return Ok(new
        {
            config.Enabled,
            config.LibraryPagesEnabled,
            config.HeroEnabled,
            HeroMaxAgeDays = Math.Clamp(config.HeroMaxAgeDays, 7, 730),
            HeroItemCount = Math.Clamp(config.HeroItemCount, 1, 12),
            HeroIntervalSeconds = Math.Clamp(config.HeroIntervalSeconds, 4, 60),
            config.AccentColor,
            NewSeriesBadgeDays = Math.Clamp(config.NewSeriesBadgeDays, 1, 365),
            NewMovieBadgeDays = Math.Clamp(config.NewMovieBadgeDays, 1, 365),
            config.ShowPhonePreview,
            RecentlyAddedBadgeDays = Math.Clamp(config.RecentlyAddedBadgeDays, 1, 365),
            config.GenreRows,
            config.HeadlineGenres,
            config.TrailersEnabled,
            RowItemLimit = Math.Clamp(config.RowItemLimit, 6, 50),
            HasSeerr = !string.IsNullOrWhiteSpace(config.SeerrUrl) && !string.IsNullOrWhiteSpace(config.SeerrApiKey),
            // Only admins get the User view's test user (and only while the test buttons are on).
            UserView = UserViewTarget()
        });
    }

    private object? UserViewTarget()
    {
        var config = Plugin.Instance!.Configuration;
        var user = CurrentUser();
        if (!config.ShowPhonePreview || user is null || !user.HasPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator)
            || !Guid.TryParse(config.UserViewUserId, out var testId) || testId == user.Id)
        {
            return null;
        }

        var test = _userManager.GetUserById(testId);
        return test is null ? null : new { Id = test.Id.ToString("N", System.Globalization.CultureInfo.InvariantCulture), Name = test.Username };
    }

    /// <summary>
    /// Gets the signed-in user's Watch List. Always the caller's own list: there is no user id
    /// parameter, so nobody can read or change someone else's.
    /// </summary>
    [HttpGet("Watchlist")]
    [Authorize]
    public ActionResult<IReadOnlyList<Guid>> GetWatchlist()
    {
        var user = CurrentUser();
        return user is null ? Unauthorized() : Ok(_watchlist.GetActive(user));
    }

    /// <summary>
    /// Adds an item to the signed-in user's Watch List.
    /// </summary>
    /// <param name="itemId">The item.</param>
    [HttpPost("Watchlist/{itemId}")]
    [Authorize]
    public ActionResult<IReadOnlyList<Guid>> AddToWatchlist([FromRoute] Guid itemId)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null || !item.IsVisible(user))
        {
            return NotFound();
        }

        _watchlist.Add(user.Id, itemId);
        return Ok(_watchlist.GetActive(user));
    }

    /// <summary>
    /// Removes an item from the signed-in user's Watch List.
    /// </summary>
    /// <param name="itemId">The item.</param>
    [HttpDelete("Watchlist/{itemId}")]
    [Authorize]
    public ActionResult<IReadOnlyList<Guid>> RemoveFromWatchlist([FromRoute] Guid itemId)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        _watchlist.Remove(user.Id, itemId);
        return Ok(_watchlist.GetActive(user));
    }

    /// <summary>
    /// Gets the playable trailers of an item (episodes and seasons use their show's). When none
    /// exist yet, tries to find one first.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("Trailers/{itemId}")]
    [Authorize]
    public async Task<ActionResult<IReadOnlyList<TrailerInfo>>> GetTrailers([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null || !item.IsVisible(user))
        {
            return NotFound();
        }

        // The title whose trailer this is: a season for a season or episode page (its own
        // trailer if it has one), else the movie or show.
        var season = item switch
        {
            MediaBrowser.Controller.Entities.TV.Episode episode => episode.Season,
            MediaBrowser.Controller.Entities.TV.Season own => own,
            _ => null
        };
        var title = item is MediaBrowser.Controller.Entities.TV.Episode or MediaBrowser.Controller.Entities.TV.Season
            ? _libraryManager.GetItemById(item is MediaBrowser.Controller.Entities.TV.Episode e ? e.SeriesId : ((MediaBrowser.Controller.Entities.TV.Season)item).SeriesId) ?? item
            : item;

        // An admin's choice wins: the season's, else the movie's or show's.
        var chosen = (season is null ? null : _trailerOverrides.Get(season.Id)) ?? _trailerOverrides.Get(title.Id);
        if (chosen is not null)
        {
            return Ok(chosen.None || chosen.Key is null
                ? Array.Empty<TrailerInfo>()
                : new[] { new TrailerInfo { Key = chosen.Key, Title = chosen.Title, Season = season?.IndexNumber } });
        }

        var trailers = await _trailers.GetTrailersAsync(title, cancellationToken).ConfigureAwait(false);
        if (trailers.Count == 0
            && Plugin.Instance!.Configuration.AutoFillMetadata
            && await _trailerFinder.FindAsync(title, cancellationToken).ConfigureAwait(false))
        {
            trailers = await _trailers.GetTrailersAsync(title, cancellationToken).ConfigureAwait(false);
        }

        // Shows with more than one season: a trailer per season. On a season (or episode) page
        // that season's comes first, so it's the one selected; on the show's page the show's own
        // trailers come first, then one per season.
        if (title is MediaBrowser.Controller.Entities.TV.Series series)
        {
            var seasonNumbers = _seasonTrailers.SeasonNumbers(series);
            if (seasonNumbers.Count > 1)
            {
                var perSeason = await _seasonTrailers.GetAsync(series, cancellationToken).ConfigureAwait(false);
                var last = seasonNumbers.Max();

                // The show's own trailers say which season too ("Season 1 Trailer", "Final Season").
                var all = trailers.Select(trailer => new TrailerInfo { Key = trailer.Key, Title = trailer.Title, Season = trailer.Season ?? SeasonTrailerService.SeasonOf(trailer.Title, series.Name, last) }).ToList();
                var known = all.Select(trailer => trailer.Key).ToHashSet();
                var wanted = perSeason.OrderBy(entry => entry.Key)
                    .SelectMany(entry => entry.Value.Select(video => (video.Key, video.Title, (int?)entry.Key)))
                    .Where(video => !known.Contains(video.Key));
                all.AddRange(await _trailers.CheckVideosAsync(wanted, cancellationToken).ConfigureAwait(false));

                trailers = season?.IndexNumber is int number
                    // A season: its own trailers (selected first), then the show's general ones.
                    ? all.Where(trailer => trailer.Season == number).Concat(all.Where(trailer => trailer.Season is null)).Take(6).ToList()
                    // The show: its general trailers, then one per season.
                    : all.Where(trailer => trailer.Season is null)
                        .Concat(all.Where(trailer => trailer.Season is not null).GroupBy(trailer => trailer.Season).OrderBy(group => group.Key).Select(group => group.First()))
                        .Take(10).ToList();
            }
        }

        return Ok(trailers);
    }

    /// <summary>
    /// Gets an admin's trailer choice for a title (an episode's is its season's).
    /// </summary>
    /// <param name="itemId">The movie, show, season or episode.</param>
    [HttpGet("Admin/Trailers/{itemId}")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<object> GetTrailerChoice([FromRoute] Guid itemId)
    {
        var target = TrailerTarget(itemId);
        if (target is null)
        {
            return NotFound();
        }

        return Ok(new { TargetId = target.Id.ToString("N", System.Globalization.CultureInfo.InvariantCulture), TargetName = target is MediaBrowser.Controller.Entities.TV.Season s ? $"{s.SeriesName}, {s.Name}" : target.Name, Choice = _trailerOverrides.Get(target.Id) });
    }

    /// <summary>
    /// Sets an admin's trailer choice: a YouTube link, or no trailer.
    /// </summary>
    /// <param name="itemId">The movie, show, season or episode (an episode sets its season's).</param>
    /// <param name="choice">The YouTube link, or <c>None</c> for no trailer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost("Admin/Trailers/{itemId}")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<object>> SetTrailerChoice([FromRoute] Guid itemId, [FromBody] TrailerChoiceRequest choice, CancellationToken cancellationToken)
    {
        var target = TrailerTarget(itemId);
        if (target is null)
        {
            return NotFound();
        }

        if (choice.None)
        {
            _trailerOverrides.Set(target.Id, new TrailerOverride { None = true });
            return Ok(new { Saved = true });
        }

        var key = TrailerService.YouTubeKey(choice.Url) ?? (choice.Url is { Length: 11 } raw && raw.All(c => char.IsLetterOrDigit(c) || c is '-' or '_') ? raw : null);
        if (key is null)
        {
            return BadRequest("That isn't a YouTube link.");
        }

        var checkedVideo = (await _trailers.CheckVideosAsync([(key, null, null)], cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        if (checkedVideo is null)
        {
            return BadRequest("That video can't be played here (removed, private, or embedding is off).");
        }

        _trailerOverrides.Set(target.Id, new TrailerOverride { Key = key, Title = checkedVideo.Title });
        return Ok(new { Saved = true, checkedVideo.Title });
    }

    /// <summary>
    /// Removes an admin's trailer choice: back to the automatic trailer.
    /// </summary>
    /// <param name="itemId">The movie, show, season or episode.</param>
    [HttpDelete("Admin/Trailers/{itemId}")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult ResetTrailerChoice([FromRoute] Guid itemId)
    {
        var target = TrailerTarget(itemId);
        if (target is null)
        {
            return NotFound();
        }

        _trailerOverrides.Set(target.Id, null);
        return NoContent();
    }

    // What a trailer choice applies to: an episode's goes to its season.
    private MediaBrowser.Controller.Entities.BaseItem? TrailerTarget(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        return item is MediaBrowser.Controller.Entities.TV.Episode episode ? (MediaBrowser.Controller.Entities.BaseItem?)episode.Season ?? item : item;
    }

    /// <summary>
    /// Gets the Coming Soon row: the same for everyone (cut down to what the user can see):
    /// upcoming episodes of every show here, and requested movies and new shows on their way.
    /// </summary>
    [HttpGet("ComingSoon")]
    [Authorize]
    public ActionResult<IReadOnlyList<ComingSoonItem>> GetComingSoon()
    {
        var user = CurrentUser();
        return user is null ? Unauthorized() : Ok(_comingSoon.Get(user));
    }

    /// <summary>
    /// Gets the signed-in user's Watch List and Favorites choices for titles not on the server yet.
    /// </summary>
    [HttpGet("Pending")]
    [Authorize]
    public ActionResult<IReadOnlyList<PendingChoice>> GetPending()
    {
        var user = CurrentUser();
        return user is null ? Unauthorized() : Ok(_pending.Get(user.Id));
    }

    /// <summary>
    /// Puts a title that isn't on the server yet on the signed-in user's Watch List or
    /// Favorites (carried out when it arrives), or takes that back.
    /// </summary>
    /// <param name="tmdbId">"tv:1234" or "movie:5678".</param>
    /// <param name="what">"watchlist" or "favorite".</param>
    /// <param name="on">On or off.</param>
    [HttpPost("Pending/{tmdbId}/{what}")]
    [Authorize]
    public ActionResult SetPending([FromRoute] string tmdbId, [FromRoute] string what, [FromQuery] bool on = true)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        return _pending.Set(user.Id, tmdbId, what, on) ? Ok(_pending.Get(user.Id)) : BadRequest();
    }

    /// <summary>
    /// Gets the collections the signed-in user can see (franchises across movies and shows,
    /// TMDb movie series, Jellyfin's own collections), each with its titles in release order.
    /// </summary>
    [HttpGet("Collections")]
    [Authorize]
    public ActionResult<IReadOnlyList<CollectionInfo>> GetCollections()
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        // Big ones (decades, All Anime, Netflix) come with their first titles only; the page asks
        // for the rest when it's opened, so this list stays small however big the library is.
        return Ok(_collections.Get(user).Select(collection => collection.ItemIds.Count <= MaxListedIds ? collection : new CollectionInfo
        {
            Id = collection.Id,
            Name = collection.Name,
            Kind = collection.Kind,
            Aliases = collection.Aliases,
            Movies = collection.Movies,
            Shows = collection.Shows,
            Count = collection.ItemIds.Count,
            Partial = true,
            ItemIds = collection.ItemIds.Take(12).ToList()
        }).ToList());
    }

    /// <summary>
    /// Gets every title of one collection, in release order.
    /// </summary>
    /// <param name="id">The collection.</param>
    [HttpGet("Collections/{id}")]
    [Authorize]
    public ActionResult<CollectionInfo> GetCollection([FromRoute] string id)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        var collection = _collections.Get(user).FirstOrDefault(entry => entry.Id == id);
        return collection is null ? NotFound() : Ok(collection);
    }

    /// <summary>
    /// Gets the collections a title is part of (for "Part of" on its page).
    /// </summary>
    /// <param name="itemId">The movie or show.</param>
    [HttpGet("Collections/Of/{itemId}")]
    [Authorize]
    public ActionResult<IEnumerable<object>> GetCollectionsOf([FromRoute] Guid itemId)
    {
        var user = CurrentUser();
        return user is null
            ? Unauthorized()
            : Ok(_collections.Get(user).Where(entry => entry.ItemIds.Contains(itemId)).Select(entry => new { entry.Id, entry.Name, entry.Kind }).ToList());
    }

    /// <summary>
    /// Gets the signed-in user's taste: the titles that count for the order of their genre rows.
    /// </summary>
    [HttpGet("Taste")]
    [Authorize]
    public ActionResult<IReadOnlyList<TasteEntry>> GetTaste()
    {
        var user = CurrentUser();
        return user is null ? Unauthorized() : Ok(_genreTaste.GetTaste(user));
    }

    /// <summary>
    /// Gets the signed-in user's own genre-row choices.
    /// </summary>
    [HttpGet("GenrePrefs")]
    [Authorize]
    public ActionResult<GenrePrefs> GetGenrePrefs()
    {
        var user = CurrentUser();
        return user is null ? Unauthorized() : Ok(_genreTaste.GetPrefs(user.Id));
    }

    /// <summary>
    /// Saves the signed-in user's own genre-row choices.
    /// </summary>
    /// <param name="prefs">Pinned and hidden genres, and whether rows follow what they watch.</param>
    [HttpPost("GenrePrefs")]
    [Authorize]
    public ActionResult<GenrePrefs> SaveGenrePrefs([FromBody] GenrePrefs prefs)
    {
        var user = CurrentUser();
        return user is null ? Unauthorized() : Ok(_genreTaste.SavePrefs(user.Id, prefs ?? new GenrePrefs()));
    }

    /// <summary>
    /// Gets the movies and shows the signed-in user took out of Continue Watching.
    /// </summary>
    [HttpGet("ContinueWatching/Hidden")]
    [Authorize]
    public ActionResult<IReadOnlyList<Guid>> GetContinueWatchingHidden()
    {
        var user = CurrentUser();
        return user is null ? Unauthorized() : Ok(_continueWatching.Hidden(user.Id));
    }

    /// <summary>
    /// Takes a title out of the signed-in user's Continue Watching, saying why (which counts for
    /// their recommendations and genre rows).
    /// </summary>
    /// <param name="itemId">The movie, show or episode.</param>
    /// <param name="reason">didnt-like, lost-interest, finished, later or not-me.</param>
    [HttpPost("ContinueWatching/{itemId}/Remove")]
    [Authorize]
    public ActionResult RemoveFromContinueWatching([FromRoute] Guid itemId, [FromQuery] string reason)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        return _continueWatching.Remove(user, itemId, reason ?? string.Empty) ? NoContent() : BadRequest();
    }

    /// <summary>
    /// Gets the signed-in user's own Collections page choices.
    /// </summary>
    [HttpGet("CollectionPrefs")]
    [Authorize]
    public ActionResult<CollectionPrefs> GetCollectionPrefs()
    {
        var user = CurrentUser();
        return user is null ? Unauthorized() : Ok(_genreTaste.GetCollectionPrefs(user.Id));
    }

    /// <summary>
    /// Saves the signed-in user's own Collections page choices.
    /// </summary>
    /// <param name="prefs">Section order, hidden sections, and pinned and hidden collections.</param>
    [HttpPost("CollectionPrefs")]
    [Authorize]
    public ActionResult<CollectionPrefs> SaveCollectionPrefs([FromBody] CollectionPrefs prefs)
    {
        var user = CurrentUser();
        return user is null ? Unauthorized() : Ok(_genreTaste.SaveCollectionPrefs(user.Id, prefs ?? new CollectionPrefs()));
    }

    /// <summary>
    /// Gets the titles and posters that still need a manual fix.
    /// </summary>
    [HttpGet("Admin/Issues")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<IReadOnlyList<IdentityIssue>> GetIssues() => Ok(_identity.Issues);

    /// <summary>
    /// Marks an issue as handled so it leaves the list.
    /// </summary>
    /// <param name="itemId">The item.</param>
    [HttpPost("Admin/Issues/{itemId}/Done")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult DismissIssue([FromRoute] Guid itemId)
    {
        _identity.Dismiss(itemId);
        return NoContent();
    }

    /// <summary>
    /// Tests the Seerr connection and runs a sync straight away.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost("Admin/SeerrTest")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<string>> TestSeerr(CancellationToken cancellationToken)
    {
        var result = await _seerr.TestAsync(cancellationToken).ConfigureAwait(false);
        await _seerr.SyncAsync(cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    private Jellyfin.Database.Implementations.Entities.User? CurrentUser()
    {
        var claim = User.Claims.FirstOrDefault(c => c.Type == "Jellyfin-UserId")?.Value;
        return Guid.TryParse(claim, out var userId) ? _userManager.GetUserById(userId) : null;
    }

    private ActionResult ServeResource(string name, string contentType)
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourcePrefix + name);
        if (stream is null)
        {
            return NotFound();
        }

        // The tag's URL already carries a version query parameter (see ScriptMarkup); this is a
        // second layer in case something between here and the client caches it anyway.
        Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";

        return File(stream, contentType);
    }
}

/// <summary>
/// An admin's trailer choice, as sent by the page.
/// </summary>
public sealed class TrailerChoiceRequest
{
    /// <summary>Gets or sets the YouTube link (or video id).</summary>
    public string? Url { get; set; }

    /// <summary>Gets or sets a value indicating whether the title should show no trailer.</summary>
    public bool None { get; set; }
}
