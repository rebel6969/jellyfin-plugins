namespace Jellyfin.Plugin.SimklMarkSync.Tests;

/// <summary>
/// Plugin settings a test can change between batches.
/// </summary>
public sealed class FakeSettings : IMarkSyncSettings
{
    /// <inheritdoc />
    public bool TrackRewatches { get; set; }
}
