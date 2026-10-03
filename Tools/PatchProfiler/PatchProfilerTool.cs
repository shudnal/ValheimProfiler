#nullable disable

using BepInEx.Bootstrap;
using BepInEx.Logging;
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

internal sealed partial class PatchProfilerTool : IProfilerTool
{
    internal const string ToolId = "PatchProfiler";
    internal const string PluginGuid = ValheimProfilerPlugin.PluginGuid + ".PatchProfiler";
    internal const string PluginName = "Patch Profiler";
    internal const string PluginVersion = ValheimProfilerPlugin.PluginVersion;

    private const string TranspiledTargetsGuid = PluginGuid + ".TranspiledTargets";
    private const string TranspiledTargetsName = "Transpiled methods";

    private const float MinModColumnWidth = 140f;
    private const float MaxModColumnWidth = 460f;
    private const float TimeColumnWidth = 82f;
    private const float CountColumnWidth = 82f;
    private const float AvgTimeColumnWidth = 104f;
    private const float AvgCountColumnWidth = 112f;
    private const float AvgCallColumnWidth = 96f;
    private const float MethodColumnMinWidth = 260f;
    private const float TargetColumnMinWidth = 180f;
    private const float TargetColumnMaxWidth = 900f;
    private const float PatchMethodColumnMaxWidth = 1200f;
    private const float CombinedMethodColumnMaxWidth = 1800f;
    private const float WindowHorizontalPadding = 28f;
    private const float GroupToggleWidth = 14f;
    private const int MaxGroupedModNameCharsOnMax60 = 50;

    private const float RowHeightDefault = 20f;
    private const float RowHeightMax60 = 20f;
    private const float GroupRowHeight = 22f;
    private const float TotalRowHeight = 22f;
    private const float VirtualizationOverscan = 80f;

    private const int MaxDisplayedCount = 9999999;
    private const double MaxDisplayedMs = 999.999;
    private const double IsolatedRawMaxMinMs = 8.0;
    private const double IsolatedRawMaxMultiplier = 4.0;
    private const double GcCheckMinSampleMs = 1.0;


    private readonly ValheimProfilerApp _app;
    private readonly ManualLogSource _logger;
    private readonly InstrumentationLog _instrumentationLog;
    private readonly WindowManager _windows;
    private readonly ThemeManager _theme;
    private readonly Harmony _harmony;
    private readonly InstrumentationSession _instrumentation;
    private readonly ProfilerWindow _mainWindow;
    private readonly ProfilerWindow _detailsWindow;
    private readonly ModSelectionPolicy _modSelectionPolicy;
    private readonly InstrumentationReportView _safetyReport = new InstrumentationReportView();

    private Vector2 _scroll;
    private Vector2 _modsScroll;
    private Vector2 _transpilerDetailsScroll;
    private PatchContext _selectedTranspilerDetailsContext;
    private string _selectedTranspilerDetailsTitle = string.Empty;

    private GUIStyle _labelStyle;
    private GUIStyle _headerLabelStyle;
    private GUIStyle _activeHeaderLabelStyle;
    private GUIStyle _groupLabelStyle;
    private GUIStyle _compactButtonStyle;
    private GUISkin _styleSkin;

    private string _status = "Closed";
    private readonly object _lock = new object();
    private bool _listReady;
    private bool _modsSelectionDirty;
    private volatile bool _profilingActive;

    private enum InstrumentationTransition
    {
        None = 0,
        Installing = 1,
        Removing = 2,
        RollingBack = 3
    }

    private InstrumentationTransition _instrumentationTransition;
    private List<MethodBase> _installPlan = new List<MethodBase>();
    private InstrumentationCatalog _installCatalog;
    private int _installIndex;
    private int _installSelectedCount;
    private int _installNotAttempted;
    private MethodBase _installCurrentMethod;
    private HarmonyMethod _installCurrentPrefix;
    private HarmonyMethod _installCurrentFinalizer;
    private HarmonyMethod _installCurrentCallSiteTranspiler;
    private bool _installTimingInstalled;
    private int _installInstrumentationFailures;
    private int _installCallSiteFailures;
    private int _installSafetySkipped;
    private int _installSkippedByLimit;
    private bool _installCancelRequested;
    private InstrumentationLog.Operation _transitionLog;
    private MethodBase[] _removalPlan = Array.Empty<MethodBase>();
    private int _removalIndex;
    private int _removalAttempted;
    private int _removalRemoved;
    private bool _restartAfterRemoval;
    private string _transitionCurrentMethod = string.Empty;
    private int _rollbackProgressStart;
    private int _rollbackProgressTotal;

    private bool IsTransitioning => _instrumentationTransition != InstrumentationTransition.None;

    private bool _groupByMod = true;
    private volatile bool _ignoreGcAllocations;
    private volatile bool _excludeMeasuredNestedCalls;
    private bool _gradualProfilingStart;
    private string _targetFpsText;
    private bool _crashTraceEnabled;
    private int _diagnosticInstrumentationLimit;

    private int _currentFrame;
    private int _currentRealtimeMs;
    private int _frozenFrame;
    private float _frozenRealtime;
    private volatile bool _statsFrozen;

    private int _lastObservedGc0;
    private int _lastObservedGc1;
    private int _lastObservedGc2;

    private float _drawModColumnWidth;
    private float _drawPatchTypeColumnWidth;
    private float _drawTargetColumnWidth;
    private float _drawPatchMethodColumnWidth;
    private float _drawMethodColumnWidth;
    private float _drawContentWidth;
    private float _drawTimeColumnWidth;
    private float _drawCountColumnWidth;
    private float _drawAvgTimeColumnWidth;
    private float _drawAvgCountColumnWidth;
    private float _drawAvgCallColumnWidth;
    private bool _layoutMetricsDirty = true;
    private float _lastLayoutWindowWidth = -1f;
    private ProfilerView _lastLayoutProfilerView = (ProfilerView)(-1);
    private bool _lastLayoutGroupByMod;
    private GUISkin _lastLayoutSkin;
    private int _lastLayoutRowsSignature = int.MinValue;

    private Dictionary<Assembly, string> _assemblyToPluginName = new Dictionary<Assembly, string>();
    private Dictionary<Assembly, string> _assemblyToPluginGuid = new Dictionary<Assembly, string>();
    private Dictionary<string, string> _pluginGuidToName = new Dictionary<string, string>();

    private int _nextProfileEntryId;
    private readonly Dictionary<int, PatchStat> _stats = new Dictionary<int, PatchStat>(256);
    private readonly Dictionary<int, PatchContext> _context = new Dictionary<int, PatchContext>(256);
    private readonly Dictionary<string, int> _profileEntryIds = new Dictionary<string, int>(256);
    private readonly Dictionary<MethodBase, List<int>> _entriesByInstrumentedMethod = new Dictionary<MethodBase, List<int>>(256);
    private Dictionary<MethodBase, RuntimeProfileEntry[]> _runtimeEntriesByInstrumentedMethod = new Dictionary<MethodBase, RuntimeProfileEntry[]>(0);
    private HashSet<MethodBase> _runtimeTranspiledTargets = new HashSet<MethodBase>();
    private readonly HashSet<MethodBase> _transpiledTargets = new HashSet<MethodBase>();

    private readonly Dictionary<string, bool> _modExpanded = new Dictionary<string, bool>();
    private readonly Dictionary<string, bool> _modsToProfile = new Dictionary<string, bool>();
    private readonly Dictionary<string, int> _modPatchCounts = new Dictionary<string, int>();
    private readonly Dictionary<string, string> _modGuidToName = new Dictionary<string, string>();

    private ProfilerView _lastRenderedProfilerView = (ProfilerView)(-1);
    private bool _lastRenderedGroupByMod;
    private float _nextViewRefreshTime;
    private bool _viewDirty = true;
    private List<FlatRowView> _cachedFlatRows = new List<FlatRowView>();
    private List<ModGroupView> _cachedGroupedRows = new List<ModGroupView>();
    private GroupSummary _cachedTotalSummary;

    private readonly object _analyticsQueueLock = new object();
    private readonly object _analyticsProcessingLock = new object();
    private readonly Queue<PatchStat> _dirtyAnalyticsQueue = new Queue<PatchStat>();
    private readonly HashSet<PatchStat> _queuedAnalyticsStats = new HashSet<PatchStat>();
    private readonly HashSet<PatchStat> _activeAnalyticsStats = new HashSet<PatchStat>();
    private AutoResetEvent _analyticsWakeEvent;
    private Thread _analyticsThread;
    private volatile bool _analyticsThreadRunning;
    private float _lastAnalyticsSweepRealtime;

    private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
    private static int _mainThreadId;
    private static PatchProfilerTool _instance;

    [ThreadStatic]
    private static List<MethodBase> _activeTranspiledTargetStack;

    private enum MainTab
    {
        Profiler = 0,
        ModsToProfile = 1,
        Help = 2,
        Safety = 3
    }

    private enum ProfilerView
    {
        Avg1s = 0,
        MaxOver60Sec = 1
    }

    private enum TableSortColumn
    {
        Mod = 0,
        AvgMsPerFrame = 1,
        AvgCallsPerFrame = 2,
        AvgCall = 3,
        RawMax = 4,
        SecondMax = 5,
        ThirdMax = 6,
        AboveP99 = 7,
        AvgAboveP99 = 8,
        P99 = 9,
        AboveP95 = 10,
        AvgAboveP95 = 11,
        P95 = 12,
        Samples = 13,
        GcSamples = 14,
        PatchType = 15,
        Target = 16,
        PatchMethod = 17,
        CombinedMethod = 18
    }

    private MainTab _mainTab = MainTab.Profiler;
    private ProfilerView _profilerView = ProfilerView.MaxOver60Sec;
    private TableSortColumn _avgSortColumn = TableSortColumn.AvgMsPerFrame;
    private TableSortColumn _maxSortColumn = TableSortColumn.ThirdMax;

    internal PatchProfilerTool(ValheimProfilerApp app)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _logger = app.Logger;
        _instrumentationLog = new InstrumentationLog(_logger, app.Config, PluginName);
        _windows = app.Windows;
        _theme = app.Theme;
        _harmony = new Harmony(PluginGuid);
        _instrumentation = new InstrumentationSession(_harmony, app.InstrumentationSafety, _safetyReport.Report, _instrumentationLog, WriteCrashTrace);
        _modSelectionPolicy = new ModSelectionPolicy(
            ProfilerPaths.GetConfigFilePath("PatchProfilerSelection.cfg"));

        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        _status = "Loaded. Press F8 or use the launcher to open.";

        var config = app.Config;
        _avgSortColumn = ParseSortColumn(
            config.PatchProfilerAvgSortColumn.Value,
            ProfilerView.Avg1s,
            TableSortColumn.AvgMsPerFrame);
        _maxSortColumn = ParseSortColumn(
            config.PatchProfilerMaxSortColumn.Value,
            ProfilerView.MaxOver60Sec,
            TableSortColumn.ThirdMax);
        _ignoreGcAllocations = config.PatchProfilerIgnoreGcAllocations.Value;
        _excludeMeasuredNestedCalls = config.PatchProfilerExcludeMeasuredNestedCalls.Value;
        _gradualProfilingStart = config.PatchProfilerGradualProfilingStart.Value;
        _targetFpsText = Math.Max(1, config.FrameBudgetTargetFps.Value).ToString();
        ReadCrashDiagnosticOptions();

        var patchMinimumSize = new Vector2(720f, 360f);
        var detailsMinimumSize = new Vector2(520f, 280f);
        Vector2 patchDefaultSize = _windows.GetDefaultToolWindowSize(600f, patchMinimumSize);
        Vector2 detailsDefaultSize = _windows.GetDefaultCompactWindowSize(520f, detailsMinimumSize);

        _mainWindow = _windows.Register(new ProfilerWindow(
            "ValheimProfiler.PatchProfiler",
            PluginName,
            new Rect(ValheimProfilerConfig.DefaultPatchWindowPosition, patchDefaultSize),
            patchMinimumSize,
            resizable: true,
            requestedVisible: false,
            drawContents: DrawWindow,
            positionConfig: config.PatchWindowPosition,
            sizeConfig: config.PatchWindowSize));

        _detailsWindow = _windows.Register(new ProfilerWindow(
            "ValheimProfiler.PatchProfiler.TranspilerDetails",
            "Transpiled method details",
            new Rect(ValheimProfilerConfig.DefaultTranspilerWindowPosition, detailsDefaultSize),
            detailsMinimumSize,
            resizable: true,
            requestedVisible: false,
            drawContents: DrawTranspilerDetailsWindow,
            positionConfig: config.TranspilerWindowPosition,
            sizeConfig: config.TranspilerWindowSize));

        SetCurrentRealtime(Time.realtimeSinceStartup);
        UpdateGcBaseline();

        _analyticsWakeEvent = new AutoResetEvent(false);
        StartAnalyticsThread();
        _instance = this;
    }

    internal bool IsWindowVisible
    {
        get => _mainWindow.RequestedVisible;
        set
        {
            _mainWindow.RequestedVisible = value;
            if (!value)
                _detailsWindow.RequestedVisible = false;
        }
    }

    internal bool IsProfilingActive => _profilingActive;

    string IProfilerTool.Id => ToolId;
    string IProfilerTool.DisplayName => PluginName;
    bool IProfilerTool.IsWindowVisible => IsWindowVisible;
    bool IProfilerTool.IsActive => IsProfilingActive;
    void IProfilerTool.ShowWindow() => ShowWindow();
    void IProfilerTool.ToggleWindow() => ToggleWindow();
    void IProfilerTool.Update() => Update();
    void IProfilerTool.Shutdown() => Shutdown();

    private float CurrentRealtime => Volatile.Read(ref _currentRealtimeMs) * 0.001f;
    private int FrameBudgetTargetFps => Math.Max(1, _app.Config.FrameBudgetTargetFps.Value);
    private int DisplayFrame => _statsFrozen ? _frozenFrame : _currentFrame;
    private float DisplayRealtime => _statsFrozen ? _frozenRealtime : CurrentRealtime;
    private float CurrentRowHeight => Mathf.Max(
        _profilerView == ProfilerView.MaxOver60Sec ? RowHeightMax60 : RowHeightDefault,
        (_labelStyle?.lineHeight ?? RowHeightDefault) + 4f);
    private float CurrentHeaderHeight => Mathf.Max(RowHeightDefault, (_headerLabelStyle?.lineHeight ?? RowHeightDefault) + 4f);
    private float CurrentGroupRowHeight => Mathf.Max(GroupRowHeight, (_groupLabelStyle?.lineHeight ?? GroupRowHeight) + 4f);
    private float CurrentTotalRowHeight => Mathf.Max(TotalRowHeight, (_groupLabelStyle?.lineHeight ?? TotalRowHeight) + 4f);
    private float MainWindowWidth => _mainWindow.Rect.width;
    private float MainWindowHeight => _mainWindow.Rect.height;

    internal void ToggleWindow()
    {
        IsWindowVisible = !IsWindowVisible;
        if (IsWindowVisible)
            ShowWindow();
    }

    internal void ShowWindow()
    {
        IsWindowVisible = true;
        _app.ShowUi();
        _windows.BringToFront(_mainWindow);
    }

    internal void Update()
    {
        if (IsTransitioning)
            ProcessInstrumentationTransitionSafely();
        else
            ProcessCallSiteRebuildFailures();
        if (_profilingActive && _app.InstrumentationSafety.RestartRequired && !IsTransitioning)
            BeginStopProfiling(freezeData: true, restartAfterRemoval: false);
        _currentFrame = Time.frameCount;
        SetCurrentRealtime(Time.realtimeSinceStartup);
        UpdateGcBaseline();
    }

    internal void Shutdown()
    {
        if (_instance == this)
            _instance = null;

        try
        {
            AbortTransitionForSynchronousCleanup();
            StopProfilingInternal(false);
        }
        catch (Exception ex)
        {
            _safetyReport.Failure(null, "Shutdown cleanup interrupted", ex, restartRequired: true);
            _instrumentation.RequireRestart(null, "Shutdown cleanup did not complete.");
            _instrumentationLog.Failure("Shutdown cleanup interrupted", ex, warning: true);
        }

        StopAnalyticsThread();
        EndCrashTraceRun();
        IsWindowVisible = false;
    }

    private void SetCurrentRealtime(float realtime) =>
        Interlocked.Exchange(ref _currentRealtimeMs, Mathf.RoundToInt(realtime * 1000f));

    private void UpdateGcBaseline()
    {
        _lastObservedGc0 = GC.CollectionCount(0);
        _lastObservedGc1 = GC.CollectionCount(1);
        _lastObservedGc2 = GC.CollectionCount(2);
    }
}