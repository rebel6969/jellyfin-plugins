using Jellyfin.Plugin.EnglishSdh;
using MediaBrowser.Model.Entities;
using Xunit;
using static SubtitleSelectorTests;

public class SelectorBoundaryTests
{
    [Theory]
    [InlineData("en")][InlineData("eng")][InlineData("EN")][InlineData("English")][InlineData(" eng ")]
    public void MatchesEnglishCodes(string language) => Assert.Equal(2, SubtitleSelector.Select([Sub(2, language)]));

    [Theory]
    [InlineData("non-SDH")][InlineData("No CC")][InlineData("not hearing impaired")]
    [InlineData("without closed captions")][InlineData("Hindi")][InlineData("sdhish")]
    public void AvoidsFalseSdhLabels(string title) => Assert.False(SubtitleSelector.IsSdh(Sub(2, title: title)));

    [Fact]
    public void FallsBackToRegularEnglish() => Assert.Equal(4, SubtitleSelector.Select([Sub(1, "fra", hearing: true), Sub(4)]));
    [Fact]
    public void LeavesNonEnglishOnlyUnchanged() => Assert.Null(SubtitleSelector.Select([Sub(1, "fra", hearing: true)]));
    [Fact]
    public void LeavesEmptyUnchanged() => Assert.Null(SubtitleSelector.Select([]));
    [Fact]
    public void LeavesForcedOnlyUnchanged() => Assert.Null(SubtitleSelector.Select([Sub(1, forced: true)]));
    [Fact]
    public void ExcludesTitleForced() => Assert.Equal(4, SubtitleSelector.Select([Sub(1, title: "Forced SDH"), Sub(4)]));
    [Fact]
    public void AllowsNonForcedTitle() => Assert.Equal(1, SubtitleSelector.Select([Sub(1, title: "Non-Forced SDH"), Sub(4)]));
    [Fact]
    public void FlagOverridesNegativeTitle() => Assert.True(SubtitleSelector.IsSdh(Sub(1, title: "non-SDH", hearing: true)));
    [Fact]
    public void DefaultDoesNotBeatSdh()
    {
        var regular = Sub(2); regular.IsDefault = true;
        Assert.Equal(3, SubtitleSelector.Select([regular, Sub(3, hearing: true)]));
    }
    [Fact]
    public void MetadataFlagBeatsTitleOnly() => Assert.Equal(4, SubtitleSelector.Select([Sub(1, title: "SDH"), Sub(4, hearing: true)]));
    [Fact]
    public void ExcludesAudio()
    {
        var audio = Sub(1, hearing: true); audio.Type = MediaStreamType.Audio;
        Assert.Equal(4, SubtitleSelector.Select([audio, Sub(4)]));
    }
    [Fact]
    public void DoesNotGuessLanguageFromTitle() => Assert.Null(SubtitleSelector.Select([Sub(1, "und", "English SDH")]));
    [Fact]
    public void ExcludesCommentary() => Assert.Equal(4, SubtitleSelector.Select([Sub(1, title: "Director Commentary SDH"), Sub(4)]));
    [Fact]
    public void DeterministicAcrossInputOrder() => Assert.Equal(2, SubtitleSelector.Select([Sub(8, hearing: true), Sub(2, hearing: true)]));
}
