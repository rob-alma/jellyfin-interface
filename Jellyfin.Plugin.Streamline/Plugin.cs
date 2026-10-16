using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Streamline.Configuration;
using Jellyfin.Plugin.Streamline.Helpers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Streamline;

/// <summary>
/// Main entry point for the Streamline plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// The stable plugin identifier.
    /// </summary>
    public static readonly Guid PluginGuid = Guid.Parse("d0c3935f-a1ab-430c-95ed-388ce83b1431");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Streamline";

    /// <inheritdoc />
    public override string Description => "A streamlined, cinematic home screen for the Jellyfin web client.";

    /// <inheritdoc />
    public override Guid Id => PluginGuid;

    /// <summary>
    /// Gets the active plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = "Streamline",
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace),
                EnableInMainMenu = true,
                MenuSection = "plugins",
                MenuIcon = "view_carousel"
            }
        ];
    }

    /// <inheritdoc />
    public override void OnUninstalling()
    {
        // Best-effort: if the UI script was patched directly into index.html, try to remove it
        // so uninstalling the plugin doesn't leave a dead <script> tag behind.
        var method = FrontendInjectionMethods.Normalize(Configuration.FrontendInjectionMethod);
        if (method is FrontendInjectionMethods.Direct or FrontendInjectionMethods.Automatic)
        {
            DirectScriptInjector.TryRemove(ApplicationPaths, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        }

        base.OnUninstalling();
    }
}
