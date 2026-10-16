namespace Jellyfin.Plugin.Streamline.Configuration;

/// <summary>
/// Valid values for <see cref="PluginConfiguration.FrontendInjectionMethod"/>.
/// </summary>
public static class FrontendInjectionMethods
{
    /// <summary>
    /// Try File Transformation, then JavaScript Injector, then direct injection.
    /// </summary>
    public const string Automatic = "automatic";

    /// <summary>
    /// Register through the community "File Transformation" plugin, if installed.
    /// </summary>
    public const string FileTransformation = "file-transformation";

    /// <summary>
    /// Register through the community "JavaScript Injector" plugin, if installed.
    /// </summary>
    public const string JavaScriptInjector = "javascript-injector";

    /// <summary>
    /// Patch jellyfin-web/index.html directly.
    /// </summary>
    public const string Direct = "direct";

    /// <summary>
    /// Don't inject the UI script at all.
    /// </summary>
    public const string Disabled = "disabled";

    /// <summary>
    /// Normalizes a possibly-invalid stored value to one of the known constants.
    /// </summary>
    /// <param name="value">The stored configuration value.</param>
    public static string Normalize(string? value)
    {
        return value switch
        {
            FileTransformation => FileTransformation,
            JavaScriptInjector => JavaScriptInjector,
            Direct => Direct,
            Disabled => Disabled,
            _ => Automatic
        };
    }
}
