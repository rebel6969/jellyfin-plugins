using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SimklMarkSync.Simkl;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.SimklMarkSync.Tests;

/// <summary>
/// The whole path for an anime episode Simkl cannot match by TVDB numbering: the processor, the real client and the
/// resolver, against Simkl's recorded answers for A Returner's Magic Should Be Special (2026-10-15).
/// </summary>
public sealed class CatalogFallbackTests : IDisposable
{
    private const string ClientId = "fixture-client-id";
    private const string Show = "A Returner's Magic Should Be Special";
    private const string Added = """{"added":{"movies":0,"shows":0,"episodes":1,"statuses":[]},"not_found":{"movies":[],"shows":[],"episodes":[]}}""";
    private const string Deleted = """{"deleted":{"movies":0,"shows":0,"episodes":1},"not_found":{"movies":[],"shows":[]}}""";

    private static readonly Guid SeriesId = new("4267479c-0963-b93e-da01-ffbf1f627652");
    private static readonly SimklUser User = new("token-a", syncMovies: true, syncShows: true);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 15, 7, 38, 13, TimeSpan.Zero));
    private readonly FakeHttpHandler _handler;
    private readonly HttpClient _httpClient;
    private readonly ListLogger<MarkSyncProcessor> _logger = new();
    private readonly MarkSyncProcessor _processor;

    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogFallbackTests"/> class.
    /// </summary>
    public CatalogFallbackTests()
    {
        _handler = new FakeHttpHandler(_clock);
        _httpClient = new HttpClient(_handler, disposeHandler: false);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(NamedClient.Default)).Returns(_httpClient);
        var credentials = new Mock<ISimklCredentials>();
        credentials.Setup(c => c.GetClientId(It.IsAny<string>())).Returns(ClientId);
        credentials.Setup(c => c.GetUser(Marks.User)).Returns(User);
        var client = new SimklClient(factory.Object, _clock, new ListLogger<SimklClient>());
        _processor = new MarkSyncProcessor(credentials.Object, client, new FakeSettings(), _clock, _logger);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <inheritdoc />
    public void Dispose()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    /// <summary>
    /// The production case: S2E2 is not found by TVDB numbering, is placed as episode 2 of Simkl title 2330432 by
    /// its position and air date, and is sent again by Simkl's own numbering with its watch time.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task MarksTheEpisodeByTheCatalogueWhenTvdbNumberingFails()
    {
        EnqueueNotFoundThenCatalogue(Added);

        await Run(_processor.ProcessAsync([Episode(played: true)], Token));

        var requests = _handler.Requests;
        Assert.Equal(
            ["POST /sync/history", "GET /search/id", "GET /anime/2125698", "GET /anime/2330432", "GET /anime/episodes/2330432", "POST /sync/history"],
            requests.Select(r => $"{r.Method} {r.Uri.AbsolutePath}"));
        Assert.Contains("\"use_tvdb_anime_seasons\":true", requests[0].Body, StringComparison.Ordinal);
        Assert.Equal(
            """{"shows":[{"ids":{"simkl":2330432},"use_tvdb_anime_seasons":false,"seasons":[{"number":1,"episodes":[{"number":2,"watched_at":"2026-10-15T07:38:10Z"}]}]}]}""",
            requests[5].Body);
        var info = _logger.At(LogLevel.Information);
        Assert.Contains($"Simkl: {Show} S2E2 is episode 2 of Simkl title 2330432, placed by its position in the season and its air date", info);
        Assert.Contains(info, m => m.Contains("1 episodes sent again by Simkl's own numbering", StringComparison.Ordinal) && m.Contains("newly added 1 episodes", StringComparison.Ordinal));
        Assert.Empty(_logger.At(LogLevel.Warning));
    }

    /// <summary>
    /// An unmark follows the same path and names the episode explicitly, with no watch time.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task UnmarksTheEpisodeByTheCatalogueWhenTvdbNumberingFails()
    {
        EnqueueNotFoundThenCatalogue(Deleted);

        await Run(_processor.ProcessAsync([Episode(played: false)], Token));

        var requests = _handler.Requests;
        Assert.Equal("/sync/history/remove", requests[0].Uri.AbsolutePath);
        Assert.Equal("/sync/history/remove", requests[5].Uri.AbsolutePath);
        Assert.Equal(
            """{"shows":[{"ids":{"simkl":2330432},"use_tvdb_anime_seasons":false,"seasons":[{"number":1,"episodes":[{"number":2}]}]}]}""",
            requests[5].Body);
        Assert.Contains(_logger.At(LogLevel.Information), m => m.Contains("1 episodes unmarked again by Simkl's own numbering", StringComparison.Ordinal) && m.Contains("1 episodes removed", StringComparison.Ordinal));
        Assert.Empty(_logger.At(LogLevel.Warning));
    }

    /// <summary>
    /// When no episode fits (here the premiere date is five days off), nothing more is sent, and the warning names the
    /// episode and the reason once, without the generic not-found line.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SendsNothingMoreWhenNoEpisodeFits()
    {
        EnqueueNotFoundThenCatalogue(null);
        var mark = Episode(played: true) with { PremiereDate = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc) };

        await Run(_processor.ProcessAsync([mark], Token));

        Assert.Equal(5, _handler.Requests.Count);
        var warning = Assert.Single(_logger.At(LogLevel.Warning));
        Assert.StartsWith($"Simkl could not match {Show} S2E2: no Simkl episode is filed under TVDB S2E2", warning, StringComparison.Ordinal);
        Assert.EndsWith("; it was not marked watched", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// A movie Simkl cannot match reads no catalogue and is logged as before.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ReadsNoCatalogueForAnUnmatchedMovie()
    {
        _handler.Enqueue(Reply("""{"added":{"movies":0,"shows":0,"episodes":0},"not_found":{"movies":[{"title":"Nope","ids":{"imdb":"tt1"}}],"shows":[],"episodes":[]}}"""));

        await Run(_processor.ProcessAsync([Marks.Movie("tt1")], Token));

        Assert.Single(_handler.Requests);
        Assert.Equal("""Simkl could not match Nope {"imdb":"tt1"}; it was not marked watched""", Assert.Single(_logger.At(LogLevel.Warning)));
    }

    /// <summary>
    /// A not-found show whose IDs match no sent episode reads no catalogue and is logged as before.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ReadsNoCatalogueForAShowNoMarkWasSentFor()
    {
        _handler.Enqueue(Reply("""{"added":{"movies":0,"shows":0,"episodes":0},"not_found":{"movies":[],"shows":[{"title":"Other","ids":{"tvdb":"1"}}],"episodes":[]}}"""));

        await Run(_processor.ProcessAsync([Episode(played: true)], Token));

        Assert.Single(_handler.Requests);
        Assert.Equal("""Simkl could not match Other {"tvdb":"1"}; it was not marked watched""", Assert.Single(_logger.At(LogLevel.Warning)));
    }

    /// <summary>
    /// A not-found show matches a sent episode only when its IDs are exactly the ones sent: the same names, the same
    /// values, nothing more.
    /// </summary>
    /// <param name="ids">The IDs Simkl echoes.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData("""{"anidb":"17944","imdb":"tt28313387","tmdb":"230050","tvdb":"999"}""")]
    [InlineData("""{"anidb":"17944","imdb":"tt28313387","tmdb":"230050","tvdb":"432815","simkl":5}""")]
    public async Task MatchesANotFoundShowOnlyByExactlyTheSentIds(string ids)
    {
        _handler.Enqueue(Reply($$$"""{"added":{"movies":0,"shows":0,"episodes":0},"not_found":{"movies":[],"shows":[{"ids":{{{ids}}}}],"episodes":[]}}"""));

        await Run(_processor.ProcessAsync([Episode(played: true)], Token));

        Assert.Single(_handler.Requests);
        Assert.Contains("it was not marked watched", Assert.Single(_logger.At(LogLevel.Warning)), StringComparison.Ordinal);
    }

    private static EpisodeMark Episode(bool played) =>
        new(
            Marks.User,
            new Guid("b257c0c6-d998-8746-aa33-ff34e8ea6cb7"),
            played,
            new DateTime(2026, 10, 15, 7, 38, 10, DateTimeKind.Utc),
            SeriesId,
            new Dictionary<string, object> { ["anidb"] = "17944", ["imdb"] = "tt28313387", ["tmdb"] = "230050", ["tvdb"] = "432815" },
            Show,
            2023,
            2,
            [2])
        {
            PremiereDate = new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc),
        };

    private static Func<HttpResponseMessage> Reply(string body) =>
        () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // Simkl echoes an unmatched show verbatim under not_found.shows, so the first answer echoes what was sent. A null
    // final answer queues no second write.
    private void EnqueueNotFoundThenCatalogue(string? finalAnswer)
    {
        _handler.Enqueue(() =>
        {
            var sent = JsonNode.Parse(_handler.Requests[^1].Body!)!["shows"]!.ToJsonString();
            return Reply($$$"""{"added":{"movies":0,"shows":0,"episodes":0,"statuses":[]},"not_found":{"movies":[],"shows":{{{sent}}},"episodes":[]}}""")();
        });
        _handler.Enqueue(Reply(Fixtures.Read("simkl-search-id-tvdb-432815.json")));
        _handler.Enqueue(Reply(Fixtures.Read("simkl-anime-2125698.json")));
        _handler.Enqueue(Reply(Fixtures.Read("simkl-anime-2330432.json")));
        _handler.Enqueue(Reply(Fixtures.Read("simkl-anime-episodes-2330432.json")));
        if (finalAnswer is not null)
        {
            _handler.Enqueue(Reply(finalAnswer));
        }
    }

    // Moves the fake clock until the batch finishes, so the spacing between calls elapses.
    private async Task Run(Task batch)
    {
        while (!batch.IsCompleted)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(1, Token).ConfigureAwait(false);
        }

        await batch.ConfigureAwait(false);
    }
}
