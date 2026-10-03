#nullable disable

using HarmonyLib;
using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace ValheimProfiler.Tools.GeneralMethodProfiler;

internal sealed partial class GeneralMethodProfilerTool
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
        if (!_listReady)
            RefreshMethodList();
        if (!_listReady || !SaveSelection())
            return;

        _transitionLog = _instrumentationLog.BeginOperation("Installation");
        try
        {
            if (!StopProfilingInternal(false))
            {
                EndTransitionLog();
                return;
            }
            ResetAllStats();
            _safetyReport.Clear();
            _measuredEntries.Clear();
            _maxTreeNodes = _app.Config.GeneralMethodMaxTreeNodes.Value;
            _callStack = new InvocationFrame[_app.Config.GeneralMethodMaxCallDepth.Value];
            _installPlan = _methodsByKey.Values.Where(entry => entry.Selected)
                .OrderBy(entry => entry.TypeEntry.AssemblyName, StringComparer.Ordinal)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal).ToArray();
            if (_installPlan.Length == 0)
            {
                EndTransitionLog();
                _status = "Select mod methods in Methods to profile. Nothing is enabled by default.";
                _mainTab = MainTab.MethodsToProfile;
                return;
            }

            _installCatalog = InstrumentationCatalog.Capture();
            if (!_installCatalog.IsComplete)
                throw new InvalidOperationException("Safety catalog is incomplete: " + _installCatalog.Error);

            _installIndex = 0;
            _installInstrumentationFailures = 0;
            _installSafetySkipped = 0;
            _installCancelRequested = false;
            _transitionCurrentMethod = string.Empty;
            _instrumentationTransition = InstrumentationTransition.Installing;
            _status = $"Installing instrumentation: 0/{_installPlan.Length}.";
            MarkSelectionRowsDirty();
            MarkViewDirty();
        }
        catch (Exception ex)
        {
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
            DiscardActiveStack();
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
                _runtimeEntries.Clear();
                _status = _app.InstrumentationSafety.RestartMessage;
            }
            MarkSelectionRowsDirty();
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

        while (_installIndex < _installPlan.Length)
        {
            if (_installCancelRequested || _app.InstrumentationSafety.RestartRequired)
                break;
            if (InstrumentationFrameBudget.IsExhausted(frameStart, backendOperations, _gradualProfilingStart))
                break;

            GeneralMethodEntry entry = _installPlan[_installIndex];
            MethodInfo method = entry.Method;
            _transitionCurrentMethod = InstrumentationFrameBudget.MethodLabel(method);

            bool installed = PrepareAndInstallGeneralMethod(entry, method, out bool backendAttempted);
            if (backendAttempted)
                backendOperations++;
            _installIndex++;
            if (!installed)
                continue;
        }

        _status = $"Installing instrumentation: {_installIndex}/{_installPlan.Length}.";

        if (_installCancelRequested || _app.InstrumentationSafety.RestartRequired)
        {
            BeginRollbackFromInstall(cancelled: _installCancelRequested && !_app.InstrumentationSafety.RestartRequired);
            return;
        }
        if (_installIndex >= _installPlan.Length)
            FinishGeneralInstallation();
    }

    private bool PrepareAndInstallGeneralMethod(GeneralMethodEntry entry, MethodInfo method, out bool backendAttempted)
    {
        backendAttempted = false;
        HarmonyMethod prefix;
        HarmonyMethod finalizer;
        try
        {
            string unsupported = _app.InstrumentationSafety.GetPreviousFailure(method) ?? GetGeneralUnsupportedReason(method, _installCatalog);
            entry.UnsupportedReason = unsupported;
            entry.Tooltip = entry.TypeEntry.AssemblyName + " / " + entry.TypeEntry.ModuleName + "\n" +
                InstrumentationSafety.GetSignature(method) +
                (unsupported == null ? string.Empty : "\nUnavailable: " + unsupported);
            if (unsupported != null)
            {
                _installSafetySkipped++;
                _safetyReport.Add(method, unsupported);
                _transitionLog.Skipped(method, unsupported);
                return false;
            }

            string[] owners = Harmony.GetPatchInfo(method)?.Owners.ToArray() ?? Array.Empty<string>();
            prefix = new HarmonyMethod(AccessTools.Method(typeof(GeneralMethodProfilerTool), nameof(TimingPrefix)))
            {
                priority = Priority.First,
                before = owners
            };
            finalizer = new HarmonyMethod(AccessTools.Method(typeof(GeneralMethodProfilerTool), nameof(TimingFinalizer)))
            {
                priority = Priority.Last,
                after = owners
            };
            if (!_app.InstrumentationSafety.TryReserve(method, HarmonyId, _installCatalog, out unsupported))
            {
                _installSafetySkipped++;
                _safetyReport.Add(method, unsupported);
                _transitionLog.Skipped(method, unsupported);
                return false;
            }
        }
        catch (Exception ex)
        {
            _installInstrumentationFailures++;
            _safetyReport.Failure(method, "Installation preflight failed", ex);
            _transitionLog.MethodFailure(method, "Installation preflight failed", ex);
            return false;
        }

        backendAttempted = true;
        if (!_instrumentation.TryInstall(method, prefix, finalizer, _transitionLog))
        {
            _installInstrumentationFailures++;
            entry.Stat = null;
            entry.UnsupportedReason = _app.InstrumentationSafety.GetPreviousFailure(method);
            if (entry.UnsupportedReason != null)
                entry.Tooltip += "\nUnavailable: " + entry.UnsupportedReason;
            return false;
        }

        entry.Stat = new RollingProfilerStat(useSampleTimestamps: true);
        _runtimeEntries.Add(method, entry);
        _measuredEntries.Add(entry);
        return true;
    }

    private void FinishGeneralInstallation()
    {
        _currentFrame = Time.frameCount;
        float startTime = Time.realtimeSinceStartup;
        SetCurrentRealtime(startTime);
        UpdateGcBaseline();
        _statsFrozen = false;
        _profilingActive = !_app.InstrumentationSafety.RestartRequired && _runtimeEntries.Count > 0;
        string summary = BuildGeneralInstallSummary(notAttempted: 0);
        _transitionLog?.Summary(summary, _installInstrumentationFailures > 0 || _app.InstrumentationSafety.RestartRequired);
        EndTransitionLog();
        _instrumentationTransition = InstrumentationTransition.None;
        _status = _app.InstrumentationSafety.RestartRequired
            ? "Installation aborted. Recovery uncertain; restart required. " + summary
            : (_profilingActive ? summary : "No methods were instrumented. " + summary);
        if (_safetyReport.Count > 0)
            _status += " See Skipped methods.";
        ClearInstallTransitionData();
        MarkSelectionRowsDirty();
        MarkViewDirty();
    }

    private string BuildGeneralInstallSummary(int notAttempted) =>
        $"Selected: {_installPlan.Length}. Instrumented: {_runtimeEntries.Count}. " +
        $"Safety skipped: {_installSafetySkipped}. Installation failures: {_installInstrumentationFailures}. " +
        $"Unresolved methods: {_instrumentation.PendingFailureCount}. Not attempted: {notAttempted}.";

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
        int notAttempted = Math.Max(0, _installPlan.Length - _installIndex);
        if (!cancelled)
        {
            foreach (GeneralMethodEntry entry in _installPlan.Skip(_installIndex))
                _safetyReport.Add(entry.Method, "Not attempted: installation stopped after an uncertain patch recovery. Restart the game.");
        }
        string summary = BuildGeneralInstallSummary(notAttempted);
        _transitionLog?.Summary((cancelled ? "Installation cancelled. " : "Installation aborted. ") + summary,
            !cancelled || _installInstrumentationFailures > 0);
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
        DiscardActiveStack();
        if (wasActive && freezeData)
            FreezeGeneralDataNow();
        BeginRemovalTransition(restartAfterRemoval, rollingBack: false);
        if (_removalPlan.Length == 0)
            FinishGeneralRemoval();
        else
            _status = $"Removing instrumentation: 0/{_removalPlan.Length}.";
    }

    private void BeginRemovalTransition(bool restartAfterRemoval, bool rollingBack)
    {
        _profilingActive = false;
        DiscardActiveStack();
        _restartAfterRemoval = restartAfterRemoval;
        _runtimeEntries.Clear();
        _removalPlan = _instrumentation.GetRemovalPlan();
        _removalIndex = 0;
        _removalAttempted = 0;
        _removalRemoved = 0;
        _transitionCurrentMethod = string.Empty;
        if (rollingBack)
        {
            _rollbackProgressTotal = _installPlan.Length;
            _rollbackProgressStart = Math.Max(0, Math.Min(_installIndex, _rollbackProgressTotal));
        }
        else
        {
            _rollbackProgressTotal = 0;
            _rollbackProgressStart = 0;
        }
        _transitionLog = _instrumentationLog.BeginOperation("Removal");
        _instrumentationTransition = rollingBack ? InstrumentationTransition.RollingBack : InstrumentationTransition.Removing;
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
            FinishGeneralRemoval();
    }

    private void FinishGeneralRemoval()
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
            _status = "Profiling paused. Removal checks passed; method statistics and call contexts are frozen.";

        MarkSelectionRowsDirty();
        MarkViewDirty();
        if (restart)
            BeginStartProfiling();
    }

    private void FreezeGeneralDataNow()
    {
        _frozenFrame = Time.frameCount;
        _frozenRealtime = Time.realtimeSinceStartup;
        SetCurrentRealtime(_frozenRealtime);
        FlushAnalyticsAt(_frozenRealtime);
        _statsFrozen = true;
    }

    private void AbortTransitionForSynchronousCleanup()
    {
        if (!IsTransitioning)
            return;
        _profilingActive = false;
        DiscardActiveStack();
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
        _installPlan = Array.Empty<GeneralMethodEntry>();
        _installCatalog = null;
        _installIndex = 0;
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
                return _installPlan.Length <= 0 ? 0f : Mathf.Clamp01((float)_installIndex / _installPlan.Length);
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
            int total = _instrumentationTransition == InstrumentationTransition.Installing ? _installPlan.Length : _removalPlan.Length;
            return $"{phase}: {done}/{total} ({TransitionProgress * 100f:0.0}%){method}";
        }
    }
}
