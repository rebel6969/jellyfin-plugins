using System;
using Jellyfin.Plugin.SimklMarkSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SimklMarkSync;

/// <summary>
/// Sends the movies and episodes a user marks played or unplayed in Jellyfin to Simkl.
/// </summary>
/// <remarks>
/// Derives from the generic <see cref="BasePlugin{TConfigurationType}"/> because only its constructor calls
/// <c>SetAttributes</c>, which fills in <see cref="BasePlugin.Version"/> and <see cref="BasePlugin.AssemblyFilePath"/>.
/// Jellyfin's plugin loader reads <c>Version.ToString()</c> right after creating the instance.
/// </remarks>
public sealed class Plugin : BasePlugin<PluginConfiguration>
{
    /// <summary>
    /// The plugin identifier.
    /// </summary>
    public static readonly Guid PluginId = Guid.Parse("8388cbab-8a47-488b-a56a-2fc135bc27a0");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="xmlSerializer">The XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
    }

    /// <inheritdoc />
    public override string Name => "Simkl Mark Sync";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description =>
        "Sends a movie or episode you mark played or unplayed in Jellyfin to Simkl. "
        + "It uses the Simkl login saved by the official Simkl plugin, which only sends playback.";
}
