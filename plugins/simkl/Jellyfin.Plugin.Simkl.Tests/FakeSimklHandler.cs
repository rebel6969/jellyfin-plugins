using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Simkl.Tests;

/// <summary>
/// Stands in for api.simkl.com: records every scrobble call and answers the way Simkl does.
/// </summary>
internal sealed class FakeSimklHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<ScrobbleCall> _calls = new();
    private readonly ConcurrentDictionary<string, string> _errors = new(StringComparer.Ordinal);

    public IReadOnlyList<ScrobbleCall> Calls => _calls.ToArray();

    /// <summary>
    /// Makes every later call to <paramref name="endpoint"/> answer with a Simkl error body.
    /// </summary>
    public void FailWith(string endpoint, string error) => _errors[endpoint] = error;

    public void Succeed(string endpoint) => _errors.TryRemove(endpoint, out _);

    public int Count(string endpoint) => Calls.Count(c => c.Endpoint == endpoint);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var endpoint = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var progress = JsonDocument.Parse(body).RootElement.TryGetProperty("progress", out var p) ? p.GetSingle() : -1f;
        _calls.Enqueue(new ScrobbleCall(endpoint, progress));

        string answer;
        if (_errors.TryGetValue(endpoint, out var error))
        {
            answer = JsonSerializer.Serialize(new { error });
        }
        else
        {
            // Simkl turns a stop below 80% into a pause; at or above it records the watch.
            var action = endpoint switch
            {
                "/scrobble/start" => "start",
                "/scrobble/pause" => "pause",
                "/scrobble/stop" => progress >= 80f ? "scrobble" : "pause",
                _ => "unknown",
            };
            answer = JsonSerializer.Serialize(new { action, progress });
        }

        return new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(answer, Encoding.UTF8, "application/json"),
        };
    }
}

/// <summary>
/// One call the plugin made to Simkl.
/// </summary>
/// <param name="Endpoint">The path called, for example <c>/scrobble/stop</c>.</param>
/// <param name="Progress">The progress percentage sent.</param>
internal sealed record ScrobbleCall(string Endpoint, float Progress);
