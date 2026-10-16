using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.Streamline.Helpers;

/// <summary>
/// Registers the UI script with the community "File Transformation" plugin, if it's
/// installed. Uses reflection (no compile-time dependency on that plugin) to find its
/// registration entry point and hand it a callback that inserts the script tag whenever it
/// serves index.html.
/// </summary>
public static class FileTransformationRegistrar
{
    private const string PluginInterfaceTypeName = "Jellyfin.Plugin.FileTransformation.PluginInterface";
    private const string TransformationId = "208760eb-31f6-4b43-b1bb-ef8ca6a97ad3";

    /// <summary>
    /// Attempts registration. Returns false (without error) if File Transformation isn't installed.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public static bool TryRegister(ILogger logger)
    {
        try
        {
            var assembly = AssemblyLoadContext.All
                .SelectMany(context => context.Assemblies)
                .FirstOrDefault(candidate => candidate.FullName?.Contains(".FileTransformation", StringComparison.OrdinalIgnoreCase) ?? false);

            if (assembly is null)
            {
                logger.LogDebug("File Transformation plugin was not found.");
                return false;
            }

            var pluginInterfaceType = assembly.GetType(PluginInterfaceTypeName);
            var registerMethod = pluginInterfaceType?.GetMethod("RegisterTransformation");
            if (registerMethod is null)
            {
                logger.LogWarning("File Transformation registration interface was not found.");
                return false;
            }

            var payload = new JObject
            {
                { "id", TransformationId },
                { "fileNamePattern", "index.html" },
                { "callbackAssembly", typeof(FileTransformationRegistrar).Assembly.FullName },
                { "callbackClass", typeof(Transformations).FullName },
                { "callbackMethod", nameof(Transformations.IndexTransformation) }
            };

            var result = registerMethod.Invoke(null, [payload]);
            if (result is false)
            {
                logger.LogWarning("File Transformation rejected the Streamline registration.");
                return false;
            }

            logger.LogInformation("Streamline home screen registered with File Transformation.");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to register the Streamline home screen with File Transformation.");
            return false;
        }
    }
}
