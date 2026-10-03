#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ValheimProfiler.Tools.GeneralMethodProfiler;

internal sealed partial class GeneralMethodProfilerTool
{
    private enum MainTab { Profiler, MethodsToProfile, Help, Safety }
    private enum ProfilerView { Avg1s, MaxOver60Sec }
    private enum TableSortColumn
    {
        Mod, AvgMsPerFrame, AvgCallsPerFrame, AvgCall, RawMax, SecondMax, ThirdMax,
        AboveP99, AvgAboveP99, P99, AboveP95, AvgAboveP95, P95, Samples, GcSamples, Class, Method
    }
    private enum SelectionRowKind { Group, Module, Type, Method }
    private enum TreeSortColumn { Inclusive, Self, Calls, RawMax, Name }

    private sealed class MethodTypeEntry
    {
        internal Type Type;
        internal string GroupId;
        internal string GroupName;
        internal string AssemblyName;
        internal string ModuleName;
        internal string ModuleKey;
        internal string TypeName;
        internal bool Expanded;
        internal readonly List<GeneralMethodEntry> Methods = new List<GeneralMethodEntry>();
    }

    private sealed class GeneralMethodEntry
    {
        internal MethodTypeEntry TypeEntry;
        internal MethodInfo Method;
        internal string Key;
        internal string DisplayName;
        internal string TableDisplayName;
        internal string Tooltip;
        internal string SearchText;
        internal string UnsupportedReason;
        internal bool Selected;
        // Discovery must not allocate 60 analytics buckets for every method in every installed mod.
        internal RollingProfilerStat Stat;
        internal readonly List<CallNode> Contexts = new List<CallNode>();
    }

    private sealed class FlatRowView
    {
        internal GeneralMethodEntry Entry;
        internal RollingProfilerSnapshot Snapshot;
    }

    private sealed class GroupRowView
    {
        internal string GroupId;
        internal string GroupName;
        internal List<FlatRowView> Rows;
        internal GroupSummary Summary;
        internal double AggregateScore;
    }

    private struct GroupSummary
    {
        internal double Avg1sMsPerFrame;
        internal double Avg1sCallsPerFrame;
        internal double Avg1sAverageCallMs;
        internal RollingMaxSnapshot Max;
    }

    private sealed class SelectionRow
    {
        internal SelectionRowKind Kind;
        internal string Key;
        internal string Text;
        internal string Tooltip;
        internal MethodTypeEntry TypeEntry;
        internal GeneralMethodEntry MethodEntry;
        internal List<GeneralMethodEntry> Entries;
        internal int SelectedCount;
        internal int AvailableCount;
        internal int AvailableSelectedCount;
        internal int Indent;
        internal bool Expanded;
    }

    /// <summary>One node per complete runtime path, not per method or immediate edge alone.</summary>
    private sealed class CallNode
    {
        internal int Id;
        internal GeneralMethodEntry Entry;
        internal CallNode Parent;
        internal CallNode Root;
        internal int Depth;
        internal readonly Dictionary<GeneralMethodEntry, CallNode> Children = new Dictionary<GeneralMethodEntry, CallNode>();
        internal RollingProfilerStat Stat;
        internal long Calls;
        internal double InclusiveMs;
        internal double SelfMs;
        internal double MaxSelfMs;
        internal double LastSelfMs;
    }

    private struct InvocationFrame
    {
        internal long Sequence;
        internal long StartTicks;
        internal long ChildTicks;
        internal long ProfilerPrefixTicks;
        internal GeneralMethodEntry Entry;
        internal CallNode Node;
    }

    private struct InvocationToken
    {
        internal long Generation;
        internal long Sequence;
        internal int Index;
    }

    private sealed class TreeNodeView
    {
        internal CallNode Node;
        internal RollingProfilerSnapshot Snapshot;
        internal long Calls;
        internal double InclusiveMs;
        internal double SelfMs;
        internal double MaxSelfMs;
        internal double LastSelfMs;
        internal string Path;
    }

    private sealed class SelectionPolicy
    {
        private readonly string _path;
        private readonly Dictionary<string, bool> _overrides = new Dictionary<string, bool>(StringComparer.Ordinal);
        internal string LastError { get; private set; }

        internal SelectionPolicy(string path)
        {
            _path = path;
            Reload();
        }

        internal bool Resolve(string key) => _overrides.TryGetValue(key, out bool selected) && selected;

        internal void Reload()
        {
            try
            {
                var loaded = new Dictionary<string, bool>(StringComparer.Ordinal);
                if (File.Exists(_path))
                {
                    foreach (string raw in File.ReadAllLines(_path))
                    {
                        string line = raw.Trim();
                        if (line.Length < 3 || line.StartsWith("#", StringComparison.Ordinal))
                            continue;
                        if (line.StartsWith("+ ", StringComparison.Ordinal) || line.StartsWith("- ", StringComparison.Ordinal))
                            loaded[line.Substring(2)] = line[0] == '+';
                    }
                }
                _overrides.Clear();
                foreach (var pair in loaded)
                    _overrides.Add(pair.Key, pair.Value);
                LastError = null;
            }
            catch (Exception ex)
            {
                // Keep the last valid in-memory selection when the file becomes temporarily unavailable.
                LastError = ex.GetType().Name + ": " + ex.Message;
            }
        }

        internal bool Save(IEnumerable<GeneralMethodEntry> entries)
        {
            string temporary = _path + ".tmp";
            try
            {
                // Preserve overrides for temporarily disabled mods or methods absent in this version.
                var merged = new Dictionary<string, bool>(_overrides, StringComparer.Ordinal);
                foreach (GeneralMethodEntry entry in entries)
                {
                    if (entry.Selected)
                        merged[entry.Key] = true;
                    else
                        merged.Remove(entry.Key);
                }
                string directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                var lines = new List<string>
                {
                    "# Valheim Profiler General Method selection",
                    "# + entry = enabled. Missing methods are disabled by default.",
                    "# Keys use plugin GUIDs, assembly/module names and complete signatures, not metadata tokens or versions.",
                    string.Empty
                };
                lines.AddRange(merged.Where(pair => pair.Value).OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => "+ " + pair.Key));
                File.WriteAllLines(temporary, lines, new UTF8Encoding(false));
                if (File.Exists(_path))
                    File.Replace(temporary, _path, null);
                else
                    File.Move(temporary, _path);
                _overrides.Clear();
                foreach (var pair in merged)
                    _overrides.Add(pair.Key, pair.Value);
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch { /* The previous selection file remains intact. */ }
            }
        }
    }
}
