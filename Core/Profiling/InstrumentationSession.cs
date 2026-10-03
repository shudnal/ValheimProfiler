#nullable disable

using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace ValheimProfiler.Core.Profiling;

/// <summary>
/// Owns the patch-mutation ledger for one profiler. No native pointers, speculative JIT or target invocation.
/// A failed rebuild is not assumed atomic. Each attempted method is retained until cleanup is verified.
/// </summary>
internal sealed class InstrumentationSession
{
    private readonly Harmony _harmony;
    private readonly InstrumentationSafety _safety;
    private readonly InstrumentationReport _report;
    private readonly InstrumentationLog _log;
    private readonly int _mainThreadId;
    private readonly Action<string, MethodBase> _trace;
    private readonly Dictionary<MethodBase, Entry> _entries = new Dictionary<MethodBase, Entry>();
    private long _nextOrder;

    internal InstrumentationSession(Harmony harmony, InstrumentationSafety safety, InstrumentationReport report, InstrumentationLog log,
        Action<string, MethodBase> trace = null)
    {
        _harmony = harmony ?? throw new ArgumentNullException(nameof(harmony));
        if (string.IsNullOrWhiteSpace(harmony.Id) || harmony.Id == "*")
            throw new ArgumentException("An explicit, non-wildcard Harmony owner is required.", nameof(harmony));
        _safety = safety ?? throw new ArgumentNullException(nameof(safety));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        _trace = trace;
    }

    internal bool HasPending => _entries.Count > 0;
    internal int PendingCount => _entries.Count;
    internal int PendingFailureCount => _entries.Values.Count(entry => entry.RemovalFailed);
    internal int InstalledCount => _entries.Values.Count(entry => entry.Installed && !entry.RemovalFailed);

    internal bool TryInstall(MethodBase method, HarmonyMethod prefix, HarmonyMethod finalizer, InstrumentationLog.Operation log)
    {
        RequireMainThread();
        if (_safety.RestartRequired)
        {
            _safety.Release(method, _harmony.Id); // This caller has reserved but has not mutated the method.
            return false;
        }

        Entry entry = null;
        InstrumentationPatchSnapshot before = null;
        try
        {
            if (prefix?.method == null || finalizer?.method == null)
                throw new InvalidOperationException("Both timing callbacks must be available before installation.");
            before = InstrumentationPatchSnapshot.Capture(method);
            if (_entries.ContainsKey(method) || before.HasOwner(_harmony.Id))
            {
                var conflict = new InvalidOperationException("Unexpected pre-existing patches or ledger entry for this profiler owner; no new mutation was attempted.");
                if (!_entries.ContainsKey(method))
                    _entries.Add(method, new Entry { Method = method, Order = ++_nextOrder, RemovalFailed = true });
                _report.Failure(method, "Installation preflight failed", conflict, restartRequired: true);
                log.MethodFailure(method, "Installation preflight failed", conflict);
                RequireRestart(method, "Unexpected profiler-owned instrumentation was found before installation.");
                return false;
            }
            entry = new Entry { Method = method, Order = ++_nextOrder, Prefix = prefix?.method, Finalizer = finalizer?.method };
            // Record before calling Harmony, including methods whose wrapper generation later throws.
            _entries.Add(method, entry);
            RunBackend(method, () =>
            {
                entry.MutationAttempted = true;
                _harmony.Patch(method, prefix: prefix, finalizer: finalizer);
            });
            VerifyInstall(entry, before);
            entry.Installed = true;
            return true;
        }
        catch (Exception ex)
        {
            RecordFailure(method, entry == null ? "Installation preflight failed" : "Installation failed", ex, log);
            if (entry?.MutationAttempted == true)
                Remove(entry, log, before, rollback: true);
            else
            {
                // A failed initial cache reset never entered Harmony. Do not turn that failure
                // into an unnecessary unpatch/rebuild of an untouched method.
                if (entry != null)
                    _entries.Remove(method);
                _safety.Release(method, _harmony.Id);
                if (entry != null)
                    _report.Recovery(method, "No backend mutation was attempted; the reservation was released.");
            }
            return false;
        }
    }

    internal bool TryAddTranspiler(MethodBase method, HarmonyMethod transpiler, InstrumentationLog.Operation log)
    {
        RequireMainThread();
        if (!_entries.TryGetValue(method, out Entry entry) || !entry.Installed || _safety.RestartRequired)
            return false;
        InstrumentationPatchSnapshot before = null;
        try
        {
            if (transpiler?.method == null)
                throw new InvalidOperationException("The call-site transpiler must be available before installation.");
            before = InstrumentationPatchSnapshot.Capture(method);
            if (!before.MatchesOwned(_harmony.Id, entry.Prefix, entry.Finalizer, null))
                throw new InvalidOperationException("Timing patches changed before call-site instrumentation.");
            entry.Transpiler = transpiler.method;
            RunBackend(method, () => _harmony.Patch(method, transpiler: transpiler));
            VerifyInstall(entry, before);
            return true;
        }
        catch (Exception ex)
        {
            // A failed second rebuild may already have registered the transpiler. Do not claim that
            // the first timing wrapper is still intact; remove the entire owner in one rebuild.
            RecordFailure(method, "Call-site installation failed", ex, log);
            Remove(entry, log, before, rollback: true);
            return false;
        }
    }

    private void VerifyInstall(Entry entry, InstrumentationPatchSnapshot before)
    {
        InstrumentationPatchSnapshot after = InstrumentationPatchSnapshot.Capture(entry.Method);
        if (!before.SameForeignPatches(after, _harmony.Id))
            throw new InvalidOperationException("Foreign patch registrations changed during installation; they will not be overwritten by the profiler.");
        if (!after.MatchesOwned(_harmony.Id, entry.Prefix, entry.Finalizer, entry.Transpiler))
            throw new InvalidOperationException("The installed profiler callback set does not match the requested callback set.");
    }

    private void RecordFailure(MethodBase method, string stage, Exception exception, InstrumentationLog.Operation log)
    {
        _safety.Quarantine(method, InstrumentationLog.ExceptionSummary(exception));
        bool cacheFailure = exception is ResolverCacheException;
        if (cacheFailure)
        {
            stage += " (resolver cache reset " + ((ResolverCacheException)exception).Phase + ")";
            RequireRestart(method, "The resolver cache could not be cleared; further instrumentation is blocked.");
        }
        _report.Failure(method, stage, exception, restartRequired: cacheFailure);
        log.MethodFailure(method, stage, exception);
    }

    private bool Remove(Entry entry, InstrumentationLog.Operation log, InstrumentationPatchSnapshot expectedForeign = null, bool rollback = false)
    {
        if (entry.RemovalFailed)
            return false; // Do not repeatedly rebuild an uncertain detour on refresh/reset/shutdown.

        Exception inspectionError = null;
        InstrumentationPatchSnapshot before = null;
        try { before = InstrumentationPatchSnapshot.Capture(entry.Method); }
        catch (Exception ex) { inspectionError = ex; }

        try
        {
            // Always rebuild after an attempted mutation, even if owner metadata is already absent:
            // Harmony can remove metadata before a failing wrapper rebuild.
            RunBackend(entry.Method, () => _harmony.Unpatch(entry.Method, HarmonyPatchType.All, _harmony.Id));
            InstrumentationPatchSnapshot after = InstrumentationPatchSnapshot.Capture(entry.Method);
            if (after.HasOwner(_harmony.Id))
                throw new InvalidOperationException("Profiler-owned patches remain registered after removal.");
            if (inspectionError != null)
                throw new InvalidOperationException("Removal returned, but the pre-removal patch set could not be inspected.", inspectionError);
            if (!before.SameForeignPatches(after, _harmony.Id) ||
                (expectedForeign != null && !expectedForeign.SameForeignPatches(after, _harmony.Id)))
                throw new InvalidOperationException("Foreign patch registrations differ from the pre-operation snapshot; recovery is not verified.");

            _entries.Remove(entry.Method);
            _safety.Release(entry.Method, _harmony.Id);
            if (rollback)
                _report.Recovery(entry.Method, "Owner-scoped rebuild succeeded; no profiler callbacks remain registered and foreign registrations match. This method is not retried in this process.");
            return true;
        }
        catch (Exception ex)
        {
            entry.Installed = false;
            entry.RemovalFailed = true;
            string stage = rollback ? "Rollback failed" : "Unpatch failed";
            RequireRestart(entry.Method, stage); // Fence future mutations before formatting optional diagnostics.
            _report.Failure(entry.Method, stage, ex, restartRequired: true);
            log.MethodFailure(entry.Method, stage + "; recovery uncertain; restart required", ex);
            return false;
        }
    }

    internal MethodBase[] GetRemovalPlan()
    {
        RequireMainThread();
        return _entries.Values
            .Where(entry => !entry.RemovalFailed)
            .OrderByDescending(entry => entry.Order)
            .Select(entry => entry.Method)
            .ToArray();
    }

    internal bool RemoveTracked(MethodBase method, InstrumentationLog.Operation log)
    {
        RequireMainThread();
        if (method == null || !_entries.TryGetValue(method, out Entry entry))
            return true;
        return Remove(entry, log);
    }

    internal CleanupResult AuditCleanup(InstrumentationLog.Operation log, int attempted, int removed)
    {
        RequireMainThread();
        var result = new CleanupResult { Attempted = attempted, Removed = removed };

        // The ledger covers attempted mutations; also inspect Harmony's registry for untracked
        // leftovers. Absence of metadata alone never repairs a failed native/detour rebuild.
        try
        {
            foreach (MethodBase method in Harmony.GetAllPatchedMethods().ToArray())
            {
                try
                {
                    Patches patches = Harmony.GetPatchInfo(method);
                    if (patches == null || !patches.Owners.Contains(_harmony.Id))
                        continue;
                    result.ResidualMethods++;
                    if (_entries.ContainsKey(method))
                        continue;
                    _entries.Add(method, new Entry { Method = method, Order = ++_nextOrder, RemovalFailed = true });
                    var ex = new InvalidOperationException("Untracked profiler-owned callbacks remain after cleanup; no speculative second rebuild was attempted.");
                    _report.Failure(method, "Removal verification failed", ex, restartRequired: true);
                    log.MethodFailure(method, "Removal verification failed", ex);
                    RequireRestart(method, "Residual profiler callbacks were found.");
                }
                catch (Exception ex)
                {
                    result.AuditFailures++;
                    _report.Failure(method, "Removal audit failed", ex, restartRequired: true);
                    log.MethodFailure(method, "Removal audit failed", ex);
                    RequireRestart(method, "The remaining owner registrations could not be verified.");
                }
            }
        }
        catch (Exception ex)
        {
            result.AuditFailures++;
            _report.Failure(null, "Removal audit failed", ex, restartRequired: true);
            log.MethodFailure(null, "Removal audit failed", ex);
            RequireRestart(null, "The remaining owner registrations could not be enumerated.");
        }
        result.Unresolved = _entries.Count;
        return result;
    }

    internal CleanupResult RemoveAll(InstrumentationLog.Operation log)
    {
        RequireMainThread();
        int attempted = 0;
        int removed = 0;

        // A failure on one method cannot prevent cleanup of the rest. Reverse mutation order
        // unwinds the installation sequence; it does not assume that the sequence is a call graph.
        foreach (MethodBase method in GetRemovalPlan())
        {
            attempted++;
            if (RemoveTracked(method, log))
                removed++;
        }

        return AuditCleanup(log, attempted, removed);
    }

    private void RunBackend(MethodBase method, Action operation)
    {
        Action<string> trace = _trace == null ? null : message => _trace(message, method);
        MonoModResolverCache.Run(operation, trace);
    }

    internal void RequireRestart(MethodBase method, string reason)
    {
        if (_safety.RequireRestart(_harmony.Id, method, reason))
            _log.RecoveryRequired(_safety.RestartMessage);
    }

    private void RequireMainThread()
    {
        if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            throw new InvalidOperationException("Instrumentation mutations must run on the Unity main thread.");
    }

    private sealed class Entry
    {
        internal MethodBase Method;
        internal long Order;
        internal MethodInfo Prefix;
        internal MethodInfo Finalizer;
        internal MethodInfo Transpiler;
        internal bool MutationAttempted;
        internal bool Installed;
        internal bool RemovalFailed;
    }

    internal sealed class CleanupResult
    {
        internal int Attempted;
        internal int Removed;
        internal int Unresolved;
        internal int ResidualMethods;
        internal int AuditFailures;
        internal bool Verified => Unresolved == 0 && ResidualMethods == 0 && AuditFailures == 0;
        internal string Summary => "Cleanup attempted: " + Attempted + ". Removed: " + Removed +
            ". Unresolved: " + Unresolved + ". Residual owner methods: " + ResidualMethods + ". Audit failures: " + AuditFailures + ".";
    }
}
