using System;
using System.IO;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SimklMarkSync.Simkl;

/// <summary>
/// Reads the client ID and user logins from the official Simkl plugin, so users link Simkl once, in that plugin.
/// </summary>
/// <remarks>
/// Simkl ties an access token to the client ID it was issued to, so the plugin must use the official plugin's client
/// ID. It is read from that plugin's loaded assembly rather than copied. Simkl documents a client ID as a public
/// identifier, not a secret. Only the single <see cref="MarkSyncService"/> worker calls this class, so it keeps its
/// caches without locks.
/// </remarks>
public sealed class SimklCredentials : ISimklCredentials
{
    /// <summary>
    /// The file the official Simkl plugin saves its settings to, in Jellyfin's plugin configurations folder.
    /// </summary>
    public const string ConfigurationFileName = "Jellyfin.Plugin.Simkl.xml";

    /// <summary>
    /// The official plugin's API class.
    /// </summary>
    public const string ApiTypeName = "Jellyfin.Plugin.Simkl.API.SimklApi";

    /// <summary>
    /// The official plugin's client ID field.
    /// </summary>
    public const string ClientIdFieldName = "Apikey";

    /// <summary>
    /// The official Simkl plugin's identifier.
    /// </summary>
    public static readonly Guid OfficialPluginId = Guid.Parse("07caef58-a94b-4211-a62c-f9774e04ebdb");

    private static readonly XmlReaderSettings _xmlSettings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };

    private readonly IPluginManager _pluginManager;
    private readonly string _configurationPath;
    private readonly ILogger<SimklCredentials> _logger;
    private string? _clientId;
    private XDocument? _configuration;
    private DateTime _configurationWriteTime;
    private long _configurationLength;

    /// <summary>
    /// Initializes a new instance of the <see cref="SimklCredentials"/> class.
    /// </summary>
    /// <param name="pluginManager">The plugin manager.</param>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public SimklCredentials(IPluginManager pluginManager, IApplicationPaths applicationPaths, ILogger<SimklCredentials> logger)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        _pluginManager = pluginManager;
        _configurationPath = Path.Combine(applicationPaths.PluginConfigurationsPath, ConfigurationFileName);
        _logger = logger;
    }

    /// <inheritdoc />
    public string? GetClientId()
    {
        if (_clientId is not null)
        {
            return _clientId;
        }

        var plugin = _pluginManager.GetPlugin(OfficialPluginId);
        if (plugin?.Instance is null)
        {
            _logger.LogWarning("Marks are not sent to Simkl: the official Simkl plugin is not installed or did not load");
            return null;
        }

        _clientId = ReadClientId(plugin.Instance.GetType().Assembly);
        if (_clientId is null)
        {
            _logger.LogError(
                "Marks are not sent to Simkl: Simkl plugin {Version} has no {Type}.{Field}, where this plugin reads its client ID",
                plugin.Version,
                ApiTypeName,
                ClientIdFieldName);
        }

        return _clientId;
    }

    /// <inheritdoc />
    public SimklUser? GetUser(Guid userId)
    {
        var configuration = LoadConfiguration();
        return configuration is null ? null : ReadUser(configuration, userId);
    }

    /// <summary>
    /// Reads the client ID from the official Simkl plugin's assembly.
    /// </summary>
    /// <param name="assembly">The official plugin's assembly.</param>
    /// <returns>The client ID, or <c>null</c> when the assembly does not have it.</returns>
    public static string? ReadClientId(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var field = assembly.GetType(ApiTypeName, throwOnError: false)
            ?.GetField(ClientIdFieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (field is null)
        {
            return null;
        }

        var value = field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(null);
        return value is string id && !string.IsNullOrWhiteSpace(id) ? id : null;
    }

    /// <summary>
    /// Reads one user's login from the official Simkl plugin's settings.
    /// </summary>
    /// <param name="configuration">The settings document.</param>
    /// <param name="userId">The Jellyfin user.</param>
    /// <returns>The login, or <c>null</c> when the user has none.</returns>
    public static SimklUser? ReadUser(XDocument configuration, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var userConfigs = configuration.Root?.Element("UserConfigs")?.Elements("UserConfig") ?? [];
        foreach (var userConfig in userConfigs)
        {
            if (!Guid.TryParse((string?)userConfig.Element("Id"), out var id) || id != userId)
            {
                continue;
            }

            var token = ((string?)userConfig.Element("UserToken"))?.Trim();
            return string.IsNullOrEmpty(token)
                ? null
                : new SimklUser(token, ReadSwitch(userConfig, "ScrobbleMovies"), ReadSwitch(userConfig, "ScrobbleShows"));
        }

        return null;
    }

    // The official plugin defaults both switches to on.
    private static bool ReadSwitch(XElement userConfig, string name) =>
        !bool.TryParse((string?)userConfig.Element(name), out var value) || value;

    private XDocument? LoadConfiguration()
    {
        var file = new FileInfo(_configurationPath);
        if (!file.Exists)
        {
            _configuration = null;
            return null;
        }

        if (_configuration is not null && file.LastWriteTimeUtc == _configurationWriteTime && file.Length == _configurationLength)
        {
            return _configuration;
        }

        try
        {
            using var stream = file.OpenRead();
            using var reader = XmlReader.Create(stream, _xmlSettings);
            _configuration = XDocument.Load(reader);
            _configurationWriteTime = file.LastWriteTimeUtc;
            _configurationLength = file.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            // Jellyfin may be rewriting the file; keep the last good copy until it reads cleanly.
            _logger.LogWarning("Could not read {Path}: {Message}", _configurationPath, ex.Message);
        }

        return _configuration;
    }
}
