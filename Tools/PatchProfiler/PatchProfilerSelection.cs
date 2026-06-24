#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ValheimProfiler.Tools.PatchProfiler;

internal sealed partial class PatchProfilerTool
{
    private sealed class ModSelectionPolicy
    {
        private readonly string _path;
        private readonly Dictionary<string, bool> _overrides = new(StringComparer.Ordinal);

        internal ModSelectionPolicy(string path)
        {
            _path = path;
            Reload();
        }

        internal bool Resolve(string modGuid, bool defaultValue) =>
            !string.IsNullOrEmpty(modGuid) && _overrides.TryGetValue(modGuid, out bool value)
                ? value
                : defaultValue;

        internal void Reload()
        {
            _overrides.Clear();

            try
            {
                if (!File.Exists(_path))
                    return;

                foreach (string rawLine in File.ReadAllLines(_path))
                {
                    string line = rawLine?.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#", StringComparison.Ordinal))
                        continue;

                    bool value;
                    if (line.StartsWith("+", StringComparison.Ordinal))
                        value = true;
                    else if (line.StartsWith("-", StringComparison.Ordinal))
                        value = false;
                    else
                        continue;

                    string modGuid = line.Substring(1).Trim();
                    if (!string.IsNullOrEmpty(modGuid))
                        _overrides[modGuid] = value;
                }
            }
            catch
            {
            }
        }

        internal void Save(IReadOnlyDictionary<string, bool> selection)
        {
            try
            {
                string directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var lines = new List<string>
                {
                    "# Valheim Profiler Patch Profiler mod selection overrides",
                    "# + mod-guid = enabled, - mod-guid = disabled",
                    "# Missing entries are enabled by default.",
                    string.Empty
                };

                foreach (KeyValuePair<string, bool> pair in selection.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    if (pair.Value)
                        continue;

                    lines.Add("- " + pair.Key);
                }

                File.WriteAllLines(_path, lines);
                Reload();
            }
            catch
            {
            }
        }
    }

    private void SaveModSelection()
    {
        Dictionary<string, bool> snapshot;
        lock (_lock)
            snapshot = _modsToProfile.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        _modSelectionPolicy.Save(snapshot);
        _modsSelectionDirty = false;
    }
}
