using System.Collections.Generic;

namespace Jellyfin.Plugin.SimklMarkSync.Simkl;

/// <summary>
/// One item of Simkl's <c>POST /sync/watched</c> lookup.
/// </summary>
/// <param name="Ids">The item's identifiers.</param>
/// <param name="Type">The item type, which keeps a TMDB ID from matching a show instead of a movie.</param>
public sealed record WatchedLookupItem(IReadOnlyDictionary<string, object> Ids, string Type);
