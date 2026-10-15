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
    /// <param name="allowRewatch">
    /// Whether an item already watched may be recorded as a rewatch (<c>allow_rewatch=yes</c>). Simkl drops the whole
    /// write for a free account that sends it, so pass <c>true</c> only for a PRO or VIP account.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What Simkl added, or <c>null</c> when the call failed; the failure is already logged.</returns>
    Task<SimklWriteResult?> AddToHistoryAsync(HistoryRequest request, string clientId, SimklUser user, bool allowRewatch, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the user's Simkl plan with <c>GET /users/settings</c>.
    /// </summary>
    /// <param name="clientId">The client ID.</param>
    /// <param name="user">The user's login.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The plan (<c>free</c>, <c>pro</c> or <c>vip</c>), or <c>null</c> when the call failed; the failure is already logged.</returns>
    Task<string?> GetAccountTypeAsync(string clientId, SimklUser user, CancellationToken cancellationToken);

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

    /// <summary>
    /// Finds the Simkl anime titles filed under an external ID, with <c>GET /search/id</c>.
    /// </summary>
    /// <param name="idName">The ID's name, for example <c>tvdb</c>.</param>
    /// <param name="idValue">The ID.</param>
    /// <param name="clientId">The client ID.</param>
    /// <param name="user">The user's login.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The anime titles' Simkl IDs (empty when the ID is no anime), or <c>null</c> when the call failed; the failure is already logged.</returns>
    Task<IReadOnlyList<int>?> FindAnimeAsync(string idName, string idValue, string clientId, SimklUser user, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a Simkl anime title, with <c>GET /anime/{id}?extended=full</c>.
    /// </summary>
    /// <param name="simklId">The title's Simkl ID.</param>
    /// <param name="clientId">The client ID.</param>
    /// <param name="user">The user's login.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The title, without its episodes, or <c>null</c> when the call failed; the failure is already logged.</returns>
    Task<AnimeTitle?> GetAnimeAsync(int simklId, string clientId, SimklUser user, CancellationToken cancellationToken);

    /// <summary>
    /// Lists a Simkl anime title's episodes, with <c>GET /anime/episodes/{id}?extended=full</c>.
    /// </summary>
    /// <param name="simklId">The title's Simkl ID.</param>
    /// <param name="clientId">The client ID.</param>
    /// <param name="user">The user's login.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The episodes, specials included, or <c>null</c> when the call failed; the failure is already logged.</returns>
    Task<IReadOnlyList<AnimeEpisode>?> GetAnimeEpisodesAsync(int simklId, string clientId, SimklUser user, CancellationToken cancellationToken);
}
