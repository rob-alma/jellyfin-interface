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
    private readonly CollectionService _collections;

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
        CollectionService collections)
    {
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

        var seriesId = item switch
        {
            MediaBrowser.Controller.Entities.TV.Episode episode => episode.SeriesId,
            MediaBrowser.Controller.Entities.TV.Season season => season.SeriesId,
            _ => Guid.Empty
        };
        if (seriesId != Guid.Empty)
        {
            item = _libraryManager.GetItemById(seriesId) ?? item;
        }

        var trailers = await _trailers.GetTrailersAsync(item, cancellationToken).ConfigureAwait(false);
        if (trailers.Count == 0
            && Plugin.Instance!.Configuration.AutoFillMetadata
            && await _trailerFinder.FindAsync(item, cancellationToken).ConfigureAwait(false))
        {
            trailers = await _trailers.GetTrailersAsync(item, cancellationToken).ConfigureAwait(false);
        }

        return Ok(trailers);
    }

    /// <summary>
    /// Gets the Coming Soon row: the signed-in user's own upcoming episodes plus the requested
    /// titles on their way, which everyone sees.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("ComingSoon")]
    [Authorize]
    public async Task<ActionResult<IReadOnlyList<ComingSoonItem>>> GetComingSoon(CancellationToken cancellationToken)
    {
        var user = CurrentUser();
        return user is null ? Unauthorized() : Ok(await _comingSoon.GetAsync(user, cancellationToken).ConfigureAwait(false));
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

        // Big ones (decades, Under 90 Minutes) come with their first titles only; the page asks
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

