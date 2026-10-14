namespace Jellyfin.Plugin.EnglishSdh;

/// <summary>The one opt-in rule shared by the playback handler and the PlaybackInfo filter.</summary>
public static class OptIn
{
    public static bool EnabledFor(PluginConfiguration? config, Guid userId) =>
        userId != Guid.Empty && config?.Enabled == true
        && (config.UserIds ?? []).Any(s => Guid.TryParse(s, out var id) && id == userId);
}
