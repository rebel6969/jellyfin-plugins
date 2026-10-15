namespace Jellyfin.Plugin.SimklMarkSync.Simkl;

/// <summary>
/// A title related to a Simkl anime title, such as its sequel.
/// </summary>
/// <param name="SimklId">The related title's Simkl ID.</param>
/// <param name="Tvdb">The TVDB series ID Simkl links the related title to, if any.</param>
public sealed record AnimeRelation(int SimklId, string? Tvdb);
