#nullable disable

using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace ValheimProfiler.Tools.MonoBehaviourProfiler;

internal sealed partial class MonoBehaviourProfilerTool
{
    private static void TimingPrefix(ref long __state) =>
        __state = Stopwatch.GetTimestamp();

    private static Exception TimingFinalizer(Exception __exception, long __state, MethodBase __originalMethod, object __instance)
    {
        try
        {
            if (__state <= 0 ||
                Thread.CurrentThread.ManagedThreadId != _mainThreadId)
                return __exception;

            MonoBehaviourProfilerTool instance = _instance;
            if (instance == null || !instance._profilingActive)
                return __exception;

            long ticks = Stopwatch.GetTimestamp() - __state;
            if (ticks < 0)
                return __exception;

            double ms = ticks * MsPerTick;
            bool gcSample = false;

            if (ms >= GcCheckMinSampleMs)
            {
                gcSample =
                    GC.CollectionCount(0) != instance._lastObservedGc0 ||
                    GC.CollectionCount(1) != instance._lastObservedGc1 ||
                    GC.CollectionCount(2) != instance._lastObservedGc2;
            }

            int frame = Time.frameCount;
            float now = Time.realtimeSinceStartup;
            Type runtimeType = __instance?.GetType();

            instance.SetCurrentRealtime(now);
            instance.Record(__originalMethod, runtimeType, ms, frame, now, gcSample);
        }
        catch
        {
        }

        return __exception;
    }

    private void StartProfiling()
    {
        if (_profilingActive)
            return;

        if (!_listReady)
            RefreshBehaviourList();

        if (!_listReady)
            return;

        try
        {
            ApplySelectionPolicy();
            ResetAllStats();
            StartProfilingCore();
        }
        catch (Exception ex)
        {
            _status = $"Start error: {ex.GetType().Name}: {ex.Message}";
            _logger.LogError(ex);
        }
    }

    private void StartProfilingCore()
    {
        var map = new Dictionary<MethodBase, List<RuntimeBehaviourEntry>>();
        int selectedCount = 0;

        lock (_lock)
        {
            foreach (BehaviourTypeEntry typeEntry in _types)
            {
                foreach (BehaviourMethodEntry entry in typeEntry.Methods)
                {
                    if (!entry.Selected || entry.Method == null)
                        continue;

                    selectedCount++;

                    if (!map.TryGetValue(entry.Method, out List<RuntimeBehaviourEntry> runtimeEntries))
                    {
                        runtimeEntries = new List<RuntimeBehaviourEntry>();
                        map[entry.Method] = runtimeEntries;
                    }

                    runtimeEntries.Add(new RuntimeBehaviourEntry(typeEntry.Type, entry));
                }
            }

            _runtimeEntries = map.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
            _instrumentedMethods.Clear();
        }

        if (selectedCount == 0)
        {
            _status = "No MonoBehaviour methods are selected.";
            return;
        }

        var prefix = new HarmonyMethod(AccessTools.Method(typeof(MonoBehaviourProfilerTool), nameof(TimingPrefix)))
        {
            priority = Priority.Last
        };

        var finalizer = new HarmonyMethod(AccessTools.Method(typeof(MonoBehaviourProfilerTool), nameof(TimingFinalizer)))
        {
            priority = Priority.First
        };

        int instrumented = 0;

        foreach (MethodBase method in map.Keys)
        {
            try
            {
                _harmony.Patch(method, prefix: prefix, finalizer: finalizer);
                _instrumentedMethods.Add(method);
                instrumented++;
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"Could not instrument MonoBehaviour method {method}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (instrumented == 0)
        {
            lock (_lock)
            {
                _runtimeEntries = new Dictionary<MethodBase, RuntimeBehaviourEntry[]>();
                _instrumentedMethods.Clear();
            }

            _status = "No selected MonoBehaviour methods could be instrumented.";
            return;
        }

        _statsFrozen = false;
        _profilingActive = true;
        _status = $"Profiling enabled. Selected entries: {selectedCount}. Instrumented methods: {instrumented}.";
        MarkViewDirty();
    }

    private void StopProfiling()
    {
        try
        {
            StopProfilingInternal(true);
            _status = "Profiling paused. Data frozen.";
            MarkViewDirty();
        }
        catch (Exception ex)
        {
            _status = $"Pause error: {ex.GetType().Name}: {ex.Message}";
            _logger.LogError(ex);
        }
    }

    private void StopProfilingInternal(bool freezeData)
    {
        bool wasActive = _profilingActive;
        _profilingActive = false;
        _harmony.UnpatchSelf();

        lock (_lock)
        {
            _instrumentedMethods.Clear();
            _runtimeEntries = new Dictionary<MethodBase, RuntimeBehaviourEntry[]>();
        }

        if (!wasActive || !freezeData)
            return;

        float now = Time.realtimeSinceStartup;
        int frame = Time.frameCount;

        SetCurrentRealtime(now);
        FlushAnalyticsAt(now);

        _frozenRealtime = now;
        _frozenFrame = frame;
        _statsFrozen = true;
    }

    private void ApplySelection()
    {
        bool restart = _profilingActive;

        if (restart)
            StopProfilingInternal(false);

        ApplySelectionPolicy();
        ResetAllStats();

        if (restart)
            StartProfilingCore();
        else
            _status = "Selection applied. Start profiling to collect data.";

        _selectionDirty = false;
        MarkSelectionRowsDirty();
        MarkViewDirty();
    }

    private void ApplySelectionPolicy()
    {
        List<BehaviourMethodEntry> entries;
        lock (_lock)
            entries = _types.SelectMany(type => type.Methods).ToList();

        _selectionPolicy.Save(entries);
    }

    private void ResetAllStats()
    {
        lock (_lock)
        {
            foreach (BehaviourTypeEntry typeEntry in _types)
            {
                foreach (BehaviourMethodEntry entry in typeEntry.Methods)
                    entry.Stat.Reset();
            }
        }

        ClearAnalyticsQueues();
        _statsFrozen = false;
        MarkViewDirty();
    }

    private void Record(
        MethodBase instrumentedMethod,
        Type runtimeType,
        double elapsedMs,
        int frame,
        float now,
        bool gcSample)
    {
        if (!_profilingActive || instrumentedMethod == null)
            return;

        Dictionary<MethodBase, RuntimeBehaviourEntry[]> map = _runtimeEntries;
        if (!map.TryGetValue(instrumentedMethod, out RuntimeBehaviourEntry[] entries) ||
            entries == null ||
            entries.Length == 0)
            return;

        bool exactMatchExists = false;
        if (runtimeType != null)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i] != null && entries[i].RuntimeType == runtimeType)
                {
                    exactMatchExists = true;
                    break;
                }
            }
        }

        for (int i = 0; i < entries.Length; i++)
        {
            RuntimeBehaviourEntry runtimeEntry = entries[i];
            if (runtimeEntry == null || runtimeEntry.Entry == null)
                continue;

            if (runtimeType != null)
            {
                if (exactMatchExists)
                {
                    if (runtimeEntry.RuntimeType != runtimeType)
                        continue;
                }
                else if (runtimeEntry.RuntimeType == null || !runtimeEntry.RuntimeType.IsAssignableFrom(runtimeType))
                {
                    continue;
                }
            }

            RollingProfilerStat stat = runtimeEntry.Entry.Stat;
            bool queued = stat.Add(elapsedMs, frame, now, gcSample);
            if (queued)
                QueueAnalyticsWork(stat);
        }
    }
}