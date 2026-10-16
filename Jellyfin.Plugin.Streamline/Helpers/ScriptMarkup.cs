using System;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Streamline.Helpers;

/// <summary>
/// Builds and recognizes the UI's injected <c>&lt;script&gt;</c> tag, so it can be found,
/// replaced, or removed idempotently across direct-injection runs and uninstall.
/// </summary>
public static partial class ScriptMarkup
{
    private const string PluginMarker = "Streamline";

    /// <summary>
    /// Builds the script's URL, including a cache-busting version query parameter so a plugin
    /// update always fetches fresh instead of a browser/WebView potentially reusing a cached
    /// copy of a previous version's script indefinitely.
    /// </summary>
    /// <param name="basePath">The server's reverse-proxy base path (e.g. "/jellyfin"), or "".</param>
    public static string ScriptUrl(string basePath)
    {
        return $"{basePath}/Streamline/script?v={ScriptRuntime.ScriptVersion}";
    }

    /// <summary>
    /// Builds the script tag markup.
    /// </summary>
    /// <param name="basePath">The server's reverse-proxy base path (e.g. "/jellyfin"), or "".</param>
    /// <param name="extraAttribute">An extra marker attribute distinguishing how it was inserted.</param>
    public static string Build(string basePath, string extraAttribute)
    {
        // Two tags: a tiny one that runs right away, before Jellyfin's own (deferred) code, for
        // the admin's User view; then the real script, deferred like Jellyfin's.
        return $"<script {extraAttribute} plugin=\"{PluginMarker}\" src=\"{basePath}/Streamline/early?v={ScriptRuntime.ScriptVersion}\"></script>"
            + $"<script {extraAttribute} plugin=\"{PluginMarker}\" defer=\"defer\" src=\"{ScriptUrl(basePath)}\"></script>";
    }

    /// <summary>
    /// Removes any previously-inserted copy of the script tag from the given HTML.
    /// </summary>
    /// <param name="contents">The HTML to clean.</param>
    public static string RemoveExisting(string contents)
    {
        return ExistingScriptRegex().Replace(contents, string.Empty);
    }

    [GeneratedRegex("<script\\b(?=[^>]*\\bplugin=([\"'])Streamline\\1)[^>]*>\\s*</script>", RegexOptions.IgnoreCase)]
    private static partial Regex ExistingScriptRegex();
}
