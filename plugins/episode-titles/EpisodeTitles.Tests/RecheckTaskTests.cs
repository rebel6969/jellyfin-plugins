using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;
namespace Jellyfin.Plugin.EpisodeTitles.Tests;

public class RecheckTaskTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 18, 0, 0, TimeSpan.Zero);
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IProviderManager> _providers = new();
    private readonly Mock<ILogger<RecheckTask>> _logger = new();
    private readonly List<(BaseItem Item, MetadataRefreshOptions Options)> _refreshed = [];
    private InternalItemsQuery? _query;

    public RecheckTaskTests()
    {
        JellyfinStatics.Install();
        _providers
            .Setup(p => p.RefreshSingleItem(It.IsAny<BaseItem>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()))
            .Callback<BaseItem, MetadataRefreshOptions, CancellationToken>((item, options, _) =>
            {
                _refreshed.Add((item, options));
                if (item.Name == "Episode 3")
                {
                    item.Name = "A Title That Arrived Late";
                }
            })
            .ReturnsAsync(ItemUpdateType.MetadataEdit);
    }

    private RecheckTask Task(params BaseItem[] items)
    {
        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => _query = q)
            .Returns(items);
        return new RecheckTask(
            _library.Object, _providers.Object, new Mock<IFileSystem>().Object, _logger.Object, new FakeTimeProvider(Now));
    }

    private static Episode Episode(string name, int premiereDaysAgo, double refreshedHoursAgo)
    {
        var episode = JellyfinStatics.Episode(name, $"/media/anime/Show/Season 1/Show - S01E0{premiereDaysAgo % 10} - {name}.mkv");
        episode.PremiereDate = Now.UtcDateTime.AddDays(-premiereDaysAgo);
        episode.DateCreated = Now.UtcDateTime.AddDays(-premiereDaysAgo);
        episode.DateLastRefreshed = Now.UtcDateTime.AddHours(-refreshedHoursAgo);
        return episode;
    }

    private void VerifyLog(LogLevel level, string text) =>
        _logger.Verify(
            l => l.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains(text)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

    [Fact]
    public async Task OnlyDuePlaceholderEpisodesAreRefreshed()
    {
        var due = Episode("Episode 3", premiereDaysAgo: 2, refreshedHoursAgo: 30);
        var notDueYet = Episode("Episode 4", premiereDaysAgo: 2, refreshedHoursAgo: 2);
        var oldButDue = Episode("TBA", premiereDaysAgo: 300, refreshedHoursAgo: 24 * 31);
        var oldNotDue = Episode("Episode 5", premiereDaysAgo: 300, refreshedHoursAgo: 24 * 20);
        var real = Episode("The Floating Island", premiereDaysAgo: 2, refreshedHoursAgo: 30);
        var locked = Episode("Episode 6", premiereDaysAgo: 2, refreshedHoursAgo: 30);
        locked.IsLocked = true;
        var nameLocked = Episode("Episode 7", premiereDaysAgo: 2, refreshedHoursAgo: 30);
        nameLocked.LockedFields = [MetadataField.Name];
        var movie = new Movie { Name = "Episode 8", Path = "/media/movies/x.mkv" };

        await Task(due, notDueYet, oldButDue, oldNotDue, real, locked, nameLocked, movie)
            .ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal(new BaseItem[] { due, oldButDue }, _refreshed.Select(r => r.Item));
        Assert.Equal("A Title That Arrived Late", due.Name);
    }

    [Fact]
    public async Task TheQueryAsksForRealEpisodesInEveryLibrary()
    {
        await Task().ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.NotNull(_query);
        Assert.Equal(new[] { BaseItemKind.Episode }, _query.IncludeItemTypes);
        Assert.False(_query.IsVirtualItem);
        Assert.True(_query.Recursive);
    }

    [Fact]
    public async Task TheRefreshQueriesEveryProviderWithoutReplacingWhatIsThere()
    {
        await Task(Episode("Episode 3", 2, 30)).ExecuteAsync(new Progress<double>(), CancellationToken.None);

        var options = Assert.Single(_refreshed).Options;
        Assert.Equal(MetadataRefreshMode.FullRefresh, options.MetadataRefreshMode);
        Assert.Equal(MetadataRefreshMode.Default, options.ImageRefreshMode);
        Assert.False(options.ReplaceAllMetadata);
        Assert.False(options.ReplaceAllImages);
        Assert.False(options.RemoveOldMetadata);
    }

    [Fact]
    public async Task OneFailedRefreshDoesNotStopTheRest()
    {
        var broken = Episode("TBA", 2, 30);
        var next = Episode("Episode 3", 2, 30);
        _providers
            .Setup(p => p.RefreshSingleItem(broken, It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        await Task(broken, next).ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal("A Title That Arrived Late", next.Name);
        VerifyLog(LogLevel.Warning, "database is locked");
        VerifyLog(LogLevel.Information, "re-checked 2, renamed 1, still a placeholder 1");
    }

    [Fact]
    public async Task TheRunReportsHowManyWereDueAndRenamed()
    {
        await Task(Episode("Episode 3", 2, 30), Episode("TBA", 2, 30), Episode("Real", 2, 30))
            .ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLog(LogLevel.Information, "2 placeholder-named episodes are due");
        VerifyLog(LogLevel.Information, "re-checked 2, renamed 1, still a placeholder 1");
    }

    [Fact]
    public async Task ProgressEndsAtOneHundred()
    {
        var reports = new List<double>();
        var progress = new SynchronousProgress(reports);

        await Task(Episode("Episode 3", 2, 30), Episode("TBA", 2, 30)).ExecuteAsync(progress, CancellationToken.None);

        Assert.Equal(new[] { 50.0, 100.0, 100.0 }, reports);
    }

    [Fact]
    public async Task CancellationStopsTheRun()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Task(Episode("Episode 3", 2, 30)).ExecuteAsync(new Progress<double>(), cancel.Token));
        Assert.Empty(_refreshed);
    }

    [Fact]
    public void ItRunsDailyByDefault()
    {
        var trigger = Assert.Single(Task().GetDefaultTriggers());
        Assert.Equal(TaskTriggerInfoType.IntervalTrigger, trigger.Type);
        Assert.Equal(TimeSpan.FromHours(24).Ticks, trigger.IntervalTicks);
    }

    [Fact]
    public void ItIsListedUnderItsOwnCategoryAndKey()
    {
        var task = Task();
        Assert.Equal("EpisodeTitlesRecheck", task.Key);
        Assert.Equal("Episode Titles", task.Category);
        Assert.Equal("Re-check placeholder episode titles", task.Name);
    }

    [Fact]
    public void JellyfinCanBuildItWithThePublicConstructor()
    {
        var constructors = typeof(RecheckTask).GetConstructors();
        var only = Assert.Single(constructors);
        Assert.Equal(
            new[] { typeof(ILibraryManager), typeof(IProviderManager), typeof(IFileSystem), typeof(ILogger<RecheckTask>) },
            only.GetParameters().Select(p => p.ParameterType));
    }

    private sealed class SynchronousProgress(List<double> reports) : IProgress<double>
    {
        public void Report(double value) => reports.Add(value);
    }
}
