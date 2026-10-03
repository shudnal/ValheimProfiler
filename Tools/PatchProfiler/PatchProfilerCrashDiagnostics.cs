#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ValheimProfiler.Tools.PatchProfiler;

internal sealed partial class PatchProfilerTool
{
    // Crash-only diagnostics. A native Mono failure cannot be caught by C# try/catch;
    // the preceding flushed BEGIN record identifies the method and installation phase.
    private readonly object _crashTraceLock = new object();
    private FileStream _crashTraceStream;
    private int _crashTraceSequence;

    private void ReadCrashDiagnosticOptions()
    {
        var config = _app.Config;
        _crashTraceEnabled = config.PatchProfilerCrashTrace.Value;
        _diagnosticInstrumentationLimit = Math.Max(0, config.PatchProfilerInstrumentationLimit.Value);
    }

    private void BeginCrashTraceRun()
    {
        lock (_crashTraceLock)
        {
            _crashTraceStream?.Dispose();
            _crashTraceStream = null;
            _crashTraceSequence = 0;

            if (!_crashTraceEnabled)
                return;

            try
            {
                string path = ProfilerPaths.GetConfigFilePath("PatchProfiler-CrashTrace.log");
                // Overwrite stale runs so the last recorded BEGIN always belongs to this run.
                // WriteThrough + Flush(true) deliberately trade performance for crash resilience.
                _crashTraceStream = new FileStream(
                    path,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    4096,
                    FileOptions.WriteThrough);
            }
            catch (Exception ex)
            {
                _crashTraceEnabled = false;
                _logger.LogWarning("Patch Profiler crash tracing is unavailable: " + ex.Message);
            }
        }

        WriteCrashTrace(
            "RUN BEGIN | profiler=" + ValheimProfilerPlugin.PluginVersion +
            " | resolverCache=clear-before-and-after-each-operation | duplicateNameGuard=False" +
            " | gradualStart=" + _gradualProfilingStart +
            " | frameSpreadBudgetMs=" + (_gradualProfilingStart ? InstrumentationFrameBudget.Milliseconds.ToString("0") : "unbounded") +
            " | frameSpreadOperationCap=" + (_gradualProfilingStart ? InstrumentationFrameBudget.MaxBackendOperations.ToString() : "unbounded") +
            " | instrumentLimit=" + _diagnosticInstrumentationLimit);
    }

    private void EndCrashTraceRun()
    {
        lock (_crashTraceLock)
        {
            if (_crashTraceStream == null)
                return;

            try
            {
                _crashTraceStream.Flush(true);
                _crashTraceStream.Dispose();
            }
            catch
            {
            }
            finally
            {
                _crashTraceStream = null;
            }
        }
    }

    private void WriteCrashTrace(string phase, MethodBase method = null)
    {
        if (!_crashTraceEnabled)
            return;

        try
        {
            lock (_crashTraceLock)
            {
                if (_crashTraceStream == null)
                    return;

                string methodDescriptor = method == null ? string.Empty : " | " + DescribeDiagnosticMethod(method);
                string line = (++_crashTraceSequence).ToString("D6") + " " +
                              DateTime.UtcNow.ToString("O") + " | " + phase + methodDescriptor + "\n";
                byte[] bytes = Encoding.UTF8.GetBytes(line);
                _crashTraceStream.Write(bytes, 0, bytes.Length);
                _crashTraceStream.Flush(true);
            }
        }
        catch (Exception ex)
        {
            // Diagnostic logging must not become another reason for the game to crash.
            _crashTraceEnabled = false;
            _logger.LogWarning("Patch Profiler crash trace write failed: " + ex.Message);
        }
    }

    private static string GetDiagnosticMethodKey(MethodBase method)
    {
        if (method == null)
            return "(null)";

        try
        {
            string assemblyName = method.DeclaringType?.Assembly?.GetName().Name ?? "(dynamic)";
            return assemblyName + "|" + GetMethodDisplay(method) + "|" + GetMethodIdentity(method);
        }
        catch
        {
            return GetMethodDisplay(method);
        }
    }

    private static string DescribeDiagnosticMethod(MethodBase method)
    {
        if (method == null)
            return "(null method)";

        try
        {
            Assembly assembly = method.DeclaringType?.Assembly;
            string assemblyName = assembly?.GetName().Name ?? "(unknown assembly)";
            string assemblyPath;
            try { assemblyPath = assembly?.Location ?? "(dynamic)"; }
            catch { assemblyPath = "(dynamic)"; }

            return "method=" + GetMethodDisplay(method) +
                   " | identity=" + GetMethodIdentity(method) +
                   " | assembly=" + assemblyName +
                   " | path=" + assemblyPath;
        }
        catch
        {
            return "method=" + GetMethodDisplay(method);
        }
    }

    private string DescribeDiagnosticContext(MethodBase method)
    {
        try
        {
            var map = _runtimeEntriesByInstrumentedMethod;
            var descriptions = new List<string>();
            if (map != null && map.TryGetValue(method, out RuntimeProfileEntry[] entries))
            {
                foreach (RuntimeProfileEntry entry in entries)
                {
                    PatchContext context = entry?.Context;
                    if (context == null)
                        continue;

                    descriptions.Add("[mod=" + context.ModName + " (" + context.ModGuid + ")" +
                                     ", owner=" + context.OwnerHarmonyId +
                                     ", kind=" + context.PatchType +
                                     ", target=" + context.TargetDisplay + "]");
                }
            }

            if (_runtimeTranspilerCallPlans != null &&
                _runtimeTranspilerCallPlans.TryGetValue(method, out RuntimeTranspilerCallPlan[] plans))
            {
                foreach (RuntimeTranspilerCallPlan plan in plans)
                {
                    if (_context.TryGetValue(plan.EntryId, out PatchContext context))
                    {
                        descriptions.Add("[call-site mod=" + context.ModName + " (" + context.ModGuid +
                                         "), target=" + context.TargetDisplay + "]");
                    }
                }
            }

            return descriptions.Count == 0
                ? "contexts=(none)"
                : "contexts=" + string.Join(" ", descriptions);
        }
        catch (Exception ex)
        {
            return "contexts=(could not describe: " + ex.GetType().Name + ")";
        }
    }

    private static string DescribeExistingHarmonyPatches(MethodBase method)
    {
        try
        {
            var info = HarmonyLib.Harmony.GetPatchInfo(method);
            if (info == null)
                return "existingPatches=(none)";

            var patches = new List<string>();
            foreach (HarmonyLib.Patch patch in info.Prefixes)
                patches.Add("Prefix:" + patch.owner);
            foreach (HarmonyLib.Patch patch in info.Postfixes)
                patches.Add("Postfix:" + patch.owner);
            foreach (HarmonyLib.Patch patch in info.Transpilers)
                patches.Add("Transpiler:" + patch.owner);
            foreach (HarmonyLib.Patch patch in info.Finalizers)
                patches.Add("Finalizer:" + patch.owner);

            return "existingPatches=" + (patches.Count == 0 ? "(none)" : string.Join(",", patches));
        }
        catch (Exception ex)
        {
            return "existingPatches=(inspect failed: " + ex.GetType().Name + ")";
        }
    }

    private void DumpDiagnosticSelectionAndSharedOwners()
    {
        if (!_crashTraceEnabled)
            return;

        try
        {
            List<string> selected;
            List<PatchContext> activeContexts;
            lock (_lock)
            {
                selected = _modsToProfile
                    .Where(pair => pair.Value)
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => _modGuidToName.TryGetValue(pair.Key, out string name)
                        ? name + " (" + pair.Key + ")"
                        : pair.Key)
                    .ToList();

                activeContexts = _context.Values.Where(IsContextEnabledLocked).ToList();
            }

            WriteCrashTrace("SELECTED MODS | " + (selected.Count == 0 ? "(none)" : string.Join("; ", selected)));
            WriteCrashTrace("ENABLED CONTEXTS | count=" + activeContexts.Count);

            // Same owner ID in separate selected assemblies is relevant to bundled helper DLLs.
            // Report observations only; collisions are not, by themselves, proof of a crash.
            var sharedOwners = activeContexts
                .Where(ctx => !ctx.IsTranspilerCallEntry &&
                              !ctx.IsTranspiledTargetEntry &&
                              !string.IsNullOrWhiteSpace(ctx.OwnerHarmonyId))
                .GroupBy(ctx => ctx.OwnerHarmonyId, StringComparer.Ordinal)
                .Select(group => new
                {
                    Owner = group.Key,
                    Assemblies = group.Select(ctx => ctx.InstrumentedMethod?.DeclaringType?.Assembly)
                        .Where(asm => asm != null)
                        .Distinct()
                        .ToList(),
                    Mods = group.Select(ctx => ctx.ModGuid).Distinct().ToList()
                })
                .Where(group => group.Assemblies.Count > 1 && group.Mods.Count > 1)
                .OrderBy(group => group.Owner, StringComparer.Ordinal);

            foreach (var group in sharedOwners)
                WriteCrashTrace("SHARED OWNER | owner=" + group.Owner +
                                " | mods=" + string.Join(",", group.Mods) +
                                " | assemblies=" + string.Join(",", group.Assemblies.Select(asm => asm.GetName().Name)));
        }
        catch (Exception ex)
        {
            WriteCrashTrace("SELECTION DIAGNOSTIC MANAGED EXCEPTION | " + ex);
        }
    }

}
