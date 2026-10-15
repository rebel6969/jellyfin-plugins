using System.Collections.Generic;

namespace Jellyfin.Plugin.SimklMarkSync.Simkl;

/// <summary>
/// A show Simkl listed under <c>not_found</c>, which echoes the show as it was sent.
/// </summary>
/// <param name="Description">The description logged for it, also listed in <see cref="SimklWriteResult.NotFound"/>.</param>
/// <param name="Ids">The IDs it was sent with, every value as text.</param>
public sealed record UnmatchedShow(string Description, IReadOnlyDictionary<string, string> Ids);
