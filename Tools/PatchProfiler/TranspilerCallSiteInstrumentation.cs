#nullable disable

using HarmonyLib;
using System;
using System.Collections.Generic;
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

    private Dictionary<MethodBase, RuntimeTranspilerCallPlan[]> _runtimeTranspilerCallPlans =
        new Dictionary<MethodBase, RuntimeTranspilerCallPlan[]>(0);

    private Dictionary<int, PatchStat> _runtimeTranspilerCallStats =
        new Dictionary<int, PatchStat>(0);

    private readonly object _transpilerCallWrapperLock = new object();
    private readonly Dictionary<string, MethodInfo> _transpilerCallWrappers =
        new Dictionary<string, MethodInfo>(StringComparer.Ordinal);

    private static int _nextTranspilerCallWrapperId;

    [ThreadStatic]
    private static List<ActiveTranspilerCallTiming> _activeTranspilerCallTimings;

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
            return InjectCallSiteTiming(source, plans, instance);
        }
        catch (Exception ex)
        {
            instance._logger.LogDebug(
                $"Could not inject exact transpiler-call timing into {GetMethodDisplay(__originalMethod)}: " +
                $"{ex.GetType().Name}: {ex.Message}");
            return source;
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
                _logger.LogDebug(
                    $"Could not create transpiler-call wrapper for {GetMethodDisplay(calledMethod)}: " +
                    $"{ex.GetType().Name}: {ex.Message}");
                return null;
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

    private static void BeginInjectedTranspilerCall(int entryId)
    {
        try
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
                return;

            PatchProfilerTool instance = _instance;
            if (instance == null || !instance._profilingActive)
                return;

            Dictionary<int, PatchStat> stats = instance._runtimeTranspilerCallStats;
            if (stats == null || !stats.TryGetValue(entryId, out PatchStat stat) || stat == null)
                return;

            _activeTranspilerCallTimings ??= new List<ActiveTranspilerCallTiming>(8);
            _activeTranspilerCallTimings.Add(new ActiveTranspilerCallTiming
            {
                EntryId = entryId,
                Stat = stat,
                StartTicks = Stopwatch.GetTimestamp()
            });
        }
        catch
        {
        }
    }

    private static void EndInjectedTranspilerCall(int entryId)
    {
        try
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
                return;

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

            PatchProfilerTool instance = _instance;
            if (instance == null || !instance._profilingActive || timing.Stat == null || timing.StartTicks <= 0)
                return;

            long elapsedTicks = Stopwatch.GetTimestamp() - timing.StartTicks;
            if (elapsedTicks < 0)
                return;

            double elapsedMs = elapsedTicks * MsPerTick;
            bool gcSample = false;

            if (elapsedMs >= GcCheckMinSampleMs)
            {
                gcSample =
                    GC.CollectionCount(0) != instance._lastObservedGc0 ||
                    GC.CollectionCount(1) != instance._lastObservedGc1 ||
                    GC.CollectionCount(2) != instance._lastObservedGc2;
            }

            int frame = Time.frameCount;
            float now = Time.realtimeSinceStartup;
            instance.SetCurrentRealtime(now);

            bool queued = timing.Stat.Add(elapsedMs, frame, now, gcSample);
            if (queued)
                instance.QueueAnalyticsWork(timing.Stat);
        }
        catch
        {
        }
    }

    private static int CurrentInjectedTranspilerCallDepth() =>
        _activeTranspilerCallTimings?.Count ?? 0;

    private static void TrimInjectedTranspilerCallStack(int depth)
    {
        if (depth < 0)
            return;

        List<ActiveTranspilerCallTiming> stack = _activeTranspilerCallTimings;
        if (stack == null || stack.Count <= depth)
            return;

        stack.RemoveRange(depth, stack.Count - depth);
    }
}
