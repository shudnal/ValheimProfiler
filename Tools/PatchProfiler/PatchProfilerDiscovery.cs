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
    private void RefreshPatchList()
    {
        try
        {
            StopProfilingInternal(false);
            _detailsWindow.RequestedVisible = false;
            _selectedTranspilerDetailsContext = null;
            _selectedTranspilerDetailsTitle = string.Empty;

            _status = "Refreshing patch list…";

            BuildAssemblyPluginMap();

            int patchedTargets = 0;
            int profileEntriesFound = 0;

            Dictionary<string, bool> oldSelection;
            lock (_lock)
                oldSelection = _modsToProfile.ToDictionary(kv => kv.Key, kv => kv.Value);

            if (oldSelection.Count == 0)
                _modSelectionPolicy.Reload();

            lock (_lock)
            {
                _instrumented.Clear();
                _transpiledTargets.Clear();
                _stats.Clear();
                _context.Clear();
                _profileEntryIds.Clear();
                _entriesByInstrumentedMethod.Clear();
                _runtimeEntriesByInstrumentedMethod = new Dictionary<MethodBase, RuntimeProfileEntry[]>(0);
                _runtimeTranspiledTargets = new HashSet<MethodBase>();
                _runtimeTranspilerCallPlans = new Dictionary<MethodBase, RuntimeTranspilerCallPlan[]>(0);
                _runtimeTranspilerCallStats = new Dictionary<int, PatchStat>(0);
                lock (_transpilerCallWrapperLock)
                    _transpilerCallWrappers.Clear();
                _nextProfileEntryId = 0;
                _modExpanded.Clear();
                _modsToProfile.Clear();
                _modPatchCounts.Clear();
                _modGuidToName.Clear();
                _statsFrozen = false;
            }

            ClearAnalyticsQueues();

            foreach (var target in Harmony.GetAllPatchedMethods())
            {
                patchedTargets++;

                var info = Harmony.GetPatchInfo(target);
                if (info == null)
                    continue;

                foreach (var p in info.Prefixes)
                    RegisterPatchMethod(p, "Prefix", target, ref profileEntriesFound, oldSelection);

                foreach (var p in info.Postfixes)
                    RegisterPatchMethod(p, "Postfix", target, ref profileEntriesFound, oldSelection);

                foreach (var p in info.Finalizers)
                    RegisterPatchMethod(p, "Finalizer", target, ref profileEntriesFound, oldSelection);

                if (info.Transpilers != null && info.Transpilers.Any())
                    RegisterTranspilers(info.Transpilers, target, ref profileEntriesFound, oldSelection);
            }

            int transpiledTargetEntries;
            int transpilerCallEntries;
            lock (_lock)
            {
                transpiledTargetEntries = _context.Values.Count(ctx => ctx.IsTranspiledTargetEntry);
                transpilerCallEntries = _context.Values.Count(ctx => (ctx.PatchType ?? string.Empty).Contains("Transpiler call"));
            }

            _listReady = true;
            _modsSelectionDirty = false;
            _profilingActive = false;
            _status = $"Patch list ready. Targets: {patchedTargets}. Profile entries: {profileEntriesFound}. Transpiled targets: {transpiledTargetEntries}. Transpiler calls: {transpilerCallEntries}.";
            MarkViewDirty();
        }
        catch (Exception ex)
        {
            _status = $"Refresh error: {ex.GetType().Name}: {ex.Message}";
            _logger.LogError(ex);
        }
    }

    private void RegisterPatchMethod(HarmonyLib.Patch p, string patchType, MethodBase target, ref int profileEntriesFound, Dictionary<string, bool> oldSelection)
    {
        var m = p?.PatchMethod;
        if (m == null)
            return;

        if (m.DeclaringType?.Assembly == typeof(ValheimProfilerPlugin).Assembly)
            return;

        string ownerId = string.IsNullOrWhiteSpace(p.owner) ? "(no owner)" : p.owner;
        string modGuid = GuessModGuid(m, ownerId);
        string modName = GuessModName(m, modGuid);
        string targetDisplay = GetMethodDisplay(target);
        string entryKey = "patch|" + GetMethodIdentity(m);

        lock (_lock)
        {
            bool newEntry = !_profileEntryIds.ContainsKey(entryKey);
            int entryId = GetOrCreateProfileEntryLocked(entryKey, m, () => new PatchContext
            {
                ModName = modName,
                ModGuid = modGuid,
                OwnerHarmonyId = ownerId
            });

            RegisterModLocked(modGuid, modName, oldSelection);
            _modPatchCounts[modGuid]++;

            var ctx = _context[entryId];
            ctx.AddPatchType(patchType);
            ctx.AddTarget(targetDisplay);

            if (newEntry)
                profileEntriesFound++;
        }
    }

    private void RegisterTranspilers(
        IReadOnlyList<HarmonyLib.Patch> transpilers,
        MethodBase target,
        ref int profileEntriesFound,
        Dictionary<string, bool> oldSelection)
    {
        if (target == null || transpilers == null || transpilers.Count == 0)
            return;

        var retained = transpilers
            .Where(patch => patch?.PatchMethod != null &&
                            patch.PatchMethod.DeclaringType?.Assembly != typeof(ValheimProfilerPlugin).Assembly)
            .ToList();

        if (retained.Count == 0)
            return;

        foreach (HarmonyLib.Patch patch in retained)
        {
            MethodBase transpiler = patch.PatchMethod;
            string ownerId = string.IsNullOrWhiteSpace(patch.owner) ? "(no owner)" : patch.owner;
            string modGuid = GuessModGuid(transpiler, ownerId);
            string modName = GuessModName(transpiler, modGuid);

            lock (_lock)
                RegisterModLocked(modGuid, modName, oldSelection);

            RegisterTranspiledTarget(
                target,
                transpiler,
                modGuid,
                modName,
                ownerId,
                oldSelection,
                ref profileEntriesFound);
        }

        foreach (InjectedRuntimeCall call in FindInjectedRuntimeCalls(target, retained))
        {
            RegisterTranspilerRuntimeCall(
                call,
                target,
                oldSelection,
                ref profileEntriesFound);
        }
    }

    private void RegisterTranspiledTarget(MethodBase target, MethodBase transpiler, string sourceModGuid, string sourceModName, string ownerId, Dictionary<string, bool> oldSelection, ref int profileEntriesFound)
    {
        if (target == null)
            return;

        string entryKey = "transpiled-target|" + GetMethodIdentity(target);
        string targetDisplay = GetMethodDisplay(target);

        lock (_lock)
        {
            bool newEntry = !_profileEntryIds.ContainsKey(entryKey);
            int entryId = GetOrCreateProfileEntryLocked(entryKey, target, () => new PatchContext
            {
                ModName = TranspiledTargetsName,
                ModGuid = TranspiledTargetsGuid,
                OwnerHarmonyId = "(multiple transpilers)"
            });

            _transpiledTargets.Add(target);
            RegisterModLocked(TranspiledTargetsGuid, TranspiledTargetsName, oldSelection);

            if (newEntry)
            {
                _modPatchCounts[TranspiledTargetsGuid]++;
                profileEntriesFound++;
            }

            var ctx = _context[entryId];
            ctx.AddPatchType("Transpiled target");
            ctx.AddTarget(targetDisplay);
            ctx.AddRelatedModGuid(sourceModGuid);
            ctx.AddTranspilerDetail(sourceModGuid, sourceModName, ownerId, transpiler);
        }
    }

    private void RegisterTranspilerRuntimeCall(
        InjectedRuntimeCall call,
        MethodBase target,
        Dictionary<string, bool> oldSelection,
        ref int profileEntriesFound)
    {
        MethodBase calledMethod = call?.CalledMethod;
        if (calledMethod == null || target == null)
            return;

        var sourceDetails = new List<(MethodBase Method, string ModGuid, string ModName, string OwnerId)>();
        foreach (HarmonyLib.Patch sourcePatch in call.SourcePatches)
        {
            MethodBase sourceMethod = sourcePatch?.PatchMethod;
            if (sourceMethod == null)
                continue;

            string ownerId = string.IsNullOrWhiteSpace(sourcePatch.owner) ? "(no owner)" : sourcePatch.owner;
            string sourceGuid = GuessModGuid(sourceMethod, ownerId);
            string sourceName = GuessModName(sourceMethod, sourceGuid);
            sourceDetails.Add((sourceMethod, sourceGuid, sourceName, ownerId));
        }

        string modGuid;
        string modName;
        string ownerHarmonyId;

        var distinctSourceGuids = sourceDetails
            .Select(detail => detail.ModGuid)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (distinctSourceGuids.Count == 1)
        {
            modGuid = distinctSourceGuids[0];
            modName = sourceDetails.First(detail => detail.ModGuid == modGuid).ModName;
            ownerHarmonyId = sourceDetails.Count == 1
                ? sourceDetails[0].OwnerId
                : "(multiple transpilers)";
        }
        else
        {
            modGuid = TranspiledTargetsGuid;
            modName = TranspiledTargetsName;
            ownerHarmonyId = "(multiple transpilers)";
        }

        string entryKey = "transpiler-call|" +
                          GetMethodIdentity(target) + "|" +
                          modGuid + "|" +
                          GetMethodIdentity(calledMethod);

        lock (_lock)
        {
            bool newEntry = !_profileEntryIds.ContainsKey(entryKey);
            int entryId = GetOrCreateProfileEntryLocked(entryKey, calledMethod, () => new PatchContext
            {
                ModName = modName,
                ModGuid = modGuid,
                OwnerHarmonyId = ownerHarmonyId
            });

            RegisterModLocked(modGuid, modName, oldSelection);

            if (newEntry)
            {
                _modPatchCounts[modGuid]++;
                profileEntriesFound++;
            }

            var ctx = _context[entryId];
            ctx.IsTranspilerCallEntry = true;
            ctx.TranspiledTargetMethod = target;
            ctx.InjectedCallSiteCount = Math.Max(ctx.InjectedCallSiteCount, call.AddedCallSites);
            ctx.AddPatchType("Transpiler call");
            ctx.AddTarget(GetMethodDisplay(target));
            ctx.AddRequiredActiveTranspiledTarget(target);

            foreach (int ordinal in call.FinalMethodOrdinals)
                ctx.AddInjectedCallOrdinal(ordinal);

            foreach (var detail in sourceDetails)
            {
                ctx.AddRelatedModGuid(detail.ModGuid);
                ctx.AddTranspilerDetail(detail.ModGuid, detail.ModName, detail.OwnerId, detail.Method);
            }
        }
    }

    private int GetOrCreateProfileEntryLocked(string entryKey, MethodBase instrumentedMethod, Func<PatchContext> createContext)
    {
        if (_profileEntryIds.TryGetValue(entryKey, out var existingId))
            return existingId;

        int id = _nextProfileEntryId++;
        _profileEntryIds[entryKey] = id;
        _stats[id] = new PatchStat();

        var ctx = createContext != null ? createContext() : new PatchContext();
        ctx.EntryId = id;
        ctx.InstrumentedMethod = instrumentedMethod;
        _context[id] = ctx;

        if (!_entriesByInstrumentedMethod.TryGetValue(instrumentedMethod, out var entries))
        {
            entries = new List<int>(1);
            _entriesByInstrumentedMethod[instrumentedMethod] = entries;
        }

        entries.Add(id);
        return id;
    }

    private void RegisterModLocked(string modGuid, string modName, Dictionary<string, bool> oldSelection)
    {
        if (string.IsNullOrWhiteSpace(modGuid))
            modGuid = "(unknown guid)";

        if (string.IsNullOrWhiteSpace(modName))
            modName = "Unknown";

        if (!_modExpanded.ContainsKey(modGuid))
            _modExpanded[modGuid] = false;

        if (!_modsToProfile.ContainsKey(modGuid))
        {
            _modsToProfile[modGuid] =
                oldSelection != null && oldSelection.TryGetValue(modGuid, out bool wasSelected)
                    ? wasSelected
                    : _modSelectionPolicy.Resolve(modGuid, defaultValue: true);
        }

        if (!_modPatchCounts.ContainsKey(modGuid))
            _modPatchCounts[modGuid] = 0;

        if (!_modGuidToName.ContainsKey(modGuid))
            _modGuidToName[modGuid] = modName;
    }

    private void StartProfiling()
    {
        try
        {
            RefreshPatchList();

            if (!_listReady)
            {
                _status = "Patch list is empty.";
                return;
            }

            if (_profilingActive)
            {
                _status = "Profiling is already active.";
                return;
            }

            SaveModSelection();
            _statsFrozen = false;

            int newlyInstrumented = 0;
            int instrumentationFailures = 0;
            int callSiteTimingFailures = 0;

            List<MethodBase> methods;
            lock (_lock)
            {
                RebuildRuntimeProfileMapLocked();
                methods = _runtimeEntriesByInstrumentedMethod.Keys
                    .Concat(_runtimeTranspiledTargets)
                    .Where(method => method != null)
                    .Distinct()
                    .ToList();
            }

            foreach (var method in methods)
            {
                lock (_lock)
                {
                    if (_instrumented.Contains(method))
                        continue;

                    _instrumented.Add(method);
                }

                try
                {
                    var prefix = new HarmonyMethod(AccessTools.Method(typeof(PatchProfilerTool), nameof(TimingPrefix)))
                    {
                        priority = Priority.Last
                    };

                    var finalizer = new HarmonyMethod(AccessTools.Method(typeof(PatchProfilerTool), nameof(TimingFinalizer)))
                    {
                        priority = Priority.First
                    };

                    HarmonyMethod callSiteTranspiler = null;
                    if (_runtimeTranspilerCallPlans.ContainsKey(method))
                    {
                        string[] existingTranspilerOwners = Harmony.GetPatchInfo(method)?.Transpilers
                            ?.Where(patch => patch != null &&
                                             !string.IsNullOrWhiteSpace(patch.owner) &&
                                             !string.Equals(patch.owner, PluginGuid, StringComparison.Ordinal))
                            .Select(patch => patch.owner)
                            .Distinct(StringComparer.Ordinal)
                            .ToArray() ?? Array.Empty<string>();

                        callSiteTranspiler = new HarmonyMethod(
                            AccessTools.Method(typeof(PatchProfilerTool), nameof(InjectedCallTimingTranspiler)))
                        {
                            priority = Priority.Last,
                            after = existingTranspilerOwners
                        };
                    }

                    // Install target/method timing first. Exact transpiler-call timing is added in a
                    // separate wrapper rebuild so a call-site instrumentation failure cannot remove
                    // the ordinary patch or whole-target measurement for this method.
                    _harmony.Patch(
                        method,
                        prefix: prefix,
                        finalizer: finalizer);
                    newlyInstrumented++;

                    if (callSiteTranspiler != null)
                    {
                        try
                        {
                            _harmony.Patch(method, transpiler: callSiteTranspiler);
                        }
                        catch (Exception callSiteException)
                        {
                            callSiteTimingFailures++;
                            _logger.LogWarning(
                                $"Could not instrument exact transpiler call sites in {GetMethodDisplay(method)}: " +
                                $"{callSiteException.GetType().Name}: {callSiteException.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    instrumentationFailures++;
                    lock (_lock)
                        _instrumented.Remove(method);

                    _logger.LogWarning($"Could not instrument {GetMethodDisplay(method)}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            _profilingActive = true;
            _status = $"Profiling enabled. Instrumented: {newlyInstrumented}.";
            if (instrumentationFailures > 0)
                _status += $" Failed: {instrumentationFailures}.";
            if (callSiteTimingFailures > 0)
                _status += $" Transpiler call-site failures: {callSiteTimingFailures}.";
            MarkViewDirty();
        }
        catch (Exception ex)
        {
            _status = $"Start error: {ex.GetType().Name}: {ex.Message}";
            _logger.LogError(ex);
        }
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

    private void StopProfilingInternal(bool freezeData = false)
    {
        bool wasActive = _profilingActive;

        _profilingActive = false;
        _harmony.UnpatchSelf();

        lock (_lock)
        {
            _instrumented.Clear();
            _runtimeEntriesByInstrumentedMethod = new Dictionary<MethodBase, RuntimeProfileEntry[]>(0);
            _runtimeTranspiledTargets = new HashSet<MethodBase>();
            _runtimeTranspilerCallPlans = new Dictionary<MethodBase, RuntimeTranspilerCallPlan[]>(0);
            _runtimeTranspilerCallStats = new Dictionary<int, PatchStat>(0);
        }

        if (wasActive && freezeData)
        {
            float now = Time.realtimeSinceStartup;
            int frame = Time.frameCount;

            SetCurrentRealtime(now);
            FlushAnalyticsAt(now);

            _frozenRealtime = now;
            _frozenFrame = frame;
            _statsFrozen = true;
        }
    }

    private void ResetProfilingSelection()
    {
        if (!_listReady)
        {
            _status = "Patch list is empty. Load patch list first.";
            return;
        }

        SaveModSelection();

        bool wasActive = _profilingActive;
        StopProfilingInternal(false);

        if (wasActive)
        {
            StartProfiling();
        }
        else
        {
            _status = "Profiling selection updated. Start profiling to apply.";
            MarkViewDirty();
        }
    }

}