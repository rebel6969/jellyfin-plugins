using System;
using System.IO;
using System.Xml.Linq;
using Jellyfin.Plugin.SimklMarkSync.Simkl;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.SimklMarkSync.Tests;

/// <summary>
/// Reading the client ID and logins from the official Simkl plugin.
/// </summary>
public sealed class SimklCredentialsTests : IDisposable
{
    private static readonly Guid OtherUser = new("11111111-2222-3333-4444-555555555555");

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("simkl-mark-sync-tests-");
    private readonly Mock<IApplicationPaths> _paths = new();
    private readonly Mock<IPluginManager> _pluginManager = new();
    private readonly ListLogger<SimklCredentials> _logger = new();
    private readonly SimklCredentials _credentials;

    /// <summary>
    /// Initializes a new instance of the <see cref="SimklCredentialsTests"/> class.
    /// </summary>
    public SimklCredentialsTests()
    {
        _paths.Setup(p => p.PluginConfigurationsPath).Returns(_directory.FullName);
        _paths.Setup(p => p.PluginsPath).Returns(_directory.FullName);
        _credentials = new SimklCredentials(_pluginManager.Object, _paths.Object, _logger);
    }

    private string ConfigurationPath => Path.Combine(_directory.FullName, SimklCredentials.ConfigurationFileName);

    /// <inheritdoc />
    public void Dispose()
    {
        _directory.Delete(recursive: true);
    }

    /// <summary>
    /// The login and switches saved for the user are read; another user's are not.
    /// </summary>
    [Fact]
    public void ReadsTheUsersLoginAndSwitches()
    {
        WriteConfiguration(UserConfig(Marks.User, " token-a ", movies: "false", shows: "true"), UserConfig(OtherUser, "token-b"));

        var user = _credentials.GetUser(Marks.User)!;

        Assert.Equal("token-a", user.Token);
        Assert.False(user.SyncMovies);
        Assert.True(user.SyncShows);
        Assert.Equal("token-b", _credentials.GetUser(OtherUser)!.Token);
    }

    /// <summary>
    /// The official plugin defaults both switches to on, so a missing or unreadable switch counts as on.
    /// </summary>
    [Fact]
    public void TreatsAMissingSwitchAsOn()
    {
        WriteConfiguration(UserConfig(Marks.User, "token", movies: null, shows: "maybe"));

        var user = _credentials.GetUser(Marks.User)!;

        Assert.True(user.SyncMovies);
        Assert.True(user.SyncShows);
    }

    /// <summary>
    /// A user with no saved login, or a blank one, has not linked Simkl.
    /// </summary>
    [Fact]
    public void HasNoLoginForAnUnlinkedUser()
    {
        WriteConfiguration(UserConfig(OtherUser, "   "));

        Assert.Null(_credentials.GetUser(Marks.User));
        Assert.Null(_credentials.GetUser(OtherUser));
    }

    /// <summary>
    /// Without the official plugin's settings file nobody has linked Simkl.
    /// </summary>
    [Fact]
    public void HasNoLoginWithoutTheSettingsFile()
    {
        Assert.Null(_credentials.GetUser(Marks.User));
    }

    /// <summary>
    /// A login saved after the first read is picked up.
    /// </summary>
    [Fact]
    public void RereadsTheSettingsWhenTheyChange()
    {
        WriteConfiguration(UserConfig(OtherUser, "token-b"));
        Assert.Null(_credentials.GetUser(Marks.User));

        WriteConfiguration(UserConfig(OtherUser, "token-b"), UserConfig(Marks.User, "token-a"));
        File.SetLastWriteTimeUtc(ConfigurationPath, DateTime.UtcNow.AddSeconds(5));

        Assert.Equal("token-a", _credentials.GetUser(Marks.User)!.Token);
    }

    /// <summary>
    /// A settings file caught mid-write keeps the last good copy in use.
    /// </summary>
    [Fact]
    public void KeepsTheLastGoodSettingsWhenTheFileDoesNotParse()
    {
        WriteConfiguration(UserConfig(Marks.User, "token-a"));
        Assert.NotNull(_credentials.GetUser(Marks.User));

        File.WriteAllText(ConfigurationPath, "<PluginConfiguration><UserConfigs>");
        File.SetLastWriteTimeUtc(ConfigurationPath, DateTime.UtcNow.AddSeconds(5));

        Assert.Equal("token-a", _credentials.GetUser(Marks.User)!.Token);
        Assert.Contains("Could not read", Assert.Single(_logger.At(LogLevel.Warning)), StringComparison.Ordinal);
    }

    /// <summary>
    /// A settings file with a DTD is refused rather than expanded.
    /// </summary>
    [Fact]
    public void RefusesASettingsFileWithADtd()
    {
        File.WriteAllText(
            ConfigurationPath,
            $"""<?xml version="1.0"?><!DOCTYPE x [<!ENTITY t "token">]><PluginConfiguration><UserConfigs><UserConfig><Id>{Marks.User}</Id><UserToken>&t;</UserToken></UserConfig></UserConfigs></PluginConfiguration>""");

        Assert.Null(_credentials.GetUser(Marks.User));
        Assert.Single(_logger.At(LogLevel.Warning));
    }

    /// <summary>
    /// The client ID is read from the loaded official plugin's assembly and kept.
    /// </summary>
    [Fact]
    public void ReadsTheClientIdFromTheLoadedOfficialPlugin()
    {
        SetOfficialPlugin(new FakeSimklPlugin(_paths.Object));

        Assert.Equal(Jellyfin.Plugin.Simkl.API.SimklApi.Apikey, _credentials.GetClientId());
        Assert.Equal(Jellyfin.Plugin.Simkl.API.SimklApi.Apikey, _credentials.GetClientId());
        _pluginManager.Verify(m => m.GetPlugin(SimklCredentials.OfficialPluginId, null), Times.Once);
    }

    /// <summary>
    /// Without the official plugin there is no client ID, and the reason is logged.
    /// </summary>
    [Fact]
    public void HasNoClientIdWithoutTheOfficialPlugin()
    {
        Assert.Null(_credentials.GetClientId());
        Assert.Contains("not installed", Assert.Single(_logger.At(LogLevel.Warning)), StringComparison.Ordinal);
    }

    /// <summary>
    /// An official plugin that did not load has no instance, so there is no client ID.
    /// </summary>
    [Fact]
    public void HasNoClientIdWhenTheOfficialPluginDidNotLoad()
    {
        SetOfficialPlugin(null);

        Assert.Null(_credentials.GetClientId());
        Assert.Single(_logger.At(LogLevel.Warning));
    }

    /// <summary>
    /// An official plugin version that moved its client ID gives none, with an error naming where it looked.
    /// </summary>
    [Fact]
    public void HasNoClientIdWhenTheOfficialPluginMovedIt()
    {
        SetOfficialPlugin(Mock.Of<IPlugin>());

        Assert.Null(_credentials.GetClientId());
        Assert.Contains(SimklCredentials.ApiTypeName, Assert.Single(_logger.At(LogLevel.Error)), StringComparison.Ordinal);
    }

    /// <summary>
    /// An assembly without the official API class has no client ID.
    /// </summary>
    [Fact]
    public void ReadsNoClientIdFromAnotherAssembly()
    {
        Assert.Null(SimklCredentials.ReadClientId(typeof(object).Assembly));
    }

    /// <summary>
    /// A document without user settings has no logins.
    /// </summary>
    [Fact]
    public void ReadsNoLoginFromAnEmptyDocument()
    {
        Assert.Null(SimklCredentials.ReadUser(XDocument.Parse("<PluginConfiguration />"), Marks.User));
    }

    private static string UserConfig(Guid id, string token, string? movies = "true", string? shows = "true") =>
        "<UserConfig>"
        + (movies is null ? string.Empty : $"<ScrobbleMovies>{movies}</ScrobbleMovies>")
        + $"<ScrobbleShows>{shows}</ScrobbleShows><ScrobblePercentage>70</ScrobblePercentage>"
        + $"<UserToken>{token}</UserToken><Id>{id}</Id></UserConfig>";

    private void WriteConfiguration(params string[] userConfigs)
    {
        File.WriteAllText(
            ConfigurationPath,
            """<?xml version="1.0" encoding="utf-8"?><PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><UserConfigs>"""
            + string.Concat(userConfigs)
            + "</UserConfigs></PluginConfiguration>");
    }

    private void SetOfficialPlugin(IPlugin? instance)
    {
        var manifest = new PluginManifest { Id = SimklCredentials.OfficialPluginId, Name = "Simkl", Version = "9.0.0.0" };
        var plugin = new LocalPlugin(_directory.FullName, true, manifest) { Instance = instance };
        _pluginManager.Setup(m => m.GetPlugin(SimklCredentials.OfficialPluginId, null)).Returns(plugin);
    }
}
