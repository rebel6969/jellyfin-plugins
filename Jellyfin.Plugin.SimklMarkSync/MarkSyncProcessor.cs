using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SimklMarkSync.Simkl;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SimklMarkSync;

/// <summary>
/// Sends one batch of marks to Simkl: one add and one remove per user at most.
/// </summary>
public sealed class MarkSyncProcessor
{
    private readonly ISimklCredentials _credentials;
    private readonly ISimklClient _client;
    private readonly ILogger<MarkSyncProcessor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MarkSyncProcessor"/> class.
    /// </summary>
    /// <param name="credentials">The Simkl credentials.</param>
    /// <param name="client">The Simkl client.</param>
    /// <param name="logger">The logger.</param>
    public MarkSyncProcessor(ISimklCredentials credentials, ISimklClient client, ILogger<MarkSyncProcessor> logger)
    {
        _credentials = credentials;
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Logs whether the official Simkl plugin's client ID was found, so a missing or changed official plugin shows in
    /// the log at startup rather than at the first mark.
    /// </summary>
    public void LogReadiness()
    {
        if (_credentials.GetClientId() is not null)
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

            var clientId = _credentials.GetClientId();
            if (clientId is null)
            {
                return;
            }

            // The last mark of an item wins, so marking and then unmarking within one batch sends only the unmark.
            var latest = userMarks
                .GroupBy(m => m.ItemId)
                .Select(item => item.Last())
                .Where(m => m is MovieMark ? user.SyncMovies : user.SyncShows)
                .ToList();
            var played = latest.Where(m => m.Played).ToList();
            var unplayed = latest.Where(m => !m.Played).ToList();
            if (played.Count > 0)
            {
                await AddAsync(userMarks.Key, played, clientId, user, cancellationToken).ConfigureAwait(false);
            }

            if (unplayed.Count > 0)
            {
                await RemoveAsync(userMarks.Key, unplayed, clientId, user, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task AddAsync(Guid userId, List<MarkChange> played, string clientId, SimklUser user, CancellationToken cancellationToken)
    {
        var result = await _client.AddToHistoryAsync(SimklPayload.BuildAdd(played), clientId, user, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return;
        }

        _logger.LogInformation(
            "Simkl: {Count} items marked watched for user {UserId}; newly added {Movies} movies and {Episodes} episodes, the rest were already watched",
            played.Count,
            userId,
            result.Movies,
            result.Episodes);
        LogNotFound(result, "marked watched");
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
        LogNotFound(result, "unmarked");
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

    private void LogNotFound(SimklWriteResult result, string action)
    {
        foreach (var item in result.NotFound)
        {
            _logger.LogWarning("Simkl could not match {Item}; it was not {Action}", item, action);
        }
    }
}
