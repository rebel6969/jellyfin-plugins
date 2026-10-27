using System;

namespace Jellyfin.Plugin.SimklMarkSync.Simkl;

/// <summary>
/// The client IDs the official Simkl plugin issues its logins to, and how to tell which one a login belongs to.
/// </summary>
public sealed class SimklClientIds
{
    private readonly Func<string, bool>? _isOAuth2Token;

    /// <summary>
    /// Initializes a new instance of the <see cref="SimklClientIds"/> class.
    /// </summary>
    /// <param name="legacy">The client ID of pin flow logins, version 9's only one.</param>
    /// <param name="oAuth2">The client ID of OAuth 2 device flow logins, which version 10 added, if any.</param>
    /// <param name="isOAuth2Token">The official plugin's test of whether a token came from the device flow, if any.</param>
    public SimklClientIds(string legacy, string? oAuth2, Func<string, bool>? isOAuth2Token)
    {
        Legacy = legacy;
        OAuth2 = oAuth2;
        _isOAuth2Token = isOAuth2Token;
    }

    /// <summary>
    /// Gets the client ID of pin flow logins.
    /// </summary>
    public string Legacy { get; }

    /// <summary>
    /// Gets the client ID of OAuth 2 device flow logins, or <c>null</c> for an official plugin without one.
    /// </summary>
    public string? OAuth2 { get; }

    /// <summary>
    /// Gets the client ID a login's token was issued to.
    /// </summary>
    /// <param name="token">The access token.</param>
    /// <returns>
    /// The client ID; <c>null</c> when the official plugin has a device flow client ID but no token test, since the
    /// token's client ID cannot then be told.
    /// </returns>
    public string? For(string token)
    {
        if (OAuth2 is null)
        {
            return Legacy;
        }

        if (_isOAuth2Token is null)
        {
            return null;
        }

        return _isOAuth2Token(token) ? OAuth2 : Legacy;
    }
}
