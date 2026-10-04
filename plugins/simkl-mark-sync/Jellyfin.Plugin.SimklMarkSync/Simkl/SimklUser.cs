namespace Jellyfin.Plugin.SimklMarkSync.Simkl;

/// <summary>
/// A Jellyfin user's Simkl login and switches, as the official Simkl plugin saved them.
/// </summary>
/// <remarks>
/// A class rather than a record, so the token never appears in a generated <c>ToString</c>.
/// </remarks>
public sealed class SimklUser
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SimklUser"/> class.
    /// </summary>
    /// <param name="token">The Simkl access token.</param>
    /// <param name="syncMovies">Whether the user lets the Simkl plugin send movies.</param>
    /// <param name="syncShows">Whether the user lets the Simkl plugin send shows.</param>
    public SimklUser(string token, bool syncMovies, bool syncShows)
    {
        Token = token;
        SyncMovies = syncMovies;
        SyncShows = syncShows;
    }

    /// <summary>
    /// Gets the Simkl access token.
    /// </summary>
    public string Token { get; }

    /// <summary>
    /// Gets a value indicating whether the user lets the Simkl plugin send movies.
    /// </summary>
    public bool SyncMovies { get; }

    /// <summary>
    /// Gets a value indicating whether the user lets the Simkl plugin send shows.
    /// </summary>
    public bool SyncShows { get; }
}
