using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Streamline.Services;

/// <summary>
/// Each user's Watch List: the movies and shows they still want to watch. Jellyfin has no
/// watchlist of its own (favorites are a different thing, and playlists turn a show into its
/// episodes), so it's kept here: one small JSON file per user, named by their user id, in the
/// plugin's data folder. Every read and write goes through the signed-in user's id, so nobody can
/// see or change anyone else's list. A title leaves the list once it's been watched after it
/// was added (adding it again afterwards puts it back).
/// </summary>
public sealed class WatchlistService
{
    private const int MaxEntries = 2000;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<WatchlistService> _logger;
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchlistService"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="userDataManager">Jellyfin's user data manager.</param>
    /// <param name="logger">Logger.</param>
    public WatchlistService(ILibraryManager libraryManager, IUserDataManager userDataManager, ILogger<WatchlistService> logger)
    {
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets a user's list as stored, most recently added first.
    /// </summary>
    /// <param name="userId">The user.</param>
    public IReadOnlyList<Guid> Get(Guid userId)
    {
        lock (_lock)
        {
            return Load(userId).Select(entry => entry.Id).ToList();
        }
    }

    /// <summary>
    /// Gets a user's list, after dropping (for good) anything they've watched since adding it.
    /// </summary>
    /// <param name="user">The user.</param>
    public IReadOnlyList<Guid> GetActive(User user)
    {
        lock (_lock)
        {
            var entries = Load(user.Id);
            var kept = entries.Where(entry => !WatchedSince(user, entry)).ToList();
            if (kept.Count != entries.Count)
            {
                Save(user.Id, kept);
            }

            return kept.Select(entry => entry.Id).ToList();
        }
    }

    /// <summary>
    /// Adds an item to the top of a user's list (or moves it there), as of now.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="itemId">The movie or show.</param>
    public void Add(Guid userId, Guid itemId)
    {
        lock (_lock)
        {
            var list = Load(userId).Where(entry => entry.Id != itemId).Prepend(new Entry { Id = itemId, Added = DateTime.UtcNow }).Take(MaxEntries).ToList();
            Save(userId, list);
        }
    }

    /// <summary>
    /// Removes an item from a user's list.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="itemId">The movie or show.</param>
    public void Remove(Guid userId, Guid itemId)
    {
        lock (_lock)
        {
            Save(userId, Load(userId).Where(entry => entry.Id != itemId).ToList());
        }
    }

    // Watched after it was added: a movie played since; a show with every episode watched, the
    // last of them since.
    private bool WatchedSince(User user, Entry entry)
    {
        var item = _libraryManager.GetItemById(entry.Id);
        if (item is null)
        {
            return false;
        }

        if (item is Series series)
        {
            if (!series.IsPlayed(user, _userDataManager.GetUserData(user, series)))
            {
                return false;
            }

            var last = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                AncestorIds = [series.Id],
                IncludeItemTypes = [BaseItemKind.Episode],
                Recursive = true,
                OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)],
                Limit = 1,
                DtoOptions = new DtoOptions(false)
            }).FirstOrDefault();
            var lastPlayed = last is null ? null : _userDataManager.GetUserData(user, last)?.LastPlayedDate;
            return lastPlayed is null || lastPlayed >= entry.Added;
        }

        var data = _userDataManager.GetUserData(user, item);
        return data is not null && data.Played && (data.LastPlayedDate is null || data.LastPlayedDate >= entry.Added);
    }

    private static string Folder() => Path.Combine(Plugin.Instance!.DataFolderPath, "watchlists");

    private static string FileFor(Guid userId) => Path.Combine(Folder(), userId.ToString("N") + ".json");

    private List<Entry> Load(Guid userId)
    {
        try
        {
            var file = FileFor(userId);
            if (!File.Exists(file))
            {
                return [];
            }

            var json = File.ReadAllText(file);
            // Lists saved before 0.10 were plain ids; their titles count as added long ago.
            return json.TrimStart().StartsWith("[\"", StringComparison.Ordinal)
                ? (JsonSerializer.Deserialize<List<Guid>>(json) ?? []).Select(id => new Entry { Id = id, Added = DateTime.MinValue }).ToList()
                : JsonSerializer.Deserialize<List<Entry>>(json) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the watchlist of user {UserId}; treating it as empty.", userId);
            return [];
        }
    }

    private static void Save(Guid userId, List<Entry> list)
    {
        Directory.CreateDirectory(Folder());
        var file = FileFor(userId);
        // Write-then-move, so a crash mid-write can't leave a half-written list behind.
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list));
        File.Move(temp, file, true);
    }

    private sealed class Entry
    {
        public Guid Id { get; set; }

        public DateTime Added { get; set; }
    }
}
