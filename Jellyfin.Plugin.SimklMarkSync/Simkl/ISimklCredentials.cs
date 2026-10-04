using System;

namespace Jellyfin.Plugin.SimklMarkSync.Simkl;

/// <summary>
/// Supplies the Simkl client ID and user logins that the official Simkl plugin holds.
/// </summary>
public interface ISimklCredentials
{
    /// <summary>
    /// Gets the client ID the official Simkl plugin's logins were issued to.
    /// </summary>
    /// <returns>The client ID, or <c>null</c> when the official plugin is missing or does not expose it.</returns>
    string? GetClientId();

    /// <summary>
    /// Gets a Jellyfin user's Simkl login.
    /// </summary>
    /// <param name="userId">The Jellyfin user.</param>
    /// <returns>The login, or <c>null</c> when the user has not linked Simkl.</returns>
    SimklUser? GetUser(Guid userId);
}
