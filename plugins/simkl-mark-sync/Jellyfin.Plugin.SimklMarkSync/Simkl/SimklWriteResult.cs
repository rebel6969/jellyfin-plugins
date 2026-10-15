using System.Collections.Generic;

namespace Jellyfin.Plugin.SimklMarkSync.Simkl;

/// <summary>
/// What a history add or remove changed.
/// </summary>
/// <param name="Movies">The movies added or removed.</param>
/// <param name="Shows">The shows added, or removed from the library.</param>
/// <param name="Episodes">The episodes marked or unmarked.</param>
/// <param name="NotFound">A short description of each item Simkl could not match.</param>
public sealed record SimklWriteResult(int Movies, int Shows, int Episodes, IReadOnlyList<string> NotFound)
{
    /// <summary>
    /// Gets the <c>rewatch_status</c> Simkl reported for each item of an add sent with <c>allow_rewatch=yes</c>, for
    /// example <c>completed</c> for a recorded movie rewatch or <c>too_soon</c> within two days of the last watch.
    /// </summary>
    public IReadOnlyList<string> RewatchStatuses { get; init; } = [];

    /// <summary>
    /// Gets the shows Simkl could not match, with the IDs each was sent with.
    /// </summary>
    public IReadOnlyList<UnmatchedShow> NotFoundShows { get; init; } = [];
}
