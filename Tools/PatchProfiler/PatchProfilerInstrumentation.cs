#nullable disable

using BepInEx.Bootstrap;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;

namespace ValheimProfiler.Tools.PatchProfiler;

internal sealed partial class PatchProfilerTool
{
    [ThreadStatic]
    private static List<PatchMeasurementFrame> _activePatchMeasurementStack;

    private static long _nextPatchMeasurementSequence;

    private static long BeginPatchProfilerOverhead(PatchProfilerTool instance)
    {
        if (instance == null || !instance._excludeMeasuredNestedCalls)
            return 0L;
        List<PatchMeasurementFrame> stack = _activePatchMeasurementStack;
        return stack != null && stack.Count > 0 ? Stopwatch.GetTimestamp() : 0L;
    }

    private static void CompletePatchProfilerOverhead(long startTicks)
    {
        if (startTicks <= 0)
            return;
        try
        {
            List<PatchMeasurementFrame> stack = _activePatchMeasurementStack;
            if (stack == null || stack.Count == 0)
                return;
            long endTicks = Stopwatch.GetTimestamp();
            int parentIndex = stack.Count - 1;
            PatchMeasurementFrame parent = stack[parentIndex];
            parent.ChildTicks += Math.Max(0L, endTicks - startTicks);
            stack[parentIndex] = parent;
        }
        catch
        {
        }
    }

    private static void BeginPatchMeasurement(long profilerOverheadStartTicks, out long sequence, out int index, out long startTicks)
    {
        _activePatchMeasurementStack ??= new List<PatchMeasurementFrame>(16);
        index = _activePatchMeasurementStack.Count;
        sequence = Interlocked.Increment(ref _nextPatchMeasurementSequence);
        var frame = new PatchMeasurementFrame
        {
            Sequence = sequence,
            ChildTicks = 0
        };
        _activePatchMeasurementStack.Add(frame);
        startTicks = Stopwatch.GetTimestamp();
        frame.StartTicks = startTicks;
        if (profilerOverheadStartTicks > 0)
            frame.ProfilerPrefixTicks = Math.Max(0L, startTicks - profilerOverheadStartTicks);
        _activePatchMeasurementStack[index] = frame;
    }

    private static bool CompletePatchMeasurement(long sequence, int index, long endTicks, out long inclusiveTicks, out long childTicks)
    {
        inclusiveTicks = 0;
        childTicks = 0;
        List<PatchMeasurementFrame> stack = _activePatchMeasurementStack;
        if (sequence == 0 || stack == null || index < 0 || index != stack.Count - 1 || stack[index].Sequence != sequence)
        {
            stack?.Clear();
            return false;
        }

        PatchMeasurementFrame frame = stack[index];
        stack.RemoveAt(index);
        inclusiveTicks = Math.Max(0L, endTicks - frame.StartTicks);
        childTicks = Math.Max(0L, frame.ChildTicks);

        if (stack.Count > 0)
        {
            int parentIndex = stack.Count - 1;
            PatchMeasurementFrame parent = stack[parentIndex];
            // Self mode excludes both the physical child interval and profiler prefix work before that interval.
            parent.ChildTicks += inclusiveTicks + Math.Max(0L, frame.ProfilerPrefixTicks);
            stack[parentIndex] = parent;
        }

        return true;
    }

    private static void ResetPatchMeasurementStack()
    {
        _activePatchMeasurementStack?.Clear();
        _activeTranspilerCallTimings?.Clear();
        _activeTranspilerCallGroups?.Clear();
    }

    private static void TimingPrefix(MethodBase __originalMethod, ref TimingState __state)
    {
        __state.StartTicks = 0;
        __state.MeasurementSequence = 0;
        __state.MeasurementIndex = -1;
        __state.ActiveTranspiledTarget = null;
        __state.TranspilerCallStackDepth = -1;
        __state.TranspilerCallGroupDepth = -1;

        try
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
                return;

            var inst = _instance;
            long profilerOverheadStartTicks = inst != null && inst._excludeMeasuredNestedCalls
                ? BeginPatchProfilerOverhead(inst)
                : 0L;
            if (inst == null || !inst._profilingActive || inst._app.InstrumentationSafety.RestartRequired)
                return;

            if (inst.IsTranspiledTarget(__originalMethod))
            {
                __state.TranspilerCallStackDepth = CurrentInjectedTranspilerCallDepth();
                __state.TranspilerCallGroupDepth = CurrentInjectedTranspilerCallGroupDepth();
                PushActiveTranspiledTarget(__originalMethod);
                __state.ActiveTranspiledTarget = __originalMethod;
            }

            // Match General Method Profiler semantics: profiler bookkeeping is outside the measured interval.
            if (inst._excludeMeasuredNestedCalls)
                BeginPatchMeasurement(profilerOverheadStartTicks, out __state.MeasurementSequence, out __state.MeasurementIndex, out __state.StartTicks);
            else
                __state.StartTicks = Stopwatch.GetTimestamp();
        }
        catch
        {
        }
    }

    private static Exception TimingFinalizer(Exception __exception, TimingState __state, MethodBase __originalMethod)
    {
        bool excludeNested = __state.MeasurementSequence != 0;
        long end = excludeNested && __state.StartTicks > 0 ? Stopwatch.GetTimestamp() : 0L;
        try
        {
            if (__state.StartTicks <= 0)
                return __exception;

            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
                return __exception;

            if (!excludeNested)
                end = Stopwatch.GetTimestamp();
            long ticks = end - __state.StartTicks;
            long childTicks = 0;
            if (excludeNested)
            {
                if (!CompletePatchMeasurement(__state.MeasurementSequence, __state.MeasurementIndex, end, out ticks, out childTicks))
                {
                    ticks = end - __state.StartTicks;
                    childTicks = 0;
                }
            }
            if (ticks < 0)
                return __exception;

            var inst = _instance;
            if (inst == null || !inst._profilingActive || inst._app.InstrumentationSafety.RestartRequired)
                return __exception;

            double inclusiveMs = ticks * MsPerTick;
            double measuredMs = (excludeNested ? Math.Max(0L, ticks - childTicks) : ticks) * MsPerTick;

            bool gcSample = false;

            if (inclusiveMs >= GcCheckMinSampleMs)
            {
                gcSample =
                    GC.CollectionCount(0) != inst._lastObservedGc0 ||
                    GC.CollectionCount(1) != inst._lastObservedGc1 ||
                    GC.CollectionCount(2) != inst._lastObservedGc2;
            }

            if (gcSample && inst._ignoreGcAllocations)
                return __exception;

            int frame = Time.frameCount;
            float now = Time.realtimeSinceStartup;
            MethodBase activeTranspiledTarget = CurrentActiveTranspiledTarget();

            inst.SetCurrentRealtime(now);
            inst.Record(__originalMethod, measuredMs, frame, now, gcSample, activeTranspiledTarget);
        }
        catch
        {
        }
        finally
        {
            if (__state.ActiveTranspiledTarget != null)
                PopActiveTranspiledTarget(__state.ActiveTranspiledTarget);

            TrimInjectedTranspilerCallStack(__state.TranspilerCallStackDepth, __state.TranspilerCallGroupDepth);
            if (excludeNested)
                CompletePatchProfilerOverhead(end);
        }

        return __exception;
    }

    private void Record(MethodBase instrumentedMethod, double elapsedMs, int frame, float now, bool gcSample, MethodBase activeTranspiledTarget)
    {
        if (!_profilingActive)
            return;

        var runtimeMap = _runtimeEntriesByInstrumentedMethod;
        if (runtimeMap == null || !runtimeMap.TryGetValue(instrumentedMethod, out var entries) || entries == null || entries.Length == 0)
            return;

        for (int i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];

            if (!entry.AcceptsActiveTranspiledTarget(activeTranspiledTarget))
                continue;

            var stat = entry.Stat;
            if (stat == null)
                continue;

            bool queued = stat.Add(elapsedMs, frame, now, gcSample);

            if (queued)
                QueueAnalyticsWork(stat);
        }
    }

    private void RebuildRuntimeProfileMapLocked()
    {
        var map = new Dictionary<MethodBase, List<RuntimeProfileEntry>>(Math.Max(16, _entriesByInstrumentedMethod.Count));
        var transpiledTargets = new HashSet<MethodBase>();
        var callPlansByTarget = new Dictionary<MethodBase, List<RuntimeTranspilerCallPlan>>();
        var callStatsByEntryId = new Dictionary<int, PatchStat>();

        foreach (var kv in _context)
        {
            var ctx = kv.Value;
            if (!IsContextEnabledLocked(ctx))
                continue;

            if (!_stats.TryGetValue(kv.Key, out var stat))
                continue;

            if (ctx.IsTranspilerCallEntry)
            {
                MethodBase target = ctx.TranspiledTargetMethod;
                MethodBase calledMethod = ctx.InstrumentedMethod;
                int[] ordinals = ctx.GetInjectedCallOrdinals();

                if (target != null &&
                    calledMethod != null &&
                    ordinals.Length > 0 &&
                    IsCallSiteTimingSafe(calledMethod))
                {
                    if (!callPlansByTarget.TryGetValue(target, out List<RuntimeTranspilerCallPlan> plans))
                    {
                        plans = new List<RuntimeTranspilerCallPlan>();
                        callPlansByTarget[target] = plans;
                    }

                    plans.Add(new RuntimeTranspilerCallPlan
                    {
                        EntryId = kv.Key,
                        CalledMethodIdentity = GetMethodIdentity(calledMethod),
                        MethodOrdinals = new HashSet<int>(ordinals)
                    });

                    callStatsByEntryId[kv.Key] = stat;
                    transpiledTargets.Add(target);
                    continue;
                }
            }

            var method = ctx.InstrumentedMethod;
            if (method == null)
                continue;

            if (!map.TryGetValue(method, out var list))
            {
                list = new List<RuntimeProfileEntry>(1);
                map[method] = list;
            }

            list.Add(new RuntimeProfileEntry(ctx, stat));

            if (ctx.IsTranspiledTargetEntry)
                transpiledTargets.Add(method);

            ctx.AddRequiredActiveTranspiledTargetsTo(transpiledTargets);
        }

        _runtimeEntriesByInstrumentedMethod = map.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        _runtimeTranspiledTargets = transpiledTargets;
        _runtimeTranspilerCallPlans = callPlansByTarget.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        _runtimeTranspilerCallStats = callStatsByEntryId;
    }

    private bool IsTranspiledTarget(MethodBase method)
    {
        if (method == null)
            return false;

        var runtimeTargets = _runtimeTranspiledTargets;
        return runtimeTargets != null && runtimeTargets.Contains(method);
    }

    private bool IsContextEnabledLocked(PatchContext ctx)
    {
        if (ctx == null)
            return false;

        if (!_modsToProfile.TryGetValue(ctx.ModGuid, out var enabled) || !enabled)
            return false;

        if (ctx.ModGuid != TranspiledTargetsGuid)
            return true;

        if (ctx.RelatedModGuids.Count == 0)
            return true;

        foreach (var guid in ctx.RelatedModGuids)
        {
            if (_modsToProfile.TryGetValue(guid, out var relatedEnabled) && relatedEnabled)
                return true;
        }

        return false;
    }

    private static void PushActiveTranspiledTarget(MethodBase method)
    {
        if (_activeTranspiledTargetStack == null)
            _activeTranspiledTargetStack = new List<MethodBase>(4);

        _activeTranspiledTargetStack.Add(method);
    }

    private static MethodBase CurrentActiveTranspiledTarget()
    {
        var stack = _activeTranspiledTargetStack;
        if (stack == null || stack.Count == 0)
            return null;

        return stack[stack.Count - 1];
    }

    private static void PopActiveTranspiledTarget(MethodBase method)
    {
        var stack = _activeTranspiledTargetStack;
        if (stack == null || stack.Count == 0)
            return;

        int last = stack.Count - 1;

        if (ReferenceEquals(stack[last], method) || Equals(stack[last], method))
        {
            stack.RemoveAt(last);
            return;
        }

        for (int i = last; i >= 0; i--)
        {
            if (ReferenceEquals(stack[i], method) || Equals(stack[i], method))
            {
                stack.RemoveAt(i);
                return;
            }
        }
    }

}