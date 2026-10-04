using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
/// The Simkl API calls, against a fake HTTP handler and a fake clock.
/// </summary>
public sealed class SimklClientTests : IDisposable
{
    private const string ClientId = "fixture-client-id";
    private const string AddedJson = """{"added":{"movies":1,"shows":0,"episodes":0,"statuses":[]},"not_found":{"movies":[],"shows":[],"episodes":[]}}""";

    private static readonly SimklUser User = new("token-a", syncMovies: true, syncShows: true);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 4, 3, 30, 0, TimeSpan.Zero));
    private readonly FakeHttpHandler _handler;
    private readonly HttpClient _httpClient;
    private readonly ListLogger<SimklClient> _logger = new();
    private readonly SimklClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="SimklClientTests"/> class.
    /// </summary>
    public SimklClientTests()
    {
        _handler = new FakeHttpHandler(_clock);
        _httpClient = new HttpClient(_handler, disposeHandler: false);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(NamedClient.Default)).Returns(_httpClient);
        _client = new SimklClient(factory.Object, _clock, _logger);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <inheritdoc />
    public void Dispose()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    /// <summary>
    /// A call carries the three query parameters, the headers and the JSON body Simkl's documentation requires.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SendsTheDocumentedQueryHeadersAndBody()
    {
        _handler.Enqueue(Reply(HttpStatusCode.Created, AddedJson));
        var request = SimklPayload.BuildAdd([Marks.Movie("tt1")]);

        await Run(() => _client.AddToHistoryAsync(request, ClientId, User, Token));

        var sent = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://api.simkl.com/sync/history?client_id=fixture-client-id&app-name=jellyfin-simkl-mark-sync&app-version=1.0.0.0", sent.Uri.AbsoluteUri);
        Assert.Contains("User-Agent: jellyfin-simkl-mark-sync/1.0.0.0", sent.Headers, StringComparison.Ordinal);
        Assert.Contains("Authorization: Bearer token-a", sent.Headers, StringComparison.Ordinal);
        Assert.Contains("Accept: application/json", sent.Headers, StringComparison.Ordinal);
        Assert.Equal("application/json; charset=utf-8", sent.ContentType);
        Assert.Equal(JsonSerializer.Serialize(request, SimklPayload.JsonOptions), sent.Body);
    }

    /// <summary>
    /// The client ID is escaped, so a character with meaning in a query cannot break the URL.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task EscapesTheClientId()
    {
        _handler.Enqueue(Reply(HttpStatusCode.Created, AddedJson));

        await Run(() => _client.AddToHistoryAsync(SimklPayload.BuildAdd([Marks.Movie("tt1")]), "a&b", User, Token));

        Assert.StartsWith("?client_id=a%26b&app-name=", Assert.Single(_handler.Requests).Uri.Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// An add reports what Simkl added and describes each item it could not match.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ReportsWhatWasAddedAndWhatWasNotFound()
    {
        _handler.Enqueue(Reply(
            HttpStatusCode.Created,
            """{"added":{"movies":1,"shows":0,"episodes":2},"not_found":{"movies":[{"title":"Nope","ids":{"imdb":"tt0"}}],"shows":[{"ids":{"tvdb":"9"}}],"episodes":[]}}"""));

        var result = await Run(() => _client.AddToHistoryAsync(SimklPayload.BuildAdd([Marks.Movie("tt1")]), ClientId, User, Token));

        Assert.NotNull(result);
        Assert.Equal((1, 0, 2), (result.Movies, result.Shows, result.Episodes));
        Assert.Equal(["""Nope {"imdb":"tt0"}""", """{"ids":{"tvdb":"9"}}"""], result.NotFound);
    }

    /// <summary>
    /// A remove posts to the remove endpoint and reports what Simkl deleted.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ReportsWhatWasRemoved()
    {
        _handler.Enqueue(Reply(HttpStatusCode.Created, """{"deleted":{"movies":1,"shows":0,"episodes":3},"not_found":{"movies":[],"shows":[]}}"""));
        var request = SimklPayload.BuildRemove([Marks.Episode(1, 1, played: false)], [Marks.Movie("tt1", played: false)]);

        var result = await Run(() => _client.RemoveFromHistoryAsync(request, ClientId, User, Token));

        Assert.Equal("/sync/history/remove", Assert.Single(_handler.Requests).Uri.AbsolutePath);
        Assert.NotNull(result);
        Assert.Equal((1, 0, 3), (result.Movies, result.Shows, result.Episodes));
        Assert.Empty(result.NotFound);
    }

    /// <summary>
    /// The client checks a removal again and refuses one that would remove a whole show, without sending it.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RefusesToSendARemovalOfAWholeShow()
    {
        var wholeShow = new HistoryRequest(null, [new HistoryShow(Marks.Episode(1, 1).ShowIds, null, null, true, [])]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _client.RemoveFromHistoryAsync(wholeShow, ClientId, User, Token));

        Assert.Empty(_handler.Requests);
    }

    /// <summary>
    /// A 429 is retried after the time Simkl asks for.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RetriesA429AfterTheTimeSimklAsksFor()
    {
        _handler.Enqueue(Reply(HttpStatusCode.TooManyRequests, """{"error":"rate_limit","code":429}""", TimeSpan.FromSeconds(5)));
        _handler.Enqueue(Reply(HttpStatusCode.Created, AddedJson));

        var result = await Run(() => _client.AddToHistoryAsync(SimklPayload.BuildAdd([Marks.Movie("tt1")]), ClientId, User, Token));

        Assert.NotNull(result);
        var requests = _handler.Requests;
        Assert.Equal(2, requests.Count);
        Assert.InRange(requests[1].At - requests[0].At, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5.5));
        Assert.Contains("HTTP 429", Assert.Single(_logger.At(LogLevel.Warning)), StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>Retry-After</c> given as a date is honoured too.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task HonoursARetryAfterDate()
    {
        _handler.Enqueue(() =>
        {
            var response = Reply(HttpStatusCode.ServiceUnavailable, "{}")();
            response.Headers.RetryAfter = new RetryConditionHeaderValue(_clock.GetUtcNow().AddSeconds(7));
            return response;
        });
        _handler.Enqueue(Reply(HttpStatusCode.Created, AddedJson));

        await Run(() => _client.AddToHistoryAsync(SimklPayload.BuildAdd([Marks.Movie("tt1")]), ClientId, User, Token));

        var requests = _handler.Requests;
        Assert.InRange(requests[1].At - requests[0].At, TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7.5));
    }

    /// <summary>
    /// A very long <c>Retry-After</c> is capped, so one call cannot hold the queue for an hour.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task CapsALongRetryAfter()
    {
        _handler.Enqueue(Reply(HttpStatusCode.TooManyRequests, "{}", TimeSpan.FromHours(1)));
        _handler.Enqueue(Reply(HttpStatusCode.Created, AddedJson));

        await Run(() => _client.AddToHistoryAsync(SimklPayload.BuildAdd([Marks.Movie("tt1")]), ClientId, User, Token));

        var requests = _handler.Requests;
        Assert.InRange(requests[1].At - requests[0].At, SimklClient.MaxRetryDelay, SimklClient.MaxRetryDelay + TimeSpan.FromSeconds(0.5));
    }

    /// <summary>
    /// Server errors back off 2 s, then 4 s, and the call gives up after three attempts.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task BacksOffOnServerErrorsThenGivesUp()
    {
        for (var i = 0; i < SimklClient.MaxAttempts; i++)
        {
            _handler.Enqueue(Reply(HttpStatusCode.InternalServerError, """{"error":"internal","code":500}"""));
        }

        var result = await Run(() => _client.AddToHistoryAsync(SimklPayload.BuildAdd([Marks.Movie("tt1")]), ClientId, User, Token));

        Assert.Null(result);
        var requests = _handler.Requests;
        Assert.Equal(SimklClient.MaxAttempts, requests.Count);
        Assert.InRange(requests[1].At - requests[0].At, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2.5));
        Assert.InRange(requests[2].At - requests[1].At, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4.5));
        Assert.Contains("failed 3 times", Assert.Single(_logger.At(LogLevel.Error)), StringComparison.Ordinal);
    }

    /// <summary>
    /// A network failure is retried, and its message is logged.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RetriesANetworkFailure()
    {
        _handler.Enqueue(() => throw new HttpRequestException("connection reset by peer"));
        _handler.Enqueue(Reply(HttpStatusCode.Created, AddedJson));

        var result = await Run(() => _client.AddToHistoryAsync(SimklPayload.BuildAdd([Marks.Movie("tt1")]), ClientId, User, Token));

        Assert.NotNull(result);
        Assert.Equal(2, _handler.Requests.Count);
        Assert.Contains("connection reset by peer", Assert.Single(_logger.At(LogLevel.Warning)), StringComparison.Ordinal);
    }

    /// <summary>
    /// A timeout is retried.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RetriesATimeout()
    {
        _handler.Enqueue(() => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));
        _handler.Enqueue(Reply(HttpStatusCode.Created, AddedJson));

        var result = await Run(() => _client.AddToHistoryAsync(SimklPayload.BuildAdd([Marks.Movie("tt1")]), ClientId, User, Token));

        Assert.NotNull(result);
        Assert.Contains("timed out", Assert.Single(_logger.At(LogLevel.Warning)), StringComparison.Ordinal);
    }

    /// <summary>
    /// A call the caller cancels is not retried.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task DoesNotRetryACancelledCall()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _client.AddToHistoryAsync(SimklPayload.BuildAdd([Marks.Movie("tt1")]), ClientId, User, cancelled.Token));

        Assert.Empty(_logger.Entries);
    }

    /// <summary>
    /// A rejected login is not retried, and the log says how to fix it.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task DoesNotRetryARejectedLogin()
    {
        _handler.Enqueue(Reply(HttpStatusCode.Unauthorized, """{"error":"user_token_failed"}"""));

        var result = await Run(() => _client.AddToHistoryAsync(SimklPayload.BuildAdd([Marks.Movie("tt1")]), ClientId, User, Token));

        Assert.Null(result);
        Assert.Single(_handler.Requests);
        Assert.Contains("link Simkl again", Assert.Single(_logger.At(LogLevel.Error)), StringComparison.Ordinal);
    }

    /// <summary>
    /// A rejected or throttled client ID is not retried; retrying would extend Simkl's block.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task DoesNotRetryARejectedClientId()
    {
        _handler.Enqueue(Reply(HttpStatusCode.PreconditionFailed, """{"error":"client_id_failed","code":412}"""));

        Assert.Null(await Run(() => _client.AddToHistoryAsync(SimklPayload.BuildAdd([Marks.Movie("tt1")]), ClientId, User, Token)));
        Assert.Single(_handler.Requests);
        Assert.Contains("HTTP 412", Assert.Single(_logger.At(LogLevel.Error)), StringComparison.Ordinal);
    }

    /// <summary>
    /// Any other client error is not retried.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task DoesNotRetryABadRequest()
    {
        _handler.Enqueue(Reply(HttpStatusCode.BadRequest, """{"error":"wrong_parameter","code":400}"""));

        Assert.Null(await Run(() => _client.AddToHistoryAsync(SimklPayload.BuildAdd([Marks.Movie("tt1")]), ClientId, User, Token)));
        Assert.Single(_handler.Requests);
        Assert.Contains("HTTP 400", Assert.Single(_logger.At(LogLevel.Error)), StringComparison.Ordinal);
    }

    /// <summary>
    /// A web page instead of JSON (a proxy's challenge or error page) is not parsed and not retried.
    /// </summary>
    /// <param name="status">The HTTP status the page came with.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.OK)]
    public async Task DoesNotRetryAWebPage(HttpStatusCode status)
    {
        _handler.Enqueue(Reply(status, "  <!DOCTYPE html><title>Just a moment...</title>"));

        Assert.Null(await Run(() => _client.AddToHistoryAsync(SimklPayload.BuildAdd([Marks.Movie("tt1")]), ClientId, User, Token)));
        Assert.Single(_handler.Requests);
        Assert.Contains("web page", Assert.Single(_logger.At(LogLevel.Error)), StringComparison.Ordinal);
    }

    /// <summary>
    /// Invalid JSON fails the call with a logged reason.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task FailsOnInvalidJson()
    {
        _handler.Enqueue(Reply(HttpStatusCode.Created, "not json"));

        Assert.Null(await Run(() => _client.AddToHistoryAsync(SimklPayload.BuildAdd([Marks.Movie("tt1")]), ClientId, User, Token)));
        Assert.Contains("invalid JSON", Assert.Single(_logger.At(LogLevel.Error)), StringComparison.Ordinal);
    }

    /// <summary>
    /// JSON that is not an object fails a write.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task FailsOnAWriteAnswerThatIsNotAnObject()
    {
        _handler.Enqueue(Reply(HttpStatusCode.Created, "[]"));

        Assert.Null(await Run(() => _client.AddToHistoryAsync(SimklPayload.BuildAdd([Marks.Movie("tt1")]), ClientId, User, Token)));
        Assert.Contains("unexpected JSON", Assert.Single(_logger.At(LogLevel.Error)), StringComparison.Ordinal);
    }

    /// <summary>
    /// Two calls in a row are spaced at least the minimum POST interval apart.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SpacesPostsApart()
    {
        _handler.Enqueue(Reply(HttpStatusCode.Created, AddedJson));
        _handler.Enqueue(Reply(HttpStatusCode.Created, AddedJson));

        await Run(() => _client.AddToHistoryAsync(SimklPayload.BuildAdd([Marks.Movie("tt1")]), ClientId, User, Token));
        await Run(() => _client.AddToHistoryAsync(SimklPayload.BuildAdd([Marks.Movie("tt2")]), ClientId, User, Token));

        var requests = _handler.Requests;
        Assert.InRange(requests[1].At - requests[0].At, SimklClient.MinPostInterval, SimklClient.MinPostInterval + TimeSpan.FromSeconds(0.5));
    }

    /// <summary>
    /// A call long after the last one is not delayed.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task DoesNotDelayACallLongAfterTheLast()
    {
        _handler.Enqueue(Reply(HttpStatusCode.Created, AddedJson));
        _handler.Enqueue(Reply(HttpStatusCode.Created, AddedJson));

        await Run(() => _client.AddToHistoryAsync(SimklPayload.BuildAdd([Marks.Movie("tt1")]), ClientId, User, Token));
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _client.AddToHistoryAsync(SimklPayload.BuildAdd([Marks.Movie("tt2")]), ClientId, User, Token);

        Assert.Equal(2, _handler.Requests.Count);
    }

    /// <summary>
    /// The lookup reads each item's match, watched state and list.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ReadsWatchedStatuses()
    {
        _handler.Enqueue(Reply(
            HttpStatusCode.OK,
            """[{"result":true,"list":"completed"},{"result":false,"list":null},{"result":"not_found"},42]"""));
        var lookup = SimklPayload.BuildMovieLookup([Marks.Movie("tt1"), Marks.Movie("tt2"), Marks.Movie("tt3"), Marks.Movie("tt4")]);

        var statuses = await Run(() => _client.GetWatchedAsync(lookup, ClientId, User, Token));

        Assert.Equal(
            [new WatchedStatus(true, true, "completed"), new WatchedStatus(true, false, null), new WatchedStatus(false, false, null), new WatchedStatus(false, false, null)],
            statuses!);
        Assert.Equal("/sync/watched", Assert.Single(_handler.Requests).Uri.AbsolutePath);
    }

    /// <summary>
    /// Simkl answers an empty lookup with a literal null, which means no items.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ReadsANullLookupAnswerAsNoItems()
    {
        _handler.Enqueue(Reply(HttpStatusCode.OK, "null"));

        Assert.Empty((await Run(() => _client.GetWatchedAsync([], ClientId, User, Token)))!);
    }

    /// <summary>
    /// A lookup answer that is not an array fails the lookup.
    /// </summary>
    /// <param name="body">The answer.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData("{}")]
    [InlineData("[{")]
    public async Task FailsOnALookupAnswerThatIsNotAnArray(string body)
    {
        _handler.Enqueue(Reply(HttpStatusCode.OK, body));

        Assert.Null(await Run(() => _client.GetWatchedAsync(SimklPayload.BuildMovieLookup([Marks.Movie("tt1")]), ClientId, User, Token)));
        Assert.Single(_logger.At(LogLevel.Error));
    }

    /// <summary>
    /// A lookup over Simkl's limit is refused without a call.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RefusesALookupOverTheLimit()
    {
        var lookup = SimklPayload.BuildMovieLookup(Enumerable.Range(0, SimklClient.MaxLookupItems + 1).Select(i => Marks.Movie($"tt{i}")).ToList());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _client.GetWatchedAsync(lookup, ClientId, User, Token));

        Assert.Empty(_handler.Requests);
    }

    /// <summary>
    /// A long answer is cut short in the log.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task CutsALongAnswerShortInTheLog()
    {
        _handler.Enqueue(Reply(HttpStatusCode.BadRequest, new string('x', 5000)));

        await Run(() => _client.AddToHistoryAsync(SimklPayload.BuildAdd([Marks.Movie("tt1")]), ClientId, User, Token));

        var error = Assert.Single(_logger.At(LogLevel.Error));
        Assert.EndsWith(new string('x', 512) + "…", error, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 513), error, StringComparison.Ordinal);
    }

    private static Func<HttpResponseMessage> Reply(HttpStatusCode status, string body, TimeSpan? retryAfter = null) => () =>
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (retryAfter is TimeSpan delay)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        }

        return response;
    };

    // Moves the fake clock until the call finishes, so retry and spacing delays elapse.
    private async Task<T> Run<T>(Func<Task<T>> call)
    {
        var task = call();
        while (!task.IsCompleted)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(1, Token).ConfigureAwait(false);
        }

        return await task.ConfigureAwait(false);
    }
}
