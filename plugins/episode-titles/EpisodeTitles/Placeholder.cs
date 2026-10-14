using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
namespace Jellyfin.Plugin.EpisodeTitles;

/// <summary>Names an episode carries while its real title is not known yet.</summary>
public static partial class Placeholder
{
    // AniDB's English stand-in for an untitled episode ("Episode 3"), which TheTVDB and TMDB also list; IMDb's, which
    // OMDb passes on ("Episode #1.3", season and episode); and TheTVDB's "TBA"/"TBD". A title that only starts with
    // these words ("Episode 6.5 ...") is a real one.
    [GeneratedRegex(@"^(?:Episode\s+(?:\d+|#\d+\.\d+)|TBA|TBD)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StandIn();

    /// <summary>True when <paramref name="name"/> is empty, a stand-in, or the file name Jellyfin keeps when no
    /// provider gave a title.</summary>
    public static bool IsPlaceholder([NotNullWhen(false)] string? name, string? path)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        var trimmed = name.Trim();
        return StandIn().IsMatch(trimmed)
            || (!string.IsNullOrEmpty(path)
                && string.Equals(trimmed, System.IO.Path.GetFileNameWithoutExtension(path), StringComparison.Ordinal));
    }
}
