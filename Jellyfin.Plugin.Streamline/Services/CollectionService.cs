using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Streamline.Services;

/// <summary>
/// The Collections page. Worked out live from the library so new titles join their collection by
/// themselves:
/// <list type="bullet">
/// <item>Featured: handy themed collections up front (Disney, Marvel, DC, Musicals, Best Picture
/// winners...), plus the admin's own. Shown from 3 titles.</item>
/// <item>Universes: franchises that span movies and shows (Marvel, Star Wars, Breaking Bad...),
/// from a built-in list plus the admin's own (settings page). Shown from 2 titles when shows are
/// in it, else from 3 movies.</item>
/// <item>Movie series from TMDb (James Bond, Toy Story...), shown from 3 movies: two is a sequel,
/// not a collection.</item>
/// <item>Directors with 4 or more movies here.</item>
/// <item>Collections made in Jellyfin itself, always shown.</item>
/// </list>
/// Everything is per user: only titles the user can see count.
/// </summary>
public sealed class CollectionService
{
    private const int MinUniverseItems = 2;
    private const int MinTmdbItems = 3;
    private const int MinFeaturedItems = 3;
    private const int MinDirectorMovies = 4;
    private static readonly TimeSpan PeopleLifetime = TimeSpan.FromHours(6);

    // Best Picture winners, as "title|year" (the film's year, not the ceremony's).
    private static readonly string[] BestPictureWinners =
    [
        "gone with the wind|1939", "casablanca|1942", "ben-hur|1959", "the apartment|1960", "west side story|1961", "lawrence of arabia|1962",
        "my fair lady|1964", "the sound of music|1965", "midnight cowboy|1969", "patton|1970", "the french connection|1971", "the godfather|1972",
        "the sting|1973", "the godfather part ii|1974", "one flew over the cuckoo's nest|1975", "rocky|1976", "annie hall|1977", "the deer hunter|1978",
        "kramer vs. kramer|1979", "ordinary people|1980", "chariots of fire|1981", "gandhi|1982", "terms of endearment|1983", "amadeus|1984",
        "out of africa|1985", "platoon|1986", "the last emperor|1987", "rain man|1988", "driving miss daisy|1989", "dances with wolves|1990",
        "the silence of the lambs|1991", "unforgiven|1992", "schindler's list|1993", "forrest gump|1994", "braveheart|1995", "the english patient|1996",
        "titanic|1997", "shakespeare in love|1998", "american beauty|1999", "gladiator|2000", "a beautiful mind|2001", "chicago|2002",
        "the lord of the rings: the return of the king|2003", "million dollar baby|2004", "crash|2004", "the departed|2006", "no country for old men|2007",
        "slumdog millionaire|2008", "the hurt locker|2008", "the king's speech|2010", "the artist|2011", "argo|2012", "12 years a slave|2013",
        "birdman or (the unexpected virtue of ignorance)|2014", "birdman|2014", "spotlight|2015", "moonlight|2016", "the shape of water|2017",
        "green book|2018", "parasite|2019", "nomadland|2020", "coda|2021", "everything everywhere all at once|2022", "oppenheimer|2023", "anora|2024"
    ];

    // Emmy winners for Outstanding Drama, Comedy and Limited Series (Jellyfin has no award data).
    private static readonly string[] EmmyWinningShows =
    [
        "nypd blue", "er", "law & order", "the practice", "the west wing", "the sopranos", "lost", "24", "mad men", "homeland",
        "breaking bad", "game of thrones", "the handmaid's tale", "the crown", "succession", "shogun", "the pitt",
        "frasier", "ally mcbeal", "will & grace", "sex and the city", "friends", "everybody loves raymond", "arrested development",
        "the office", "30 rock", "modern family", "veep", "the marvelous mrs. maisel", "fleabag", "schitt's creek", "ted lasso",
        "the bear", "hacks", "the studio",
        "band of brothers", "angels in america", "john adams", "the pacific", "downton abbey", "fargo", "olive kitteridge",
        "the people v. o.j. simpson: american crime story", "big little lies", "the assassination of gianni versace: american crime story",
        "chernobyl", "watchmen", "the queen's gambit", "the white lotus", "beef", "baby reindeer", "adolescence"
    ];

    // Decades, after the classics. Movies by release year, shows by the year they started.
    private static readonly Universe[] Decades =
    [
        new("classics", "Classics", ["classic", "classics", "old", "before 1980"], MaxYear: 1979),
        new("1980s", "80s", ["80s", "1980s", "eighties"], MinYear: 1980, MaxYear: 1989),
        new("1990s", "90s", ["90s", "1990s", "nineties"], MinYear: 1990, MaxYear: 1999),
        new("2000s", "00s", ["00s", "2000s", "noughties"], MinYear: 2000, MaxYear: 2009),
        new("2010s", "10s", ["10s", "2010s", "tens"], MinYear: 2010, MaxYear: 2019),
        new("2020s", "20s", ["20s", "2020s", "twenties"], MinYear: 2020, MaxYear: 2029),
    ];

    // Featured: themed collections that cut across franchises, shown first, in this order.
    private static readonly Universe[] Featured =
    [
        // Disney's own studios, not its distributors (Walt Disney Japan put out Studio Ghibli's).
        new("disney", "Disney", ["disney", "walt disney"], StudioContains: ["walt disney pictures", "walt disney animation", "walt disney productions", "walt disney feature animation", "walt disney television", "pixar", "disney television animation", "disneytoon", "disney channel"]),
        new("marvel", "Marvel", ["marvel", "mcu", "x-men", "xmen"], Tags: ["marvel cinematic universe (mcu)"], Studios: ["Marvel Studios"],
            Collections: ["748", "453993", "448150"], Prefixes: ["x-men"], Titles: ["logan", "legion", "the gifted"]),
        new("dc", "DC", ["dc", "dc comics", "dceu", "dcu", "batman", "superman", "justice league"],
            Tags: ["dc extended universe (dceu)", "dc universe (dcu)", "dc comics", "based on dc comics", "batman", "superman", "wonder woman", "justice league"],
            Studios: ["DC", "DC Studios", "DC Films", "DC Entertainment", "DC Comics"],
            Titles: ["peacemaker", "creature commandos", "the penguin", "gotham", "titans", "doom patrol", "harley quinn", "smallville", "watchmen", "the sandman", "superman & lois", "arrow", "the flash", "supergirl", "dc's legends of tomorrow", "batwoman", "black lightning", "stargirl", "joker", "joker: folie a deux"]),
        new("pixar", "Pixar", ["pixar"], StudioContains: ["pixar"]),
        new("books", "Book Adaptations", ["book", "books", "novel", "novels", "based on a book", "boek", "boeken", "roman", "verfilming"],
            Tags: ["based on novel or book", "based on novel", "based on young adult novel", "based on book", "based on short story", "based on children's book", "based on play or musical"]),
        new("musicals", "Musicals", ["musical", "musicals", "sing along"], Tags: ["musical", "musical film", "broadway", "jukebox musical", "song and dance"]),
        new("best-picture", "Best Picture Winners", ["oscar", "oscars", "academy award", "best picture"], TitleYears: BestPictureWinners, MoviesOnly: true),
        new("award-shows", "Award-winning Shows", ["emmy", "emmys", "award", "awards", "award winning"], Titles: EmmyWinningShows, SeriesOnly: true, ShowsOnly: true),
        new("ghibli", "Studio Ghibli", ["ghibli", "miyazaki"], StudioContains: ["studio ghibli"]),
        new("dreamworks", "DreamWorks Animation", ["dreamworks"], StudioContains: ["dreamworks animation"]),
        new("christmas", "Christmas", ["christmas", "xmas", "holiday"], Tags: ["christmas", "christmas eve", "santa claus", "christmas party", "christmas tree"]),
        new("halloween", "Halloween", ["halloween", "spooky"], Tags: ["halloween", "trick or treating"]),
        new("a24", "A24", ["a24"], StudioContains: ["a24"]),
        new("stephen-king", "Stephen King", ["stephen king", "king"], Tags: ["based on stephen king's work", "stephen king"], People: ["Stephen King"]),
        new("video-games", "Video Game Adaptations", ["video game", "game"], Tags: ["based on video game", "video game adaptation"]),
        new("short", "Under 90 Minutes", ["short", "quick"], MaxMinutes: 90, MoviesOnly: true),
    ];
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    // Built-in universes. A title belongs when it's in one of the TMDb collections, has one of
    // the TMDb keywords, comes from one of the studios, or has one of the titles (or starts with
    // one of the prefixes). TMDb collections listed here are folded into the universe instead of
    // also showing on their own.
    private static readonly Universe[] Universes =
    [
        new("mcu", "Marvel Cinematic Universe", ["marvel", "mcu", "avengers"], Tags: ["marvel cinematic universe (mcu)"], Studios: ["Marvel Studios"]),
        new("star-wars", "Star Wars", ["star wars", "jedi"], Collections: ["10"], Tags: ["star wars"], Prefixes: ["star wars"],
            Titles: ["andor", "the mandalorian", "ahsoka", "the book of boba fett", "obi-wan kenobi", "the acolyte", "skeleton crew", "rogue one: a star wars story", "solo: a star wars story", "the bad batch"]),
        new("x-men", "X-Men", ["x-men", "xmen", "wolverine", "deadpool", "mutants"], Collections: ["748", "453993", "448150"], Prefixes: ["x-men"], Titles: ["logan", "legion", "the gifted", "the new mutants"]),
        new("batman", "Batman", ["batman", "gotham", "dark knight"], Collections: ["263", "120794"], Prefixes: ["batman"],
            Titles: ["the batman", "the dark knight", "the dark knight rises", "batman begins", "the penguin", "gotham", "the lego batman movie"]),
        new("superman", "Superman", ["superman", "man of steel", "clark kent"], Collections: ["8537"],
            Titles: ["superman", "superman ii", "superman iii", "superman iv: the quest for peace", "superman returns", "man of steel", "superman & lois", "smallville", "my adventures with superman"]),
        new("wizarding-world", "Wizarding World", ["harry potter", "hogwarts", "fantastic beasts"], Collections: ["1241", "435259"], Tags: ["wizarding world"], Titles: ["harry potter"]),
        new("middle-earth", "Middle-earth", ["lord of the rings", "lotr", "hobbit", "tolkien"], Collections: ["119", "121312"],
            Titles: ["the lord of the rings: the rings of power", "the lord of the rings: the war of the rohirrim"]),
        new("got", "Game of Thrones Universe", ["game of thrones", "westeros", "house of the dragon"], Titles: ["game of thrones", "house of the dragon", "a knight of the seven kingdoms"]),
        new("breaking-bad", "Breaking Bad Universe", ["breaking bad", "better call saul"], Titles: ["breaking bad", "better call saul", "el camino: a breaking bad movie"]),
        new("dune", "Dune", ["dune"], Collections: ["726871"], Titles: ["dune: prophecy"]),
        new("alien", "Alien", ["alien", "xenomorph"], Collections: ["8091"], Titles: ["alien: earth"]),
        new("john-wick", "John Wick", ["john wick"], Collections: ["404609"], Titles: ["the continental: from the world of john wick"]),
        new("monsterverse", "MonsterVerse", ["godzilla", "kong"], Collections: ["535313"], Titles: ["monarch: legacy of monsters"]),
        new("conjuring", "The Conjuring Universe", ["conjuring", "annabelle", "the nun"], Collections: ["313086", "402074", "968052"]),
        new("walking-dead", "The Walking Dead Universe", ["walking dead", "twd"], Prefixes: ["the walking dead", "fear the walking dead"], Titles: ["tales of the walking dead"]),
        new("star-trek", "Star Trek", ["star trek", "trek"], Prefixes: ["star trek"]),
        new("yellowstone", "Yellowstone Universe", ["yellowstone", "dutton"], Titles: ["yellowstone", "1883", "1923", "1944", "marshals", "the madison"], SeriesOnly: true),
        new("the-boys", "The Boys Universe", ["the boys", "vought"], Titles: ["the boys", "gen v", "the boys presents: diabolical", "vought rising"], SeriesOnly: true),
        new("avatar-legends", "Avatar: The Last Airbender Universe", ["aang", "korra", "last airbender"], Prefixes: ["avatar: the last airbender", "avatar: seven havens"], Titles: ["the legend of korra"]),
        new("arrowverse", "Arrowverse", ["arrowverse"], Titles: ["arrow", "the flash", "supergirl", "dc's legends of tomorrow", "batwoman", "black lightning", "superman & lois"], SeriesOnly: true),
        new("dexter", "Dexter", ["dexter"], Prefixes: ["dexter"], SeriesOnly: true),
        new("downton-abbey", "Downton Abbey", ["downton"], Prefixes: ["downton abbey"]),
        new("peaky-blinders", "Peaky Blinders", ["peaky", "shelby"], Prefixes: ["peaky blinders"]),
    ];

    // Extra search words for TMDb movie series people call something else.
    private static readonly Dictionary<string, string[]> TmdbAliases = new(StringComparer.Ordinal)
    {
        ["645"] = ["007", "bond"],
        ["9485"] = ["fast and furious", "fast & furious"],
        ["87359"] = ["mission impossible"],
        ["328"] = ["jurassic"],
        ["10194"] = ["pixar"],
    };

    private readonly ILibraryManager _libraryManager;
    private readonly ConcurrentDictionary<Guid, (DateTime At, string Config, List<CollectionInfo> List)> _cache = new();
    private readonly ConcurrentDictionary<Guid, Credits> _credits = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="CollectionService"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    public CollectionService(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Gets the collections a user can see: featured, franchises, directors, movie series.
    /// </summary>
    /// <param name="user">The user.</param>
    public IReadOnlyList<CollectionInfo> Get(User user)
    {
        var custom = Plugin.Instance!.Configuration.CustomCollections ?? string.Empty;
        if (_cache.TryGetValue(user.Id, out var cached) && cached.Config == custom && DateTime.UtcNow - cached.At < Lifetime)
        {
            return cached.List;
        }

        var list = Build(user, custom);
        _cache[user.Id] = (DateTime.UtcNow, custom, list);
        return list;
    }

    /// <summary>
    /// Parses the admin's own universes: one per line, "Name = Title; Title; Title".
    /// </summary>
    /// <param name="text">The setting.</param>
    internal static IEnumerable<Universe> ParseCustom(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var at = line.IndexOf('=', StringComparison.Ordinal);
            if (at <= 0)
            {
                continue;
            }

            var name = line[..at].Trim();
            var titles = line[(at + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (name.Length == 0 || titles.Length == 0)
            {
                continue;
            }

            yield return new Universe(Normalize(name).Replace(' ', '-'), name, [name], Titles: titles);
        }
    }

    /// <summary>
    /// Lowercase, accents and punctuation gone, "&amp;" as "and", single spaces: "Shōgun" and
    /// "shogun" match, as do "Superman &amp; Lois" and "superman and lois".
    /// </summary>
    /// <param name="text">The text.</param>
    internal static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var c in text.Replace("&", " and ", StringComparison.Ordinal).Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string CleanCollectionName(string name)
    {
        // TMDb calls them "James Bond Collection", "The Lord of the Rings Collection".
        const string Suffix = " Collection";
        return name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) ? name[..^Suffix.Length] : name;
    }

    private static List<Guid> InOrder(IEnumerable<BaseItem> items) => items
        .DistinctBy(item => item.Id)
        .OrderBy(item => item.PremiereDate ?? (item.ProductionYear is int year ? new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc) : DateTime.MaxValue))
        .ThenBy(item => item.SortName, StringComparer.OrdinalIgnoreCase)
        .Select(item => item.Id)
        .ToList();

    // Directors and writers of a movie, kept library-wide (they're the same for everyone) and
    // looked up once per movie every few hours, so building a user's list stays cheap.
    private Credits CreditsOf(BaseItem item)
    {
        if (_credits.TryGetValue(item.Id, out var known) && DateTime.UtcNow - known.At < PeopleLifetime)
        {
            return known;
        }

        var people = _libraryManager.GetPeople(item);
        var credits = new Credits(
            DateTime.UtcNow,
            people.Where(person => person.Type == PersonKind.Director).Select(person => person.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            people.Where(person => person.Type is PersonKind.Writer or PersonKind.Director).Select(person => person.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
        _credits[item.Id] = credits;
        return credits;
    }

    private CollectionInfo Info(string id, string name, string kind, IReadOnlyList<string> aliases, List<BaseItem> members) => new()
    {
        Id = id,
        Name = name,
        Kind = kind,
        Aliases = aliases,
        Movies = members.Count(item => item is Movie),
        Shows = members.Count(item => item is not Movie),
        ItemIds = InOrder(members)
    };

    private List<CollectionInfo> Build(User user, string custom)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Recursive = true,
            // Provider ids (the TMDb collection) only come back when asked for.
            DtoOptions = new DtoOptions(false) { Fields = [MediaBrowser.Model.Querying.ItemFields.ProviderIds, MediaBrowser.Model.Querying.ItemFields.Tags, MediaBrowser.Model.Querying.ItemFields.Studios, MediaBrowser.Model.Querying.ItemFields.OriginalTitle] }
        });

        var featured = new List<CollectionInfo>();
        var universes = new List<CollectionInfo>();
        var directors = new List<CollectionInfo>();
        var series = new List<CollectionInfo>();
        var absorbed = new HashSet<string>(StringComparer.Ordinal);
        var claimed = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        Func<BaseItem, ISet<string>> people = item => item is Movie ? CreditsOf(item).People : new HashSet<string>();

        // Featured: the admin's own first (and they win over a built-in one with the same name).
        foreach (var (theme, own) in ParseCustom(custom).Select(theme => (theme, true)).Concat(Featured.Select(theme => (theme, false))))
        {
            if (!names.Add(Normalize(theme.Name)))
            {
                continue;
            }

            var members = items.Where(item => theme.Matches(item, people)).ToList();
            if (members.Count >= (own ? 1 : MinFeaturedItems))
            {
                featured.Add(Info((own ? "custom-" : "featured-") + theme.Id, theme.Name, "featured", theme.Aliases, members));
            }
        }

        // Universes. Movies only: the same 3-movie minimum as a TMDb movie series. With shows in
        // it (a show and its spin-off), 2 is enough.
        foreach (var universe in Universes)
        {
            if (!names.Add(Normalize(universe.Name)))
            {
                continue;
            }

            var members = items.Where(item => universe.Matches(item, people)).ToList();
            if (members.Count < (members.All(item => item is Movie) ? MinTmdbItems : MinUniverseItems))
            {
                continue;
            }

            foreach (var id in universe.Collections ?? [])
            {
                absorbed.Add(id);
            }

            universes.Add(Info("universe-" + universe.Id, universe.Name, "universe", universe.Aliases, members));
            claimed.UnionWith(members.Select(item => item.Id));
        }

        // Decades.
        var decades = new List<CollectionInfo>();
        foreach (var decade in Decades)
        {
            var members = items.Where(item => decade.Matches(item, people)).ToList();
            if (members.Count >= MinFeaturedItems && names.Add(Normalize(decade.Name)))
            {
                decades.Add(Info("decade-" + decade.Id, decade.Name, "decade", decade.Aliases, members));
            }
        }

        // Directors with a body of work here.
        foreach (var group in items.OfType<Movie>()
            .SelectMany(movie => CreditsOf(movie).Directors.Select(name => (Name: name, Movie: (BaseItem)movie)))
            .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() >= MinDirectorMovies))
        {
            directors.Add(Info("director-" + Normalize(group.Key).Replace(' ', '-'), group.Key, "director", [], group.Select(entry => entry.Movie).ToList()));
        }

        // Collections made in Jellyfin (by hand, or by its "add movies to collections" option).
        var boxSetTmdb = new HashSet<string>(StringComparer.Ordinal);
        foreach (var boxSet in _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.BoxSet],
            Recursive = true,
            DtoOptions = new DtoOptions(false)
        }).OfType<BoxSet>())
        {
            var members = boxSet.GetLinkedChildren().Where(child => child is Movie or MediaBrowser.Controller.Entities.TV.Series && child.IsVisible(user)).ToList();
            var tmdb = boxSet.GetProviderId(MetadataProvider.Tmdb);
            if (members.Count == 0 || (tmdb is not null && absorbed.Contains(tmdb)) || !names.Add(Normalize(CleanCollectionName(boxSet.Name))))
            {
                continue;
            }

            if (tmdb is not null)
            {
                boxSetTmdb.Add(tmdb);
            }

            series.Add(Info("boxset-" + boxSet.Id.ToString("N", CultureInfo.InvariantCulture), CleanCollectionName(boxSet.Name), "collection", [], members));
        }

        // Movie series from TMDb.
        foreach (var group in items.OfType<Movie>()
            .Select(movie => (Movie: movie, Id: movie.GetProviderId(MetadataProvider.TmdbCollection)))
            .Where(entry => !string.IsNullOrEmpty(entry.Id) && !absorbed.Contains(entry.Id!) && !boxSetTmdb.Contains(entry.Id!))
            .GroupBy(entry => entry.Id!))
        {
            var movies = group.Select(entry => (BaseItem)entry.Movie).ToList();
            var name = group.Select(entry => entry.Movie.TmdbCollectionName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (name is null)
            {
                continue;
            }

            // Spin-off shows make it a franchise by themselves: a show named after the series or
            // one of its movies and then more ("Jurassic World: Camp Cretaceous", or without the
            // colon, next to the movie "Jurassic World"). Without a colon the name has to be at
            // least two words, so a one-word title like "Alien" doesn't catch "Alien Worlds".
            // Shows a built-in franchise already has are left alone, so "Avatar: The Last
            // Airbender" doesn't join James Cameron's Avatar.
            var stems = movies.Select(movie => movie.Name).Append(CleanCollectionName(name))
                .Select(stem => stem.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            bool IsSpinOff(BaseItem show) => stems.Any(stem =>
                show.Name.StartsWith(stem + ":", StringComparison.OrdinalIgnoreCase)
                || (Normalize(stem).Contains(' ', StringComparison.Ordinal) && Normalize(show.Name).StartsWith(Normalize(stem) + " ", StringComparison.Ordinal)));
            var spinOffs = items.Where(item => item is MediaBrowser.Controller.Entities.TV.Series && !claimed.Contains(item.Id) && IsSpinOff(item)).ToList();
            var franchise = spinOffs.Count > 0 && movies.Count >= MinUniverseItems;
            if ((!franchise && movies.Count < MinTmdbItems) || !names.Add(Normalize(CleanCollectionName(name))))
            {
                continue;
            }

            var aliases = TmdbAliases.GetValueOrDefault(group.Key) ?? [];
            if (franchise)
            {
                claimed.UnionWith(spinOffs.Select(item => item.Id));
                universes.Add(Info("tmdb-" + group.Key, CleanCollectionName(name), "universe", aliases, movies.Concat(spinOffs).ToList()));
            }
            else
            {
                series.Add(Info("tmdb-" + group.Key, CleanCollectionName(name), "collection", aliases, movies));
            }
        }

        static IEnumerable<CollectionInfo> Biggest(List<CollectionInfo> list) => list
            .OrderByDescending(collection => collection.ItemIds.Count)
            .ThenBy(collection => collection.Name, StringComparer.OrdinalIgnoreCase);

        // Featured keep their curated order; then franchises, movie series, decades, directors.
        return featured.Concat(Biggest(universes)).Concat(Biggest(series)).Concat(decades).Concat(Biggest(directors)).ToList();
    }

    private sealed record Credits(DateTime At, string[] Directors, ISet<string> People);

    /// <summary>
    /// A collection definition.
    /// </summary>
    /// <param name="Id">Stable id.</param>
    /// <param name="Name">Shown name.</param>
    /// <param name="Aliases">Extra search words.</param>
    /// <param name="Collections">TMDb collection ids (folded in, for universes).</param>
    /// <param name="Tags">TMDb keywords (Jellyfin tags).</param>
    /// <param name="Studios">Studios, exact.</param>
    /// <param name="Titles">Exact titles.</param>
    /// <param name="Prefixes">Title beginnings.</param>
    /// <param name="SeriesOnly">Titles only match shows (for names a movie could share).</param>
    /// <param name="StudioContains">Studios, by part of the name ("walt disney").</param>
    /// <param name="People">Directors or writers (movies).</param>
    /// <param name="TitleYears">"title|year" pairs; the year may be one off.</param>
    /// <param name="MaxMinutes">Movies at most this long.</param>
    /// <param name="MoviesOnly">Only movies.</param>
    /// <param name="MinYear">From this year (release year; a show's first year).</param>
    /// <param name="MaxYear">Up to this year.</param>
    /// <param name="ShowsOnly">Only shows.</param>
    internal sealed record Universe(
        string Id,
        string Name,
        string[] Aliases,
        string[]? Collections = null,
        string[]? Tags = null,
        string[]? Studios = null,
        string[]? Titles = null,
        string[]? Prefixes = null,
        bool SeriesOnly = false,
        string[]? StudioContains = null,
        string[]? People = null,
        string[]? TitleYears = null,
        int MaxMinutes = 0,
        bool MoviesOnly = false,
        int MinYear = 0,
        int MaxYear = 0,
        bool ShowsOnly = false)
    {
        private readonly HashSet<string> _titles = (Titles ?? []).Select(Normalize).ToHashSet(StringComparer.Ordinal);
        private readonly string[] _prefixes = (Prefixes ?? []).Select(Normalize).ToArray();
        private readonly HashSet<string> _tags = (Tags ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _studios = (Studios ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _titleYears = (TitleYears ?? [])
            .Select(entry => entry.Split('|'))
            .GroupBy(parts => Normalize(parts[0]))
            .ToDictionary(group => group.Key, group => int.Parse(group.First()[1], CultureInfo.InvariantCulture), StringComparer.Ordinal);

        public bool Matches(BaseItem item, Func<BaseItem, ISet<string>> people)
        {
            if ((MoviesOnly && item is not Movie) || (ShowsOnly && item is Movie))
            {
                return false;
            }

            if (MinYear > 0 || MaxYear > 0)
            {
                return item.ProductionYear is int made && made >= MinYear && (MaxYear == 0 || made <= MaxYear);
            }

            if (MaxMinutes > 0)
            {
                return item.RunTimeTicks is long ticks && ticks > 0 && TimeSpan.FromTicks(ticks).TotalMinutes <= MaxMinutes;
            }

            if (Collections is { Length: > 0 } && item is Movie && Collections.Contains(item.GetProviderId(MetadataProvider.TmdbCollection)))
            {
                return true;
            }

            if ((_tags.Count > 0 && item.Tags.Any(_tags.Contains))
                || (_studios.Count > 0 && item.Studios.Any(_studios.Contains))
                || (StudioContains is { Length: > 0 } && item.Studios.Any(studio => StudioContains.Any(part => studio.Contains(part, StringComparison.OrdinalIgnoreCase)))))
            {
                return true;
            }

            var name = Normalize(item.Name);
            if (_titleYears.Count > 0)
            {
                return _titleYears.TryGetValue(name, out var year) && item.ProductionYear is int made && Math.Abs(made - year) <= 1;
            }

            if (People is { Length: > 0 } && people(item).Overlaps(People))
            {
                return true;
            }

            if (SeriesOnly && item is Movie)
            {
                return false;
            }

            var original = Normalize(item.OriginalTitle);
            return _titles.Contains(name) || _titles.Contains(original)
                || _prefixes.Any(prefix => name == prefix || name.StartsWith(prefix + " ", StringComparison.Ordinal));
        }
    }
}

/// <summary>
/// A collection as the client sees it.
/// </summary>
public sealed class CollectionInfo
{
    /// <summary>Gets or sets the id ("universe-mcu", "tmdb-645", "boxset-...", "custom-...").</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets "featured" (themed, up front), "universe" (a franchise), "decade" (Classics, 80s...), "director", or "collection" (a movie series, or one made in Jellyfin).</summary>
    public string Kind { get; set; } = "collection";

    /// <summary>Gets or sets extra words search should find it by.</summary>
    public IReadOnlyList<string> Aliases { get; set; } = [];

    /// <summary>Gets or sets how many movies are in it.</summary>
    public int Movies { get; set; }

    /// <summary>Gets or sets how many shows are in it.</summary>
    public int Shows { get; set; }

    /// <summary>Gets or sets the titles, in release order.</summary>
    public List<Guid> ItemIds { get; set; } = [];
}
