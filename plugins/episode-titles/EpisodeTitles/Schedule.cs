namespace Jellyfin.Plugin.EpisodeTitles;

/// <summary>How often a placeholder-named episode is re-checked: a seventh of its age, at least daily, at most every
/// 30 days. A new simulcast usually gets its English title within days; a show that never had titles settles at
/// monthly checks.</summary>
internal static class Schedule
{
    // A daily run starts a few seconds earlier or later each day; without slack an episode refreshed at the last run
    // would be skipped by this one and checked only every other day.
    private static readonly TimeSpan Slack = TimeSpan.FromHours(1);

    internal static TimeSpan Interval(DateTime now, DateTime? premiere, DateTime created)
    {
        var ageDays = (now - (premiere ?? created)).TotalDays;
        return TimeSpan.FromDays(Math.Clamp(ageDays / 7, 1, 30));
    }

    internal static bool IsDue(DateTime now, DateTime? premiere, DateTime created, DateTime lastRefreshed) =>
        now - lastRefreshed >= Interval(now, premiere, created) - Slack;
}
