using System.Collections.Generic;
using Jellyfin.Plugin.SimklMarkSync.Simkl;
using Xunit;

namespace Jellyfin.Plugin.SimklMarkSync.Tests;

/// <summary>
/// Mapping Jellyfin provider IDs to Simkl ID names.
/// </summary>
public sealed class SimklIdsTests
{
    /// <summary>
    /// Every key this library stores, plus the anime plugins' keys, maps to Simkl's name for it.
    /// </summary>
    [Fact]
    public void MapsTheKeysJellyfinStores()
    {
        var ids = SimklIds.FromProviderIds(new Dictionary<string, string>
        {
            ["Imdb"] = "tt1520211",
            ["Tmdb"] = "1402",
            ["Tvdb"] = "153021",
            ["AniDB"] = "9541",
            ["AniList"] = "16498",
            ["Kitsu"] = "7442",
            ["Simkl"] = "2090",
        });

        Assert.Equal(
            new Dictionary<string, object>
            {
                ["imdb"] = "tt1520211",
                ["tmdb"] = "1402",
                ["tvdb"] = "153021",
                ["anidb"] = "9541",
                ["anilist"] = "16498",
                ["kitsu"] = "7442",
                ["simkl"] = 2090,
            },
            ids);
    }

    /// <summary>
    /// Keys match whatever their case, as Jellyfin's provider-ID dictionary does.
    /// </summary>
    [Fact]
    public void IgnoresTheCaseOfKeys()
    {
        var ids = SimklIds.FromProviderIds(new Dictionary<string, string> { ["IMDB"] = "tt1", ["anidb"] = "9" });

        Assert.Equal(new Dictionary<string, object> { ["imdb"] = "tt1", ["anidb"] = "9" }, ids);
    }

    /// <summary>
    /// A collection's ID is not the title's ID, so collection keys and keys Simkl does not know are dropped.
    /// </summary>
    [Fact]
    public void DropsCollectionAndUnknownKeys()
    {
        var ids = SimklIds.FromProviderIds(new Dictionary<string, string>
        {
            ["TmdbCollection"] = "10",
            ["TvdbCollection"] = "11",
            ["TvdbSlug"] = "the-walking-dead",
            ["TvRage"] = "25056",
        });

        Assert.Empty(ids);
    }

    /// <summary>
    /// Blank values are dropped, and values are trimmed.
    /// </summary>
    [Fact]
    public void DropsBlankValuesAndTrims()
    {
        var ids = SimklIds.FromProviderIds(new Dictionary<string, string> { ["Imdb"] = "  ", ["Tvdb"] = " 153021 " });

        Assert.Equal(new Dictionary<string, object> { ["tvdb"] = "153021" }, ids);
    }

    /// <summary>
    /// Simkl's own ID is sent as an integer, and only when it is a positive integer.
    /// </summary>
    /// <param name="value">The stored value.</param>
    [Theory]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("0")]
    [InlineData("1.5")]
    public void DropsASimklIdThatIsNotAPositiveInteger(string value)
    {
        Assert.Empty(SimklIds.FromProviderIds(new Dictionary<string, string> { ["Simkl"] = value }));
    }

    /// <summary>
    /// An item without provider IDs maps to an empty set.
    /// </summary>
    [Fact]
    public void MapsNoProviderIdsToNone()
    {
        Assert.Empty(SimklIds.FromProviderIds(null));
    }
}
