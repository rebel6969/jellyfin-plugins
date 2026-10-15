using System;
using System.IO;

namespace Jellyfin.Plugin.SimklMarkSync.Tests;

/// <summary>
/// Reads the recorded Simkl answers in the Fixtures folder.
/// </summary>
public static class Fixtures
{
    /// <summary>
    /// Reads one recorded answer.
    /// </summary>
    /// <param name="name">The file name.</param>
    /// <returns>The answer's JSON.</returns>
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
