using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

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
/// <item>Anime by kind (Shōnen, Isekai, Anime Movies...) and TV: shows by channel or streaming
/// service (Netflix, HBO, BBC...) and kinds of TV (Miniseries, Reality, Kids' Shows...).</item>
/// <item>Directors with 4 or more movies here (not when they only made one series' films).</item>
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

    private static readonly HashSet<string> MedicalTags = new(StringComparer.OrdinalIgnoreCase) { "hospital", "doctor", "nurse", "surgeon", "paramedic", "medical", "physician" };

    // Medical titles their keywords don't show (one hospital keyword only).
    private static readonly HashSet<string> MedicalTitles = new(StringComparer.Ordinal) { "this is going to hurt", "ratched" };

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
        new("crime", "Crime", ["crime", "misdaad", "detective", "police", "politie", "crime thriller", "crime drama"],
            Test: item => HasGenre(item, "crime", "misdaad") || HasTag(item, "police procedural")),
        // A clear medical keyword, or two hospital ones: "hospital" or "doctor" alone is also
        // Scream, Doctor Strange and Totoro.
        new("medical", "Medical", ["medical", "hospital", "doctor", "ziekenhuis", "dokter", "artsen"],
            Test: item => HasTag(item, "medical drama", "medical resident", "medical student", "emergency room")
                || item.Tags.Count(tag => MedicalTags.Contains(tag)) >= 2
                || MedicalTitles.Contains(Normalize(item.Name))),
        new("musicals", "Musicals", ["musical", "musicals", "sing along"], Tags: ["musical", "musical film", "broadway", "jukebox musical", "song and dance"]),
        new("best-picture", "Best Picture Winners", ["oscar", "oscars", "academy award", "best picture"], TitleYears: BestPictureWinners, MoviesOnly: true),
        new("award-shows", "Award-winning Shows", ["emmy", "emmys", "award", "awards", "award winning"], Titles: EmmyWinningShows, SeriesOnly: true, ShowsOnly: true),
        new("ghibli", "Studio Ghibli", ["ghibli", "miyazaki"], StudioContains: ["studio ghibli"]),
        new("dreamworks", "DreamWorks Animation", ["dreamworks"], StudioContains: ["dreamworks animation"]),
        new("christmas", "Christmas", ["christmas", "xmas", "holiday"], Tags: ["christmas", "christmas eve", "santa claus", "christmas party", "christmas tree"]),
        new("halloween", "Halloween", ["halloween", "spooky"], Tags: ["halloween", "trick or treating"]),
        new("stephen-king", "Stephen King", ["stephen king", "king"], Tags: ["based on stephen king's work", "stephen king"], People: ["Stephen King"]),
        // Pokémon has its own franchise; its films would otherwise fill this one.
        new("video-games", "Video Game Adaptations", ["video game", "game"],
            Test: item => HasTag(item, "based on video game", "video game adaptation") && !Normalize(item.Name).StartsWith("pokemon", StringComparison.Ordinal)),
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
        // Brands TMDb splits into many little series.
        new("barbie", "Barbie", ["barbie"], Prefixes: ["barbie"]),
        new("lego", "LEGO", ["lego"], Prefixes: ["lego", "the lego"]),
        new("scooby-doo", "Scooby-Doo", ["scooby", "scooby doo"], Prefixes: ["scooby doo"]),
        new("pokemon", "Pokémon", ["pokemon", "pokémon"], Prefixes: ["pokemon"]),
        new("tom-and-jerry", "Tom and Jerry", ["tom and jerry", "tom & jerry"], Prefixes: ["tom and jerry"]),
        new("my-little-pony", "My Little Pony", ["my little pony", "mlp"], Prefixes: ["my little pony"]),
        new("tinker-bell", "Tinker Bell", ["tinker bell", "disney fairies"], Prefixes: ["tinker bell"]),
        new("winnie-the-pooh", "Winnie the Pooh", ["pooh", "winnie"], Prefixes: ["winnie the pooh"], Titles: ["the many adventures of winnie the pooh", "the tigger movie", "piglet's big movie", "pooh's heffalump movie", "christopher robin"]),
        new("dragon-ball", "Dragon Ball", ["dragon ball", "dbz", "goku"], Prefixes: ["dragon ball"]),
        new("warhammer", "Warhammer", ["warhammer", "warhammer 40k", "40k", "space marines"], Tags: ["warhammer", "warhammer 40k", "warhammer 40,000"], Studios: ["Warhammer TV"],
            Titles: ["angels of death", "angels of death (2021)", "pariah nexus", "hammer and bolter", "adepta sororitas: penitence", "kill lupercal", "interrogator"]),
        new("evil-dead", "Evil Dead", ["evil dead", "ash", "deadites"], Prefixes: ["evil dead", "the evil dead", "ash vs evil dead"], Titles: ["army of darkness"]),
    ];

    // TMDb keywords that make an animation anime. TVDB-style metadata has an "Anime" genre too.
    private static readonly HashSet<string> AnimeTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "anime", "shounen", "shonen", "seinen", "shoujo", "shojo", "josei", "isekai", "based on manga", "based on light novel", "donghua"
    };

    // Japanese animation studios, for anime movies that carry no anime keyword (Ghibli's). Only
    // as a title's main (first) studio: Pixar and Illumination films list them for outsourced work.
    private static readonly string[] AnimeStudios =
    [
        "studio ghibli", "toei animation", "madhouse", "bones", "mappa", "ufotable", "kyoto animation", "production i.g", "wit studio", "sunrise",
        "a-1 pictures", "cloverworks", "studio trigger", "shaft", "david production", "j.c.staff", "studio deen", "olm", "pierrot", "toho animation",
        "studio ponoc", "comix wave", "studio chizu", "science saru", "tms entertainment", "lerche", "silver link", "white fox", "doga kobo"
    ];

    /// <summary>
    /// Whether a title is anime: the "Anime" genre, or an animation with an anime keyword or from
    /// a Japanese animation studio as its main studio.
    /// </summary>
    /// <param name="item">The title.</param>
    internal static bool IsAnime(BaseItem item)
    {
        if (HasGenre(item, "anime"))
        {
            return true;
        }

        return HasGenre(item, "animation", "animatie")
            && (item.Tags.Any(AnimeTags.Contains) || (item.Studios.FirstOrDefault() is string studio && AnimeStudios.Contains(studio.Trim(), StringComparer.OrdinalIgnoreCase)));
    }

    private static bool HasGenre(BaseItem item, params string[] genres)
        => item.Genres.Any(genre => genres.Any(wanted => genre.Contains(wanted, StringComparison.OrdinalIgnoreCase)));

    private static bool HasTag(BaseItem item, params string[] tags)
        => item.Tags.Any(tag => tags.Contains(tag, StringComparer.OrdinalIgnoreCase));

    private static bool OnChannel(BaseItem item, params string[] channels)
        => item.Studios.Any(studio => channels.Contains(studio.Trim(), StringComparer.OrdinalIgnoreCase));

    private static Universe AnimeTheme(string id, string name, string[] aliases, Func<BaseItem, bool> test, bool moviesOnly = false)
        => new("anime-" + id, name, aliases, MoviesOnly: moviesOnly, Test: item => IsAnime(item) && test(item));

    // Anime, by kind: Jellyfin has one flat Animation genre, so these are what anime fans browse by.
    private static readonly Universe[] AnimeThemes =
    [
        AnimeTheme("all", "All Anime", ["anime", "manga", "japanese animation"], _ => true),
        AnimeTheme("movies", "Anime Movies", ["anime movie", "anime movies", "anime film"], _ => true, moviesOnly: true),
        AnimeTheme("shonen", "Shōnen", ["shonen", "shounen"], item => HasTag(item, "shounen", "shonen")),
        AnimeTheme("seinen", "Seinen", ["seinen"], item => HasTag(item, "seinen", "josei")),
        AnimeTheme("isekai", "Isekai", ["isekai", "another world", "reincarnation"], item => HasTag(item, "isekai", "reincarnation", "transported to another world", "parallel world", "another world", "summoned to another world")),
        AnimeTheme("action", "Action Anime", ["action anime"], item => HasGenre(item, "action", "actie", "martial arts")),
        AnimeTheme("fantasy", "Fantasy Anime", ["fantasy anime"], item => HasGenre(item, "fantasy", "fantasie") || HasTag(item, "magic", "dark fantasy", "high fantasy")),
        AnimeTheme("romance", "Romance Anime", ["romance anime", "shojo", "shoujo"], item => HasGenre(item, "romance", "romantiek") || HasTag(item, "romance", "shoujo", "shojo")),
        AnimeTheme("comedy", "Comedy Anime", ["comedy anime"], item => HasGenre(item, "comedy", "komedie")),
        AnimeTheme("dark", "Dark & Horror Anime", ["horror anime", "dark anime"], item => HasGenre(item, "horror") || HasTag(item, "gore", "dark fantasy", "psychological", "tragedy")),
        AnimeTheme("scifi", "Sci-Fi & Mecha", ["mecha", "sci-fi anime", "robots"], item => HasGenre(item, "science fiction", "sci-fi") || HasTag(item, "mecha", "robot", "giant robot", "space")),
        AnimeTheme("slice", "Slice of Life", ["slice of life", "iyashikei"], item => HasTag(item, "slice of life", "school life", "iyashikei", "cute girls doing cute things")),
        AnimeTheme("sports", "Sports Anime", ["sports anime"], item => HasGenre(item, "sport") || HasTag(item, "sports", "volleyball", "basketball", "football (soccer)", "swimming", "baseball", "boxing")),
        AnimeTheme("light-novels", "Based on Light Novels", ["light novel", "light novels"], item => HasTag(item, "based on light novel")),
        AnimeTheme("donghua", "Donghua", ["donghua", "chinese animation"], item => HasTag(item, "donghua")),
    ];

    private static Universe Channel(string id, string name, string[] aliases, params string[] channels)
        => new("tv-" + id, name, aliases, ShowsOnly: true, Test: item => OnChannel(item, channels));

    private static Universe TvTheme(string id, string name, string[] aliases, Func<BaseItem, bool> test)
        => new("tv-" + id, name, aliases, ShowsOnly: true, Test: test);

    // Shows by where they come from (the network or streaming service, which Jellyfin keeps as
    // the show's studio), and kinds of TV that aren't a genre.
    private static readonly Universe[] TvThemes =
    [
        Channel("netflix", "Netflix", ["netflix"], "Netflix"),
        Channel("hbo", "HBO", ["hbo", "max", "hbo max"], "HBO", "HBO Max", "Max"),
        Channel("disney-plus", "Disney+", ["disney+", "disney plus"], "Disney+"),
        Channel("prime", "Prime Video", ["prime", "prime video", "amazon"], "Prime Video", "Amazon", "Amazon Prime Video", "Freevee"),
        Channel("apple", "Apple TV+", ["apple", "apple tv"], "Apple TV", "Apple TV+"),
        Channel("paramount", "Paramount+", ["paramount", "paramount+"], "Paramount+", "Paramount+ with Showtime", "CBS All Access"),
        Channel("hulu", "Hulu", ["hulu"], "Hulu"),
        Channel("showtime", "Showtime", ["showtime"], "Showtime"),
        Channel("fx", "FX", ["fx"], "FX", "FXX", "FX on Hulu"),
        Channel("amc", "AMC", ["amc"], "AMC", "AMC+"),
        Channel("starz", "Starz", ["starz"], "STARZ", "Starz"),
        TvTheme("bbc", "BBC", ["bbc"], item => item.Studios.Any(studio => studio.StartsWith("BBC", StringComparison.OrdinalIgnoreCase))),
        Channel("itv", "ITV", ["itv"], "ITV", "ITV1", "ITV2", "ITVX", "ITV Encore"),
        Channel("channel-4", "Channel 4", ["channel 4", "e4"], "Channel 4", "E4", "All 4"),
        Channel("k-drama", "K-Dramas", ["k-drama", "kdrama", "korean", "korea"], "tvN", "JTBC", "KBS1", "KBS2", "MBC", "ENA", "OCN", "Coupang Play", "TVING", "Channel A", "MBN"),
        TvTheme("cartoons", "Cartoons", ["cartoons", "cartoon network", "adult swim", "adult animation", "tekenfilms"],
            item => (OnChannel(item, "Cartoon Network", "Adult Swim", "Boomerang") || HasTag(item, "adult animation")) && !IsAnime(item)),
        Channel("nickelodeon", "Nickelodeon", ["nickelodeon", "nick"], "Nickelodeon", "Nick Jr.", "Nicktoons", "Nick at Nite"),
        Channel("disney-channel", "Disney Channel", ["disney channel", "disney junior", "disney xd"], "Disney Channel", "Disney Junior", "Disney XD", "Playhouse Disney"),
        Channel("nat-geo", "National Geographic", ["national geographic", "nat geo"], "National Geographic", "Nat Geo Wild", "Nat Geo"),
        TvTheme("miniseries", "Miniseries", ["miniseries", "mini-series", "limited series"], item => HasGenre(item, "mini-series", "miniseries") || HasTag(item, "miniseries", "limited series")),
        TvTheme("reality", "Reality & Cooking", ["reality", "cooking", "game show", "competition"], item => HasGenre(item, "reality", "game show", "food", "travel") || HasTag(item, "reality", "reality tv", "cooking competition", "competition")),
        TvTheme("kids", "Kids' Shows", ["kids", "children", "kinderen", "kinder"], item => HasGenre(item, "children", "kids") && !IsAnime(item)),
        TvTheme("true-crime", "True Crime", ["true crime"], item => HasGenre(item, "documentary", "documentaire") && HasTag(item, "true crime", "serial killer", "murder", "crime investigation")),
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

    // During the day, new titles are worked in at most this often (cheap: only their own
    // credits are looked up). The full rebuild, with everyone's credits, is the nightly task.
    private static readonly TimeSpan DaytimeRebuildGap = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions FileJson = new() { WriteIndented = false };

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<CollectionService> _logger;
    private readonly ConcurrentDictionary<Guid, (DateTime At, HashSet<Guid> Ids)> _visible = new();
    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private ConcurrentDictionary<Guid, Credits> _credits = new();
    private Snapshot? _snapshot;
    private volatile bool _dirty;

    /// <summary>
    /// Initializes a new instance of the <see cref="CollectionService"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="logger">Logger.</param>
    public CollectionService(ILibraryManager libraryManager, ILogger<CollectionService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _libraryManager.ItemAdded += OnLibraryChanged;
        _libraryManager.ItemRemoved += OnLibraryChanged;
        Load();

        // First start after installing: prepare everything already in the library straight
        // away (in the background, once the server is up), not on someone's first visit.
        if (_snapshot is null)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
                if (_snapshot is null)
                {
                    await RebuildAsync(false, CancellationToken.None).ConfigureAwait(false);
                }
            });
        }
    }

    private static string DataFolder => Plugin.Instance!.DataFolderPath;

    /// <summary>
    /// Gets the collections a user can see: featured, franchises, movie series, decades,
    /// directors. Served from the prepared (nightly) snapshot, cut down to what this user can
    /// see; nothing is worked out per request.
    /// </summary>
    /// <param name="user">The user.</param>
    public IReadOnlyList<CollectionInfo> Get(User user)
    {
        var snapshot = Current();
        var visible = VisibleIds(user);
        var result = new List<CollectionInfo>();
        foreach (var entry in snapshot.Collections)
        {
            var ids = entry.ItemIds.Where(visible.Contains).ToList();
            if (ids.Count == 0 || ids.Count < entry.Min)
            {
                continue;
            }

            var movies = ids.Count(snapshot.MovieIds.Contains);
            result.Add(new CollectionInfo
            {
                Id = entry.Id,
                Name = entry.Name,
                Kind = entry.Kind,
                Aliases = entry.Aliases,
                Movies = movies,
                Shows = ids.Count - movies,
                ItemIds = ids
            });
        }

        return result;
    }

    /// <summary>
    /// Rebuilds the snapshot. The nightly task passes <paramref name="refreshCredits"/> to look
    /// every movie's directors and writers up again; otherwise only new movies are looked up.
    /// </summary>
    /// <param name="refreshCredits">Whether to refresh everyone's credits.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task RebuildAsync(bool refreshCredits, CancellationToken cancellationToken)
    {
        await _buildLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (refreshCredits)
            {
                _credits = new ConcurrentDictionary<Guid, Credits>();
            }

            _dirty = false;
            var custom = ConfigKey();
            var started = DateTime.UtcNow;
            var snapshot = Build(custom);
            _snapshot = snapshot;
            _visible.Clear();
            Save(snapshot);
            _logger.LogInformation("Prepared {Count} collections in {Seconds:0.0}s.", snapshot.Collections.Count, (DateTime.UtcNow - started).TotalSeconds);
        }
        finally
        {
            _buildLock.Release();
        }
    }

    // The snapshot to serve. Built right away only when there is none yet (first start) or the
    // admin changed their own collections; new titles are worked in in the background.
    private Snapshot Current()
    {
        var custom = ConfigKey();
        var snapshot = _snapshot;
        if (snapshot is null || snapshot.Config != custom)
        {
            RebuildAsync(false, CancellationToken.None).GetAwaiter().GetResult();
            return _snapshot!;
        }

        if (_dirty && DateTime.UtcNow - snapshot.BuiltAt > DaytimeRebuildGap && _buildLock.CurrentCount > 0)
        {
            _ = Task.Run(() => RebuildAsync(false, CancellationToken.None));
        }

        return snapshot;
    }

    // The settings the collections depend on; a change rebuilds them straight away.
    private static string ConfigKey()
    {
        var config = Plugin.Instance!.Configuration;
        return (config.IncludeJellyfinCollections ? "boxsets\n" : string.Empty) + (config.CustomCollections ?? string.Empty);
    }

    // What the user may see, as ids: one cheap id-only query per user every few minutes.
    private HashSet<Guid> VisibleIds(User user)
    {
        if (_visible.TryGetValue(user.Id, out var known) && DateTime.UtcNow - known.At < Lifetime)
        {
            return known.Ids;
        }

        var ids = _libraryManager.GetItemIds(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Recursive = true
        }).ToHashSet();
        _visible[user.Id] = (DateTime.UtcNow, ids);
        return ids;
    }

    private void OnLibraryChanged(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Movie or MediaBrowser.Controller.Entities.TV.Series or BoxSet)
        {
            _dirty = true;
        }
    }

    private void Load()
    {
        try
        {
            var path = Path.Combine(DataFolder, "collections.json");
            if (File.Exists(path))
            {
                _snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path), FileJson);
            }

            var creditsPath = Path.Combine(DataFolder, "credits.json");
            if (File.Exists(creditsPath))
            {
                var saved = JsonSerializer.Deserialize<Dictionary<Guid, Credits>>(File.ReadAllText(creditsPath), FileJson);
                _credits = new ConcurrentDictionary<Guid, Credits>(saved ?? []);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't read the saved collections; they'll be prepared again.");
        }
    }

    private void Save(Snapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(Path.Combine(DataFolder, "collections.json"), JsonSerializer.Serialize(snapshot, FileJson));
            File.WriteAllText(Path.Combine(DataFolder, "credits.json"), JsonSerializer.Serialize(_credits, FileJson));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't save the prepared collections.");
        }
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

    // TMDb series names that read oddly once " Collection" is gone.
    private static readonly Dictionary<string, string> BetterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["The Bourne"] = "Bourne",
        ["The Ultimate"] = "The Ultimate Gift",
        ["Dreamworks' Pentateuch"] = "The Prince of Egypt",
        ["Son of Batman"] = "DC Animated Movie Universe",
    };

    private static string CleanCollectionName(string name)
    {
        // TMDb calls them "James Bond Collection", "The Lord of the Rings Collection", and some
        // "Tarzan (Animation) Collection" or "Hercule Poirot (Kenneth Branagh) Collection".
        const string Suffix = " Collection";
        name = name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) ? name[..^Suffix.Length] : name;
        var bracket = name.LastIndexOf(" (", StringComparison.Ordinal);
        if (bracket > 0 && name.EndsWith(')'))
        {
            name = name[..bracket];
        }

        return BetterNames.GetValueOrDefault(name.Trim()) ?? name.Trim();
    }

    // Words a collection name can't be grouped by ("The Matrix" and "The Godfather").
    private static readonly HashSet<string> NotABrand = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "de", "het", "een", "la", "le", "les", "el", "der", "die", "das", "my", "star", "john", "big", "little", "new", "dark", "night", "black", "white", "red", "blue", "super", "mr", "mrs", "dr", "lord",
        "top", "how", "what", "who", "why", "when", "where", "one", "two", "all", "love", "last", "first", "great", "best", "home", "old", "young", "man", "men", "girl", "girls", "boy", "boys", "day", "days", "war", "world", "king", "queen", "death", "dead", "life", "time", "space", "secret", "lost", "true", "bad", "good", "happy", "high", "american", "christmas", "ice", "fire", "blood"
    };

    // Brand groups need this many TMDb series: two that start alike ("Top Gun", "Top Gear") are
    // more likely chance than a brand.
    private const int MinBrandSeries = 3;

    // The brand word a TMDb series could be grouped by: its name's first word, when every one
    // of its movies starts with it too.
    private static string? SharedWord(string name, List<BaseItem> movies)
    {
        var word = Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (word is null || word.Length < 3 || NotABrand.Contains(word))
        {
            return null;
        }

        return movies.All(movie => Normalize(movie.Name).Split(' ').FirstOrDefault() == word) ? word : null;
    }

    // The words names have in common at the start ("Barbie Fairytopia", "Barbie Mariposa": "Barbie").
    private static string CommonName(List<string> names)
    {
        var split = names.Select(name => name.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
        var count = 0;
        while (split.All(words => words.Length > count) && split.All(words => string.Equals(Normalize(words[count]), Normalize(split[0][count]), StringComparison.Ordinal)))
        {
            count++;
        }

        return string.Join(' ', split[0].Take(Math.Max(1, count))).TrimEnd(':', '-', ',');
    }

    // Release order, each title once: a second copy of a film (another edition or quality) has
    // its own id but the same TMDb id, or the same name and year.
    private static List<Guid> InOrder(IEnumerable<BaseItem> items) => items
        .DistinctBy(item => item.Id)
        .DistinctBy(item => (item is Movie ? "m" : "s") + (item.GetProviderId(MetadataProvider.Tmdb) ?? Normalize(item.Name) + "|" + item.ProductionYear))
        .OrderBy(item => item.PremiereDate ?? (item.ProductionYear is int year ? new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc) : DateTime.MaxValue))
        .ThenBy(item => item.SortName, StringComparer.OrdinalIgnoreCase)
        .Select(item => item.Id)
        .ToList();

    // Directors and writers of a movie, kept library-wide (they're the same for everyone) and
    // saved, so a movie's credits are only looked up once (and again at night).
    private Credits CreditsOf(BaseItem item)
    {
        if (_credits.TryGetValue(item.Id, out var known))
        {
            return known;
        }

        var credits = item is MediaBrowser.Controller.Entities.TV.Series series ? ShowCredits(series) : MovieCredits(item);

        // Nothing yet (a new title whose people haven't come in): don't remember that, so the
        // next rebuild looks again instead of leaving it out of its director until the night.
        if (credits.Directors.Length > 0 || credits.People.Length > 0)
        {
            _credits[item.Id] = credits;
        }

        return credits;
    }

    private Credits MovieCredits(BaseItem movie)
    {
        var people = _libraryManager.GetPeople(movie);
        return new Credits(
            people.Where(person => person.Type == PersonKind.Director).Select(person => person.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            people.Where(person => person.Type is PersonKind.Writer or PersonKind.Director).Select(person => person.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    // A show's directors are on its episodes: someone counts for the show when they directed its
    // first episode or at least a third of the episodes here (Ben Stiller and Severance), plus
    // its creators when Jellyfin has them.
    private Credits ShowCredits(MediaBrowser.Controller.Entities.TV.Series series)
    {
        var creators = _libraryManager.GetPeople(series)
            .Where(person => person.Type is PersonKind.Creator or PersonKind.Director)
            .Select(person => person.Name.Trim());
        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
        {
            AncestorIds = [series.Id],
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            DtoOptions = new DtoOptions(false)
        }).OfType<MediaBrowser.Controller.Entities.TV.Episode>().ToList();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var pilot = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var episode in episodes)
        {
            foreach (var name in _libraryManager.GetPeople(episode).Where(person => person.Type == PersonKind.Director).Select(person => person.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                counts[name] = counts.GetValueOrDefault(name) + 1;
                if (episode.ParentIndexNumber == 1 && episode.IndexNumber == 1)
                {
                    pilot.Add(name);
                }
            }
        }

        var directors = counts
            .Where(entry => pilot.Contains(entry.Key) || (entry.Value >= 2 && entry.Value * 3 >= episodes.Count))
            .Select(entry => entry.Key)
            .Concat(creators)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new Credits(directors, directors);
    }

    // A collection in the snapshot: everything in it library-wide, and how many of those a user
    // must be able to see for it to show for them.
    private static SnapshotEntry Info(string id, string name, string kind, IReadOnlyList<string> aliases, List<BaseItem> members, int min) => new()
    {
        Id = id,
        Name = name,
        Kind = kind,
        Aliases = aliases,
        Min = min,
        ItemIds = InOrder(members)
    };

    private Snapshot Build(string configKey)
    {
        var custom = Plugin.Instance!.Configuration.CustomCollections ?? string.Empty;
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Recursive = true,
            // Provider ids (the TMDb collection) only come back when asked for.
            DtoOptions = new DtoOptions(false) { Fields = [MediaBrowser.Model.Querying.ItemFields.ProviderIds, MediaBrowser.Model.Querying.ItemFields.Tags, MediaBrowser.Model.Querying.ItemFields.Studios, MediaBrowser.Model.Querying.ItemFields.OriginalTitle] }
        });

        var featured = new List<SnapshotEntry>();
        var universes = new List<SnapshotEntry>();
        var directors = new List<SnapshotEntry>();
        var series = new List<SnapshotEntry>();
        var absorbed = new HashSet<string>(StringComparer.Ordinal);
        var claimed = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        Func<BaseItem, ISet<string>> people = item => item is Movie ? CreditsOf(item).People.ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>();

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
                featured.Add(Info((own ? "custom-" : "featured-") + theme.Id, theme.Name, "featured", theme.Aliases, members, own ? 1 : MinFeaturedItems));
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
            var min = members.All(item => item is Movie) ? MinTmdbItems : MinUniverseItems;
            if (members.Count < min)
            {
                continue;
            }

            foreach (var id in universe.Collections ?? [])
            {
                absorbed.Add(id);
            }

            universes.Add(Info("universe-" + universe.Id, universe.Name, "universe", universe.Aliases, members, min));
            claimed.UnionWith(members.Select(item => item.Id));
        }

        // Decades.
        var decades = new List<SnapshotEntry>();
        foreach (var decade in Decades)
        {
            var members = items.Where(item => decade.Matches(item, people)).ToList();
            if (members.Count >= MinFeaturedItems && names.Add(Normalize(decade.Name)))
            {
                decades.Add(Info("decade-" + decade.Id, decade.Name, "decade", decade.Aliases, members, MinFeaturedItems));
            }
        }

        // Anime by kind, and shows by channel or kind of TV. Same 3-title minimum as Featured.
        var anime = new List<SnapshotEntry>();
        var tv = new List<SnapshotEntry>();
        foreach (var (themes, kind, list) in new[] { (AnimeThemes, "anime", anime), (TvThemes, "tv", tv) })
        {
            foreach (var theme in themes)
            {
                var members = items.Where(item => theme.Matches(item, people)).ToList();
                if (members.Count >= MinFeaturedItems && names.Add(Normalize(theme.Name)))
                {
                    list.Add(Info(theme.Id, theme.Name, kind, theme.Aliases, members, MinFeaturedItems));
                }
            }
        }

        // Directors with a body of work here: their movies, and shows they shaped.
        foreach (var group in items.Where(item => item is Movie or MediaBrowser.Controller.Entities.TV.Series)
            .SelectMany(title => CreditsOf(title).Directors.Select(name => (Name: name, Movie: title)))
            .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() >= MinDirectorMovies))
        {
            directors.Add(Info("director-" + Normalize(group.Key).Replace(' ', '-'), group.Key, "director", [], group.Select(entry => entry.Movie).ToList(), MinDirectorMovies));
        }

        // Directors who only made one series' movies (the Pokémon films, the Barbie films) add
        // nothing that series doesn't already show: left out once the series is known, below.

        // Collections made in Jellyfin (by hand, or by its "add movies to collections" option):
        // only when the admin wants them; by default the page is Streamline's own.
        var boxSetTmdb = new HashSet<string>(StringComparer.Ordinal);
        var boxSets = Plugin.Instance!.Configuration.IncludeJellyfinCollections
            ? _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.BoxSet],
                Recursive = true,
                DtoOptions = new DtoOptions(false)
            }).OfType<BoxSet>()
            : [];
        foreach (var boxSet in boxSets)
        {
            var members = boxSet.GetLinkedChildren().Where(child => child is Movie or MediaBrowser.Controller.Entities.TV.Series).ToList();
            var tmdb = boxSet.GetProviderId(MetadataProvider.Tmdb);
            if (members.Count == 0 || (tmdb is not null && absorbed.Contains(tmdb)) || !names.Add(Normalize(CleanCollectionName(boxSet.Name))))
            {
                continue;
            }

            if (tmdb is not null)
            {
                boxSetTmdb.Add(tmdb);
            }

            series.Add(Info("boxset-" + boxSet.Id.ToString("N", CultureInfo.InvariantCulture), CleanCollectionName(boxSet.Name), "collection", [], members, 1));
        }

        // Movie series from TMDb.
        var pending = new List<(string Key, string Name, string[] Aliases, List<BaseItem> Movies)>();
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
            var aliases = TmdbAliases.GetValueOrDefault(group.Key) ?? [];
            if (franchise)
            {
                if (names.Add(Normalize(CleanCollectionName(name))))
                {
                    claimed.UnionWith(spinOffs.Select(item => item.Id));
                    universes.Add(Info("tmdb-" + group.Key, CleanCollectionName(name), "universe", aliases, movies.Concat(spinOffs).ToList(), MinUniverseItems));
                }

                continue;
            }

            // Already (mostly) a franchise here, like the Barbie films: not again as a series.
            if (universes.Any(universe => movies.Count(movie => universe.ItemIds.Contains(movie.Id)) * 5 >= movies.Count * 4))
            {
                continue;
            }

            pending.Add((group.Key, CleanCollectionName(name), aliases, movies));
        }

        // TMDb splits some brands into many small series ("Barbie Fairytopia", "Barbie Mariposa",
        // and so on). Three or more series whose names start with the same word, and whose movies all start with it too,
        // are one collection, named by what their names have in common.
        var brandCounts = pending.Select(entry => SharedWord(entry.Name, entry.Movies)).OfType<string>().GroupBy(word => word).ToDictionary(group => group.Key, group => group.Count());
        string BrandKey((string Key, string Name, string[] Aliases, List<BaseItem> Movies) entry)
            => SharedWord(entry.Name, entry.Movies) is string word && brandCounts[word] >= MinBrandSeries ? word : "\u0000" + entry.Key;
        foreach (var cluster in pending
            .GroupBy(BrandKey)
            .Select(group => group.ToList()))
        {
            var movies = cluster.SelectMany(entry => entry.Movies).DistinctBy(movie => movie.Id).ToList();
            var name = cluster.Count == 1 ? cluster[0].Name : CommonName(cluster.Select(entry => entry.Name).ToList());
            if (movies.Count < MinTmdbItems || !names.Add(Normalize(name)))
            {
                continue;
            }

            var id = "tmdb-" + string.Join("-", cluster.Select(entry => entry.Key).OrderBy(key => key, StringComparer.Ordinal));
            series.Add(Info(id, name, "collection", cluster.SelectMany(entry => entry.Aliases).Distinct().ToList(), movies, MinTmdbItems));
        }

        directors.RemoveAll(director => universes.Concat(series).Any(other => director.ItemIds.Count(other.ItemIds.Contains) * 4 >= director.ItemIds.Count * 3));

        static IEnumerable<SnapshotEntry> Biggest(List<SnapshotEntry> list) => list
            .OrderByDescending(collection => collection.ItemIds.Count)
            .ThenBy(collection => collection.Name, StringComparer.OrdinalIgnoreCase);

        // Featured, anime and TV keep their curated order; then franchises, movie series, decades, directors.
        return new Snapshot
        {
            BuiltAt = DateTime.UtcNow,
            Config = configKey,
            MovieIds = items.OfType<Movie>().Select(item => item.Id).ToHashSet(),
            Collections = featured.Concat(Biggest(universes)).Concat(anime).Concat(tv).Concat(Biggest(series)).Concat(decades).Concat(directors.OrderBy(director => director.Name, StringComparer.OrdinalIgnoreCase)).ToList()
        };
    }

    /// <summary>
    /// A movie's directors, and its directors and writers.
    /// </summary>
    /// <param name="Directors">Directors.</param>
    /// <param name="People">Directors and writers.</param>
    internal sealed record Credits(string[] Directors, string[] People);

    /// <summary>
    /// Every collection library-wide, as prepared at night (saved to disk).
    /// </summary>
    internal sealed class Snapshot
    {
        public DateTime BuiltAt { get; set; }

        public string Config { get; set; } = string.Empty;

        public HashSet<Guid> MovieIds { get; set; } = [];

        public List<SnapshotEntry> Collections { get; set; } = [];
    }

    /// <summary>
    /// One collection in the snapshot.
    /// </summary>
    internal sealed class SnapshotEntry
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Kind { get; set; } = string.Empty;

        public IReadOnlyList<string> Aliases { get; set; } = [];

        public int Min { get; set; }

        public List<Guid> ItemIds { get; set; } = [];
    }

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
    /// <param name="MoviesOnly">Only movies.</param>
    /// <param name="MinYear">From this year (release year; a show's first year).</param>
    /// <param name="MaxYear">Up to this year.</param>
    /// <param name="ShowsOnly">Only shows.</param>
    /// <param name="Test">Decides by itself (anime themes, TV channels), instead of the lists.</param>
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
        bool MoviesOnly = false,
        int MinYear = 0,
        int MaxYear = 0,
        bool ShowsOnly = false,
        Func<BaseItem, bool>? Test = null)
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

            if (Test is not null)
            {
                return Test(item);
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

    /// <summary>Gets or sets how many titles it has, when <see cref="ItemIds"/> is only the start.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int Count { get; set; }

    /// <summary>Gets or sets a value indicating whether <see cref="ItemIds"/> is only the start (a big collection).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Partial { get; set; }

    /// <summary>Gets or sets the titles, in release order.</summary>
    public List<Guid> ItemIds { get; set; } = [];
}
