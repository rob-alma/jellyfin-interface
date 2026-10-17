using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Streamline.Services;

/// <summary>
/// Admins' own trailer choices for a movie, show or season, when the automatic one is wrong:
/// a YouTube video to use instead, or no trailer at all. Kept in the plugin's data folder, so
/// the title's own metadata is left as it is and "back to automatic" is just removing the entry.
/// </summary>
public sealed class TrailerOverrides
{
    private static readonly JsonSerializerOptions FileJson = new() { WriteIndented = true };

    private readonly ILogger<TrailerOverrides> _logger;
    private readonly object _lock = new();
    private Dictionary<Guid, TrailerOverride>? _overrides;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrailerOverrides"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public TrailerOverrides(ILogger<TrailerOverrides> logger)
    {
        _logger = logger;
    }

    private static string FilePath => Path.Combine(Plugin.Instance!.DataFolderPath, "trailer-overrides.json");

    /// <summary>
    /// Gets the override for a title, if an admin set one.
    /// </summary>
    /// <param name="itemId">The movie, show or season.</param>
    public TrailerOverride? Get(Guid itemId)
    {
        lock (_lock)
        {
            return Load().TryGetValue(itemId, out var value) ? value : null;
        }
    }

    /// <summary>
    /// Sets (or, with null, removes) the override for a title.
    /// </summary>
    /// <param name="itemId">The movie, show or season.</param>
    /// <param name="value">The override, or null for automatic.</param>
    public void Set(Guid itemId, TrailerOverride? value)
    {
        lock (_lock)
        {
            var all = Load();
            if (value is null)
            {
                all.Remove(itemId);
            }
            else
            {
                all[itemId] = value;
            }

            try
            {
                Directory.CreateDirectory(Plugin.Instance!.DataFolderPath);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(all, FileJson));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Couldn't save the trailer choice.");
            }
        }
    }

    private Dictionary<Guid, TrailerOverride> Load()
    {
        if (_overrides is not null)
        {
            return _overrides;
        }

        try
        {
            _overrides = File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<Guid, TrailerOverride>>(File.ReadAllText(FilePath), FileJson) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "Couldn't read the trailer choices.");
            _overrides = [];
        }

        return _overrides;
    }
}

/// <summary>
/// An admin's trailer choice.
/// </summary>
public sealed class TrailerOverride
{
    /// <summary>Gets or sets the YouTube video id to use (null with <see cref="None"/>).</summary>
    public string? Key { get; set; }

    /// <summary>Gets or sets the video's title, as YouTube has it.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets a value indicating whether the title shows no trailer at all.</summary>
    public bool None { get; set; }
}
