using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Streamline.Services;

/// <summary>
/// Finds movies and shows that Jellyfin identified wrongly (or not at all), or that have no
/// poster, and fixes them. What a title *should* be comes from its folder/file name (and year),
/// which is what Radarr/Sonarr and people adding things by hand control; when Jellyfin's match
/// disagrees with it (a different name and year, or no match at all), the title is looked up
/// again by that name and year, and re-identified only if one result clearly fits, the same as
/// using "Identify" by hand. Anything less certain is listed for the admin instead of guessed at.
/// Locked items are never touched.
/// </summary>
public sealed partial class IdentityService
{
    private const double SameName = 0.6;
    private const double ConfidentMatch = 0.85;
    private static readonly TimeSpan RecheckAfter = TimeSpan.FromDays(7);
    private static readonly TimeSpan VerifyAfter = TimeSpan.FromMinutes(10);

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<IdentityService> _logger;
    private readonly ConcurrentDictionary<Guid, DateTime> _searched = new();
    private readonly ConcurrentDictionary<Guid, (DateTime At, IdentityOutcome What)> _toVerify = new();
    private readonly object _fileLock = new();
    private IssueState? _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="IdentityService"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="providerManager">Jellyfin's metadata provider manager.</param>
    /// <param name="fileSystem">Jellyfin's file system abstraction.</param>
    /// <param name="logger">Logger.</param>
    public IdentityService(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, ILogger<IdentityService> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Gets the titles that need fixing by hand (no confident match, or still wrong after an
    /// automatic fix), for the admin. Kept on disk, so the list survives restarts.
    /// </summary>
    public IReadOnlyList<IdentityIssue> Issues
    {
        get
        {
            lock (_fileLock)
            {
                return State().Issues.Values.OrderBy(issue => issue.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
    }

    /// <summary>
    /// Takes a title off the list for good: the admin fixed it by hand, or it's right as it is.
    /// </summary>
    /// <param name="itemId">The title.</param>
    public void Dismiss(Guid itemId)
    {
        lock (_fileLock)
        {
            var state = State();
            state.Issues.Remove(itemId);
            state.Dismissed.Add(itemId);
            SaveState();
        }
    }

    /// <summary>
    /// A while after an automatic fix, checks it took; if not, the title goes on the admin's list.
    /// </summary>
    public void VerifyPending()
    {
        foreach (var (id, (at, what)) in _toVerify.Where(pair => DateTime.UtcNow - pair.Value.At >= VerifyAfter).ToList())
        {
            _toVerify.TryRemove(id, out _);
            var item = _libraryManager.GetItemById(id);
            if (item is null)
            {
                continue;
            }

            if (what == IdentityOutcome.Fixed && Diagnose(item, ExpectedTitle.FromPath(item)) is { } problem)
            {
                Flag(item, "Still wrong after an automatic fix: " + problem);
            }
            else if (!item.HasImage(ImageType.Primary, 0))
            {
                Flag(item, "No poster found automatically");
            }
        }
    }

    /// <summary>
    /// Checks one movie or show and fixes what it can.
    /// </summary>
    /// <param name="item">A movie or show.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened, for logging.</returns>
    public async Task<IdentityOutcome> CheckAsync(BaseItem item, CancellationToken cancellationToken)
    {
        if (item is not (Movie or Series) || item.IsLocked || IsDismissed(item.Id))
        {
            return IdentityOutcome.Fine;
        }

        var expected = ExpectedTitle.FromPath(item);
        var problem = Diagnose(item, expected);
        if (problem is null)
        {
            if (!item.HasImage(ImageType.Primary, 0))
            {
                // Identified fine, just no poster: fetch images again (keeping any that exist).
                _providerManager.QueueRefresh(item.Id, Options(replace: false), RefreshPriority.Low);
                _toVerify[item.Id] = (DateTime.UtcNow, IdentityOutcome.PosterRequested);
                return IdentityOutcome.PosterRequested;
            }

            Unflag(item.Id);
            return IdentityOutcome.Fine;
        }

        if (_searched.TryGetValue(item.Id, out var last) && DateTime.UtcNow - last < RecheckAfter)
        {
            return IdentityOutcome.Skipped;
        }

        _searched[item.Id] = DateTime.UtcNow;
        var match = await FindMatchAsync(item, expected!, cancellationToken).ConfigureAwait(false);
        if (match is null)
        {
            Flag(item, problem);
            return IdentityOutcome.NeedsAttention;
        }

        if (SameIds(match.ProviderIds, item.ProviderIds))
        {
            // The search lands on what it already is (e.g. the folder uses a translated title).
            Unflag(item.Id);
            return IdentityOutcome.Fine;
        }

        _logger.LogInformation(
            "Re-identifying \"{Name}\" ({Year}) as \"{Match}\" ({MatchYear}): {Problem}",
            item.Name,
            item.ProductionYear,
            match.Name,
            match.ProductionYear,
            problem);

        item.ProviderIds.Clear();
        foreach (var (provider, id) in match.ProviderIds)
        {
            item.ProviderIds[provider] = id;
        }

        var options = Options(replace: true);
        options.SearchResult = match;
        await _providerManager.RefreshFullItem(item, options, cancellationToken).ConfigureAwait(false);
        Unflag(item.Id);
        _toVerify[item.Id] = (DateTime.UtcNow, IdentityOutcome.Fixed);
        return IdentityOutcome.Fixed;
    }

    /// <summary>
    /// Says what looks wrong about an item, or null if nothing does.
    /// </summary>
    /// <param name="item">A movie or show.</param>
    /// <param name="expected">What its folder/file name says it is.</param>
    public static string? Diagnose(BaseItem item, ExpectedTitle? expected)
    {
        var hasIds = !string.IsNullOrEmpty(item.GetProviderId(MetadataProvider.Tmdb))
            || !string.IsNullOrEmpty(item.GetProviderId(MetadataProvider.Imdb))
            || !string.IsNullOrEmpty(item.GetProviderId(MetadataProvider.Tvdb));
        if (expected is null)
        {
            return hasIds ? null : "not identified";
        }

        if (!string.IsNullOrEmpty(expected.TmdbId) && expected.TmdbId != item.GetProviderId(MetadataProvider.Tmdb))
        {
            return $"folder says TMDb {expected.TmdbId}";
        }

        if (!hasIds)
        {
            return "not identified";
        }

        var nameFits = Similarity(item.Name, expected.Name) >= SameName
            || Similarity(item.OriginalTitle, expected.Name) >= SameName;
        var yearFits = expected.Year is null || item.ProductionYear is null || Math.Abs(expected.Year.Value - item.ProductionYear.Value) <= 1;
        if (!nameFits)
        {
            return $"name doesn't match the folder (\"{expected.Name}\")";
        }

        return yearFits ? null : $"year {item.ProductionYear} doesn't match the folder ({expected.Year})";
    }

    private async Task<RemoteSearchResult?> FindMatchAsync(BaseItem item, ExpectedTitle expected, CancellationToken cancellationToken)
    {
        var language = item.GetPreferredMetadataLanguage();
        var country = item.GetPreferredMetadataCountryCode();
        IEnumerable<RemoteSearchResult> results;
        if (item is Movie)
        {
            var info = new MovieInfo { Name = expected.Name, Year = expected.Year, MetadataLanguage = language, MetadataCountryCode = country };
            if (!string.IsNullOrEmpty(expected.TmdbId))
            {
                info.ProviderIds[MetadataProvider.Tmdb.ToString()] = expected.TmdbId;
            }

            results = await _providerManager.GetRemoteSearchResults<Movie, MovieInfo>(
                new RemoteSearchQuery<MovieInfo> { SearchInfo = info, ItemId = item.Id },
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var info = new SeriesInfo { Name = expected.Name, Year = expected.Year, MetadataLanguage = language, MetadataCountryCode = country };
            if (!string.IsNullOrEmpty(expected.TmdbId))
            {
                info.ProviderIds[MetadataProvider.Tmdb.ToString()] = expected.TmdbId;
            }

            results = await _providerManager.GetRemoteSearchResults<Series, SeriesInfo>(
                new RemoteSearchQuery<SeriesInfo> { SearchInfo = info, ItemId = item.Id },
                cancellationToken).ConfigureAwait(false);
        }

        var list = results.ToList();
        if (!string.IsNullOrEmpty(expected.TmdbId))
        {
            // The folder names the exact TMDb id (Radarr/Sonarr can add it): take that one.
            return list.FirstOrDefault(result => result.GetProviderId(MetadataProvider.Tmdb) == expected.TmdbId);
        }

        // One title has to fit clearly: near-identical name and (if known) the same year. Only
        // results carrying a TMDb id count; other sources often list the same title again
        // without one, which isn't a second candidate.
        var fitting = list
            .Where(result => !string.IsNullOrEmpty(result.GetProviderId(MetadataProvider.Tmdb)))
            .Where(result => expected.Year is null || result.ProductionYear is null || Math.Abs(expected.Year.Value - result.ProductionYear.Value) <= 1)
            .Select(result => (Result: result, Score: Similarity(result.Name, expected.Name)))
            .Where(entry => entry.Score >= ConfidentMatch)
            .OrderByDescending(entry => entry.Score)
            .ThenByDescending(entry => entry.Result.ProductionYear == expected.Year)
            .ToList();
        if (fitting.Count == 0)
        {
            return null;
        }

        // Two different titles fitting equally well (remakes in the same year...) is a guess.
        var best = fitting[0];
        var rival = fitting.Skip(1).FirstOrDefault(entry =>
            entry.Score >= best.Score - 0.02
            && entry.Result.ProductionYear == best.Result.ProductionYear
            && entry.Result.GetProviderId(MetadataProvider.Tmdb) != best.Result.GetProviderId(MetadataProvider.Tmdb));
        return rival.Result is null ? best.Result : null;
    }

    private bool IsDismissed(Guid itemId)
    {
        lock (_fileLock)
        {
            return State().Dismissed.Contains(itemId);
        }
    }

    private void Flag(BaseItem item, string problem)
    {
        lock (_fileLock)
        {
            State().Issues[item.Id] = new IdentityIssue
            {
                ItemId = item.Id,
                Name = item.Name,
                Type = item is Series ? "Show" : "Movie",
                Expected = ExpectedTitle.FromPath(item)?.Describe() ?? string.Empty,
                Problem = problem
            };
            SaveState();
        }
    }

    private void Unflag(Guid itemId)
    {
        lock (_fileLock)
        {
            if (State().Issues.Remove(itemId))
            {
                SaveState();
            }
        }
    }

    private static string StateFile() => System.IO.Path.Combine(Plugin.Instance!.DataFolderPath, "identity-issues.json");

    private IssueState State()
    {
        if (_state is not null)
        {
            return _state;
        }

        try
        {
            _state = System.IO.File.Exists(StateFile())
                ? System.Text.Json.JsonSerializer.Deserialize<IssueState>(System.IO.File.ReadAllText(StateFile())) ?? new IssueState()
                : new IssueState();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the list of titles needing a look; starting a new one.");
            _state = new IssueState();
        }

        return _state;
    }

    private void SaveState()
    {
        System.IO.Directory.CreateDirectory(Plugin.Instance!.DataFolderPath);
        var temp = StateFile() + ".tmp";
        System.IO.File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(_state));
        System.IO.File.Move(temp, StateFile(), true);
    }

    private MetadataRefreshOptions Options(bool replace) => new(new DirectoryService(_fileSystem))
    {
        MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
        ImageRefreshMode = MetadataRefreshMode.FullRefresh,
        ReplaceAllMetadata = replace,
        ReplaceAllImages = replace,
        RemoveOldMetadata = replace
    };

    private static bool SameIds(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
    {
        foreach (var provider in new[] { "Tmdb", "Imdb", "Tvdb" })
        {
            if (a.TryGetValue(provider, out var x) && b.TryGetValue(provider, out var y) && !string.IsNullOrEmpty(x) && x == y)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Lowercases, strips accents and punctuation, and drops a leading "the", so titles compare
    /// on what they say rather than how they're written.
    /// </summary>
    /// <param name="text">A title.</param>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        var words = builder.ToString().Replace(" and ", " ", StringComparison.Ordinal).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && words[0] == "the")
        {
            words.RemoveAt(0);
        }

        return string.Join(' ', words);
    }

    /// <summary>
    /// How alike two titles are, 0 to 1 (edit distance on the normalized forms).
    /// </summary>
    /// <param name="a">One title.</param>
    /// <param name="b">The other.</param>
    public static double Similarity(string? a, string? b)
    {
        var x = Normalize(a);
        var y = Normalize(b);
        if (x.Length == 0 || y.Length == 0)
        {
            return 0;
        }

        if (x == y)
        {
            return 1;
        }

        var previous = new int[y.Length + 1];
        var current = new int[y.Length + 1];
        for (var j = 0; j <= y.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= x.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= y.Length; j++)
            {
                var cost = x[i - 1] == y[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return 1.0 - ((double)previous[y.Length] / Math.Max(x.Length, y.Length));
    }

    [GeneratedRegex(@"\{[^}]*\}|\[[^\]]*\]")]
    private static partial Regex BracketsRegex();

    [GeneratedRegex(@"(?:tmdb(?:id)?)[-=_ ]?(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex TmdbIdRegex();

    [GeneratedRegex(@"(?<=\S)[\s._(\[-]+((?:19|20)\d{2})(?!\d)")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"\s*\(((?:19|20)\d{2})\)")]
    private static partial Regex BracketYearRegex();

    [GeneratedRegex(@"\b(2160p|1080p|720p|480p|4k|uhd|bluray|blu-ray|web-?dl|webrip|hdtv|x264|x265|h\.?264|h\.?265|hevc|remux|proper|repack|extended|unrated|imax)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseTagsRegex();

    /// <summary>
    /// What a folder or file name says a title is.
    /// </summary>
    public sealed class ExpectedTitle
    {
        /// <summary>Gets the title.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Gets the year, if the name has one.</summary>
        public int? Year { get; init; }

        /// <summary>Gets a TMDb id written into the name ({tmdb-123}), if any.</summary>
        public string? TmdbId { get; init; }

        /// <summary>Gets a short description for the admin list.</summary>
        public string Describe() => Year is null ? Name : $"{Name} ({Year})";

        /// <summary>
        /// Reads a title from a show's folder, or a movie's folder (or file, when movies sit
        /// loose in the library folder). Handles "Title (Year)", Radarr/Sonarr's
        /// "{tmdb-123}" and "[1080p]" parts, and scene names like "Title.Year.1080p.WEB-DL".
        /// </summary>
        /// <param name="item">A movie or show.</param>
        public static ExpectedTitle? FromPath(BaseItem item)
        {
            if (string.IsNullOrEmpty(item.Path))
            {
                return null;
            }

            var raw = item is Series
                ? Path.GetFileName(item.Path.TrimEnd('/', '\\'))
                : MovieName(item.Path);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var tmdb = TmdbIdRegex().Match(raw);
            var text = BracketsRegex().Replace(raw, " ");
            // The year is the one in brackets ("Blade Runner 2049 (2017)"), or else the last
            // year-like number ("Blade.Runner.2049.2017.1080p"): a title can contain a year.
            int? year = null;
            var bracketed = BracketYearRegex().Matches(text);
            var yearMatch = bracketed.Count > 0 ? bracketed[^1] : YearRegex().Matches(text).LastOrDefault();
            if (yearMatch is not null && yearMatch.Success)
            {
                year = int.Parse(yearMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                text = text[..yearMatch.Index];
            }

            text = ReleaseTagsRegex().Replace(text, string.Empty);
            // Scene names use dots/underscores for spaces; a title with real spaces keeps its dots.
            if (!text.Contains(' ', StringComparison.Ordinal))
            {
                text = text.Replace('.', ' ').Replace('_', ' ');
            }

            text = Regex.Replace(text, @"\s+", " ").Trim(' ', '-', '(', '.');
            return text.Length == 0 ? null : new ExpectedTitle { Name = text, Year = year, TmdbId = tmdb.Success ? tmdb.Groups[1].Value : null };
        }

        private static string MovieName(string path)
        {
            var file = Path.GetFileNameWithoutExtension(path);
            var folder = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
            // Movie in its own folder (the usual, and what Radarr does): the folder is the
            // cleaner name. Loose in the library folder: only the file name says anything.
            return !string.IsNullOrEmpty(folder) && Similarity(folder, file) >= 0.5 ? folder : file;
        }
    }
}

/// <summary>
/// A title that looks wrong but wasn't fixed automatically.
/// </summary>
public sealed class IdentityIssue
{
    /// <summary>Gets or sets the item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets what Jellyfin currently calls it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets whether it's a movie or a show.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets what its folder/file name says it is.</summary>
    public string Expected { get; set; } = string.Empty;

    /// <summary>Gets or sets what looks wrong.</summary>
    public string Problem { get; set; } = string.Empty;
}

/// <summary>
/// The admin's list, as kept on disk.
/// </summary>
public sealed class IssueState
{
    /// <summary>Gets or sets the titles needing a look, by item id.</summary>
    public Dictionary<Guid, IdentityIssue> Issues { get; set; } = [];

    /// <summary>Gets or sets the titles the admin marked done; they're not flagged again.</summary>
    public HashSet<Guid> Dismissed { get; set; } = [];
}

/// <summary>
/// The result of checking one title.
/// </summary>
public enum IdentityOutcome
{
    /// <summary>Nothing wrong.</summary>
    Fine,

    /// <summary>Checked recently; not searched again yet.</summary>
    Skipped,

    /// <summary>Re-identified.</summary>
    Fixed,

    /// <summary>Asked Jellyfin to fetch a missing poster.</summary>
    PosterRequested,

    /// <summary>Looks wrong, no confident fix; listed for the admin.</summary>
    NeedsAttention
}
