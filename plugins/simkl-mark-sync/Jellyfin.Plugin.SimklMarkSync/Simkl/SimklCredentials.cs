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
/// Reads the client IDs and user logins from the official Simkl plugin, so users link Simkl once, in that plugin.
/// </summary>
/// <remarks>
/// Simkl ties an access token to the client ID it was issued to, so the plugin must use the official plugin's client
/// ID for each login. Version 9 has one ID. Version 10 keeps that ID for logins from the old pin flow and issues
/// logins from its OAuth 2 device flow to a second one, telling the two apart with its own token test. All of it is
/// read from that plugin's loaded assembly rather than copied. Simkl documents a client ID as a public identifier, not
/// a secret. Only the single <see cref="MarkSyncService"/> worker calls this class, so it keeps its caches without
/// locks.
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
    /// The official plugin's per-user settings class, which holds its token test from version 10.
    /// </summary>
    public const string UserConfigTypeName = "Jellyfin.Plugin.Simkl.Configuration.UserConfig";

    /// <summary>
    /// Version 9's client ID field, the one ID it issues every login to.
    /// </summary>
    public const string ClientIdFieldName = "Apikey";

    /// <summary>
    /// Version 10's client ID field for pin flow logins; it holds the same ID as version 9's.
    /// </summary>
    public const string LegacyClientIdFieldName = "ApikeyLegacy";

    /// <summary>
    /// Version 10's client ID field for logins from its OAuth 2 device flow.
    /// </summary>
    public const string OAuth2ClientIdFieldName = "ApikeyOAuth2";

    /// <summary>
    /// Version 10's static method that tells a device flow token from a pin flow one.
    /// </summary>
    public const string IsOAuth2TokenMethodName = "IsOAuth2Token";

    /// <summary>
    /// The official Simkl plugin's identifier.
    /// </summary>
    public static readonly Guid OfficialPluginId = Guid.Parse("07caef58-a94b-4211-a62c-f9774e04ebdb");

    private static readonly XmlReaderSettings _xmlSettings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };

    private readonly IPluginManager _pluginManager;
    private readonly string _configurationPath;
    private readonly ILogger<SimklCredentials> _logger;
    private SimklClientIds? _clientIds;
    private Version? _pluginVersion;
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
    public string? GetClientId(string token)
    {
        var ids = GetClientIds();
        if (ids is null)
        {
            return null;
        }

        var clientId = ids.For(token);
        if (clientId is null)
        {
            _logger.LogError(
                "Marks are not sent to Simkl: Simkl plugin {Version} has the OAuth 2 client ID {Field} but no {Type}.{Method} to tell which client ID a login belongs to",
                _pluginVersion,
                OAuth2ClientIdFieldName,
                UserConfigTypeName,
                IsOAuth2TokenMethodName);
        }

        return clientId;
    }

    /// <inheritdoc />
    public SimklUser? GetUser(Guid userId)
    {
        var configuration = LoadConfiguration();
        return configuration is null ? null : ReadUser(configuration, userId);
    }

    /// <summary>
    /// Reads the client IDs from the official Simkl plugin's assembly.
    /// </summary>
    /// <param name="assembly">The official plugin's assembly.</param>
    /// <returns>The client IDs, or <c>null</c> when the assembly does not have them.</returns>
    public static SimklClientIds? ReadClientIds(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return ReadClientIds(
            assembly.GetType(ApiTypeName, throwOnError: false),
            assembly.GetType(UserConfigTypeName, throwOnError: false));
    }

    /// <summary>
    /// Reads the client IDs from the official plugin's API class and its token test from its settings class.
    /// </summary>
    /// <param name="apiType">The official plugin's API class, if it has one.</param>
    /// <param name="userConfigType">The official plugin's per-user settings class, if it has one.</param>
    /// <returns>The client IDs, or <c>null</c> when the API class has neither a pin flow nor a version 9 client ID.</returns>
    public static SimklClientIds? ReadClientIds(Type? apiType, Type? userConfigType)
    {
        if (apiType is null)
        {
            return null;
        }

        var legacy = ReadString(apiType, LegacyClientIdFieldName) ?? ReadString(apiType, ClientIdFieldName);
        if (legacy is null)
        {
            return null;
        }

        var test = userConfigType?.GetMethod(IsOAuth2TokenMethodName, BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
        Func<string, bool>? isOAuth2Token = test is null || test.ReturnType != typeof(bool)
            ? null
            : token => test.Invoke(null, [token]) is true;
        return new SimklClientIds(legacy, ReadString(apiType, OAuth2ClientIdFieldName), isOAuth2Token);
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

    private static string? ReadString(Type type, string fieldName)
    {
        var field = type.GetField(fieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (field is null)
        {
            return null;
        }

        var value = field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(null);
        return value is string id && !string.IsNullOrWhiteSpace(id) ? id : null;
    }

    private SimklClientIds? GetClientIds()
    {
        if (_clientIds is not null)
        {
            return _clientIds;
        }

        var plugin = _pluginManager.GetPlugin(OfficialPluginId);
        if (plugin?.Instance is null)
        {
            _logger.LogWarning("Marks are not sent to Simkl: the official Simkl plugin is not installed or did not load");
            return null;
        }

        _pluginVersion = plugin.Version;
        _clientIds = ReadClientIds(plugin.Instance.GetType().Assembly);
        if (_clientIds is null)
        {
            _logger.LogError(
                "Marks are not sent to Simkl: Simkl plugin {Version} has neither {LegacyField} nor {Field} on {Type}, where this plugin reads its client ID",
                plugin.Version,
                LegacyClientIdFieldName,
                ClientIdFieldName,
                ApiTypeName);
        }

        return _clientIds;
    }

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
