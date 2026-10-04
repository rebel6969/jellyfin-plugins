using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SimklMarkSync.Simkl;

/// <summary>
/// The Simkl API calls this plugin makes.
/// </summary>
public interface ISimklClient
{
    /// <summary>
    /// Marks items watched with <c>POST /sync/history</c>.
    /// </summary>
    /// <param name="request">The items.</param>
    /// <param name="clientId">The client ID.</param>
    /// <param name="user">The user's login.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What Simkl added, or <c>null</c> when the call failed; the failure is already logged.</returns>
    Task<SimklWriteResult?> AddToHistoryAsync(HistoryRequest request, string clientId, SimklUser user, CancellationToken cancellationToken);

    /// <summary>
    /// Unmarks items with <c>POST /sync/history/remove</c>.
    /// </summary>
    /// <param name="request">The items; checked again so no show is removed whole.</param>
    /// <param name="clientId">The client ID.</param>
    /// <param name="user">The user's login.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What Simkl removed, or <c>null</c> when the call failed; the failure is already logged.</returns>
    Task<SimklWriteResult?> RemoveFromHistoryAsync(HistoryRequest request, string clientId, SimklUser user, CancellationToken cancellationToken);

    /// <summary>
    /// Asks Simkl whether each item is in the user's library, with <c>POST /sync/watched</c>.
    /// </summary>
    /// <param name="items">The items, at most 100.</param>
    /// <param name="clientId">The client ID.</param>
    /// <param name="user">The user's login.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>One status per item in the same order, or <c>null</c> when the call failed; the failure is already logged.</returns>
    Task<IReadOnlyList<WatchedStatus>?> GetWatchedAsync(IReadOnlyList<WatchedLookupItem> items, string clientId, SimklUser user, CancellationToken cancellationToken);
}
