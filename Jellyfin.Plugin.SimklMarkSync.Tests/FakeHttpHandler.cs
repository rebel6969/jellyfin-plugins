using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SimklMarkSync.Tests;

/// <summary>
/// Answers HTTP requests from a queue of replies and records each request as it arrived.
/// </summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<HttpResponseMessage>> _replies = new();
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="FakeHttpHandler"/> class.
    /// </summary>
    /// <param name="time">The clock stamped on each recorded request.</param>
    public FakeHttpHandler(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>
    /// Gets the requests received so far.
    /// </summary>
    public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

    /// <summary>
    /// Queues the next reply.
    /// </summary>
    /// <param name="reply">Builds the reply, or throws to simulate a network failure.</param>
    public void Enqueue(Func<HttpResponseMessage> reply) => _replies.Enqueue(reply);

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // As a real handler does: a cancelled request never reaches the network.
        cancellationToken.ThrowIfCancellationRequested();
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        _requests.Enqueue(new RecordedRequest(request.Method, request.RequestUri!, request.Headers.ToString(), request.Content?.Headers.ContentType?.ToString(), body, _time.GetUtcNow()));
        if (!_replies.TryDequeue(out var reply))
        {
            throw new InvalidOperationException("The test queued no reply for this request.");
        }

        return reply();
    }
}
