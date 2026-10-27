namespace Jellyfin.Plugin.SimklMarkSync.Tests.V10;

/// <summary>
/// A settings class whose token test does not answer yes or no, which the reader must not trust.
/// </summary>
internal static class OddUserConfig
{
    /// <summary>
    /// Answers with text rather than a yes or no.
    /// </summary>
    /// <param name="token">The token to look at.</param>
    /// <returns>The token itself.</returns>
    public static string IsOAuth2Token(string? token) => token ?? string.Empty;
}
