using System;
using MediaBrowser.Common.Plugins;

namespace Jellyfin.Plugin.MaxQuality;

/// <summary>
/// Plays every video at its original quality unless the viewer picks a lower one during playback.
/// </summary>
public sealed class Plugin : BasePlugin
{
    /// <summary>
    /// The plugin identifier.
    /// </summary>
    public static readonly Guid PluginId = Guid.Parse("b1ac5cc4-9822-4979-81c6-1c4b7dc6dbad");

    /// <inheritdoc />
    public override string Name => "Max Quality";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description =>
        "Removes the client's bitrate cap at the start of each video so it direct plays at its original quality. "
        + "A quality picked during playback is honoured until that video's playback ends.";
}
