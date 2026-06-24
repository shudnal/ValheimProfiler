#nullable disable

using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace ValheimProfiler.Tools.ValheimUpdateProfiler;

internal sealed partial class ValheimUpdateProfilerTool
{
    [ThreadStatic] private static ValheimUpdateKind _activeTopLevelKind;
    [ThreadStatic] private static bool _topLevelActive;
    [ThreadStatic] private static long _activeMeasuredTicks;

    private struct BatchTimingState
    {
        internal long StartTicks;
        internal int Items;
        internal ValheimUpdateKind Kind;
        internal string Scope;
        internal object FirstItem;
    }

    private struct TopLevelTimingState
    {
        internal long StartTicks;
        internal ValheimUpdateKind Kind;
        internal bool PreviousActive;
        internal ValheimUpdateKind PreviousKind;
        internal long PreviousMeasuredTicks;
    }

    private struct WearTimingState
    {
        internal long StartTicks;
        internal bool FullPass;
        internal int InstanceCount;
        internal int StartIndex;
        internal int PlannedItems;
        internal int UpdatesPerFrame;
        internal float TimeArgument;
        internal float SleepUntilNext;
        internal object FirstItem;
    }

    private static void CustomFixedPrefix(
        List<IMonoUpdater> container,
        List<IMonoUpdater> source,
        string profileScope,
        ref BatchTimingState __state) =>
        BeginBatch(container, source, profileScope, ValheimUpdateKind.FixedUpdate, ref __state);

    private static Exception CustomFixedFinalizer(Exception __exception, BatchTimingState __state) =>
        EndBatch(__exception, __state);

    private static void CustomUpdatePrefix(
        List<IMonoUpdater> container,
        List<IMonoUpdater> source,
        string profileScope,
        ref BatchTimingState __state) =>
        BeginBatch(container, source, profileScope, ValheimUpdateKind.Update, ref __state);

    private static Exception CustomUpdateFinalizer(Exception __exception, BatchTimingState __state) =>
        EndBatch(__exception, __state);

    private static void CustomLatePrefix(
        List<IMonoUpdater> container,
        List<IMonoUpdater> source,
        string profileScope,
        ref BatchTimingState __state) =>
        BeginBatch(container, source, profileScope, ValheimUpdateKind.LateUpdate, ref __state);

    private static Exception CustomLateFinalizer(Exception __exception, BatchTimingState __state) =>
        EndBatch(__exception, __state);

    private static void UpdateAIPrefix(
        List<IUpdateAI> container,
        List<IUpdateAI> source,
        string profileScope,
        ref BatchTimingState __state) =>
        BeginBatch(container, source, profileScope, ValheimUpdateKind.AI, ref __state);

    private static Exception UpdateAIFinalizer(Exception __exception, BatchTimingState __state) =>
        EndBatch(__exception, __state);

    private static void FixedUpdatePrefix(ref TopLevelTimingState __state) =>
        BeginTopLevel(ValheimUpdateKind.FixedUpdate, ref __state);

    private static Exception FixedUpdateFinalizer(Exception __exception, TopLevelTimingState __state) =>
        EndTopLevel(__exception, __state);

    private static void UpdatePrefix(ref TopLevelTimingState __state) =>
        BeginTopLevel(ValheimUpdateKind.Update, ref __state);

    private static Exception UpdateFinalizer(Exception __exception, TopLevelTimingState __state) =>
        EndTopLevel(__exception, __state);

    private static void LateUpdatePrefix(ref TopLevelTimingState __state) =>
        BeginTopLevel(ValheimUpdateKind.LateUpdate, ref __state);

    private static Exception LateUpdateFinalizer(Exception __exception, TopLevelTimingState __state) =>
        EndTopLevel(__exception, __state);

    private static void WearPrefix(
        WearNTearUpdater __instance,
        float time,
        ref WearTimingState __state)
    {
        __state.StartTicks = Stopwatch.GetTimestamp();
        __state.TimeArgument = time;
        __state.SleepUntilNext = __instance?.m_sleepUntilNext ?? 0f;
        __state.StartIndex = __instance?.m_index ?? 0;
        __state.UpdatesPerFrame = __instance?.m_updatesPerFrame ?? 0;
        __state.FullPass = __instance != null && __instance.m_sleepUntilNext.Equals(__instance.m_sleepUntil);

        try
        {
            List<WearNTear> instances = WearNTear.GetAllInstances();
            __state.InstanceCount = instances?.Count ?? 0;
            __state.PlannedItems = __state.FullPass
                ? __state.InstanceCount
                : Math.Min(Math.Max(0, __state.UpdatesPerFrame), Math.Max(0, __state.InstanceCount - __state.StartIndex));

            if (__state.InstanceCount > 0)
            {
                int index = __state.FullPass ? 0 : Math.Min(Math.Max(0, __state.StartIndex), __state.InstanceCount - 1);
                __state.FirstItem = instances[index];
            }
        }
        catch
        {
            __state.InstanceCount = 0;
            __state.PlannedItems = 0;
            __state.FirstItem = null;
        }
    }

    private static Exception WearFinalizer(
        Exception __exception,
        WearNTearUpdater __instance,
        WearTimingState __state)
    {
        try
        {
            ValheimUpdateProfilerTool instance = _instance;
            if (instance == null || !instance._profilingActive || __state.StartTicks <= 0)
                return __exception;
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
                return __exception;

            long elapsedTicks = Stopwatch.GetTimestamp() - __state.StartTicks;
            if (elapsedTicks < 0)
                return __exception;

            double elapsedMs = elapsedTicks * MsPerTick;
            int frame = Time.frameCount;
            float now = Time.realtimeSinceStartup;
            instance.RecordWear(__state, __instance, elapsedMs, frame, now);
        }
        catch
        {
        }

        return __exception;
    }

    private static void BeginBatch<T>(
        List<T> container,
        List<T> source,
        string profileScope,
        ValheimUpdateKind kind,
        ref BatchTimingState state)
        where T : class
    {
        state.StartTicks = Stopwatch.GetTimestamp();
        state.Kind = kind;
        state.Scope = profileScope ?? string.Empty;
        int containerCount = container?.Count ?? 0;
        int sourceCount = source?.Count ?? 0;
        state.Items = containerCount + sourceCount;
        state.FirstItem = sourceCount > 0
            ? source[0]
            : containerCount > 0
                ? container[0]
                : null;
    }

    private static Exception EndBatch(Exception exception, BatchTimingState state)
    {
        try
        {
            ValheimUpdateProfilerTool instance = _instance;
            if (instance == null || !instance._profilingActive || state.StartTicks <= 0)
                return exception;
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
                return exception;

            long elapsedTicks = Stopwatch.GetTimestamp() - state.StartTicks;
            if (elapsedTicks < 0)
                return exception;

            if (_topLevelActive)
                _activeMeasuredTicks += elapsedTicks;

            double elapsedMs = elapsedTicks * MsPerTick;
            instance.RecordScope(
                state.Kind,
                state.Scope,
                state.Items,
                state.FirstItem,
                elapsedMs,
                Time.frameCount,
                Time.realtimeSinceStartup);
        }
        catch
        {
        }

        return exception;
    }

    private static void BeginTopLevel(ValheimUpdateKind kind, ref TopLevelTimingState state)
    {
        state.StartTicks = Stopwatch.GetTimestamp();
        state.Kind = kind;
        state.PreviousActive = _topLevelActive;
        state.PreviousKind = _activeTopLevelKind;
        state.PreviousMeasuredTicks = _activeMeasuredTicks;

        _topLevelActive = true;
        _activeTopLevelKind = kind;
        _activeMeasuredTicks = 0;
    }

    private static Exception EndTopLevel(Exception exception, TopLevelTimingState state)
    {
        try
        {
            ValheimUpdateProfilerTool instance = _instance;
            if (instance != null && instance._profilingActive && state.StartTicks > 0 &&
                Thread.CurrentThread.ManagedThreadId == _mainThreadId)
            {
                long totalTicks = Stopwatch.GetTimestamp() - state.StartTicks;
                if (totalTicks >= 0)
                {
                    long measuredTicks = Math.Max(0, _activeMeasuredTicks);
                    long remainderTicks = Math.Max(0, totalTicks - measuredTicks);
                    instance.RecordTopLevel(
                        state.Kind,
                        totalTicks * MsPerTick,
                        measuredTicks * MsPerTick,
                        remainderTicks * MsPerTick,
                        Time.frameCount,
                        Time.realtimeSinceStartup);
                }
            }
        }
        catch
        {
        }
        finally
        {
            _topLevelActive = state.PreviousActive;
            _activeTopLevelKind = state.PreviousKind;
            _activeMeasuredTicks = state.PreviousMeasuredTicks;
        }

        return exception;
    }

    private void StartProfiling()
    {
        if (_profilingActive)
            return;

        try
        {
            ResetAllStats();
            PatchProfilerTargets();
            _profilingActive = true;
            _statsFrozen = false;
            _status = "Profiling enabled. MonoUpdatersExtra and WearNTearUpdater are instrumented.";
        }
        catch (Exception ex)
        {
            _harmony.UnpatchSelf();
            _profilingActive = false;
            _status = $"Start error: {ex.GetType().Name}: {ex.Message}";
            _logger.LogError(ex);
        }
    }

    private void StopProfiling()
    {
        try
        {
            _profilingActive = false;
            _harmony.UnpatchSelf();
            _frozenRealtime = Time.realtimeSinceStartup;
            _frozenFrame = Time.frameCount;
            _statsFrozen = true;
            ProcessAllStats(_frozenRealtime);
            _status = "Profiling paused. Data frozen.";
        }
        catch (Exception ex)
        {
            _status = $"Pause error: {ex.GetType().Name}: {ex.Message}";
            _logger.LogError(ex);
        }
    }

    private void PatchProfilerTargets()
    {
        PatchMethod(
            AccessTools.Method(typeof(MonoUpdatersExtra), nameof(MonoUpdatersExtra.CustomFixedUpdate),
                new[] { typeof(List<IMonoUpdater>), typeof(List<IMonoUpdater>), typeof(string), typeof(float) }),
            nameof(CustomFixedPrefix), nameof(CustomFixedFinalizer));
        PatchMethod(
            AccessTools.Method(typeof(MonoUpdatersExtra), nameof(MonoUpdatersExtra.CustomUpdate),
                new[] { typeof(List<IMonoUpdater>), typeof(List<IMonoUpdater>), typeof(string), typeof(float), typeof(float) }),
            nameof(CustomUpdatePrefix), nameof(CustomUpdateFinalizer));
        PatchMethod(
            AccessTools.Method(typeof(MonoUpdatersExtra), nameof(MonoUpdatersExtra.CustomLateUpdate),
                new[] { typeof(List<IMonoUpdater>), typeof(List<IMonoUpdater>), typeof(string), typeof(float) }),
            nameof(CustomLatePrefix), nameof(CustomLateFinalizer));
        PatchMethod(
            AccessTools.Method(typeof(MonoUpdatersExtra), nameof(MonoUpdatersExtra.UpdateAI),
                new[] { typeof(List<IUpdateAI>), typeof(List<IUpdateAI>), typeof(string), typeof(float) }),
            nameof(UpdateAIPrefix), nameof(UpdateAIFinalizer));

        PatchMethod(AccessTools.Method(typeof(MonoUpdaters), nameof(MonoUpdaters.FixedUpdate)), nameof(FixedUpdatePrefix), nameof(FixedUpdateFinalizer));
        PatchMethod(AccessTools.Method(typeof(MonoUpdaters), nameof(MonoUpdaters.Update)), nameof(UpdatePrefix), nameof(UpdateFinalizer));
        PatchMethod(AccessTools.Method(typeof(MonoUpdaters), nameof(MonoUpdaters.LateUpdate)), nameof(LateUpdatePrefix), nameof(LateUpdateFinalizer));
        PatchMethod(
            AccessTools.Method(typeof(WearNTearUpdater), nameof(WearNTearUpdater.UpdateWearNTear), new[] { typeof(float), typeof(float) }),
            nameof(WearPrefix), nameof(WearFinalizer));
    }

    private void PatchMethod(MethodBase original, string prefixName, string finalizerName)
    {
        if (original == null)
            throw new MissingMethodException($"Required Valheim method for {prefixName} was not found.");

        var prefix = new HarmonyMethod(AccessTools.Method(typeof(ValheimUpdateProfilerTool), prefixName))
        {
            priority = Priority.Last
        };
        var finalizer = new HarmonyMethod(AccessTools.Method(typeof(ValheimUpdateProfilerTool), finalizerName))
        {
            priority = Priority.First
        };

        _harmony.Patch(original, prefix: prefix, finalizer: finalizer);
    }
}
