using Jellyfin.Plugin.EnglishSdh;
using Xunit;
using static SubtitleSelectorTests;

public class RegionalEnglishTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    [InlineData("EN-au")]
    [InlineData(" en-CA ")]
    [InlineData("eng-US")]
    [InlineData("en-Latn-GB")]
    [InlineData("en-001")]
    public void RegionalEnglishSdhBeatsOrdinaryEnglish(string language)
        => Assert.Equal(3, SubtitleSelector.Select([Sub(2), Sub(3, language, hearing: true)]));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("enochian")]
    [InlineData("english-US")]
    [InlineData("fr-EN")]
    [InlineData("en-")]
    [InlineData("en--US")]
    [InlineData("en-US!")]
    [InlineData("en US")]
    public void RejectsNonEnglishAndMalformedTags(string? language)
        => Assert.False(SubtitleSelector.IsEnglish(language));

    [Fact]
    public void RegionalRegularEnglishIsFallback()
        => Assert.Equal(4, SubtitleSelector.Select([Sub(1, "fr", hearing: true), Sub(4, "en-GB")]));

    [Fact]
    public void RegionalForcedSdhStillExcluded()
        => Assert.Equal(2, SubtitleSelector.Select([Sub(2), Sub(3, "en-US", hearing: true, forced: true)]));
}
