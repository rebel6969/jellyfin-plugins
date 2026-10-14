using Xunit;
namespace Jellyfin.Plugin.EpisodeTitles.Tests;

public class ScheduleTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 18, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 1)]
    [InlineData(7, 1)]
    [InlineData(14, 2)]
    [InlineData(70, 10)]
    [InlineData(210, 30)]
    [InlineData(1000, 30)]
    [InlineData(-5, 1)]
    public void TheIntervalIsASeventhOfTheAgeBetweenOneAndThirtyDays(int ageDays, double intervalDays) =>
        Assert.Equal(TimeSpan.FromDays(intervalDays), Schedule.Interval(Now, Now.AddDays(-ageDays), Now.AddDays(-400)));

    [Fact]
    public void TheAgeCountsFromThePremiereWhenThereIsOne() =>
        Assert.Equal(TimeSpan.FromDays(1), Schedule.Interval(Now, Now.AddDays(-2), Now.AddDays(-400)));

    [Fact]
    public void WithoutAPremiereTheAgeCountsFromWhenTheItemWasAdded() =>
        Assert.Equal(TimeSpan.FromDays(10), Schedule.Interval(Now, null, Now.AddDays(-70)));

    [Fact]
    public void ANeverRefreshedEpisodeIsDue() =>
        Assert.True(Schedule.IsDue(Now, Now.AddDays(-400), Now.AddDays(-400), DateTime.MinValue));

    [Fact]
    public void ADailyEpisodeRefreshedByTheLastRunIsDueAgainDespiteAFewMinutesOfDrift()
    {
        Assert.True(Schedule.IsDue(Now, Now.AddDays(-2), Now.AddDays(-2), Now.AddHours(-23.5)));
        Assert.True(Schedule.IsDue(Now, Now.AddDays(-2), Now.AddDays(-2), Now.AddHours(-23)));
        Assert.False(Schedule.IsDue(Now, Now.AddDays(-2), Now.AddDays(-2), Now.AddHours(-22.9)));
    }

    [Fact]
    public void AnOldEpisodeWaitsItsLongerInterval()
    {
        var premiere = Now.AddDays(-70);
        Assert.False(Schedule.IsDue(Now, premiere, premiere, Now.AddDays(-5)));
        Assert.True(Schedule.IsDue(Now, premiere, premiere, Now.AddDays(-10)));
    }
}
