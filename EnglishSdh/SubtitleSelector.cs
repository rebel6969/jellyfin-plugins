using System.Text.RegularExpressions;
using MediaBrowser.Model.Entities;
namespace Jellyfin.Plugin.EnglishSdh;

/// <summary>Deterministic English/full-subtitle selection; no network or file mutation.</summary>
public static class SubtitleSelector
{
    private static readonly Regex Separators = new("[^a-z0-9]+", RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    public static bool IsEnglish(string? language) => language?.Trim().ToLowerInvariant() is "en" or "eng" or "english";
    private static string[] Words(string? title) => Separators.Split((title ?? "").ToLowerInvariant());
    private static bool Negated(string[] words, int i) => i > 0 && words[i - 1] is "non" or "not" or "no" or "without";
    private static bool HasLabel(string? title, bool hearing)
    {
        var words = Words(title);
        for (var i = 0; i < words.Length; i++)
        {
            if (Negated(words, i)) continue;
            if (!hearing && words[i] == "forced") return true;
            if (!hearing) continue;
            if (words[i] is "sdh" or "cc" or "hi") return true;
            if (i + 1 < words.Length && ((words[i] == "hearing" && words[i + 1] == "impaired")
                || (words[i] == "closed" && words[i + 1] is "caption" or "captions"))) return true;
        }
        return false;
    }
    public static bool IsForced(MediaStream stream) => stream.IsForced
        || HasLabel(stream.Title, false) || HasLabel(stream.DisplayTitle, false);
    public static bool IsSdh(MediaStream stream) => stream.IsHearingImpaired
        || HasLabel(stream.Title, true) || HasLabel(stream.DisplayTitle, true);

    // No English/full track: leave the client's selection untouched, never disable it.
    public static int? Select(IReadOnlyList<MediaStream> streams) => streams
        .Where(s => s.Type == MediaStreamType.Subtitle && s.Index >= 0 && IsEnglish(s.Language)
            && !IsForced(s) && !Words(s.Title).Contains("commentary"))
        .OrderByDescending(IsSdh)
        .ThenByDescending(s => s.IsHearingImpaired)
        .ThenByDescending(s => s.IsDefault)
        .ThenBy(s => s.Index)
        .Select(s => (int?)s.Index).FirstOrDefault();
}
