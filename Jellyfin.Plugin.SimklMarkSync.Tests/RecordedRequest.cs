using System;
using System.Net.Http;

namespace Jellyfin.Plugin.SimklMarkSync.Tests;

/// <summary>
/// A request the <see cref="FakeHttpHandler"/> received.
/// </summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Uri">The request URI.</param>
/// <param name="Headers">The request headers, one per line.</param>
/// <param name="ContentType">The body's content type.</param>
/// <param name="Body">The body.</param>
/// <param name="At">When it arrived, by the test clock.</param>
public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Headers, string? ContentType, string? Body, DateTimeOffset At);
