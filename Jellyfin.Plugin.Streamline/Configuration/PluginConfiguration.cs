using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Streamline.Configuration;

/// <summary>
/// Plugin configuration. Everything except <see cref="FrontendInjectionMethod"/> is read by the
/// web client through <see cref="Api.StreamlineController.GetClientConfig"/>, so changes apply
/// on the next page load without a server restart.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the new home screen is enabled at all.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets how the UI script is loaded into the web client. See <see cref="FrontendInjectionMethods"/>.
    /// </summary>
    public string FrontendInjectionMethod { get; set; } = FrontendInjectionMethods.Automatic;

    /// <summary>
    /// Gets or sets a value indicating whether the Movies and Shows library pages (and the
    /// "see all" pages) get the streamlined layout too, not just home.
    /// </summary>
    public bool LibraryPagesEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the rotating hero banner is shown.
    /// </summary>
    public bool HeroEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets how recent a movie's release (or a show's newest episode) must be to appear
    /// in the hero banner, in days.
    /// </summary>
    public int HeroMaxAgeDays { get; set; } = 30;

    /// <summary>
    /// Gets or sets how many titles the hero banner rotates through.
    /// </summary>
    public int HeroItemCount { get; set; } = 6;

    /// <summary>
    /// Gets or sets how long each hero title is shown, in seconds.
    /// </summary>
    public int HeroIntervalSeconds { get; set; } = 9;

    /// <summary>
    /// Gets or sets how long a show keeps its "New Series"/"New Episodes" tag after its latest
    /// episode aired, in days.
    /// </summary>
    public int NewSeriesBadgeDays { get; set; } = 30;

    /// <summary>
    /// Gets or sets how long a movie keeps its "New Release" tag after release, in days.
    /// </summary>
    public int NewMovieBadgeDays { get; set; } = 60;

    /// <summary>
    /// Gets or sets how many days back New on Jellyfin counts additions (the name is kept from
    /// when this was also a tag, so saved settings carry over).
    /// </summary>
    public int RecentlyAddedBadgeDays { get; set; } = 15;

    /// <summary>
    /// Gets or sets the accent color used for highlights (progress bars, focus rings, tags).
    /// </summary>
    public string AccentColor { get; set; } = "#aa5cc3";

    /// <summary>
    /// Gets or sets the genre rows shown, in order, comma separated. Empty hides them.
    /// </summary>
    public string GenreRows { get; set; } = "Action, Drama, Crime, Fantasy, Adventure, Romance, Comedy";

    /// <summary>
    /// Gets or sets the genres shown first and larger on the All Genres page, in order.
    /// </summary>
    public string HeadlineGenres { get; set; } = "Action, Drama, Adventure, Fantasy, Science Fiction, Romance, Comedy, Horror, Thriller";

    /// <summary>
    /// Gets or sets a value indicating whether item pages get an embedded trailer (YouTube,
    /// loaded only when someone presses play).
    /// </summary>
    public bool TrailersEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of titles per row.
    /// </summary>
    public int RowItemLimit { get; set; } = 20;

    /// <summary>
    /// Gets or sets a value indicating whether a button for previewing the phone layout is shown
    /// (for testing on a computer).
    /// </summary>
    public bool ShowPhonePreview { get; set; }

    /// <summary>
    /// Gets or sets the account the admin's "User view" button signs in as (a test account),
    /// so the admin sees exactly what that user sees: their lists, favorites and menus.
    /// </summary>
    public string UserViewUserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets extra collections for the Collections page, one per line:
    /// "Name = Title; Title; Title". For franchises the built-in list doesn't know.
    /// </summary>
    public string CustomCollections { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether collections made in Jellyfin itself (boxsets) also
    /// show on the Collections page. Off: the page is built only from Streamline's own rules,
    /// from everything in the library.
    /// </summary>
    public bool IncludeJellyfinCollections { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether newly added movies and shows that are still
    /// missing genres, keywords or a trailer get that metadata fetched again automatically.
    /// </summary>
    public bool AutoFillMetadata { get; set; } = true;

    /// <summary>
    /// Gets or sets how far back (days since added) the daily gap-filling task looks.
    /// </summary>
    public int MetadataLookbackDays { get; set; } = 60;

    /// <summary>
    /// Gets or sets a value indicating whether titles that look wrongly identified (name/year
    /// not matching their folder, or not identified) are re-identified when one match clearly
    /// fits, and missing posters fetched again.
    /// </summary>
    public bool AutoFixIdentity { get; set; } = true;

    /// <summary>
    /// Gets or sets an optional YouTube Data API key, used to search YouTube for a trailer when
    /// neither TMDb nor KinoCheck has one.
    /// </summary>
    public string YouTubeApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets Seerr's (Jellyseerr/Overseerr) address, e.g. http://192.168.1.10:5055.
    /// </summary>
    public string SeerrUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets Seerr's API key (Seerr: Settings &gt; General).
    /// </summary>
    public string SeerrApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether requested titles go on their requester's Watch
    /// List when they arrive.
    /// </summary>
    public bool SeerrAddToWatchList { get; set; } = true;

}
