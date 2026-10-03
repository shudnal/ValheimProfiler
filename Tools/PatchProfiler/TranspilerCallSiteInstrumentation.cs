#nullable disable

using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using UnityEngine;

namespace ValheimProfiler.Tools.PatchProfiler;

internal sealed partial class PatchProfilerTool
{
    private sealed class RuntimeTranspilerCallPlan
    {
        public int EntryId;
        public string CalledMethodIdentity;
        public HashSet<int> MethodOrdinals;
    }

    private struct ActiveTranspilerCallTiming
    {
        public int EntryId;
        public PatchStat Stat;
        public long StartTicks;
    }

    private sealed class ActiveTranspilerCallGroup
    {
        public readonly List<PatchStat> Stats = new List<PatchStat>(2);
        public long MeasurementSequence;
        public int MeasurementIndex = -1;
        public long StartTicks;
        public long InclusiveTicks;
        public long ChildTicks;
        public long ProfilerPrefixStartTicks;
        public long ProfilerFinalizerStartTicks;
        public bool Completed;
    }

    private Dictionary<MethodBase, RuntimeTranspilerCallPlan[]> _runtimeTranspilerCallPlans =
        new Dictionary<MethodBase, RuntimeTranspilerCallPlan[]>(0);

    private Dictionary<int, PatchStat> _runtimeTranspilerCallStats =
        new Dictionary<int, PatchStat>(0);

    private readonly object _transpilerCallWrapperLock = new object();
    private readonly Dictionary<string, MethodInfo> _transpilerCallWrappers =
        new Dictionary<string, MethodInfo>(StringComparer.Ordinal);

    private static int _nextTranspilerCallWrapperId;

    [ThreadStatic]
    private static MethodBase _callSiteInstallationTarget;
    private readonly ConcurrentQueue<CallSiteRebuildFailure> _callSiteRebuildFailures = new ConcurrentQueue<CallSiteRebuildFailure>();

    private sealed class CallSiteRebuildFailure
    {
        internal MethodBase Method;
        internal Exception Exception;
    }

    private bool ProcessCallSiteRebuildFailures()
    {
        if (_callSiteRebuildFailures.IsEmpty)
            return false;
        using var log = _instrumentationLog.BeginOperation("Reapplication");
        int failures = 0;
        while (_callSiteRebuildFailures.TryDequeue(out CallSiteRebuildFailure failure))
        {
            failures++;
            _safetyReport.Failure(failure.Method, "Call-site rebuild failed", failure.Exception);
            _app.InstrumentationSafety.Quarantine(failure.Method, InstrumentationLog.ExceptionSummary(failure.Exception));
            log.MethodFailure(failure.Method, "Call-site rebuild failed; untouched source returned; measurements stopped", failure.Exception);
        }
        log.Summary("Deferred call-site rebuild failures: " + failures + ". Measurements stopped.", hasFailures: true);
        BeginStopProfiling(freezeData: true, restartAfterRemoval: false);
        _status = "Call-site rebuild failed. Removing instrumentation; see Skipped methods.";
        MarkViewDirty();
        return true;
    }


    [ThreadStatic]
    private static List<ActiveTranspilerCallTiming> _activeTranspilerCallTimings;

    [ThreadStatic]
    private static List<ActiveTranspilerCallGroup> _activeTranspilerCallGroups;

    private static bool IsCallSiteTimingSafe(MethodBase calledMethod)
    {
        if (!(calledMethod is MethodInfo methodInfo))
            return false;

        Type returnType = methodInfo.ReturnType;
        return returnType != null &&
               !returnType.IsByRef &&
               !returnType.IsPointer &&
               returnType != typeof(TypedReference) &&
               (methodInfo.CallingConvention & CallingConventions.VarArgs) == 0;
    }

    private static IEnumerable<CodeInstruction> InjectedCallTimingTranspiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase __originalMethod)
    {
        List<CodeInstruction> source = instructions?.ToList() ?? new List<CodeInstruction>();
        PatchProfilerTool instance = _instance;

        if (instance == null || __originalMethod == null)
            return source;

        Dictionary<MethodBase, RuntimeTranspilerCallPlan[]> plansByTarget = instance._runtimeTranspilerCallPlans;
        if (plansByTarget == null ||
            !plansByTarget.TryGetValue(__originalMethod, out RuntimeTranspilerCallPlan[] plans) ||
            plans == null ||
            plans.Length == 0)
        {
            return source;
        }

        try
        {
            // Work on a separate list so an exception cannot return a partially rewritten source.
            return InjectCallSiteTiming(new List<CodeInstruction>(source), plans, instance);
        }
        catch (Exception ex)
        {
            if (Equals(_callSiteInstallationTarget, __originalMethod))
                throw; // The tracked install owns the rollback and method-level error report.
            instance._callSiteRebuildFailures.Enqueue(new CallSiteRebuildFailure { Method = __originalMethod, Exception = ex });
            return source; // Do not break a foreign Harmony rebuild because profiling failed. No partial edits escape.

        }
    }

    private static List<CodeInstruction> InjectCallSiteTiming(
        List<CodeInstruction> source,
        IReadOnlyList<RuntimeTranspilerCallPlan> plans,
        PatchProfilerTool instance)
    {
        var plansByMethod = plans
            .Where(plan => plan != null &&
                           plan.EntryId >= 0 &&
                           !string.IsNullOrWhiteSpace(plan.CalledMethodIdentity) &&
                           plan.MethodOrdinals != null &&
                           plan.MethodOrdinals.Count > 0)
            .GroupBy(plan => plan.CalledMethodIdentity, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        if (plansByMethod.Count == 0)
            return source;

        var occurrenceByMethod = new Dictionary<string, int>(StringComparer.Ordinal);
        var entriesByCallIndex = new Dictionary<int, List<int>>();

        for (int i = 0; i < source.Count; i++)
        {
            CodeInstruction instruction = source[i];
            if (instruction == null ||
                (instruction.opcode != OpCodes.Call && instruction.opcode != OpCodes.Callvirt) ||
                !(instruction.operand is MethodBase calledMethod))
            {
                continue;
            }

            string identity = GetMethodIdentity(calledMethod);
            occurrenceByMethod.TryGetValue(identity, out int ordinal);
            occurrenceByMethod[identity] = ordinal + 1;

            if (!plansByMethod.TryGetValue(identity, out RuntimeTranspilerCallPlan[] methodPlans))
                continue;

            for (int planIndex = 0; planIndex < methodPlans.Length; planIndex++)
            {
                RuntimeTranspilerCallPlan plan = methodPlans[planIndex];
                if (!plan.MethodOrdinals.Contains(ordinal))
                    continue;

                if (!entriesByCallIndex.TryGetValue(i, out List<int> entries))
                {
                    entries = new List<int>(1);
                    entriesByCallIndex[i] = entries;
                }

                if (!entries.Contains(plan.EntryId))
                    entries.Add(plan.EntryId);
            }
        }

        foreach (KeyValuePair<int, List<int>> pair in entriesByCallIndex)
        {
            int callIndex = pair.Key;
            CodeInstruction callInstruction = source[callIndex];

            if (!(callInstruction?.operand is MethodInfo calledMethod))
                continue;

            // A constrained or tail-prefixed call cannot be replaced by a normal static wrapper
            // without also reproducing the prefix semantics inside that wrapper.
            if (HasUnsupportedCallPrefix(source, callIndex))
                continue;

            int[] entryIds = pair.Value
                .Distinct()
                .OrderBy(value => value)
                .ToArray();

            MethodInfo wrapper = instance.GetOrCreateTranspilerCallWrapper(
                calledMethod,
                callInstruction.opcode,
                entryIds);

            if (wrapper == null)
                continue;

            // The wrapper has the exact same stack signature as the original call. Replacing the
            // operand avoids issuing Begin/End calls while the original arguments are already on
            // the evaluation stack, which is unreliable on Mono for calls with parameters.
            var replacement = new CodeInstruction(callInstruction)
            {
                opcode = OpCodes.Call,
                operand = wrapper
            };

            source[callIndex] = replacement;
        }

        return source;
    }

    private MethodInfo GetOrCreateTranspilerCallWrapper(
        MethodInfo calledMethod,
        OpCode originalCallOpcode,
        IReadOnlyList<int> entryIds)
    {
        if (calledMethod == null || entryIds == null || entryIds.Count == 0)
            return null;

        if (!IsCallSiteTimingSafe(calledMethod))
            return null;

        string key = GetMethodIdentity(calledMethod) + "|" +
                     originalCallOpcode.Value + "|" +
                     string.Join(",", entryIds);

        lock (_transpilerCallWrapperLock)
        {
            if (_transpilerCallWrappers.TryGetValue(key, out MethodInfo existing))
                return existing;

            try
            {
                MethodInfo wrapper = CreateTranspilerCallWrapper(
                    calledMethod,
                    originalCallOpcode,
                    entryIds);

                if (wrapper != null)
                    _transpilerCallWrappers[key] = wrapper;

                return wrapper;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Could not create the transpiler-call timing wrapper for " +
                    GetMethodDisplay(calledMethod) + ".", ex);
            }
        }
    }

    private static MethodInfo CreateTranspilerCallWrapper(
        MethodInfo calledMethod,
        OpCode originalCallOpcode,
        IReadOnlyList<int> entryIds)
    {
        if (calledMethod == null || calledMethod.DeclaringType == null)
            return null;

        if (calledMethod.ContainsGenericParameters || calledMethod.DeclaringType.ContainsGenericParameters)
            return null;

        ParameterInfo[] originalParameters = calledMethod.GetParameters();
        int instanceOffset = calledMethod.IsStatic ? 0 : 1;
        var wrapperParameters = new Type[originalParameters.Length + instanceOffset];

        if (!calledMethod.IsStatic)
        {
            Type declaringType = calledMethod.DeclaringType;
            wrapperParameters[0] = declaringType.IsValueType
                ? declaringType.MakeByRefType()
                : declaringType;
        }

        for (int i = 0; i < originalParameters.Length; i++)
            wrapperParameters[i + instanceOffset] = originalParameters[i].ParameterType;

        string wrapperName = "VP_TranspilerCall_" +
                             Interlocked.Increment(ref _nextTranspilerCallWrapperId);

        var wrapper = new DynamicMethod(
            wrapperName,
            calledMethod.ReturnType,
            wrapperParameters,
            calledMethod.Module,
            true);

        ILGenerator il = wrapper.GetILGenerator();
        MethodInfo beginMethod = AccessTools.Method(typeof(PatchProfilerTool), nameof(BeginInjectedTranspilerCall));
        MethodInfo endMethod = AccessTools.Method(typeof(PatchProfilerTool), nameof(EndInjectedTranspilerCall));

        if (beginMethod == null || endMethod == null)
            return null;

        for (int i = 0; i < entryIds.Count; i++)
        {
            EmitLoadInt32(il, entryIds[i]);
            EmitLoadInt32(il, i == 0 ? 1 : 0);
            EmitLoadInt32(il, i == entryIds.Count - 1 ? 1 : 0);
            il.Emit(OpCodes.Call, beginMethod);
        }

        LocalBuilder resultLocal = calledMethod.ReturnType == typeof(void)
            ? null
            : il.DeclareLocal(calledMethod.ReturnType);

        Label endOfExceptionBlock = il.BeginExceptionBlock();

        for (int i = 0; i < wrapperParameters.Length; i++)
            EmitLoadArgument(il, i);

        il.Emit(originalCallOpcode, calledMethod);

        if (resultLocal != null)
            il.Emit(OpCodes.Stloc, resultLocal);

        il.Emit(OpCodes.Leave, endOfExceptionBlock);

        il.BeginFinallyBlock();
        for (int i = entryIds.Count - 1; i >= 0; i--)
        {
            EmitLoadInt32(il, entryIds[i]);
            EmitLoadInt32(il, i == 0 ? 1 : 0);
            EmitLoadInt32(il, i == entryIds.Count - 1 ? 1 : 0);
            il.Emit(OpCodes.Call, endMethod);
        }
        il.EndExceptionBlock();

        if (resultLocal != null)
            il.Emit(OpCodes.Ldloc, resultLocal);

        il.Emit(OpCodes.Ret);
        return wrapper;
    }

    private static void EmitLoadArgument(ILGenerator il, int index)
    {
        switch (index)
        {
            case 0:
                il.Emit(OpCodes.Ldarg_0);
                break;
            case 1:
                il.Emit(OpCodes.Ldarg_1);
                break;
            case 2:
                il.Emit(OpCodes.Ldarg_2);
                break;
            case 3:
                il.Emit(OpCodes.Ldarg_3);
                break;
            default:
                if (index <= byte.MaxValue)
                    il.Emit(OpCodes.Ldarg_S, (byte)index);
                else
                    il.Emit(OpCodes.Ldarg, (short)index);
                break;
        }
    }

    private static void EmitLoadInt32(ILGenerator il, int value)
    {
        switch (value)
        {
            case -1:
                il.Emit(OpCodes.Ldc_I4_M1);
                break;
            case 0:
                il.Emit(OpCodes.Ldc_I4_0);
                break;
            case 1:
                il.Emit(OpCodes.Ldc_I4_1);
                break;
            case 2:
                il.Emit(OpCodes.Ldc_I4_2);
                break;
            case 3:
                il.Emit(OpCodes.Ldc_I4_3);
                break;
            case 4:
                il.Emit(OpCodes.Ldc_I4_4);
                break;
            case 5:
                il.Emit(OpCodes.Ldc_I4_5);
                break;
            case 6:
                il.Emit(OpCodes.Ldc_I4_6);
                break;
            case 7:
                il.Emit(OpCodes.Ldc_I4_7);
                break;
            case 8:
                il.Emit(OpCodes.Ldc_I4_8);
                break;
            default:
                if (value >= sbyte.MinValue && value <= sbyte.MaxValue)
                    il.Emit(OpCodes.Ldc_I4_S, (sbyte)value);
                else
                    il.Emit(OpCodes.Ldc_I4, value);
                break;
        }
    }

    private static bool HasUnsupportedCallPrefix(IReadOnlyList<CodeInstruction> instructions, int callIndex)
    {
        for (int index = callIndex - 1; index >= 0 && IsCallPrefix(instructions[index]?.opcode ?? default); index--)
        {
            OpCode opcode = instructions[index].opcode;
            if (opcode == OpCodes.Constrained || opcode == OpCodes.Tailcall)
                return true;
        }

        return false;
    }

    private static bool IsCallPrefix(OpCode opcode) =>
        opcode == OpCodes.Constrained ||
        opcode == OpCodes.Tailcall ||
        opcode == OpCodes.Readonly ||
        opcode == OpCodes.Unaligned ||
        opcode == OpCodes.Volatile;

    private static void BeginInjectedTranspilerCall(int entryId, bool groupOwner, bool startPhysicalMeasurement)
    {
        try
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
                return;

            PatchProfilerTool instance = _instance;
            long profilerOverheadStartTicks = instance != null && instance._excludeMeasuredNestedCalls && groupOwner
                ? BeginPatchProfilerOverhead(instance)
                : 0L;
            if (instance == null || !instance._profilingActive || instance._app.InstrumentationSafety.RestartRequired)
                return;

            Dictionary<int, PatchStat> stats = instance._runtimeTranspilerCallStats;
            if (stats == null || !stats.TryGetValue(entryId, out PatchStat stat) || stat == null)
                return;

            if (!instance._excludeMeasuredNestedCalls)
            {
                _activeTranspilerCallTimings ??= new List<ActiveTranspilerCallTiming>(8);
                _activeTranspilerCallTimings.Add(new ActiveTranspilerCallTiming
                {
                    EntryId = entryId,
                    Stat = stat,
                    StartTicks = Stopwatch.GetTimestamp()
                });
                return;
            }

            _activeTranspilerCallGroups ??= new List<ActiveTranspilerCallGroup>(4);
            if (groupOwner)
                _activeTranspilerCallGroups.Add(new ActiveTranspilerCallGroup
                {
                    ProfilerPrefixStartTicks = profilerOverheadStartTicks
                });
            if (_activeTranspilerCallGroups.Count == 0)
                return;

            ActiveTranspilerCallGroup group = _activeTranspilerCallGroups[_activeTranspilerCallGroups.Count - 1];
            group.Stats.Add(stat);
            if (startPhysicalMeasurement)
            {
                BeginPatchMeasurement(group.ProfilerPrefixStartTicks, out group.MeasurementSequence, out group.MeasurementIndex, out group.StartTicks);
            }
        }
        catch
        {
        }
    }

    private static void EndInjectedTranspilerCall(int entryId, bool groupOwner, bool finishPhysicalMeasurement)
    {
        try
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
                return;

            PatchProfilerTool instance = _instance;
            bool excludeNested = instance != null && instance._excludeMeasuredNestedCalls;
            long physicalEndTicks = excludeNested && finishPhysicalMeasurement ? Stopwatch.GetTimestamp() : 0L;
            if (instance == null || !instance._profilingActive || instance._app.InstrumentationSafety.RestartRequired)
                return;

            if (!excludeNested)
            {
                List<ActiveTranspilerCallTiming> stack = _activeTranspilerCallTimings;
                if (stack == null || stack.Count == 0)
                    return;

                int timingIndex = stack.Count - 1;
                while (timingIndex >= 0 && stack[timingIndex].EntryId != entryId)
                    timingIndex--;

                if (timingIndex < 0)
                    return;

                ActiveTranspilerCallTiming timing = stack[timingIndex];
                stack.RemoveAt(timingIndex);
                if (timing.Stat == null || timing.StartTicks <= 0)
                    return;

                long elapsedTicks = Stopwatch.GetTimestamp() - timing.StartTicks;
                if (elapsedTicks < 0)
                    return;

                RecordInjectedTranspilerCallSample(instance, timing.Stat, elapsedTicks, elapsedTicks);
                return;
            }

            List<ActiveTranspilerCallGroup> groups = _activeTranspilerCallGroups;
            if (groups == null || groups.Count == 0)
                return;

            ActiveTranspilerCallGroup group = groups[groups.Count - 1];
            if (finishPhysicalMeasurement && !group.Completed)
            {
                group.ProfilerFinalizerStartTicks = physicalEndTicks;
                long inclusiveTicks;
                long childTicks;
                if (!CompletePatchMeasurement(group.MeasurementSequence, group.MeasurementIndex, physicalEndTicks, out inclusiveTicks, out childTicks))
                {
                    inclusiveTicks = Math.Max(0L, physicalEndTicks - group.StartTicks);
                    childTicks = 0;
                }
                group.InclusiveTicks = inclusiveTicks;
                group.ChildTicks = childTicks;
                group.Completed = true;
            }

            if (!groupOwner)
                return;

            groups.RemoveAt(groups.Count - 1);
            try
            {
                if (!group.Completed || group.Stats.Count == 0)
                    return;

                long measuredTicks = Math.Max(0L, group.InclusiveTicks - group.ChildTicks);
                RecordInjectedTranspilerCallSample(instance, group.Stats, measuredTicks, group.InclusiveTicks);
            }
            finally
            {
                CompletePatchProfilerOverhead(group.ProfilerFinalizerStartTicks);
            }
        }
        catch
        {
        }
    }

    private static void RecordInjectedTranspilerCallSample(PatchProfilerTool instance, PatchStat stat, long measuredTicks, long inclusiveTicks)
    {
        if (stat == null || !TryPrepareInjectedTranspilerCallSample(instance, measuredTicks, inclusiveTicks, out double measuredMs, out int frame, out float now, out bool gcSample))
            return;

        bool queued = stat.Add(measuredMs, frame, now, gcSample);
        if (queued)
            instance.QueueAnalyticsWork(stat);
    }

    private static void RecordInjectedTranspilerCallSample(PatchProfilerTool instance, IEnumerable<PatchStat> stats, long measuredTicks, long inclusiveTicks)
    {
        if (!TryPrepareInjectedTranspilerCallSample(instance, measuredTicks, inclusiveTicks, out double measuredMs, out int frame, out float now, out bool gcSample))
            return;

        foreach (PatchStat stat in stats)
        {
            if (stat == null)
                continue;
            bool queued = stat.Add(measuredMs, frame, now, gcSample);
            if (queued)
                instance.QueueAnalyticsWork(stat);
        }
    }

    private static bool TryPrepareInjectedTranspilerCallSample(PatchProfilerTool instance, long measuredTicks, long inclusiveTicks, out double measuredMs, out int frame, out float now, out bool gcSample)
    {
        measuredMs = 0;
        frame = 0;
        now = 0;
        gcSample = false;
        if (instance == null || measuredTicks < 0 || inclusiveTicks < 0)
            return false;

        measuredMs = measuredTicks * MsPerTick;
        double inclusiveMs = inclusiveTicks * MsPerTick;

        if (inclusiveMs >= GcCheckMinSampleMs)
        {
            gcSample =
                GC.CollectionCount(0) != instance._lastObservedGc0 ||
                GC.CollectionCount(1) != instance._lastObservedGc1 ||
                GC.CollectionCount(2) != instance._lastObservedGc2;
        }

        if (gcSample && instance._ignoreGcAllocations)
            return false;

        frame = Time.frameCount;
        now = Time.realtimeSinceStartup;
        instance.SetCurrentRealtime(now);
        return true;
    }

    private static int CurrentInjectedTranspilerCallDepth() =>
        _activeTranspilerCallTimings?.Count ?? 0;

    private static int CurrentInjectedTranspilerCallGroupDepth() =>
        _activeTranspilerCallGroups?.Count ?? 0;

    private static void TrimInjectedTranspilerCallStack(int depth, int groupDepth)
    {
        if (depth >= 0)
        {
            List<ActiveTranspilerCallTiming> stack = _activeTranspilerCallTimings;
            if (stack != null && stack.Count > depth)
                stack.RemoveRange(depth, stack.Count - depth);
        }

        if (groupDepth >= 0)
        {
            List<ActiveTranspilerCallGroup> groups = _activeTranspilerCallGroups;
            if (groups != null && groups.Count > groupDepth)
            {
                groups.RemoveRange(groupDepth, groups.Count - groupDepth);
                // A leaked physical child makes the shared nesting stack ambiguous. Clear it so the
                // enclosing finalizer falls back to its inclusive interval instead of subtracting bad data.
                _activePatchMeasurementStack?.Clear();
            }
        }
    }

}
