#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ValheimProfiler.Tools.MonoBehaviourCallProfiler;

internal sealed partial class MonoBehaviourCallProfilerTool
{
    private enum MainTab
    {
        Profiler = 0,
        CallsToProfile = 1,
        Help = 2
    }

    private enum TableSortColumn
    {
        Source = 0,
        Calls = 1,
        Total = 2,
        Average = 3,
        Max = 4,
        Last = 5,
        P95 = 6,
        P99 = 7,
        FirstAt = 8,
        LastAt = 9,
        MonoBehaviour = 10
    }

    private enum BehaviourSource
    {
        Mod = 0,
        Valheim = 1,
        Other = 2
    }

    private enum ProfileMethodKind
    {
        Lifecycle = 0,
        Declared = 1
    }

    private enum SelectionRowKind
    {
        Group = 0,
        Type = 1,
        Method = 2
    }

    private sealed class BehaviourTypeEntry
    {
        public Type Type;
        public string GroupId;
        public string GroupName;
        public string AssemblyName;
        public string TypeName;
        public BehaviourSource Source;
        public bool Expanded;
        public readonly List<BehaviourMethodEntry> Methods = new List<BehaviourMethodEntry>();
    }

    private sealed class BehaviourMethodEntry
    {
        public BehaviourTypeEntry TypeEntry;
        public MethodInfo Method;
        public string Key;
        public string DisplayName;
        public string Tooltip;
        public ProfileMethodKind Kind;
        public bool IsAsyncOrIterator;
        public bool DefaultSelected;
        public bool Selected;
        public readonly LifetimeProfilerStat Stat = new LifetimeProfilerStat();
    }

    private sealed class RuntimeBehaviourEntry
    {
        public readonly Type RuntimeType;
        public readonly BehaviourMethodEntry Entry;

        public RuntimeBehaviourEntry(Type runtimeType, BehaviourMethodEntry entry)
        {
            RuntimeType = runtimeType;
            Entry = entry;
        }
    }

    private sealed class FlatRowView
    {
        public BehaviourMethodEntry Entry;
        public LifetimeProfilerSnapshot Snapshot;
    }

    private sealed class GroupRowView
    {
        public string GroupId;
        public string GroupName;
        public List<FlatRowView> Rows;
        public GroupSummary Summary;
    }

    private struct GroupSummary
    {
        public long Calls;
        public double TotalMs;
        public double AverageMs;
        public double MaxMs;
        public double LastMs;
        public double P95Ms;
        public double P99Ms;
        public float FirstCallAtSeconds;
        public float LastCallAtSeconds;
    }

    private sealed class SelectionRow
    {
        public SelectionRowKind Kind;
        public string GroupId;
        public string Text;
        public string Tooltip;
        public BehaviourTypeEntry TypeEntry;
        public BehaviourMethodEntry MethodEntry;
        public int Indent;
    }

    private sealed class SelectionPolicy
    {
        private readonly string _path;
        private readonly Dictionary<string, bool> _overrides = new Dictionary<string, bool>(StringComparer.Ordinal);

        public SelectionPolicy(string path)
        {
            _path = path;
            Load();
        }

        public bool Resolve(string key, bool defaultValue) =>
            key != null && _overrides.TryGetValue(key, out bool value) ? value : defaultValue;

        public void Reload() => Load();

        public void Save(IEnumerable<BehaviourMethodEntry> entries)
        {
            try
            {
                string directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var lines = new List<string>
                {
                    "# Valheim Profiler MonoBehaviour Call selection overrides",
                    "# + entry = enabled, - entry = disabled",
                    "# Missing entries use defaults: synchronous mod lifecycle callbacks are enabled; declared methods, Valheim and Other callbacks are disabled.",
                    string.Empty
                };

                foreach (BehaviourMethodEntry entry in entries.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    if (entry.Selected == entry.DefaultSelected)
                        continue;

                    lines.Add((entry.Selected ? "+ " : "- ") + entry.Key);
                }

                File.WriteAllLines(_path, lines);
                Load();
            }
            catch
            {
            }
        }

        private void Load()
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

                    string key = line.Substring(1).Trim();
                    if (!string.IsNullOrEmpty(key))
                        _overrides[key] = value;
                }
            }
            catch
            {
            }
        }
    }
}
