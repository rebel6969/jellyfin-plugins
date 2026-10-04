namespace Jellyfin.Plugin.SimklMarkSync.Simkl;

/// <summary>
/// One item's state in the user's Simkl library.
/// </summary>
/// <param name="Matched">Whether Simkl matched the item's IDs to a title.</param>
/// <param name="Watched">Whether the user has watched, or is watching, the title.</param>
/// <param name="List">The title's list (<c>completed</c>, <c>watching</c>, <c>plantowatch</c>, <c>hold</c>, <c>dropped</c>), or <c>null</c>.</param>
public sealed record WatchedStatus(bool Matched, bool Watched, string? List);
