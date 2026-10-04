using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.SimklMarkSync.Tests;

/// <summary>
/// Builds marks for tests.
/// </summary>
public static class Marks
{
    /// <summary>
    /// The user every test mark belongs to unless stated.
    /// </summary>
    public static readonly Guid User = new("51581cf5-c622-429c-ae8e-736004f5f7be");

    /// <summary>
    /// The series every test episode belongs to unless stated.
    /// </summary>
    public static readonly Guid Series = new("aaaaaaaa-0000-0000-0000-000000000001");

    /// <summary>
    /// The watch time of every test mark.
    /// </summary>
    public static readonly DateTime WatchedAt = new(2026, 10, 4, 3, 30, 0, DateTimeKind.Utc);

    /// <summary>
    /// Builds a movie mark.
    /// </summary>
    /// <param name="imdb">The movie's IMDb ID, which also seeds its item ID.</param>
    /// <param name="played">Whether the movie is now played.</param>
    /// <param name="user">The user, or <see cref="User"/>.</param>
    /// <returns>The mark.</returns>
    public static MovieMark Movie(string imdb, bool played = true, Guid? user = null) =>
        new(user ?? User, ItemId(imdb), played, WatchedAt, new Dictionary<string, object> { ["imdb"] = imdb }, $"Movie {imdb}", 2010);

    /// <summary>
    /// Builds an episode mark of <see cref="Series"/>.
    /// </summary>
    /// <param name="season">The season number.</param>
    /// <param name="episode">The episode number.</param>
    /// <param name="played">Whether the episode is now played.</param>
    /// <param name="series">The series, or <see cref="Series"/>.</param>
    /// <returns>The mark.</returns>
    public static EpisodeMark Episode(int season, int episode, bool played = true, Guid? series = null) =>
        new(
            User,
            ItemId($"{series ?? Series}-{season}-{episode}"),
            played,
            WatchedAt,
            series ?? Series,
            new Dictionary<string, object> { ["tvdb"] = "153021" },
            "The Walking Dead",
            2010,
            season,
            [episode]);

    /// <summary>
    /// Derives a stable item ID from a seed, so the same item always gets the same ID.
    /// </summary>
    /// <param name="seed">The seed.</param>
    /// <returns>The ID.</returns>
    public static Guid ItemId(string seed) => new(SHA256.HashData(Encoding.UTF8.GetBytes(seed)).AsSpan(0, 16));
}
