using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.SimklMarkSync.Simkl;

/// <summary>
/// Maps Jellyfin provider IDs to the ID names Simkl accepts.
/// </summary>
public static class SimklIds
{
    // Keys match exactly (ignoring case), so collection IDs such as "TmdbCollection" never pass as a title's ID.
    // AniDB, AniList and Kitsu are the keys Jellyfin's anime metadata plugins store.
    private static readonly FrozenDictionary<string, string> _textIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Imdb"] = "imdb",
        ["Tmdb"] = "tmdb",
        ["Tvdb"] = "tvdb",
        ["AniDB"] = "anidb",
        ["AniList"] = "anilist",
        ["Kitsu"] = "kitsu",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Builds a Simkl <c>ids</c> object from a Jellyfin item's provider IDs.
    /// </summary>
    /// <param name="providerIds">The item's provider IDs.</param>
    /// <returns>The IDs Simkl knows; empty when there are none.</returns>
    public static IReadOnlyDictionary<string, object> FromProviderIds(IReadOnlyDictionary<string, string>? providerIds)
    {
        var ids = new Dictionary<string, object>(StringComparer.Ordinal);
        if (providerIds is null)
        {
            return ids;
        }

        foreach (var (key, value) in providerIds)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            if (_textIds.TryGetValue(key, out var simklKey))
            {
                ids.TryAdd(simklKey, trimmed);
            }
            else if (key.Equals("Simkl", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var simklId)
                && simklId > 0)
            {
                // Simkl's own ID is the only integer in an ids object.
                ids.TryAdd("simkl", simklId);
            }
        }

        return ids;
    }
}
