#nullable disable

using BepInEx;
using System.IO;

namespace ValheimProfiler.Core;

internal static class ProfilerPaths
{
    internal static string ConfigDirectory => Path.Combine(Paths.ConfigPath, ValheimProfilerPlugin.PluginGuid);

    internal static string GetConfigFilePath(string fileName)
    {
        Directory.CreateDirectory(ConfigDirectory);
        return Path.Combine(ConfigDirectory, fileName);
    }
}