namespace Jellyfin.Plugin.Simkl.API;

/// <summary>
/// Stands in for the official Simkl plugin's API class: same namespace, name and client ID field.
/// </summary>
internal static class SimklApi
{
    /// <summary>
    /// The fixture's client ID, where the official plugin keeps its own.
    /// </summary>
    internal const string Apikey = "fixture-client-id";
}
