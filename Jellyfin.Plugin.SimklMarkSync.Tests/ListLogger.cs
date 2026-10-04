using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SimklMarkSync.Tests;

/// <summary>
/// A logger that keeps every message, safe to write from a background worker while a test reads.
/// </summary>
/// <typeparam name="T">The category type.</typeparam>
public sealed class ListLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    /// <summary>
    /// Gets a snapshot of the messages logged so far.
    /// </summary>
    public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

    /// <summary>
    /// Gets the messages logged at a level.
    /// </summary>
    /// <param name="level">The level.</param>
    /// <returns>The messages.</returns>
    public IReadOnlyList<string> At(LogLevel level) => Entries.Where(e => e.Level == level).Select(e => e.Message).ToList();

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception)));
    }
}
