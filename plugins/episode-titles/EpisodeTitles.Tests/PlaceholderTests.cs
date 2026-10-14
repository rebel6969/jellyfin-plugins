using Xunit;
namespace Jellyfin.Plugin.EpisodeTitles.Tests;

public class PlaceholderTests
{
    private const string FilePath = "/media/anime/Show/Season 2/Show - S02E03 - TBA WEBDL-1080p.mkv";

    [Theory]
    [InlineData("Episode 3")]
    [InlineData("Episode 12")]
    [InlineData("episode 1")]
    [InlineData("  Episode 4  ")]
    [InlineData("Episode\t7")]
    [InlineData("TBA")]
    [InlineData("tba")]
    [InlineData("TBD")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Show - S02E03 - TBA WEBDL-1080p")]
    public void StandInsEmptyNamesAndTheFileNameArePlaceholders(string? name) =>
        Assert.True(Placeholder.IsPlaceholder(name, FilePath));

    [Theory]
    [InlineData("The Floating Island")]
    [InlineData("Episode 6.5 \"Drowning in the Shadow\"")]
    [InlineData("Episode 3: The Return")]
    [InlineData("Episode One")]
    [InlineData("TBA Special")]
    [InlineData("Opening")]
    [InlineData("Show - S02E03 - TBA WEBDL-1080p.mkv")]
    [InlineData("Show - S02E04 - TBA WEBDL-1080p")]
    [InlineData("show - s02e03 - tba webdl-1080p")]
    public void RealTitlesAreNot(string name) => Assert.False(Placeholder.IsPlaceholder(name, FilePath));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WithoutAPathOnlyStandInsCount(string? path)
    {
        Assert.False(Placeholder.IsPlaceholder("Show - S02E03 - TBA WEBDL-1080p", path));
        Assert.True(Placeholder.IsPlaceholder("Episode 3", path));
    }
}
