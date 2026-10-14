using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
namespace Jellyfin.Plugin.EpisodeTitles.Tests;

public class TitleFallbackProviderTests
{
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IProviderManager> _providers = new();
    private readonly Mock<ILogger<TitleFallbackProvider>> _logger = new();
    private readonly LibraryOptions _options = new();

    public TitleFallbackProviderTests()
    {
        JellyfinStatics.Install();
        _library.Setup(l => l.GetLibraryOptions(It.IsAny<BaseItem>())).Returns(_options);
    }

    private TitleFallbackProvider Provider(params IMetadataProvider<Episode>[] inOrder)
    {
        _providers.Setup(p => p.GetMetadataProviders<Episode>(It.IsAny<BaseItem>(), _options)).Returns(inOrder);
        return new TitleFallbackProvider(_library.Object, _providers.Object, _logger.Object);
    }

    private static Task<ItemUpdateType> Fetch(TitleFallbackProvider provider, Episode episode) =>
        provider.FetchAsync(episode, new MetadataRefreshOptions(new Mock<IDirectoryService>().Object), CancellationToken.None);

    [Fact]
    public async Task AStandInFromTheFirstProviderGivesWayToTheNextRealTitle()
    {
        var anidb = new FakeProvider("AniDB", "Episode 3");
        var tvdb = new FakeProvider("TheTVDB", "The Undead Monster Was Tough as Nails");
        var episode = JellyfinStatics.Episode("Episode 3");

        var update = await Fetch(Provider(anidb, tvdb), episode);

        Assert.Equal(ItemUpdateType.MetadataEdit, update);
        Assert.Equal("The Undead Monster Was Tough as Nails", episode.Name);
        Assert.Single(anidb.Calls);
        Assert.Single(tvdb.Calls);
        Assert.Equal(3, tvdb.Calls[0].IndexNumber);
        Assert.Equal(2, tvdb.Calls[0].ParentIndexNumber);
        Assert.Equal(episode.Path, tvdb.Calls[0].Path);
    }

    [Fact]
    public async Task TheFirstRealTitleInFetcherOrderWinsAndLaterProvidersAreNotAsked()
    {
        var anidb = new FakeProvider("AniDB", "The Floating Island");
        var tvdb = new FakeProvider("TheTVDB", "A Different Translation");
        var episode = JellyfinStatics.Episode("TBA");

        await Fetch(Provider(anidb, tvdb), episode);

        Assert.Equal("The Floating Island", episode.Name);
        Assert.Empty(tvdb.Calls);
    }

    [Fact]
    public async Task ARealNameIsLeftAloneWithoutAskingAnyProvider()
    {
        var tvdb = new FakeProvider("TheTVDB", "Something Else");
        var episode = JellyfinStatics.Episode("The Floating Island");

        var update = await Fetch(Provider(tvdb), episode);

        Assert.Equal(ItemUpdateType.None, update);
        Assert.Equal("The Floating Island", episode.Name);
        Assert.Empty(tvdb.Calls);
    }

    [Fact]
    public async Task ALockedItemIsLeftAlone()
    {
        var tvdb = new FakeProvider("TheTVDB", "Real Title");
        var episode = JellyfinStatics.Episode("Episode 3");
        episode.IsLocked = true;

        Assert.Equal(ItemUpdateType.None, await Fetch(Provider(tvdb), episode));
        Assert.Equal("Episode 3", episode.Name);
        Assert.Empty(tvdb.Calls);
    }

    [Fact]
    public async Task ALockedNameIsLeftAlone()
    {
        var tvdb = new FakeProvider("TheTVDB", "Real Title");
        var episode = JellyfinStatics.Episode("Episode 3");
        episode.LockedFields = [MetadataField.Name];

        Assert.Equal(ItemUpdateType.None, await Fetch(Provider(tvdb), episode));
        Assert.Equal("Episode 3", episode.Name);
        Assert.Empty(tvdb.Calls);
    }

    [Fact]
    public async Task AnotherLockedFieldDoesNotStopTheName()
    {
        var tvdb = new FakeProvider("TheTVDB", "Real Title");
        var episode = JellyfinStatics.Episode("Episode 3");
        episode.LockedFields = [MetadataField.Overview];

        await Fetch(Provider(tvdb), episode);

        Assert.Equal("Real Title", episode.Name);
    }

    [Fact]
    public async Task TheFileNameJellyfinKeptIsReplaced()
    {
        var tvdb = new FakeProvider("TheTVDB", "Undercurrents Beyond the Pass");
        var episode = JellyfinStatics.Episode(
            "Blades of the Guardians - S02E01 - TBA WEBDL-1080p",
            "/media/anime/Blades of the Guardians/Season 2/Blades of the Guardians - S02E01 - TBA WEBDL-1080p.mkv");

        await Fetch(Provider(tvdb), episode);

        Assert.Equal("Undercurrents Beyond the Pass", episode.Name);
    }

    [Fact]
    public async Task WhenEveryProviderHasOnlyStandInsTheNameStays()
    {
        var anidb = new FakeProvider("AniDB", "Episode 3");
        var tvdb = new FakeProvider("TheTVDB", "TBA");
        var tmdb = new FakeProvider("TheMovieDb", (string?)null);
        var omdb = new FakeProvider("The Open Movie Database", "   ");
        var episode = JellyfinStatics.Episode("Episode 3");

        var update = await Fetch(Provider(anidb, tvdb, tmdb, omdb), episode);

        Assert.Equal(ItemUpdateType.None, update);
        Assert.Equal("Episode 3", episode.Name);
        Assert.All(new[] { anidb, tvdb, tmdb, omdb }, p => Assert.Single(p.Calls));
    }

    [Fact]
    public async Task AResultWithoutMetadataIsIgnoredEvenIfItCarriesAName()
    {
        var tvdb = new FakeProvider("TheTVDB", "Not Really", hasMetadata: false);
        var tmdb = new FakeProvider("TheMovieDb", "Real Title");
        var episode = JellyfinStatics.Episode("Episode 3");

        await Fetch(Provider(tvdb, tmdb), episode);

        Assert.Equal("Real Title", episode.Name);
    }

    [Fact]
    public async Task AResultWithNoItemIsIgnored()
    {
        var tvdb = new FakeProvider("TheTVDB", _ => new MetadataResult<Episode> { HasMetadata = true, Item = null! });
        var tmdb = new FakeProvider("TheMovieDb", "Real Title");
        var episode = JellyfinStatics.Episode("Episode 3");

        await Fetch(Provider(tvdb, tmdb), episode);

        Assert.Equal("Real Title", episode.Name);
    }

    [Fact]
    public async Task TheTitleIsTrimmed()
    {
        var tvdb = new FakeProvider("TheTVDB", "  Locusts \n");
        var episode = JellyfinStatics.Episode("Episode 1");

        await Fetch(Provider(tvdb), episode);

        Assert.Equal("Locusts", episode.Name);
    }

    [Fact]
    public async Task AFailingProviderIsSkippedAndLogged()
    {
        var anidb = new FakeProvider("AniDB", _ => throw new HttpRequestException("banned"));
        var tvdb = new FakeProvider("TheTVDB", "Real Title");
        var episode = JellyfinStatics.Episode("Episode 3");

        await Fetch(Provider(anidb, tvdb), episode);

        Assert.Equal("Real Title", episode.Name);
        _logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("AniDB failed") && v.ToString()!.Contains("banned")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task AProviderTimeoutIsAFailureNotACancellation()
    {
        var anidb = new FakeProvider("AniDB", _ => throw new TaskCanceledException("HttpClient.Timeout elapsed"));
        var tvdb = new FakeProvider("TheTVDB", "Real Title");
        var episode = JellyfinStatics.Episode("Episode 3");

        await Fetch(Provider(anidb, tvdb), episode);

        Assert.Equal("Real Title", episode.Name);
    }

    [Fact]
    public async Task CancellationIsPassedOn()
    {
        using var cancel = new CancellationTokenSource();
        var anidb = new FakeProvider("AniDB", _ =>
        {
            cancel.Cancel();
            throw new OperationCanceledException(cancel.Token);
        });
        var tvdb = new FakeProvider("TheTVDB", "Real Title");
        var episode = JellyfinStatics.Episode("Episode 3");
        var provider = Provider(anidb, tvdb);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.FetchAsync(
            episode, new MetadataRefreshOptions(new Mock<IDirectoryService>().Object), cancel.Token));
        Assert.Equal("Episode 3", episode.Name);
        Assert.Empty(tvdb.Calls);
    }

    [Fact]
    public async Task ProvidersThatAreNotRemoteEpisodeProvidersAreSkipped()
    {
        var local = new Mock<ILocalMetadataProvider<Episode>>(MockBehavior.Strict);
        var custom = new Mock<ICustomMetadataProvider<Episode>>(MockBehavior.Strict);
        var tvdb = new FakeProvider("TheTVDB", "Real Title");
        var episode = JellyfinStatics.Episode("Episode 3");

        await Fetch(Provider(local.Object, custom.Object, tvdb), episode);

        Assert.Equal("Real Title", episode.Name);
    }

    [Fact]
    public async Task TheLibraryOfTheItemDecidesWhichProvidersAreAsked()
    {
        var tvdb = new FakeProvider("TheTVDB", "Real Title");
        var episode = JellyfinStatics.Episode("Episode 3");

        await Fetch(Provider(tvdb), episode);

        _library.Verify(l => l.GetLibraryOptions(episode), Times.Once);
        _providers.Verify(p => p.GetMetadataProviders<Episode>(episode, _options), Times.Once);
    }

    [Fact]
    public void ItIsCalledEpisodeTitles() =>
        Assert.Equal("Episode Titles", new TitleFallbackProvider(_library.Object, _providers.Object, _logger.Object).Name);

    [Fact]
    public void JellyfinCanBuildItWithThePublicConstructor()
    {
        var only = Assert.Single(typeof(TitleFallbackProvider).GetConstructors());
        Assert.Equal(
            new[] { typeof(ILibraryManager), typeof(IProviderManager), typeof(ILogger<TitleFallbackProvider>) },
            only.GetParameters().Select(p => p.ParameterType));
    }
}
