using System;
using System.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Streamline.Helpers;

/// <summary>
/// Fallback injection method: patches jellyfin-web/index.html directly to add the UI's
/// script tag. Re-run on every server start so it survives Jellyfin updates that overwrite the
/// file. Requires the server process to have write access to the web files.
/// </summary>
public static class DirectScriptInjector
{
    /// <summary>
    /// Inserts the script tag into index.html if it isn't already there.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin's application paths.</param>
    /// <param name="basePath">The server's reverse-proxy base path, or "".</param>
    /// <param name="logger">Logger.</param>
    public static bool TryInject(IApplicationPaths applicationPaths, string basePath, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(applicationPaths.WebPath))
        {
            logger.LogWarning("Jellyfin web path is unavailable; direct UI script injection cannot be used.");
            return false;
        }

        var indexFile = Path.Combine(applicationPaths.WebPath, "index.html");

        try
        {
            if (!File.Exists(indexFile))
            {
                logger.LogWarning("Jellyfin web index was not found at {IndexFile}.", indexFile);
                return false;
            }

            var scriptElement = ScriptMarkup.Build(basePath, "injection=\"true\"");
            var indexContents = File.ReadAllText(indexFile);
            if (indexContents.Contains(scriptElement, StringComparison.Ordinal))
            {
                return true;
            }

            indexContents = ScriptMarkup.RemoveExisting(indexContents);
            var bodyClosing = indexContents.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (bodyClosing < 0)
            {
                logger.LogWarning("Could not find a closing body tag in {IndexFile}.", indexFile);
                return false;
            }

            indexContents = indexContents.Insert(bodyClosing, scriptElement);
            File.WriteAllText(indexFile, indexContents);
            logger.LogInformation("Injected the Streamline home screen script into {IndexFile}.", indexFile);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to inject the Streamline home screen script into {IndexFile}.", indexFile);
            return false;
        }
    }

    /// <summary>
    /// Removes the previously-injected script tag from index.html, if present. Best-effort,
    /// used on uninstall.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin's application paths.</param>
    /// <param name="logger">Logger.</param>
    public static void TryRemove(IApplicationPaths applicationPaths, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(applicationPaths.WebPath))
        {
            return;
        }

        var indexFile = Path.Combine(applicationPaths.WebPath, "index.html");

        try
        {
            if (!File.Exists(indexFile))
            {
                return;
            }

            var indexContents = File.ReadAllText(indexFile);
            var cleaned = ScriptMarkup.RemoveExisting(indexContents);
            if (!string.Equals(cleaned, indexContents, StringComparison.Ordinal))
            {
                File.WriteAllText(indexFile, cleaned);
                logger.LogInformation("Removed the Streamline home screen script from {IndexFile}.", indexFile);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove the Streamline home screen script from {IndexFile}.", indexFile);
        }
    }
}
