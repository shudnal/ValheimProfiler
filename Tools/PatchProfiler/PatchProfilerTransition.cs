#nullable disable

using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace ValheimProfiler.Tools.PatchProfiler;

internal sealed partial class PatchProfilerTool
{
    private void BeginStartProfiling()
    {
        if (_profilingActive || IsTransitioning)
            return;
        if (_app.InstrumentationSafety.RestartRequired)
        {
            _status = _app.InstrumentationSafety.RestartMessage;
            return;
        }

        ResetPatchMeasurementStack();
        ReadCrashDiagnosticOptions();
        BeginCrashTraceRun();
        WriteCrashTrace("START button pressed");
        _safetyReport.Clear();
        _transitionLog = _instrumentationLog.BeginOperation("Installation");

        try
        {
            WriteCrashTrace("START DISCOVERY BEGIN");
            RefreshPatchList();
            WriteCrashTrace("START DISCOVERY END");
            if (!_listReady || _app.InstrumentationSafety.RestartRequired)
            {
                EndTransitionLog();
                return;
            }

            SaveModSelection();
            _statsFrozen = false;
            DumpDiagnosticSelectionAndSharedOwners();
            _installCatalog = InstrumentationCatalog.Capture();
            if (!_installCatalog.IsComplete)
                throw new InvalidOperationException("Safety catalog is incomplete: " + _installCatalog.Error);

            List<MethodBase> methods;
            WriteCrashTrace("RUNTIME MAP BEGIN");
            lock (_lock)
            {
                RebuildRuntimeProfileMapLocked();
                methods = _runtimeEntriesByInstrumentedMethod.Keys.Concat(_runtimeTranspiledTargets)
                    .Where(method => method != null).Distinct().ToList();
            }
            WriteCrashTrace("RUNTIME MAP END | methods=" + methods.Count);
            methods.Sort((a, b) => string.CompareOrdinal(GetDiagnosticMethodKey(a), GetDiagnosticMethodKey(b)));

            _installSelectedCount = methods.Count;
            _installSkippedByLimit = 0;
            if (_diagnosticInstrumentationLimit > 0 && methods.Count > _diagnosticInstrumentationLimit)
            {
                MethodBase[] limitedOut = methods.Skip(_diagnosticInstrumentationLimit).ToArray();
                _installSkippedByLimit = limitedOut.Length;
                foreach (MethodBase method in limitedOut)
                {
                    _safetyReport.Add(method, "Not attempted: diagnostic instrumentation limit reached.");
                    RemoveRuntimeMethod(method);
                }
                methods = methods.Take(_diagnosticInstrumentationLimit).ToList();
                WriteCrashTrace($"INSTALL LIMIT APPLIED | attempted={methods.Count} skipped={_installSkippedByLimit}");
            }

            _installPlan = methods;
            _installIndex = 0;
            _installNotAttempted = 0;
            _installCurrentMethod = null;
            _installCurrentCallSiteTranspiler = null;
            _installTimingInstalled = false;
            _installInstrumentationFailures = 0;
            _installCallSiteFailures = 0;
            _installSafetySkipped = 0;
            _installCancelRequested = false;
            _transitionCurrentMethod = string.Empty;
            _instrumentationTransition = InstrumentationTransition.Installing;
            WriteCrashTrace($"INSTALL PLAN | methods={_installPlan.Count} selected={_installSelectedCount} limit={_diagnosticInstrumentationLimit}");
            _status = $"Installing instrumentation: 0/{_installPlan.Count}.";
            MarkViewDirty();
        }
        catch (Exception ex)
        {
            WriteCrashTrace("START MANAGED EXCEPTION | " + ex);
            _safetyReport.Failure(null, "Start failed", ex);
            _transitionLog?.Failure("Start failed", ex);
            EndTransitionLog();
            AbortTransitionState();
            StopProfilingInternal(false);
            _status = _app.InstrumentationSafety.RestartRequired ? _app.InstrumentationSafety.RestartMessage
                : "Start failed: " + InstrumentationLog.ExceptionSummary(ex);
        }
    }

    private void ProcessInstrumentationTransitionSafely()
    {
        try
        {
            ProcessInstrumentationTransition();
        }
        catch (Exception ex)
        {
            _safetyReport.Failure(null, "Frame-spread instrumentation operation failed", ex, restartRequired: _instrumentationTransition != InstrumentationTransition.Installing);
            _transitionLog?.Failure("Frame-spread instrumentation operation failed", ex);
            _profilingActive = false;
            if (_instrumentationTransition == InstrumentationTransition.Installing)
            {
                EndTransitionLog();
                BeginRemovalTransition(restartAfterRemoval: false, rollingBack: true);
                _status = "Installation interrupted; removing installed hooks.";
            }
            else
            {
                _instrumentation.RequireRestart(null, "Frame-spread removal did not complete.");
                EndTransitionLog();
                AbortTransitionState();
                ClearPatchRuntimeDispatch();
                _status = _app.InstrumentationSafety.RestartMessage;
            }
            MarkViewDirty();
        }
    }

    private void ProcessInstrumentationTransition()
    {
        switch (_instrumentationTransition)
        {
            case InstrumentationTransition.Installing:
                ProcessInstallFrame();
                break;
            case InstrumentationTransition.Removing:
            case InstrumentationTransition.RollingBack:
                ProcessRemovalFrame();
                break;
        }
    }

    private void ProcessInstallFrame()
    {
        if (_installCancelRequested)
        {
            BeginRollbackFromInstall(cancelled: true);
            return;
        }
        if (_app.InstrumentationSafety.RestartRequired)
        {
            BeginRollbackFromInstall(cancelled: false);
            return;
        }

        long frameStart = Stopwatch.GetTimestamp();
        int backendOperations = 0;

        while (_installIndex < _installPlan.Count)
        {
            if (_installCancelRequested || _app.InstrumentationSafety.RestartRequired)
                break;
            if (InstrumentationFrameBudget.IsExhausted(frameStart, backendOperations, _gradualProfilingStart))
                break;

            if (_installCurrentMethod == null)
            {
                MethodBase method = _installPlan[_installIndex];
                _transitionCurrentMethod = InstrumentationFrameBudget.MethodLabel(method);
                if (!PreparePatchInstallMethod(method))
                {
                    CompletePatchInstallMethod();
                    continue;
                }
            }

            MethodBase current = _installCurrentMethod;
            int attempt = _installIndex + 1;
            if (!_installTimingInstalled)
            {
                WriteCrashTrace($"HARMONY TIMING PATCH BEGIN | attempt={attempt} | {DescribeExistingHarmonyPatches(current)}", current);
                bool installed = _instrumentation.TryInstall(current, _installCurrentPrefix, _installCurrentFinalizer, _transitionLog);
                backendOperations++;
                WriteCrashTrace($"HARMONY TIMING PATCH END | attempt={attempt} success={installed}", current);

                if (!installed)
                {
                    _installInstrumentationFailures++;
                    RemoveRuntimeMethod(current);
                    CompletePatchInstallMethod();
                    continue;
                }

                _installTimingInstalled = true;
                if (_installCurrentCallSiteTranspiler == null)
                {
                    CompletePatchInstallMethod();
                    continue;
                }

                if (InstrumentationFrameBudget.IsExhausted(frameStart, backendOperations, _gradualProfilingStart))
                    break;
            }

            WriteCrashTrace($"HARMONY CALLSITE PATCH BEGIN | attempt={attempt}", current);
            MethodBase previousInstallTarget = _callSiteInstallationTarget;
            _callSiteInstallationTarget = current;
            bool callSiteInstalled;
            try { callSiteInstalled = _instrumentation.TryAddTranspiler(current, _installCurrentCallSiteTranspiler, _transitionLog); }
            finally { _callSiteInstallationTarget = previousInstallTarget; }
            backendOperations++;
            WriteCrashTrace($"HARMONY CALLSITE PATCH END | attempt={attempt} success={callSiteInstalled}", current);
            if (!callSiteInstalled)
            {
                _installCallSiteFailures++;
                RemoveRuntimeMethod(current);
            }
            CompletePatchInstallMethod();
        }

        _status = $"Installing instrumentation: {_installIndex}/{_installPlan.Count}.";

        if (_installCancelRequested || _app.InstrumentationSafety.RestartRequired)
        {
            BeginRollbackFromInstall(cancelled: _installCancelRequested && !_app.InstrumentationSafety.RestartRequired);
            return;
        }
        if (_installIndex >= _installPlan.Count)
            FinishPatchInstallation();
    }

    private bool PreparePatchInstallMethod(MethodBase method)
    {
        int attempt = _installIndex + 1;
        if (!_app.InstrumentationSafety.TryReserve(method, PluginGuid, _installCatalog, out string skipReason))
        {
            _installSafetySkipped++;
            _safetyReport.Add(method, skipReason);
            WriteCrashTrace("SAFETY SKIP | " + skipReason, method);
            _transitionLog.Skipped(method, skipReason);
            RemoveRuntimeMethod(method);
            return false;
        }

        try
        {
            WriteCrashTrace($"INSTALL METHOD BEGIN {attempt}/{_installPlan.Count} | {DescribeDiagnosticContext(method)}", method);
            _installCurrentPrefix = new HarmonyMethod(AccessTools.Method(typeof(PatchProfilerTool), nameof(TimingPrefix))) { priority = Priority.Last };
            _installCurrentFinalizer = new HarmonyMethod(AccessTools.Method(typeof(PatchProfilerTool), nameof(TimingFinalizer))) { priority = Priority.First };
            _installCurrentCallSiteTranspiler = null;
            if (_runtimeTranspilerCallPlans.ContainsKey(method))
            {
                string[] owners = Harmony.GetPatchInfo(method)?.Transpilers
                    ?.Where(patch => patch != null && !string.IsNullOrWhiteSpace(patch.owner) && patch.owner != PluginGuid)
                    .Select(patch => patch.owner).Distinct(StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();
                _installCurrentCallSiteTranspiler = new HarmonyMethod(AccessTools.Method(typeof(PatchProfilerTool), nameof(InjectedCallTimingTranspiler)))
                {
                    priority = Priority.Last,
                    after = owners
                };
            }
            WriteCrashTrace($"HARMONY PRECHECK BEGIN | attempt={attempt}", method);
            _installCurrentMethod = method;
            _installTimingInstalled = false;
            return true;
        }
        catch (Exception ex)
        {
            _app.InstrumentationSafety.Release(method, PluginGuid);
            _installInstrumentationFailures++;
            _safetyReport.Failure(method, "Installation preflight failed", ex);
            _transitionLog.MethodFailure(method, "Installation preflight failed", ex);
            RemoveRuntimeMethod(method);
            return false;
        }
    }

    private void CompletePatchInstallMethod()
    {
        MethodBase completed = _installCurrentMethod;
        if (completed != null)
            WriteCrashTrace($"INSTALL METHOD END {_installIndex + 1}/{_installPlan.Count}", completed);
        _installIndex++;
        _installCurrentMethod = null;
        _installCurrentPrefix = null;
        _installCurrentFinalizer = null;
        _installCurrentCallSiteTranspiler = null;
        _installTimingInstalled = false;
        _transitionCurrentMethod = string.Empty;
    }

    private void FinishPatchInstallation()
    {
        _installNotAttempted = Math.Max(0, _installSelectedCount - _installIndex - _installSkippedByLimit);
        int installedCount = _instrumentation.InstalledCount;
        _profilingActive = installedCount > 0 && !_app.InstrumentationSafety.RestartRequired;
        bool deferredFailure = DrainCallSiteRebuildFailuresWithoutStopping();
        installedCount = _instrumentation.InstalledCount;
        string summary = BuildPatchInstallSummary(installedCount);
        WriteCrashTrace("START COMPLETE | " + summary);
        _transitionLog?.Summary(summary, _installInstrumentationFailures > 0 || _installCallSiteFailures > 0 || deferredFailure || _app.InstrumentationSafety.RestartRequired);
        EndTransitionLog();
        _instrumentationTransition = InstrumentationTransition.None;

        if (deferredFailure || _app.InstrumentationSafety.RestartRequired)
        {
            _profilingActive = false;
            BeginRemovalTransition(restartAfterRemoval: false, rollingBack: true);
            return;
        }

        _status = (_profilingActive ? "Profiling enabled. " : "No selected methods were instrumented. ") + summary;
        if (_safetyReport.Count > 0)
            _status += " See Skipped methods.";
        if (_crashTraceEnabled)
            _status += " Crash trace enabled.";
        ClearInstallTransitionData();
        MarkViewDirty();
    }

    private bool DrainCallSiteRebuildFailuresWithoutStopping()
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
        return failures > 0;
    }

    private string BuildPatchInstallSummary(int installedCount) =>
        $"Selected: {_installSelectedCount}. Instrumented: {installedCount}. Safety skipped: {_installSafetySkipped}. " +
        $"Installation failures: {_installInstrumentationFailures}. Call-site failures: {_installCallSiteFailures}. " +
        $"Unresolved methods: {_instrumentation.PendingFailureCount}. Skipped by limit: {_installSkippedByLimit}. Not attempted: {_installNotAttempted}.";

    private void RequestCancelStart()
    {
        if (_instrumentationTransition != InstrumentationTransition.Installing)
            return;
        _installCancelRequested = true;
        _status = "Cancelling start; installed hooks will be removed.";
        MarkViewDirty();
    }

    private void BeginRollbackFromInstall(bool cancelled)
    {
        int firstUnattempted = _installIndex + (_installCurrentMethod != null ? 1 : 0);
        _installNotAttempted = Math.Max(0, _installSelectedCount - firstUnattempted - _installSkippedByLimit);
        if (!cancelled)
        {
            foreach (MethodBase method in _installPlan.Skip(firstUnattempted))
            {
                _safetyReport.Add(method, "Not attempted: installation stopped after an uncertain patch recovery. Restart the game.");
                RemoveRuntimeMethod(method);
            }
        }
        string summary = BuildPatchInstallSummary(_instrumentation.InstalledCount);
        _transitionLog?.Summary((cancelled ? "Installation cancelled. " : "Installation aborted. ") + summary,
            !cancelled || _installInstrumentationFailures > 0 || _installCallSiteFailures > 0);
        EndTransitionLog();
        _profilingActive = false;
        BeginRemovalTransition(restartAfterRemoval: false, rollingBack: true);
        _status = cancelled ? "Cancelling start; removing installed hooks." : "Installation aborted; removing installed hooks.";
    }

    private void BeginStopProfiling(bool freezeData, bool restartAfterRemoval)
    {
        if (_instrumentationTransition == InstrumentationTransition.Installing)
        {
            RequestCancelStart();
            return;
        }
        if (IsTransitioning)
            return;

        bool wasActive = _profilingActive;
        _profilingActive = false;
        ResetPatchMeasurementStack();
        if (wasActive && freezeData)
            FreezePatchDataNow();
        BeginRemovalTransition(restartAfterRemoval: restartAfterRemoval, rollingBack: false);
        if (_removalPlan.Length == 0)
            FinishPatchRemoval();
        else
            _status = $"Removing instrumentation: 0/{_removalPlan.Length}.";
    }

    private void BeginRemovalTransition(bool restartAfterRemoval, bool rollingBack)
    {
        _profilingActive = false;
        ResetPatchMeasurementStack();
        _restartAfterRemoval = restartAfterRemoval;
        ClearPatchRuntimeDispatch();
        _removalPlan = _instrumentation.GetRemovalPlan();
        _removalIndex = 0;
        _removalAttempted = 0;
        _removalRemoved = 0;
        _transitionCurrentMethod = string.Empty;
        if (rollingBack)
        {
            _rollbackProgressTotal = _installPlan.Count;
            _rollbackProgressStart = Math.Max(0, Math.Min(_installIndex, _rollbackProgressTotal));
        }
        else
        {
            _rollbackProgressTotal = 0;
            _rollbackProgressStart = 0;
        }
        _transitionLog = _instrumentationLog.BeginOperation("Removal");
        _instrumentationTransition = rollingBack ? InstrumentationTransition.RollingBack : InstrumentationTransition.Removing;
        WriteCrashTrace("UNPATCH BEGIN | methods=" + _removalPlan.Length);
    }

    private void ProcessRemovalFrame()
    {
        long frameStart = Stopwatch.GetTimestamp();
        int backendOperations = 0;
        while (_removalIndex < _removalPlan.Length)
        {
            if (InstrumentationFrameBudget.IsExhausted(frameStart, backendOperations, _gradualProfilingStart))
                break;
            MethodBase method = _removalPlan[_removalIndex];
            _transitionCurrentMethod = InstrumentationFrameBudget.MethodLabel(method);
            _removalAttempted++;
            if (_instrumentation.RemoveTracked(method, _transitionLog))
                _removalRemoved++;
            _removalIndex++;
            backendOperations++;
        }
        _status = _instrumentationTransition == InstrumentationTransition.RollingBack
            ? $"Rolling back instrumentation: {RollbackRemainingProgressCount}/{_rollbackProgressTotal}."
            : $"Removing instrumentation: {_removalIndex}/{_removalPlan.Length}.";
        if (_removalIndex >= _removalPlan.Length)
            FinishPatchRemoval();
    }

    private void FinishPatchRemoval()
    {
        InstrumentationSession.CleanupResult result;
        try
        {
            result = _instrumentation.AuditCleanup(_transitionLog, _removalAttempted, _removalRemoved);
        }
        catch (Exception ex)
        {
            _safetyReport.Failure(null, "Removal audit interrupted", ex, restartRequired: true);
            _transitionLog?.Failure("Removal audit interrupted", ex);
            _instrumentation.RequireRestart(null, "Removal audit did not complete.");
            result = new InstrumentationSession.CleanupResult();
        }
        bool clean = result.Verified && !_app.InstrumentationSafety.RestartRequired;
        _transitionLog?.Summary(result.Summary, !clean);
        WriteCrashTrace("UNPATCH END | " + result.Summary);
        EndTransitionLog();

        bool rollingBack = _instrumentationTransition == InstrumentationTransition.RollingBack;
        bool restart = _restartAfterRemoval && clean;
        _instrumentationTransition = InstrumentationTransition.None;
        _removalPlan = Array.Empty<MethodBase>();
        _transitionCurrentMethod = string.Empty;
        _rollbackProgressStart = 0;
        _rollbackProgressTotal = 0;
        ClearInstallTransitionData();

        if (!clean)
            _status = _app.InstrumentationSafety.RestartMessage;
        else if (rollingBack)
            _status = "Instrumentation rollback completed.";
        else
            _status = "Profiling paused. Removal checks passed; data frozen.";

        MarkViewDirty();
        if (restart)
            BeginStartProfiling();
    }

    private void FreezePatchDataNow()
    {
        float now = Time.realtimeSinceStartup;
        int frame = Time.frameCount;
        SetCurrentRealtime(now);
        FlushAnalyticsAt(now);
        _frozenRealtime = now;
        _frozenFrame = frame;
        _statsFrozen = true;
    }

    private void ClearPatchRuntimeDispatch()
    {
        lock (_lock)
        {
            _runtimeEntriesByInstrumentedMethod = new Dictionary<MethodBase, RuntimeProfileEntry[]>(0);
            _runtimeTranspiledTargets = new HashSet<MethodBase>();
            _runtimeTranspilerCallPlans = new Dictionary<MethodBase, RuntimeTranspilerCallPlan[]>(0);
            _runtimeTranspilerCallStats = new Dictionary<int, PatchStat>(0);
        }
    }

    private void AbortTransitionForSynchronousCleanup()
    {
        if (!IsTransitioning)
            return;
        _profilingActive = false;
        _transitionLog?.Summary("Frame-spread instrumentation operation was interrupted by synchronous cleanup.", hasFailures: false);
        EndTransitionLog();
        AbortTransitionState();
    }

    private void AbortTransitionState()
    {
        _instrumentationTransition = InstrumentationTransition.None;
        _installCancelRequested = false;
        _restartAfterRemoval = false;
        _transitionCurrentMethod = string.Empty;
        _removalPlan = Array.Empty<MethodBase>();
        _rollbackProgressStart = 0;
        _rollbackProgressTotal = 0;
        ClearInstallTransitionData();
    }

    private void ClearInstallTransitionData()
    {
        _installPlan = new List<MethodBase>();
        _installCatalog = null;
        _installIndex = 0;
        _installCurrentMethod = null;
        _installCurrentPrefix = null;
        _installCurrentFinalizer = null;
        _installCurrentCallSiteTranspiler = null;
        _installTimingInstalled = false;
    }

    private void EndTransitionLog()
    {
        if (_transitionLog == null)
            return;
        try { _transitionLog.Dispose(); }
        finally { _transitionLog = null; }
    }

    private float RollbackRemainingProgress
    {
        get
        {
            if (_rollbackProgressTotal <= 0 || _rollbackProgressStart <= 0)
                return 0f;
            if (_removalPlan.Length <= 0)
                return 0f;
            float removalProgress = Mathf.Clamp01((float)_removalRemoved / _removalPlan.Length);
            return _rollbackProgressStart * (1f - removalProgress);
        }
    }

    private int RollbackRemainingProgressCount => Mathf.Clamp(
        Mathf.CeilToInt(RollbackRemainingProgress), 0, _rollbackProgressStart);

    private float TransitionProgress
    {
        get
        {
            if (_instrumentationTransition == InstrumentationTransition.Installing)
                return _installPlan.Count <= 0 ? 0f : Mathf.Clamp01((float)_installIndex / _installPlan.Count);
            if (_instrumentationTransition == InstrumentationTransition.RollingBack)
                return _rollbackProgressTotal <= 0 ? 0f : Mathf.Clamp01(RollbackRemainingProgress / _rollbackProgressTotal);
            if (_instrumentationTransition == InstrumentationTransition.Removing)
                return _removalPlan.Length <= 0 ? 1f : Mathf.Clamp01((float)_removalIndex / _removalPlan.Length);
            return 0f;
        }
    }

    private string TransitionProgressText
    {
        get
        {
            string method = string.IsNullOrEmpty(_transitionCurrentMethod) ? string.Empty : " | " + _transitionCurrentMethod;
            if (_instrumentationTransition == InstrumentationTransition.RollingBack)
                return $"Rolling back: {RollbackRemainingProgressCount}/{_rollbackProgressTotal} ({TransitionProgress * 100f:0.0}%){method}";
            string phase = _instrumentationTransition == InstrumentationTransition.Installing ? "Installing" : "Removing";
            int done = _instrumentationTransition == InstrumentationTransition.Installing ? _installIndex : _removalIndex;
            int total = _instrumentationTransition == InstrumentationTransition.Installing ? _installPlan.Count : _removalPlan.Length;
            return $"{phase}: {done}/{total} ({TransitionProgress * 100f:0.0}%){method}";
        }
    }
}
