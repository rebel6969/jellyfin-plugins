using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SimklMarkSync.Simkl;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SimklMarkSync;

/// <summary>
/// Sends one batch of marks to Simkl: per user, one add (two when rewatches are recorded) and one remove at most.
/// </summary>
/// <remarks>
/// Only the single <see cref="MarkSyncService"/> worker calls <see cref="ProcessAsync"/>, so the plan cache needs no
/// lock.
/// </remarks>
public sealed class MarkSyncProcessor
{
    /// <summary>
    /// How long a user's Simkl plan is trusted before it is read again.
    /// </summary>
    public static readonly TimeSpan PlanCacheTime = TimeSpan.FromHours(6);

    private readonly ISimklCredentials _credentials;
    private readonly ISimklClient _client;
    private readonly IMarkSyncSettings _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<MarkSyncProcessor> _logger;
    private readonly AnimeEpisodeResolver _resolver;
    private readonly Dictionary<Guid, (string Token, bool Paid, long ReadAt)> _plans = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="MarkSyncProcessor"/> class.
    /// </summary>
    /// <param name="credentials">The Simkl credentials.</param>
    /// <param name="client">The Simkl client.</param>
    /// <param name="settings">The plugin settings.</param>
    /// <param name="time">The time provider.</param>
    /// <param name="logger">The logger.</param>
    public MarkSyncProcessor(ISimklCredentials credentials, ISimklClient client, IMarkSyncSettings settings, TimeProvider time, ILogger<MarkSyncProcessor> logger)
    {
        _credentials = credentials;
        _client = client;
        _settings = settings;
        _time = time;
        _logger = logger;
        _resolver = new AnimeEpisodeResolver(client);
    }

    /// <summary>
    /// Logs whether the official Simkl plugin's client ID was found, so a missing or changed official plugin shows in
    /// the log at startup rather than at the first mark.
    /// </summary>
    public void LogReadiness()
    {
        // Any token answers once the official plugin exposes its client IDs; an empty one stands for a pin flow login.
        if (_credentials.GetClientId(string.Empty) is not null)
        {
            _logger.LogInformation("Simkl Mark Sync is ready: found the official Simkl plugin's client ID");
        }
    }

    /// <summary>
    /// Sends a batch of marks.
    /// </summary>
    /// <param name="batch">The marks, oldest first.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the batch is sent or has failed; failures are logged.</returns>
    public async Task ProcessAsync(IReadOnlyList<MarkChange> batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        foreach (var userMarks in batch.GroupBy(m => m.UserId))
        {
            var user = _credentials.GetUser(userMarks.Key);
            if (user is null)
            {
                _logger.LogDebug("User {UserId} has not linked Simkl in the Simkl plugin; {Count} marks not sent", userMarks.Key, userMarks.Count());
                continue;
            }

            var clientId = _credentials.GetClientId(user.Token);
            if (clientId is null)
            {
                continue;
            }

            // The last mark of an item wins, so marking and then unmarking within one batch sends only the unmark.
            var latest = userMarks
                .GroupBy(m => m.ItemId)
                .Select(item => item.Last())
                .Where(m => m is MovieMark ? user.SyncMovies : user.SyncShows)
                .ToList();
            var played = latest.Where(m => m.Played).ToList();
            var unplayed = latest.Where(m => !m.Played).ToList();
            var finished = played.Where(m => m.FromPlayback).ToList();
            if (finished.Count > 0
                && _settings.TrackRewatches
                && await HasPaidPlanAsync(userMarks.Key, clientId, user, cancellationToken).ConfigureAwait(false))
            {
                // Only a finished playback may become a rewatch; a manual mark is sent as a plain watch.
                var manual = played.Where(m => !m.FromPlayback).ToList();
                if (manual.Count > 0)
                {
                    await AddAsync(userMarks.Key, manual, clientId, user, allowRewatch: false, cancellationToken).ConfigureAwait(false);
                }

                await AddAsync(userMarks.Key, finished, clientId, user, allowRewatch: true, cancellationToken).ConfigureAwait(false);
            }
            else if (played.Count > 0)
            {
                await AddAsync(userMarks.Key, played, clientId, user, allowRewatch: false, cancellationToken).ConfigureAwait(false);
            }

            if (unplayed.Count > 0)
            {
                await RemoveAsync(userMarks.Key, unplayed, clientId, user, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Simkl echoes a not-found show's IDs as they were sent. Values compare as text, as Simkl's own ID is a number.
    private static bool SameIds(IReadOnlyDictionary<string, string> echoed, IReadOnlyDictionary<string, object> sent) =>
        echoed.Count == sent.Count
        && sent.All(id => echoed.TryGetValue(id.Key, out var value)
            && string.Equals(value, Convert.ToString(id.Value, CultureInfo.InvariantCulture), StringComparison.Ordinal));

    // Simkl keeps rewatches only for PRO and VIP, and drops the whole write when a free account sends allow_rewatch,
    // so the plan is read first (and cached; an unreadable plan is not cached and counts as free).
    private async Task<bool> HasPaidPlanAsync(Guid userId, string clientId, SimklUser user, CancellationToken cancellationToken)
    {
        if (_plans.TryGetValue(userId, out var cached)
            && string.Equals(cached.Token, user.Token, StringComparison.Ordinal)
            && _time.GetElapsedTime(cached.ReadAt) < PlanCacheTime)
        {
            return cached.Paid;
        }

        var plan = await _client.GetAccountTypeAsync(clientId, user, cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            _logger.LogWarning("Simkl: could not read the Simkl plan of user {UserId}; finished playbacks are sent as plain watches this time", userId);
            return false;
        }

        var paid = string.Equals(plan, "pro", StringComparison.OrdinalIgnoreCase) || string.Equals(plan, "vip", StringComparison.OrdinalIgnoreCase);
        _plans[userId] = (user.Token, paid, _time.GetTimestamp());
        if (!paid)
        {
            _logger.LogInformation("Simkl: rewatch tracking is on, but user {UserId} is on the Simkl {Plan} plan; finished playbacks are sent as plain watches", userId, plan);
        }

        return paid;
    }

    private async Task AddAsync(Guid userId, List<MarkChange> played, string clientId, SimklUser user, bool allowRewatch, CancellationToken cancellationToken)
    {
        var result = await _client.AddToHistoryAsync(SimklPayload.BuildAdd(played), clientId, user, allowRewatch, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return;
        }

        var fromPlayback = played.Count(m => m.FromPlayback);
        _logger.LogInformation(
            "Simkl: {Count} items marked watched for user {UserId} ({Finished} from finished playbacks); newly added {Movies} movies and {Episodes} episodes, the rest were already watched",
            played.Count,
            userId,
            fromPlayback,
            result.Movies,
            result.Episodes);
        if (allowRewatch)
        {
            LogRewatches(userId, result.RewatchStatuses);
        }

        var handled = await PlaceUnmatchedAsync(userId, played.OfType<EpisodeMark>().ToList(), result, adding: true, allowRewatch, clientId, user, cancellationToken).ConfigureAwait(false);
        LogNotFound(result, "marked watched", handled);
    }

    private void LogRewatches(Guid userId, IReadOnlyList<string> statuses)
    {
        foreach (var group in statuses.GroupBy(s => s, StringComparer.Ordinal))
        {
            _logger.LogInformation("Simkl: rewatch status {Status} for {Count} items of user {UserId}", group.Key, group.Count(), userId);
        }

        if (statuses.Contains("pro_required", StringComparer.Ordinal))
        {
            // The plan changed since it was read; read it again on the next batch.
            _plans.Remove(userId);
        }
    }

    private async Task RemoveAsync(Guid userId, List<MarkChange> unplayed, string clientId, SimklUser user, CancellationToken cancellationToken)
    {
        var episodes = unplayed.OfType<EpisodeMark>().ToList();
        var completedMovies = await FindCompletedMoviesAsync(unplayed.OfType<MovieMark>().ToList(), clientId, user, cancellationToken).ConfigureAwait(false);
        var request = SimklPayload.BuildRemove(episodes, completedMovies);
        if (request.IsEmpty)
        {
            return;
        }

        var result = await _client.RemoveFromHistoryAsync(request, clientId, user, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return;
        }

        _logger.LogInformation(
            "Simkl: unmarked for user {UserId}: {Movies} of {RequestedMovies} movies and {Episodes} episodes removed",
            userId,
            result.Movies,
            completedMovies.Count,
            result.Episodes);
        var handled = await PlaceUnmatchedAsync(userId, episodes, result, adding: false, allowRewatch: false, clientId, user, cancellationToken).ConfigureAwait(false);
        LogNotFound(result, "unmarked", handled);
    }

    // Simkl can only unmark a movie by removing it from the library, which would also drop a "plan to watch" entry.
    // So only movies Simkl reports as completed are removed.
    private async Task<List<MovieMark>> FindCompletedMoviesAsync(List<MovieMark> movies, string clientId, SimklUser user, CancellationToken cancellationToken)
    {
        var completed = new List<MovieMark>();
        foreach (var chunk in movies.Chunk(SimklClient.MaxLookupItems))
        {
            var statuses = await _client.GetWatchedAsync(SimklPayload.BuildMovieLookup(chunk), clientId, user, cancellationToken).ConfigureAwait(false);
            if (statuses is null || statuses.Count != chunk.Length)
            {
                _logger.LogWarning("Simkl: could not confirm which of {Count} unmarked movies are completed; they stay as they are on Simkl", chunk.Length);
                continue;
            }

            for (var i = 0; i < chunk.Length; i++)
            {
                if (statuses[i].Watched && string.Equals(statuses[i].List, "completed", StringComparison.Ordinal))
                {
                    completed.Add(chunk[i]);
                }
                else
                {
                    _logger.LogInformation(
                        "Simkl: movie {Title} stays as it is on Simkl; it is not in the completed list (matched {Matched}, list {List})",
                        chunk[i].Title,
                        statuses[i].Matched,
                        statuses[i].List ?? "none");
                }
            }
        }

        return completed;
    }

    // Simkl matches an anime episode sent with TVDB numbering through the TVDB coordinate stored on each of its
    // episodes, and lists the show as not found when that coordinate is missing or wrong. Such episodes are placed in
    // Simkl's own catalogue and sent again by Simkl's own numbering. Returns the not-found descriptions handled here.
    private async Task<HashSet<string>> PlaceUnmatchedAsync(
        Guid userId,
        List<EpisodeMark> episodes,
        SimklWriteResult result,
        bool adding,
        bool allowRewatch,
        string clientId,
        SimklUser user,
        CancellationToken cancellationToken)
    {
        var handled = new HashSet<string>(StringComparer.Ordinal);
        var action = adding ? "marked watched" : "unmarked";
        var placements = new List<EpisodePlacement>();
        var placedSeasons = new HashSet<(Guid SeriesId, int Season)>();
        foreach (var show in result.NotFoundShows)
        {
            var marks = episodes.Where(m => SameIds(show.Ids, m.ShowIds)).ToList();
            if (marks.Count == 0)
            {
                continue;
            }

            handled.Add(show.Description);
            foreach (var season in marks.GroupBy(m => (m.SeriesId, m.Season)).Where(s => placedSeasons.Add(s.Key)))
            {
                placements.AddRange(await _resolver.PlaceAsync(season.ToList(), clientId, user, cancellationToken).ConfigureAwait(false));
            }
        }

        foreach (var placement in placements)
        {
            if (placement.IsPlaced)
            {
                _logger.LogInformation(
                    "Simkl: {Show} S{Season}E{Episode} is episode {SimklEpisode} of Simkl title {SimklId}, placed {How}",
                    placement.Mark.ShowTitle,
                    placement.Mark.Season,
                    placement.TvdbEpisode,
                    placement.SimklEpisode,
                    placement.SimklId,
                    placement.Detail);
            }
            else
            {
                _logger.LogWarning(
                    "Simkl could not match {Show} S{Season}E{Episode}: {Reason}; it was not {Action}",
                    placement.Mark.ShowTitle,
                    placement.Mark.Season,
                    placement.TvdbEpisode,
                    placement.Detail,
                    action);
            }
        }

        var placed = placements.Where(p => p.IsPlaced).ToList();
        if (placed.Count == 0)
        {
            return handled;
        }

        var request = SimklPayload.BuildCatalogWrite(placed, adding);
        var retry = adding
            ? await _client.AddToHistoryAsync(request, clientId, user, allowRewatch, cancellationToken).ConfigureAwait(false)
            : await _client.RemoveFromHistoryAsync(request, clientId, user, cancellationToken).ConfigureAwait(false);
        if (retry is null)
        {
            return handled;
        }

        if (adding)
        {
            _logger.LogInformation(
                "Simkl: {Count} episodes sent again by Simkl's own numbering for user {UserId}; newly added {Episodes} episodes, the rest were already watched",
                placed.Count,
                userId,
                retry.Episodes);
            if (allowRewatch)
            {
                LogRewatches(userId, retry.RewatchStatuses);
            }
        }
        else
        {
            _logger.LogInformation(
                "Simkl: {Count} episodes unmarked again by Simkl's own numbering for user {UserId}: {Episodes} episodes removed",
                placed.Count,
                userId,
                retry.Episodes);
        }

        LogNotFound(retry, action, new HashSet<string>(StringComparer.Ordinal));
        return handled;
    }

    private void LogNotFound(SimklWriteResult result, string action, IReadOnlySet<string> handled)
    {
        foreach (var item in result.NotFound.Where(item => !handled.Contains(item)))
        {
            _logger.LogWarning("Simkl could not match {Item}; it was not {Action}", item, action);
        }
    }
}
