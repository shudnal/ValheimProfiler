#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ValheimProfiler.Core.Profiling;

/// <summary>Session-lifetime diagnostics, independent of timing resets and optional file logging.</summary>
internal sealed class InstrumentationReport
{
    private readonly Dictionary<MethodBase, Entry> _methods = new Dictionary<MethodBase, Entry>();
    private readonly List<Entry> _entries = new List<Entry>();
    private Entry _session;

    internal IReadOnlyList<Entry> Entries => _entries;
    internal int Revision { get; private set; }
    private int _countRevision = -1;
    private int _failureCount;
    private int _otherSkipCount;
    internal int FailureCount { get { RefreshCounts(); return _failureCount; } }
    internal int OtherSkipCount { get { RefreshCounts(); return _otherSkipCount; } }
    internal bool HasFailures => FailureCount > 0;

    private void RefreshCounts()
    {
        if (_countRevision == Revision)
            return;
        _countRevision = Revision;
        _failureCount = _otherSkipCount = 0;
        foreach (Entry entry in _entries)
        {
            if (entry.HasFailure) _failureCount++;
            else _otherSkipCount++;
        }
    }

    /// <summary>Refresh expected exclusions, never erase a failure from an earlier capture.</summary>
    internal void BeginCapture()
    {
        foreach (Entry entry in _entries.Where(entry => !entry.HasFailure))
        {
            if (entry.MethodBase != null)
                _methods.Remove(entry.MethodBase);
            else
                _session = null;
        }
        _entries.RemoveAll(entry => !entry.HasFailure);
        Revision++;
    }

    internal void Skip(MethodBase method, string reason)
    {
        bool inspectionFailure = InstrumentationSafety.IsInspectionFailure(reason);
        Entry entry = GetEntry(method);
        entry.HasFailure |= inspectionFailure;
        AddMessage(entry, inspectionFailure ? "Preflight failed" : "Not measured", reason);
    }

    internal void Failure(MethodBase method, string stage, Exception exception, bool restartRequired = false) =>
        Failure(method, stage, InstrumentationLog.ExceptionSummary(exception), restartRequired);

    internal void Failure(MethodBase method, string stage, string reason, bool restartRequired = false)
    {
        Entry entry = GetEntry(method);
        entry.HasFailure = true;
        entry.RequiresRestart |= restartRequired;
        AddMessage(entry, stage, reason);
    }

    internal void Recovery(MethodBase method, string result)
    {
        Entry entry = GetEntry(method);
        // A successful metadata check does not clear an earlier native/detour uncertainty.
        AddMessage(entry, "Cleanup", result);
    }

    private void AddMessage(Entry entry, string stage, string reason)
    {
        string text = stage + ": " + (reason ?? "No additional details.");
        if (!entry.Messages.Contains(text))
            entry.Messages.Add(text);
        entry.Details = string.Join(" | ", entry.Messages);
        Revision++;
    }

    private Entry GetEntry(MethodBase method)
    {
        if (method == null && _session != null)
            return _session;
        if (method != null && _methods.TryGetValue(method, out Entry existing))
            return existing;

        string assembly = "(session)";
        string identity = "(session)";
        try
        {
            if (method != null)
            {
                assembly = method.DeclaringType?.Assembly.GetName().Name ?? "(unknown assembly)";
                identity = InstrumentationSafety.GetSignature(method);
            }
        }
        catch { identity = "(unavailable method identity)"; }
        var entry = new Entry { MethodBase = method, Assembly = assembly, Method = identity };
        _entries.Add(entry);
        if (method == null)
            _session = entry;
        else
            _methods.Add(method, entry);
        return entry;
    }

    internal sealed class Entry
    {
        internal MethodBase MethodBase;
        internal string Assembly;
        internal string Method;
        internal string Details = string.Empty;
        internal readonly List<string> Messages = new List<string>();
        internal bool HasFailure;
        internal bool RequiresRestart;
        internal int SortPriority => RequiresRestart ? 0 : HasFailure ? 1 : 2;
    }
}
