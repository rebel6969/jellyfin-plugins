using Jellyfin.Plugin.EnglishSdh;
using MediaBrowser.Model.Entities;
using Xunit;

public class SubtitleSelectorTests
{
    internal static MediaStream Sub(int index, string language = "eng", string? title = null,
        bool hearing = false, bool forced = false) => new()
    {
        Index = index, Type = MediaStreamType.Subtitle, Language = language,
        Title = title, IsHearingImpaired = hearing, IsForced = forced
    };

    [Theory]
    [InlineData("English SDH")]
    [InlineData("English [CC]")]
    [InlineData("English (HI)")]
    [InlineData("English hearing impaired")]
    [InlineData("English closed captions")]
    public void RecognizesEnglishSdhTitles(string title)
    {
        Assert.Equal(3, SubtitleSelector.Select([Sub(2), Sub(3, "en", title)]));
    }

    [Fact]
    public void NeverPrefersForcedSdhOverFullEnglish()
    {
        Assert.Equal(2, SubtitleSelector.Select([Sub(3, hearing: true, forced: true), Sub(2)]));
    }

    [Fact]
    public void PrefersEnglishHearingImpairedOverRegularEnglish()
    {
        Assert.Equal(3, SubtitleSelector.Select([Sub(2), Sub(3, hearing: true)]));
    }
}
