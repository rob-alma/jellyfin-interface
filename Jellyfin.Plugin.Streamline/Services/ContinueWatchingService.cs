using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Streamline.Services;

/// <summary>
/// Cleaning up Continue Watching: titles someone took out of the row, and why. The reason is
/// saved where both Streamline and Personal Recommendations read taste from (Jellyfin's own user
/// data), so it counts for the genre rows and the Recommended row:
/// <list type="bullet">
/// <item>didnt-like: rated Bad: counts against similar titles, never recommended.</item>
/// <item>lost-interest: a little against similar titles, never recommended (Jellyfin has no
/// mild dislike: its thumbs down is a rating of 1, so this one is only kept here, and
/// Personal Recommendations reads it from here).</item>
/// <item>finished: marked watched (they saw it somewhere else): counts as watched.</item>
/// <item>later: on the Watch List; no effect on taste.</item>
/// <item>not-me: someone else on the account, or started by accident: left out of their taste.</item>
/// </list>
/// One small file per user. A title comes back in the row once it's played again.
/// </summary>
public sealed class ContinueWatchingService
{
    private const int MaxEntries = 1000;

    private static readonly JsonSerializerOptions FileJson = new() { WriteIndented = false };

    private static readonly string[] Reasons = ["didnt-like", "lost-interest", "finished", "later", "not-me"];

    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly WatchlistService _watchlist;
    private readonly ILogger<ContinueWatchingService> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, List<Entry>> _loaded = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ContinueWatchingService"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="userDataManager">Jellyfin's user data manager.</param>
    /// <param name="watchlist">The Watch List.</param>
    /// <param name="logger">Logger.</param>
    public ContinueWatchingService(ILibraryManager libraryManager, IUserDataManager userDataManager, WatchlistService watchlist, ILogger<ContinueWatchingService> logger)
    {
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _watchlist = watchlist;
        _logger = logger;
        _userDataManager.UserDataSaved += OnUserDataSaved;
    }

    private static string Folder => Path.Combine(Plugin.Instance!.DataFolderPath, "continuewatching");

    /// <summary>
    /// Gets the movies and shows a user took out of Continue Watching.
    /// </summary>
    /// <param name="userId">The user.</param>
    public IReadOnlyList<Guid> Hidden(Guid userId)
    {
        lock (_lock)
        {
            return Load(userId).Select(entry => entry.Id).ToList();
        }
    }

    /// <summary>
    /// Whether a title should be left out of a user's taste (watched by someone else, or by accident).
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="titleId">The movie or show.</param>
    public bool IsIgnored(Guid userId, Guid titleId) => ReasonFor(userId, titleId) == "not-me";

    /// <summary>
    /// Whether a user lost interest in a title (counts a little against its genres).
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="titleId">The movie or show.</param>
    public bool LostInterest(Guid userId, Guid titleId) => ReasonFor(userId, titleId) == "lost-interest";

    private string? ReasonFor(Guid userId, Guid titleId)
    {
        lock (_lock)
        {
            return Load(userId).FirstOrDefault(entry => entry.Id == titleId)?.Reason;
        }
    }

    /// <summary>
    /// Takes a title out of a user's Continue Watching, for a reason (see the class summary).
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="itemId">The movie, show or episode in the row.</param>
    /// <param name="reason">Why.</param>
    /// <returns>Whether it was taken out.</returns>
    public bool Remove(User user, Guid itemId, string reason)
    {
        if (!Reasons.Contains(reason) || _libraryManager.GetItemById(itemId) is not { } item)
        {
            return false;
        }

        BaseItem? title = item switch
        {
            Episode episode => episode.Series,
            Season season => season.Series,
            Movie or Series => item,
            _ => null
        };
        if (title is null)
        {
            return false;
        }

        // Stored first: the user data saved below must not bring it straight back.
        lock (_lock)
        {
            var list = Load(user.Id).Where(entry => entry.Id != title.Id)
                .Prepend(new Entry { Id = title.Id, Reason = reason, At = DateTime.UtcNow })
                .Take(MaxEntries)
                .ToList();
            Save(user.Id, list);
        }

        // Out of other apps' Continue Watching too: no half-watched position left.
        if (_userDataManager.GetUserData(user, item) is { PlaybackPositionTicks: > 0 } && reason != "finished")
        {
            _userDataManager.SaveUserData(user, item, new UpdateUserItemDataDto { PlaybackPositionTicks = 0 }, UserDataSaveReason.UpdateUserData);
        }

        switch (reason)
        {
            case "didnt-like":
                // Bad (4 of 10) on Streamline's five faces. Not Likes: setting that sets the
                // rating to 1 (Terrible).
                _userDataManager.SaveUserData(user, title, new UpdateUserItemDataDto { Rating = 4 }, UserDataSaveReason.UpdateUserRating);
                break;
            case "finished":
                // A show: every episode.
                title.MarkPlayed(user, DateTime.UtcNow, true);
                break;
            case "later":
                _watchlist.Add(user.Id, title.Id);
                break;
        }

        if (reason is "lost-interest" or "not-me" or "later")
        {
            // Nothing of Jellyfin's changes for these, but saving (unchanged) tells the genre rows
            // and Personal Recommendations to look at this title again now, not tonight.
            _userDataManager.SaveUserData(user, title, new UpdateUserItemDataDto(), UserDataSaveReason.UpdateUserData);
        }

        _logger.LogDebug("{User} took {Title} out of Continue Watching ({Reason}).", user.Username, title.Name, reason);
        return true;
    }

    // Playing a title again puts it back in the row (and back into their taste).
    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        if (e.SaveReason is not (UserDataSaveReason.PlaybackStart or UserDataSaveReason.PlaybackProgress or UserDataSaveReason.PlaybackFinished))
        {
            return;
        }

        var titleId = e.Item switch
        {
            Episode episode => episode.SeriesId,
            _ => e.Item.Id
        };
        lock (_lock)
        {
            var list = Load(e.UserId);
            if (list.RemoveAll(entry => entry.Id == titleId) > 0)
            {
                Save(e.UserId, list);
            }
        }
    }

    // Kept in memory once read (the genre rows ask about every title): a copy, so callers can change it.
    private List<Entry> Load(Guid userId)
    {
        if (_loaded.TryGetValue(userId, out var known))
        {
            return known.ToList();
        }

        var path = Path.Combine(Folder, userId.ToString("N") + ".json");
        try
        {
            var list = File.Exists(path) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path), FileJson) ?? [] : [];
            _loaded[userId] = list;
            return list.ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "Couldn't read the Continue Watching clean-up for {User}.", userId);
            return [];
        }
    }

    private void Save(Guid userId, List<Entry> list)
    {
        _loaded[userId] = list.ToList();
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, userId.ToString("N") + ".json"), JsonSerializer.Serialize(list, FileJson));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't save the Continue Watching clean-up for {User}.", userId);
        }
    }

    private sealed class Entry
    {
        public Guid Id { get; set; }

        public string Reason { get; set; } = string.Empty;

        public DateTime At { get; set; }
    }
}
