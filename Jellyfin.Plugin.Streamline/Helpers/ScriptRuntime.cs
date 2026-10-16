namespace Jellyfin.Plugin.Streamline.Helpers;

/// <summary>
/// Holds values computed once at startup by
/// <see cref="ScheduledTasks.FrontendRegistrationStartupTask"/> that <see cref="ScriptMarkup"/>
/// and <see cref="Transformations"/> need but can't receive as parameters: the File
/// Transformation plugin invokes <see cref="Transformations.IndexTransformation"/> as a bare
/// static callback with no way to pass it fresh context, so these have to be readable from
/// somewhere static.
/// </summary>
internal static class ScriptRuntime
{
    /// <summary>
    /// Gets or sets the server's base path (e.g. "/jellyfin"), or "" if none is configured.
    /// </summary>
    public static string BasePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the plugin's assembly version, appended to the script URL as a cache-busting
    /// query parameter so a version bump always fetches fresh instead of a browser/WebView
    /// potentially reusing a cached copy of the previous version indefinitely.
    /// </summary>
    public static string ScriptVersion { get; set; } = "0";
}
