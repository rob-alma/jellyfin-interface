using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Streamline.Helpers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Streamline.ScheduledTasks;

/// <summary>
/// Registers the home screen UI script on every server start, so the injection
/// survives Jellyfin updates that overwrite index.html (when using direct injection).
/// </summary>
public sealed class FrontendRegistrationStartupTask : IScheduledTask
{
    private readonly IApplicationPaths _applicationPaths;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly ILogger<FrontendRegistrationStartupTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FrontendRegistrationStartupTask"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin's application paths.</param>
    /// <param name="configurationManager">Jellyfin's server configuration manager.</param>
    /// <param name="logger">Logger.</param>
    public FrontendRegistrationStartupTask(
        IApplicationPaths applicationPaths,
        IServerConfigurationManager configurationManager,
        ILogger<FrontendRegistrationStartupTask> logger)
    {
        _applicationPaths = applicationPaths;
        _configurationManager = configurationManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Register Streamline home screen";

    /// <inheritdoc />
    public string Key => "Jellyfin.Plugin.Streamline.RegisterFrontend";

    /// <inheritdoc />
    public string Description => "Registers the home screen UI script.";

    /// <inheritdoc />
    public string Category => "Streamline";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // Registered even when the home screen is switched off in the settings: the script
        // checks that itself on every page load, so turning it back on needs no restart.
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return Task.CompletedTask;
        }

        var basePath = GetBasePath();
        ScriptRuntime.BasePath = basePath;
        ScriptRuntime.ScriptVersion = GetType().Assembly.GetName().Version?.ToString() ?? "0";

        if (!FrontendRegistration.TryRegisterConfigured(config, _applicationPaths, basePath, _logger))
        {
            _logger.LogWarning("Could not register the Streamline home screen frontend script.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
    }

    private string GetBasePath()
    {
        try
        {
            var networkConfiguration = _configurationManager.GetConfiguration<NetworkConfiguration>("network");
            if (!string.IsNullOrWhiteSpace(networkConfiguration.BaseUrl))
            {
                return $"/{networkConfiguration.BaseUrl.Trim().Trim('/')}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to get the Jellyfin base path; using '/'.");
        }

        return string.Empty;
    }
}
