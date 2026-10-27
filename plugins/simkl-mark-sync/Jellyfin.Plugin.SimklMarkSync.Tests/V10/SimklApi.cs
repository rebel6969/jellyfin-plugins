namespace Jellyfin.Plugin.SimklMarkSync.Tests.V10;

/// <summary>
/// Stands in for version 10 of the official plugin's API class: a client ID per login flow.
/// </summary>
internal static class SimklApi
{
    /// <summary>
    /// The fixture's client ID of pin flow logins.
    /// </summary>
    internal const string ApikeyLegacy = "fixture-legacy-client-id";

    /// <summary>
    /// The fixture's client ID of OAuth 2 device flow logins.
    /// </summary>
    internal const string ApikeyOAuth2 = "fixture-oauth2-client-id";
}
