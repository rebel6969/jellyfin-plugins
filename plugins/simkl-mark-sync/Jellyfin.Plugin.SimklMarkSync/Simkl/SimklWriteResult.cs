using System.Collections.Generic;

namespace Jellyfin.Plugin.SimklMarkSync.Simkl;

/// <summary>
/// What a history add or remove changed.
/// </summary>
/// <param name="Movies">The movies added or removed.</param>
/// <param name="Shows">The shows added, or removed from the library.</param>
/// <param name="Episodes">The episodes marked or unmarked.</param>
/// <param name="NotFound">A short description of each item Simkl could not match.</param>
public sealed record SimklWriteResult(int Movies, int Shows, int Episodes, IReadOnlyList<string> NotFound);
