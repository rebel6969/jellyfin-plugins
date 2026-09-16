using System.Collections.Concurrent;
using System.Globalization;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EnglishSdh;

/// <summary>One bounded subtitle command per playback. Never changes media or audio.</summary>
public sealed class PlaybackHandler : IEventConsumer<PlaybackStartEventArgs>, IEventConsumer<PlaybackStopEventArgs>
{
    private sealed record Pending(string PlayId, Guid ItemId, string SourceId, Guid UserId, int? SubtitleIndex);
    private readonly ConcurrentDictionary<string, Pending> _plays = new();
    private readonly ISessionManager _sessions;
    private readonly IMediaSourceManager _sources;
    private readonly ILogger<PlaybackHandler> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly Func<PluginConfiguration?> _configuration;
    private readonly TimeSpan _settleDelay;

    public PlaybackHandler(ISessionManager sessions, IMediaSourceManager sources,
        ILogger<PlaybackHandler> logger, IHostApplicationLifetime lifetime,
        Func<PluginConfiguration?> configuration, TimeSpan? settleDelay = null)
    {
        _sessions = sessions; _sources = sources; _logger = logger;
        _lifetime = lifetime; _configuration = configuration;
        _settleDelay = settleDelay ?? TimeSpan.FromMilliseconds(1500);
    }

    private bool EnabledFor(Guid userId)
    {
        var config = _configuration();
        return userId != Guid.Empty && config?.Enabled == true
            && (config.UserIds ?? []).Any(s => Guid.TryParse(s, out var id) && id == userId);
    }

    public async Task OnEvent(PlaybackStartEventArgs args)
    {
        var session = args.Session;
        if (args.Item is null || session is null || !EnabledFor(session.UserId)) return;
        var sessionId = session.Id;
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        var source = args.MediaSourceId ?? "";
        var pending = new Pending(args.PlaySessionId ?? "", args.Item.Id, source,
            session.UserId, session.PlayState?.SubtitleStreamIndex);

        // Prune disconnected sessions; no unbounded history or background worker.
        var activeIds = _sessions.Sessions.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _plays.Keys)
            if (!activeIds.Contains(id)) _plays.TryRemove(id, out _);
        while (true)
        {
            if (_plays.TryGetValue(sessionId, out var previous))
            {
                if (previous.PlayId == pending.PlayId && previous.ItemId == pending.ItemId
                    && previous.SourceId == pending.SourceId && previous.UserId == pending.UserId) return;
                if (_plays.TryUpdate(sessionId, pending, previous)) break;
            }
            else if (_plays.TryAdd(sessionId, pending)) break;
        }

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await Task.Delay(_settleDelay, deadline.Token).ConfigureAwait(false);
            if (!_plays.TryGetValue(sessionId, out var current) || !ReferenceEquals(current, pending)) return;
            session = _sessions.Sessions.FirstOrDefault(s => s.Id == sessionId);
            if (session is null || !EnabledFor(pending.UserId) || session.UserId != pending.UserId
                || session.NowPlayingItem?.Id != pending.ItemId
                || !string.Equals(session.PlayState?.MediaSourceId ?? "", pending.SourceId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("EnglishSdh skipped stale playback in session {SessionId}", sessionId);
                return;
            }
            if (session.PlayState?.SubtitleStreamIndex != pending.SubtitleIndex)
            {
                _logger.LogDebug("EnglishSdh preserved intervening subtitle selection in session {SessionId}", sessionId);
                return;
            }
            if (!session.SupportedCommands.Contains(GeneralCommandType.SetSubtitleStreamIndex))
            {
                _logger.LogWarning("EnglishSdh skipped client {Client}: SetSubtitleStreamIndex not advertised", session.Client);
                return;
            }

            // Resolve exactly the reported source; do not guess indices from another version.
            if (!Guid.TryParse(source, out var sourceId))
            {
                _logger.LogDebug("EnglishSdh skipped non-file media source for item {ItemId}", pending.ItemId);
                return;
            }
            var streams = _sources.GetMediaStreams(sourceId);
            var selected = SubtitleSelector.Select(streams);
            if (!selected.HasValue || session.PlayState?.SubtitleStreamIndex == selected.Value) return;
            // Recheck after stream lookup and immediately before sending.
            if (!_plays.TryGetValue(sessionId, out current) || !ReferenceEquals(current, pending)
                || session.NowPlayingItem?.Id != pending.ItemId || session.UserId != pending.UserId
                || !string.Equals(session.PlayState?.MediaSourceId ?? "", pending.SourceId, StringComparison.OrdinalIgnoreCase)
                || session.PlayState?.SubtitleStreamIndex != pending.SubtitleIndex
                || !EnabledFor(pending.UserId)) return;
            var command = new GeneralCommand
            {
                Name = GeneralCommandType.SetSubtitleStreamIndex,
                Arguments = { ["Index"] = selected.Value.ToString(CultureInfo.InvariantCulture) }
            };
            await _sessions.SendGeneralCommand(string.Empty, sessionId, command, deadline.Token).ConfigureAwait(false);
            _logger.LogInformation("EnglishSdh sent subtitle index {Index} ({Kind}) for item {ItemId} to {Client}; client application is not yet confirmed",
                selected.Value, SubtitleSelector.IsSdh(streams.First(s => s.Index == selected.Value)) ? "SDH" : "English fallback",
                pending.ItemId, session.Client);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("EnglishSdh command canceled or timed out for session {SessionId}", sessionId);
        }
        catch (Exception ex)
        {
            // A plugin fault must not stop playback. Persist the failure in Jellyfin's log.
            _logger.LogError(ex, "EnglishSdh could not select subtitles for session {SessionId}", sessionId);
        }
    }

    public Task OnEvent(PlaybackStopEventArgs args)
    {
        if (args.Session is not null && _plays.TryGetValue(args.Session.Id, out var pending)
            && pending.PlayId == (args.PlaySessionId ?? "") && pending.ItemId == args.Item?.Id)
            ((ICollection<KeyValuePair<string, Pending>>)_plays).Remove(new(args.Session.Id, pending));
        return Task.CompletedTask;
    }
}
