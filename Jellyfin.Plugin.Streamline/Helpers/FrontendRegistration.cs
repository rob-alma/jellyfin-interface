using Jellyfin.Plugin.Streamline.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Streamline.Helpers;

/// <summary>
/// Picks and runs the configured (or automatic best-available) frontend injection method.
/// </summary>
public static class FrontendRegistration
{
    /// <summary>
    /// Registers the UI script using the configured method, or the first one that succeeds
    /// when set to automatic.
    /// </summary>
    /// <param name="configuration">Plugin configuration.</param>
    /// <param name="applicationPaths">Jellyfin's application paths.</param>
    /// <param name="basePath">The server's reverse-proxy base path, or "".</param>
    /// <param name="logger">Logger.</param>
    public static bool TryRegisterConfigured(
        PluginConfiguration configuration,
        IApplicationPaths applicationPaths,
        string basePath,
        ILogger logger)
    {
        var method = FrontendInjectionMethods.Normalize(configuration.FrontendInjectionMethod);

        switch (method)
        {
            case FrontendInjectionMethods.FileTransformation:
                return FileTransformationRegistrar.TryRegister(logger);

            case FrontendInjectionMethods.JavaScriptInjector:
                return JavaScriptInjectorRegistrar.TryRegister(basePath, logger);

            case FrontendInjectionMethods.Direct:
                return DirectScriptInjector.TryInject(applicationPaths, basePath, logger);

            case FrontendInjectionMethods.Disabled:
                logger.LogInformation("Streamline home screen injection is disabled.");
                return true;

            default:
                if (FileTransformationRegistrar.TryRegister(logger))
                {
                    return true;
                }

                if (JavaScriptInjectorRegistrar.TryRegister(basePath, logger))
                {
                    return true;
                }

                logger.LogInformation(
                    "Neither File Transformation nor JavaScript Injector was available. Falling back to direct index.html injection.");
                return DirectScriptInjector.TryInject(applicationPaths, basePath, logger);
        }
    }
}
