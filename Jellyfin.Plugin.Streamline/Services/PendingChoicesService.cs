using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Streamline.Services;

/// <summary>
/// Watch List and Favorites for titles that aren't on the server yet (a new show or a movie in
/// Coming Soon). Jellyfin can only favorite what it has, so the choice is kept here, by TMDb
/// id ("tv:1234", "movie:5678"), and carried out when the title arrives: as soon as Jellyfin
/// adds or identifies it, and again whenever Coming Soon is worked out. Only ever the user's
/// own choice, one small file per user.
/// </summary>
public sealed class PendingChoicesService
{
    private const int MaxEntries = 500;

    private static readonly JsonSerializerOptions FileJson = new() { WriteIndented = false };

    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly WatchlistService _watchlist;
    private readonly ILogger<PendingChoicesService> _logger;
    private readonly object _lock = new();
    private Dictionary<Guid, List<PendingChoice>>? _all;

    /// <summary>
    /// Initializes a new instance of the <see cref="PendingChoicesService"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="userDataManager">Jellyfin's user data manager.</param>
    /// <param name="userManager">Jellyfin's user manager.</param>
    /// <param name="watchlist">The Watch List.</param>
    /// <param name="logger">Logger.</param>
    public PendingChoicesService(ILibraryManager libraryManager, IUserDataManager userDataManager, IUserManager userManager, WatchlistService watchlist, ILogger<PendingChoicesService> logger)
    {
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _userManager = userManager;
        _watchlist = watchlist;
        _logger = logger;
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
    }

    private static string Folder => Path.Combine(Plugin.Instance!.DataFolderPath, "pending");

    /// <summary>
    /// Gets a user's choices for titles not on the server yet.
    /// </summary>
    /// <param name="userId">The user.</param>
    public IReadOnlyList<PendingChoice> Get(Guid userId)
    {
        lock (_lock)
        {
            return Load(userId);
        }
    }

    /// <summary>
    /// Sets or clears one choice (Watch List or Favorite) for a title not on the server yet.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="tmdbId">"tv:1234" or "movie:5678".</param>
    /// <param name="what">"watchlist" or "favorite".</param>
    /// <param name="on">On or off.</param>
    /// <returns>Whether the input made sense.</returns>
    public bool Set(Guid userId, string tmdbId, string what, bool on)
    {
        var parts = tmdbId.Split(':');
        if (parts.Length != 2 || parts[0] is not ("tv" or "movie") || !int.TryParse(parts[1], out _) || what is not ("watchlist" or "favorite"))
        {
            return false;
        }

        lock (_lock)
        {
            var list = Load(userId);
            var entry = list.FirstOrDefault(choice => choice.TmdbId == tmdbId);
            if (entry is null)
            {
                entry = new PendingChoice { TmdbId = tmdbId, Added = DateTime.UtcNow };
                list.Insert(0, entry);
            }

            if (what == "watchlist")
            {
                entry.Watchlist = on;
            }
            else
            {
                entry.Favorite = on;
            }

            list.RemoveAll(choice => !choice.Watchlist && !choice.Favorite);
            Save(userId, list.Take(MaxEntries).ToList());
        }

        return true;
    }

    /// <summary>
    /// Carries out everyone's choices for titles that have arrived since (a safety net for
    /// arrivals the library events didn't catch).
    /// </summary>
    public void ApplyArrived()
    {
        foreach (var user in _userManager.GetUsers())
        {
            List<PendingChoice> list;
            lock (_lock)
            {
                list = Load(user.Id);
            }

            foreach (var choice in list)
            {
                var parts = choice.TmdbId.Split(':');
                var item = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = [parts[0] == "tv" ? BaseItemKind.Series : BaseItemKind.Movie],
                    Recursive = true,
                    HasAnyProviderId = new Dictionary<string, string> { [MetadataProvider.Tmdb.ToString()] = parts[1] }
                }).FirstOrDefault();
                if (item is not null)
                {
                    Apply(user.Id, item, choice.TmdbId);
                }
            }
        }
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is not (Movie or Series) || e.Item.GetProviderId(MetadataProvider.Tmdb) is not { Length: > 0 } tmdb)
        {
            return;
        }

        var key = (e.Item is Series ? "tv:" : "movie:") + tmdb;
        try
        {
            // Library scans update a lot: only look further when someone is waiting for this one.
            List<Guid> waiting;
            lock (_lock)
            {
                waiting = All().Where(entry => entry.Value.Any(choice => choice.TmdbId == key)).Select(entry => entry.Key).ToList();
            }

            foreach (var userId in waiting)
            {
                Apply(userId, e.Item, key);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Carrying out Watch List and Favorites choices failed.");
        }
    }

    // The title is here: on the Watch List and/or a favorite, as the user chose, then forgotten.
    private void Apply(Guid userId, BaseItem item, string key)
    {
        PendingChoice? choice;
        lock (_lock)
        {
            var list = Load(userId);
            choice = list.FirstOrDefault(entry => entry.TmdbId == key);
            if (choice is null)
            {
                return;
            }

            list.Remove(choice);
            Save(userId, list);
        }

        if (choice.Watchlist)
        {
            _watchlist.Add(userId, item.Id);
        }

        if (choice.Favorite && _userManager.GetUserById(userId) is { } user)
        {
            _userDataManager.SaveUserData(user, item, new UpdateUserItemDataDto { IsFavorite = true }, UserDataSaveReason.UpdateUserData);
        }

        _logger.LogInformation("\"{Name}\" arrived: carried out a Watch List/Favorites choice made in Coming Soon.", item.Name);
    }

    // Everyone's choices, read once (from one small file per user) and kept in memory.
    private Dictionary<Guid, List<PendingChoice>> All()
    {
        if (_all is not null)
        {
            return _all;
        }

        _all = [];
        if (Directory.Exists(Folder))
        {
            foreach (var path in Directory.EnumerateFiles(Folder, "*.json"))
            {
                try
                {
                    if (Guid.TryParse(Path.GetFileNameWithoutExtension(path), out var userId))
                    {
                        _all[userId] = JsonSerializer.Deserialize<List<PendingChoice>>(File.ReadAllText(path), FileJson) ?? [];
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    _logger.LogWarning(ex, "Couldn't read waiting Watch List/Favorites choices from {Path}.", path);
                }
            }
        }

        return _all;
    }

    // A copy, so callers can change it and save it back.
    private List<PendingChoice> Load(Guid userId) => All().TryGetValue(userId, out var list) ? list.ToList() : [];

    private void Save(Guid userId, List<PendingChoice> list)
    {
        All()[userId] = list.ToList();
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, userId.ToString("N") + ".json"), JsonSerializer.Serialize(list, FileJson));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't save the waiting Watch List/Favorites choices for {User}.", userId);
        }
    }
}

/// <summary>
/// A choice for a title that isn't on the server yet.
/// </summary>
public sealed class PendingChoice
{
    /// <summary>Gets or sets the title: "tv:1234" or "movie:5678" (TMDb).</summary>
    public string TmdbId { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether it goes on the Watch List.</summary>
    public bool Watchlist { get; set; }

    /// <summary>Gets or sets a value indicating whether it becomes a favorite.</summary>
    public bool Favorite { get; set; }

    /// <summary>Gets or sets when the choice was made.</summary>
    public DateTime Added { get; set; }
}
