using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.MaxQuality;

/// <summary>
/// Feeds playback start, progress and stop reports to the <see cref="PlaybackRoundTracker"/>.
/// </summary>
public sealed class PlaybackActivityListener : IHostedService
{
    private readonly ISessionManager _sessionManager;
    private readonly PlaybackRoundTracker _tracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackActivityListener"/> class.
    /// </summary>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="tracker">The playback round tracker.</param>
    public PlaybackActivityListener(ISessionManager sessionManager, PlaybackRoundTracker tracker)
    {
        _sessionManager = sessionManager;
        _tracker = tracker;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnPlaybackActivity;
        _sessionManager.PlaybackProgress += OnPlaybackActivity;
        _sessionManager.PlaybackStopped += OnPlaybackActivity;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackActivity;
        _sessionManager.PlaybackProgress -= OnPlaybackActivity;
        _sessionManager.PlaybackStopped -= OnPlaybackActivity;
        return Task.CompletedTask;
    }

    private void OnPlaybackActivity(object? sender, PlaybackProgressEventArgs e)
    {
        if (e.Item is Video && !string.IsNullOrEmpty(e.DeviceId))
        {
            _tracker.RecordActivity(e.DeviceId, e.Item.Id);
        }
    }
}
