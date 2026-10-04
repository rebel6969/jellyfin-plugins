using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SimklMarkSync.Tests;

/// <summary>
/// One message a <see cref="ListLogger{T}"/> received.
/// </summary>
/// <param name="Level">The log level.</param>
/// <param name="Message">The formatted message.</param>
public sealed record LogEntry(LogLevel Level, string Message);
