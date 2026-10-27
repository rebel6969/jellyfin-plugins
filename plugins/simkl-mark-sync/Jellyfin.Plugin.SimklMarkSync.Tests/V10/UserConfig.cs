using System;

namespace Jellyfin.Plugin.SimklMarkSync.Tests.V10;

/// <summary>
/// Stands in for version 10 of the official plugin's per-user settings class and its token test.
/// </summary>
internal static class UserConfig
{
    /// <summary>
    /// The prefix version 10 gives device flow tokens.
    /// </summary>
    internal const string OAuth2Prefix = "simkl_at_";

    /// <summary>
    /// Tells a device flow token from a pin flow one, by its prefix as version 10 does.
    /// </summary>
    /// <param name="token">The token to look at.</param>
    /// <returns><c>true</c> for a device flow token.</returns>
    public static bool IsOAuth2Token(string? token) => token != null && token.StartsWith(OAuth2Prefix, StringComparison.Ordinal);
}
