#nullable disable

using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace ValheimProfiler.Tools.GeneralMethodProfiler;

internal sealed partial class GeneralMethodProfilerTool
{
    private readonly Dictionary<GeneralMethodEntry, CallNode> _roots = new Dictionary<GeneralMethodEntry, CallNode>();
    private InvocationFrame[] _callStack;
    private int _stackDepth;
    private int _maxTreeNodes;
    private int _treeNodeCount;
    private static long _sessionEpoch;
    private long _generation = Interlocked.Increment(ref _sessionEpoch);
    private long _nextSequence;
    private bool _insideHook;
    private long _depthLimitHits;
    private long _treeLimitHits;
    private long _stackMismatches;
    private long _hookFailures;
    private long _ignoredGcSamples;

    private void StartProfiling()
    {
        BeginStartProfiling();
    }

    private bool SaveSelection()
    {
        if (!_selectionPolicy.Save(_methodsByKey.Values))
        {
            _status = "Selection could not be saved: " + _selectionPolicy.LastError;
            _logger.LogWarning(_status);
            return false;
        }
        _selectionDirty = false;
        return true;
    }

    private void ApplySelection()
    {
        if (IsTransitioning)
        {
            _status = "Wait for the current instrumentation operation to finish.";
            return;
        }
        if (!SaveSelection())
            return;
        if (_profilingActive)
            BeginStopProfiling(freezeData: false, restartAfterRemoval: true);
        else
        {
            ResetAllStats();
            _status = "Selection saved. Start profiling to collect data.";
        }
        MarkSelectionRowsDirty();
    }

    private void StopProfiling()
    {
        BeginStopProfiling(freezeData: true, restartAfterRemoval: false);
        MarkViewDirty();
    }

    private bool StopProfilingInternal(bool freezeData)
    {
        AbortTransitionForSynchronousCleanup();
        bool wasActive = _profilingActive;
        _profilingActive = false;
        DiscardActiveStack();
        bool clean = true;
        if (_instrumentation.HasPending)
        {
            using var log = _instrumentationLog.BeginOperation("Removal");
            try
            {
                InstrumentationSession.CleanupResult result = _instrumentation.RemoveAll(log);
                clean = result.Verified;
                log.Summary(result.Summary, !clean);
            }
            catch (Exception ex)
            {
                clean = false;
                _safetyReport.Failure(null, "Removal interrupted", ex, restartRequired: true);
                log.Failure("Removal interrupted", ex);
                _instrumentation.RequireRestart(null, "Removal did not complete.");
            }
        }
        // Disable dispatch even if detour recovery failed. The session retains unresolved methods.
        _runtimeEntries.Clear();
        if (wasActive && freezeData)
            FreezeGeneralDataNow();
        clean &= !_app.InstrumentationSafety.RestartRequired;
        if (!clean)
            _status = _app.InstrumentationSafety.RestartMessage;
        return clean;
    }

    private void ResetAllStats()
    {
        DiscardActiveStack();
        // Coordinate with histogram processing before detaching any old tree statistics.
        lock (_analyticsProcessingLock)
        {
            ClearAnalyticsQueues();
            foreach (GeneralMethodEntry entry in _methodsByKey.Values)
            {
                entry.Contexts.Clear();
                if (_profilingActive)
                    entry.Stat?.Reset();
                else
                    entry.Stat = null;
            }
            _roots.Clear();
            _treeNodeCount = 0;
        }
        _treeSelectedMethod = null;
        _treeSelectedNode = null;
        _treeContexts.Clear();
        _treeChildren.Clear();
        _treeRootView = null;
        _treeCurrentView = null;
        _treeLimitHits = _depthLimitHits = _stackMismatches = _hookFailures = _ignoredGcSamples = 0;
        _statsFrozen = false;
        MarkViewDirty();
    }

    private void DiscardActiveStack()
    {
        if (_callStack != null && _stackDepth > 0)
            Array.Clear(_callStack, 0, _stackDepth);
        _stackDepth = 0;
        _generation = Interlocked.Increment(ref _sessionEpoch);
    }

    private static void TimingPrefix(MethodBase __originalMethod, ref InvocationToken __state)
    {
        __state = default;
        // Nothing below this gate, including Unity time and stack state, is accessed on workers.
        if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            return;
        GeneralMethodProfilerTool instance = _instance;
        long profilerOverheadStartTicks = instance != null && instance._excludeMeasuredNestedCalls && instance._stackDepth > 0
            ? Stopwatch.GetTimestamp()
            : 0L;
        if (instance == null || !instance._profilingActive || instance._app.InstrumentationSafety.RestartRequired || instance._insideHook)
            return;
        instance._insideHook = true;
        try
        {
            if (!instance._runtimeEntries.TryGetValue(__originalMethod, out GeneralMethodEntry entry))
                return;
            if (instance._stackDepth >= instance._callStack.Length)
            {
                // All deeper invocations see the same full stack and are also omitted.
                // They are never attached to an incorrect shallower parent.
                instance._depthLimitHits++;
                return;
            }
            int index = instance._stackDepth;
            CallNode parent = index > 0 ? instance._callStack[index - 1].Node : null;
            CallNode node = index > 0 && parent == null ? null : instance.GetOrCreateNode(entry, parent);
            long sequence = ++instance._nextSequence;
            instance._callStack[index] = new InvocationFrame { Entry = entry, Node = node, Sequence = sequence };
            instance._stackDepth++;
            __state = new InvocationToken { Generation = instance._generation, Sequence = sequence, Index = index };
            // First-observation allocations and dictionary work are outside this invocation's timer.
            long startTicks = Stopwatch.GetTimestamp();
            instance._callStack[index].StartTicks = startTicks;
            if (profilerOverheadStartTicks > 0)
                instance._callStack[index].ProfilerPrefixTicks = Math.Max(0L, startTicks - profilerOverheadStartTicks);
            // In self mode this prefix cost is charged to the parent only when the child completes.
        }
        catch
        {
            instance._hookFailures++;
            instance.DiscardActiveStack();
            __state = default;
        }
        finally
        {
            instance._insideHook = false;
        }
    }

    private static void TimingFinalizer(ref InvocationToken __state)
    {
        InvocationToken token = __state;
        // Harmony can re-enter finalizers when a later finalizer throws. Consume exactly once.
        __state = default;
        if (token.Sequence == 0)
            return;
        GeneralMethodProfilerTool instance = _instance;
        bool excludeNested = instance != null && instance._excludeMeasuredNestedCalls;
        long endTicks = excludeNested ? Stopwatch.GetTimestamp() : 0L;
        if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            return;
        if (instance == null || !instance._profilingActive || instance._app.InstrumentationSafety.RestartRequired || instance._insideHook || token.Generation != instance._generation)
            return;
        if (!excludeNested)
            endTicks = Stopwatch.GetTimestamp();
        instance._insideHook = true;
        try
        {
            instance.CompleteInvocation(token, endTicks);
        }
        catch
        {
            instance._hookFailures++;
            instance.DiscardActiveStack();
        }
        finally
        {
            instance._insideHook = false;
            if (excludeNested && instance._stackDepth > 0)
            {
                long profilerOverheadEndTicks = Stopwatch.GetTimestamp();
                instance._callStack[instance._stackDepth - 1].ChildTicks += Math.Max(0L, profilerOverheadEndTicks - endTicks);
            }
        }
        // A void finalizer observes execution without replacing or suppressing the original exception.
    }

    private CallNode GetOrCreateNode(GeneralMethodEntry entry, CallNode parent)
    {
        Dictionary<GeneralMethodEntry, CallNode> siblings = parent == null ? _roots : parent.Children;
        if (siblings.TryGetValue(entry, out CallNode existing))
            return existing;
        if (_treeNodeCount >= _maxTreeNodes)
        {
            _treeLimitHits++;
            return null;
        }
        var node = new CallNode { Id = _treeNodeCount + 1, Entry = entry, Parent = parent, Depth = parent == null ? 0 : parent.Depth + 1 };
        node.Root = parent?.Root ?? node;
        siblings.Add(entry, node);
        entry.Contexts.Add(node);
        _treeNodeCount++;
        return node;
    }

    private void CompleteInvocation(InvocationToken token, long endTicks)
    {
        if (token.Index != _stackDepth - 1 || token.Index < 0 ||
            _callStack[token.Index].Sequence != token.Sequence)
        {
            _stackMismatches++;
            DiscardActiveStack();
            return;
        }
        InvocationFrame sample = _callStack[token.Index];
        _callStack[token.Index] = default;
        _stackDepth--;
        long ticks = Math.Max(0L, endTicks - sample.StartTicks);
        if (_stackDepth > 0)
            _callStack[_stackDepth - 1].ChildTicks += ticks + Math.Max(0L, sample.ProfilerPrefixTicks);

        double inclusiveMs = ticks * MsPerTick;
        long selfTicks = Math.Max(0L, ticks - sample.ChildTicks);
        double selfMs = selfTicks * MsPerTick;
        double measuredMs = _excludeMeasuredNestedCalls ? selfMs : inclusiveMs;
        bool gcSample = inclusiveMs >= GcCheckMinSampleMs &&
            (GC.CollectionCount(0) != _lastObservedGc0 || GC.CollectionCount(1) != _lastObservedGc1 || GC.CollectionCount(2) != _lastObservedGc2);
        if (gcSample && _ignoreGcAllocations)
        {
            // Structural context is retained, but this invocation contributes to no statistics.
            // Child duration was already subtracted from its parent even when this sample is rejected.
            _ignoredGcSamples++;
            return;
        }

        int frame = Time.frameCount;
        float now = Time.realtimeSinceStartup;
        SetCurrentRealtime(now);
        AddSample(sample.Entry.Stat, measuredMs, frame, now, gcSample);
        if (sample.Node == null)
            return;

        CallNode node = sample.Node;
        if (node.Stat == null)
            node.Stat = new RollingProfilerStat(useSampleTimestamps: true);
        AddSample(node.Stat, measuredMs, frame, now, gcSample);
        node.Calls++;
        node.InclusiveMs += inclusiveMs;
        node.SelfMs += selfMs;
        node.LastSelfMs = selfMs;
        node.MaxSelfMs = Math.Max(node.MaxSelfMs, selfMs);
    }

    private void AddSample(RollingProfilerStat stat, double milliseconds, int frame, float now, bool gcSample)
    {
        if (stat != null && stat.Add(milliseconds, frame, now, gcSample))
            QueueAnalyticsWork(stat);
    }
}
