using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Streamline.Services;

/// <summary>
/// Each user's genre rows: their taste (what they watched, favorited and rated, as a weight
/// per movie or show), which the page uses to put the genres they watch most first, and their
/// own choices (genres pinned to the top, genres hidden, automatic order on or off). Both are
/// per user, stored in the plugin's data folder, and only ever read or written for the
/// signed-in user. The full taste is worked out at night; during the day a title is updated
/// when its user watches, favorites or rates it.
/// </summary>
public sealed class GenreTasteService
{
    private const int MaxTasteEntries = 300;

    // A rating replaces the watch weight: Terrible and Bad push a genre down, the rest up.
    private static readonly double[] RatingWeights = [-3.0, -1.5, 0.5, 2.0, 3.5];

    // Lost interest (Continue Watching clean-up), without a rating: a little against the genre.
    private const double LostInterestWeight = -1.0;

    private static readonly JsonSerializerOptions FileJson = new() { WriteIndented = false };

    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ContinueWatchingService _continueWatching;
    private readonly ILogger<GenreTasteService> _logger;
    private readonly ConcurrentDictionary<Guid, Dictionary<Guid, double>> _taste = new();
    private readonly object _fileLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="GenreTasteService"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="userDataManager">Jellyfin's user data manager.</param>
    /// <param name="userManager">Jellyfin's user manager.</param>
    /// <param name="continueWatching">Titles taken out of Continue Watching, and why.</param>
    /// <param name="logger">Logger.</param>
    public GenreTasteService(ILibraryManager libraryManager, IUserDataManager userDataManager, IUserManager userManager, ContinueWatchingService continueWatching, ILogger<GenreTasteService> logger)
    {
        _continueWatching = continueWatching;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _userManager = userManager;
        _logger = logger;
        _userDataManager.UserDataSaved += OnUserDataSaved;
    }

    private static string Folder(string kind) => Path.Combine(Plugin.Instance!.DataFolderPath, kind);

    /// <summary>
    /// Gets a user's taste: their strongest titles and how much each counts (negative for
    /// titles they rated badly).
    /// </summary>
    /// <param name="user">The user.</param>
    public IReadOnlyList<TasteEntry> GetTaste(User user)
    {
        var taste = _taste.GetOrAdd(user.Id, _ => LoadTaste(user.Id) ?? Compute(user));
        lock (taste)
        {
            return taste
                .OrderByDescending(entry => Math.Abs(entry.Value))
                .Take(MaxTasteEntries)
                .Select(entry => new TasteEntry { Id = entry.Key, Weight = Math.Round(entry.Value, 2) })
                .ToList();
        }
    }

    /// <summary>
    /// Works out every user's taste again (the nightly task).
    /// </summary>
    public void RebuildAll()
    {
        foreach (var user in _userManager.GetUsers())
        {
            var taste = Compute(user);
            _taste[user.Id] = taste;
            SaveTaste(user.Id, taste);
        }
    }

    /// <summary>
    /// Gets a user's own genre-row choices.
    /// </summary>
    /// <param name="userId">The user.</param>
    public GenrePrefs GetPrefs(Guid userId)
    {
        lock (_fileLock)
        {
            var path = Path.Combine(Folder("genreprefs"), userId.ToString("N") + ".json");
            try
            {
                return File.Exists(path) ? JsonSerializer.Deserialize<GenrePrefs>(File.ReadAllText(path), FileJson) ?? new GenrePrefs() : new GenrePrefs();
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                _logger.LogWarning(ex, "Couldn't read genre rows for {User}.", userId);
                return new GenrePrefs();
            }
        }
    }

    /// <summary>
    /// Saves a user's own genre-row choices.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="prefs">The choices.</param>
    public GenrePrefs SavePrefs(Guid userId, GenrePrefs prefs)
    {
        static List<string> Clean(IEnumerable<string>? names) => (names ?? [])
            .Select(name => name?.Trim() ?? string.Empty)
            .Where(name => name.Length is > 0 and <= 80)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(60)
            .ToList();

        var clean = new GenrePrefs { Auto = prefs.Auto, Pinned = Clean(prefs.Pinned), Hidden = Clean(prefs.Hidden) };
        clean.Hidden.RemoveAll(name => clean.Pinned.Contains(name, StringComparer.OrdinalIgnoreCase));
        lock (_fileLock)
        {
            Directory.CreateDirectory(Folder("genreprefs"));
            File.WriteAllText(Path.Combine(Folder("genreprefs"), userId.ToString("N") + ".json"), JsonSerializer.Serialize(clean, FileJson));
        }

        return clean;
    }

    /// <summary>
    /// Gets a user's own Collections page choices.
    /// </summary>
    /// <param name="userId">The user.</param>
    public CollectionPrefs GetCollectionPrefs(Guid userId)
    {
        lock (_fileLock)
        {
            var path = Path.Combine(Folder("collectionprefs"), userId.ToString("N") + ".json");
            try
            {
                return File.Exists(path) ? JsonSerializer.Deserialize<CollectionPrefs>(File.ReadAllText(path), FileJson) ?? new CollectionPrefs() : new CollectionPrefs();
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                _logger.LogWarning(ex, "Couldn't read the collections choices for {User}.", userId);
                return new CollectionPrefs();
            }
        }
    }

    /// <summary>
    /// Saves a user's own Collections page choices.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="prefs">The choices.</param>
    public CollectionPrefs SaveCollectionPrefs(Guid userId, CollectionPrefs prefs)
    {
        static List<string> Clean(IEnumerable<string>? ids, int most) => (ids ?? [])
            .Select(id => id?.Trim() ?? string.Empty)
            .Where(id => id.Length is > 0 and <= 120)
            .Distinct(StringComparer.Ordinal)
            .Take(most)
            .ToList();

        var clean = new CollectionPrefs
        {
            Sections = Clean(prefs.Sections, 20),
            HiddenSections = Clean(prefs.HiddenSections, 20),
            Pinned = Clean(prefs.Pinned, 60),
            Hidden = Clean(prefs.Hidden, 500)
        };
        clean.Hidden.RemoveAll(clean.Pinned.Contains);
        lock (_fileLock)
        {
            Directory.CreateDirectory(Folder("collectionprefs"));
            File.WriteAllText(Path.Combine(Folder("collectionprefs"), userId.ToString("N") + ".json"), JsonSerializer.Serialize(clean, FileJson));
        }

        return clean;
    }

    // A title's weight for a user: a rating if they gave one, else lost interest, else how much
    // they watched it, plus a bit for a favorite. Nothing for what someone else watched on their
    // account (they said so when cleaning up Continue Watching).
    private double Weight(User user, BaseItem title)
    {
        if (_continueWatching.IsIgnored(user.Id, title.Id))
        {
            return 0;
        }

        var data = _userDataManager.GetUserData(user, title);
        var favorite = data is { IsFavorite: true } ? 1.5 : 0;
        if (data?.Rating is double rating && rating > 0)
        {
            return RatingWeights[Math.Clamp((int)Math.Round(rating / 2) - 1, 0, 4)] + favorite;
        }

        if (_continueWatching.LostInterest(user.Id, title.Id))
        {
            return LostInterestWeight + favorite;
        }

        double watched;
        if (title is Series series)
        {
            var episodes = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                AncestorIds = [series.Id],
                IncludeItemTypes = [BaseItemKind.Episode],
                Recursive = true,
                IsPlayed = true,
                DtoOptions = new DtoOptions(false)
            }).Count;
            watched = episodes == 0 ? 0 : Math.Min(1 + (episodes / 8.0), 3);
        }
        else
        {
            watched = data is { Played: true } ? 1 : 0;
        }

        return watched + favorite;
    }

    private Dictionary<Guid, double> Compute(User user)
    {
        var started = DateTime.UtcNow;
        var taste = new Dictionary<Guid, double>();
        try
        {
            // Movies they watched, rated, favorited or gave up on; shows with watched episodes, rated, favorited or given up on.
            var titles = new Dictionary<Guid, BaseItem>();
            foreach (var movie in _libraryManager.GetItemList(new InternalItemsQuery(user) { IncludeItemTypes = [BaseItemKind.Movie], Recursive = true, IsPlayed = true, DtoOptions = new DtoOptions(false) }))
            {
                titles[movie.Id] = movie;
            }

            foreach (var episode in _libraryManager.GetItemList(new InternalItemsQuery(user) { IncludeItemTypes = [BaseItemKind.Episode], Recursive = true, IsPlayed = true, DtoOptions = new DtoOptions(false) }).OfType<Episode>())
            {
                if (episode.Series is { } show)
                {
                    titles[show.Id] = show;
                }
            }

            foreach (var item in _libraryManager.GetItemList(new InternalItemsQuery(user) { IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series], Recursive = true, DtoOptions = new DtoOptions(false) }))
            {
                var data = _userDataManager.GetUserData(user, item);
                if (data is { IsFavorite: true } || data?.Rating > 0 || _continueWatching.LostInterest(user.Id, item.Id))
                {
                    titles[item.Id] = item;
                }
            }

            foreach (var title in titles.Values)
            {
                var weight = Weight(user, title);
                if (weight != 0)
                {
                    taste[title.Id] = weight;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Working out genre taste for {User} failed.", user.Username);
        }

        _logger.LogDebug("Genre taste for {User}: {Count} titles in {Ms} ms.", user.Username, taste.Count, (DateTime.UtcNow - started).TotalMilliseconds);
        SaveTaste(user.Id, taste);
        return taste;
    }

    // Watching, favoriting or rating something during the day: just that title again.
    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        try
        {
            if (_userManager.GetUserById(e.UserId) is not { } user)
            {
                return;
            }

            // Not looked at since the server started: update the saved taste. None saved yet:
            // it's worked out in full on their first visit anyway.
            if (!_taste.TryGetValue(e.UserId, out var taste))
            {
                if (LoadTaste(e.UserId) is not { } saved)
                {
                    return;
                }

                taste = _taste.GetOrAdd(e.UserId, saved);
            }

            BaseItem? title = e.Item switch
            {
                Movie movie => movie,
                Series series => series,
                Episode episode => episode.Series,
                _ => null
            };
            if (title is null)
            {
                return;
            }

            var weight = Weight(user, title);
            lock (taste)
            {
                if (weight == 0)
                {
                    taste.Remove(title.Id);
                }
                else
                {
                    taste[title.Id] = weight;
                }
            }

            // Kept on disk too, so a restart doesn't lose it until the nightly rebuild (not on
            // every progress report while something plays).
            if (e.SaveReason != UserDataSaveReason.PlaybackProgress)
            {
                lock (taste)
                {
                    SaveTaste(user.Id, taste);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Updating genre taste failed.");
        }
    }

    private Dictionary<Guid, double>? LoadTaste(Guid userId)
    {
        var path = Path.Combine(Folder("taste"), userId.ToString("N") + ".json");
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<Guid, double>>(File.ReadAllText(path), FileJson) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private void SaveTaste(Guid userId, Dictionary<Guid, double> taste)
    {
        try
        {
            lock (_fileLock)
            {
                Directory.CreateDirectory(Folder("taste"));
                Dictionary<Guid, double> copy;
                lock (taste)
                {
                    copy = new Dictionary<Guid, double>(taste);
                }

                File.WriteAllText(Path.Combine(Folder("taste"), userId.ToString("N") + ".json"), JsonSerializer.Serialize(copy, FileJson));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't save genre taste.");
        }
    }
}

/// <summary>
/// A title and how much it counts for a user's genre rows.
/// </summary>
public sealed class TasteEntry
{
    /// <summary>Gets or sets the movie or show.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the weight (negative: rated badly).</summary>
    public double Weight { get; set; }
}

/// <summary>
/// A user's own genre-row choices.
/// </summary>
public sealed class GenrePrefs
{
    /// <summary>Gets or sets a value indicating whether rows are ordered by what the user watches.</summary>
    public bool Auto { get; set; } = true;

    /// <summary>Gets or sets the genres always at the top, in this order.</summary>
    public List<string> Pinned { get; set; } = [];

    /// <summary>Gets or sets the genres never shown as a row.</summary>
    public List<string> Hidden { get; set; } = [];
}

/// <summary>
/// One user's own Collections page: the order of its sections, sections and collections they
/// don't care about, and collections pinned to the top.
/// </summary>
public sealed class CollectionPrefs
{
    /// <summary>Gets or sets the sections in the user's order ("featured", "universe", "anime"...).</summary>
    public List<string> Sections { get; set; } = [];

    /// <summary>Gets or sets the sections left out.</summary>
    public List<string> HiddenSections { get; set; } = [];

    /// <summary>Gets or sets the collections (by id) pinned to the top, in this order.</summary>
    public List<string> Pinned { get; set; } = [];

    /// <summary>Gets or sets the collections (by id) left out.</summary>
    public List<string> Hidden { get; set; } = [];
}
