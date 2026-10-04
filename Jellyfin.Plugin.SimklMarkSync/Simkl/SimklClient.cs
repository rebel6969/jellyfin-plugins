using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SimklMarkSync.Simkl;

/// <summary>
/// Calls the Simkl API within its published limits.
/// </summary>
/// <remarks>
/// Simkl allows one POST per second per client ID and per token, and treats bursts as abuse. Every call here is a
/// POST, one at a time from the single <see cref="MarkSyncService"/> worker, at least <see cref="MinPostInterval"/>
/// apart. All three calls are safe to repeat: Simkl ignores an add for an item already watched, and a repeated remove
/// changes nothing. So a timeout, a 429 or a 5xx is retried; any other rejection is not.
/// </remarks>
public sealed class SimklClient : ISimklClient
{
    /// <summary>
    /// The <c>app-name</c> sent with every call.
    /// </summary>
    public const string AppName = "jellyfin-simkl-mark-sync";

    /// <summary>
    /// The most times one call is tried.
    /// </summary>
    public const int MaxAttempts = 3;

    /// <summary>
    /// The most items one <c>/sync/watched</c> call may carry.
    /// </summary>
    public const int MaxLookupItems = 100;

    /// <summary>
    /// The least time between two POSTs.
    /// </summary>
    public static readonly TimeSpan MinPostInterval = TimeSpan.FromSeconds(1.1);

    /// <summary>
    /// The longest a <c>Retry-After</c> header may make a retry wait.
    /// </summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);

    private const string BaseUrl = "https://api.simkl.com";
    private const int MaxLoggedBodyLength = 512;

    private static readonly string _appVersion = typeof(SimklClient).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<SimklClient> _logger;
    private long? _lastPostTimestamp;

    /// <summary>
    /// Initializes a new instance of the <see cref="SimklClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Jellyfin's HTTP client factory; its default client decompresses gzip, deflate and brotli.</param>
    /// <param name="time">The time provider.</param>
    /// <param name="logger">The logger.</param>
    public SimklClient(IHttpClientFactory httpClientFactory, TimeProvider time, ILogger<SimklClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SimklWriteResult?> AddToHistoryAsync(HistoryRequest request, string clientId, SimklUser user, CancellationToken cancellationToken)
    {
        const string Path = "/sync/history";
        var body = await PostAsync(Path, request, clientId, user, cancellationToken).ConfigureAwait(false);
        return body is null ? null : ParseWriteResult(Path, body, "added");
    }

    /// <inheritdoc />
    public async Task<SimklWriteResult?> RemoveFromHistoryAsync(HistoryRequest request, string clientId, SimklUser user, CancellationToken cancellationToken)
    {
        const string Path = "/sync/history/remove";
        SimklPayload.EnsureNoWholeShowRemoval(request);
        var body = await PostAsync(Path, request, clientId, user, cancellationToken).ConfigureAwait(false);
        return body is null ? null : ParseWriteResult(Path, body, "deleted");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WatchedStatus>?> GetWatchedAsync(IReadOnlyList<WatchedLookupItem> items, string clientId, SimklUser user, CancellationToken cancellationToken)
    {
        const string Path = "/sync/watched";
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(items.Count, MaxLookupItems);
        var body = await PostAsync(Path, items, clientId, user, cancellationToken).ConfigureAwait(false);
        return body is null ? null : ParseWatched(Path, body);
    }

    private static string Truncate(string text) =>
        text.Length <= MaxLoggedBodyLength ? text : string.Concat(text.AsSpan(0, MaxLoggedBodyLength), "…");

    private static string Describe(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty("title", out var title)
            && title.ValueKind == JsonValueKind.String)
        {
            return Truncate($"{title.GetString()} {(item.TryGetProperty("ids", out var ids) ? ids.GetRawText() : string.Empty)}".Trim());
        }

        return Truncate(item.GetRawText());
    }

    private static int ReadCount(JsonElement counts, string name) =>
        counts.ValueKind == JsonValueKind.Object
        && counts.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var count)
            ? count
            : 0;

    private async Task<string?> PostAsync<T>(string path, T body, string clientId, SimklUser user, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(body, SimklPayload.JsonOptions);
        var uri = new Uri($"{BaseUrl}{path}?client_id={Uri.EscapeDataString(clientId)}&app-name={AppName}&app-version={_appVersion}");
        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        for (var attempt = 1; ; attempt++)
        {
            await WaitForPostSlotAsync(cancellationToken).ConfigureAwait(false);
            string failure;
            TimeSpan? retryAfter = null;
            using (var request = new HttpRequestMessage(HttpMethod.Post, uri))
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                request.Headers.UserAgent.ParseAdd($"{AppName}/{_appVersion}");
                request.Headers.Accept.ParseAdd("application/json");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
                try
                {
                    using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    _lastPostTimestamp = _time.GetTimestamp();
                    var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    var status = (int)response.StatusCode;
                    if (text.AsSpan().TrimStart().StartsWith("<", StringComparison.Ordinal))
                    {
                        // A challenge or error page from a proxy in front of Simkl; retrying would only repeat it.
                        _logger.LogError("Simkl {Path} answered HTTP {Status} with a web page, not JSON: {Body}", path, status, Truncate(text));
                        return null;
                    }

                    if (response.IsSuccessStatusCode)
                    {
                        return text;
                    }

                    if (status != 429 && status < 500)
                    {
                        LogRejection(path, status, text);
                        return null;
                    }

                    failure = $"HTTP {status}";
                    retryAfter = GetRetryAfter(response);
                }
                catch (HttpRequestException ex)
                {
                    _lastPostTimestamp = _time.GetTimestamp();
                    failure = ex.Message;
                }
                catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _lastPostTimestamp = _time.GetTimestamp();
                    failure = $"timed out: {ex.Message}";
                }
            }

            if (attempt >= MaxAttempts)
            {
                _logger.LogError("Simkl {Path} failed {Attempts} times, giving up: {Failure}", path, attempt, failure);
                return null;
            }

            // 2 s, then 4 s, unless Simkl says how long to wait.
            var delay = retryAfter is { } wait && wait > TimeSpan.Zero
                ? (wait < MaxRetryDelay ? wait : MaxRetryDelay)
                : TimeSpan.FromSeconds(1 << attempt);
            _logger.LogWarning(
                "Simkl {Path} attempt {Attempt} of {MaxAttempts} failed ({Failure}); retrying in {Delay}",
                path,
                attempt,
                MaxAttempts,
                failure,
                delay);
            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WaitForPostSlotAsync(CancellationToken cancellationToken)
    {
        if (_lastPostTimestamp is not long last)
        {
            return;
        }

        var wait = MinPostInterval - _time.GetElapsedTime(last);
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is TimeSpan delta)
        {
            return delta;
        }

        return retryAfter?.Date is DateTimeOffset date ? date - _time.GetUtcNow() : null;
    }

    private void LogRejection(string path, int status, string body)
    {
        switch (status)
        {
            case 401:
                _logger.LogError("Simkl {Path} rejected the user's login (HTTP 401): link Simkl again in the Simkl plugin's settings. {Body}", path, Truncate(body));
                break;
            case 412:
                _logger.LogError("Simkl {Path} rejected the client ID or is throttling it (HTTP 412): {Body}", path, Truncate(body));
                break;
            default:
                _logger.LogError("Simkl {Path} rejected the request (HTTP {Status}): {Body}", path, status, Truncate(body));
                break;
        }
    }

    private SimklWriteResult? ParseWriteResult(string path, string body, string countsName)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                _logger.LogError("Simkl {Path} answered with unexpected JSON: {Body}", path, Truncate(body));
                return null;
            }

            var counts = root.TryGetProperty(countsName, out var found) ? found : default;
            var notFound = new List<string>();
            if (root.TryGetProperty("not_found", out var missing) && missing.ValueKind == JsonValueKind.Object)
            {
                foreach (var bucket in missing.EnumerateObject())
                {
                    if (bucket.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in bucket.Value.EnumerateArray())
                        {
                            notFound.Add(Describe(item));
                        }
                    }
                }
            }

            return new SimklWriteResult(ReadCount(counts, "movies"), ReadCount(counts, "shows"), ReadCount(counts, "episodes"), notFound);
        }
        catch (JsonException ex)
        {
            _logger.LogError("Simkl {Path} answered with invalid JSON ({Message}): {Body}", path, ex.Message, Truncate(body));
            return null;
        }
    }

    private List<WatchedStatus>? ParseWatched(string path, string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Null)
            {
                // Simkl answers an empty lookup with a literal null.
                return [];
            }

            if (root.ValueKind != JsonValueKind.Array)
            {
                _logger.LogError("Simkl {Path} answered with unexpected JSON: {Body}", path, Truncate(body));
                return null;
            }

            var statuses = new List<WatchedStatus>(root.GetArrayLength());
            foreach (var item in root.EnumerateArray())
            {
                var result = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("result", out var value) ? value.ValueKind : JsonValueKind.Undefined;
                var list = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("list", out var listValue) && listValue.ValueKind == JsonValueKind.String
                    ? listValue.GetString()
                    : null;
                statuses.Add(new WatchedStatus(
                    Matched: result is JsonValueKind.True or JsonValueKind.False,
                    Watched: result == JsonValueKind.True,
                    List: list));
            }

            return statuses;
        }
        catch (JsonException ex)
        {
            _logger.LogError("Simkl {Path} answered with invalid JSON ({Message}): {Body}", path, ex.Message, Truncate(body));
            return null;
        }
    }
}
