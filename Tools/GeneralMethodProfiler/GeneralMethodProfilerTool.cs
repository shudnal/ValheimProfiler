#nullable disable

using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace ValheimProfiler.Tools.GeneralMethodProfiler;

internal sealed partial class GeneralMethodProfilerTool : IProfilerTool
{
    internal const string ToolId = "GeneralMethodProfiler";
    internal const string HarmonyId = ValheimProfilerPlugin.PluginGuid + ".GeneralMethodProfiler";
    internal const string DisplayTitle = "General Method Profiler";

    private const float GcCheckMinSampleMs = 1.0f;
    private const float VirtualizationOverscan = 80f;
    private const float GroupToggleWidth = 14f;
    private const int MaxDisplayedCount = 9999999;
    private const double MaxDisplayedMs = 999.999;
    private const double IsolatedRawMaxMinMs = 8.0;
    private const double IsolatedRawMaxMultiplier = 4.0;

    private const float MinModColumnWidth = 140f;
    private const float MaxModColumnWidth = 460f;
    private const int MaxGroupedModNameCharsOnMax60 = 50;
    private const float TimeColumnWidth = 82f;
    private const float CountColumnWidth = 82f;
    private const float AvgTimeColumnWidth = 104f;
    private const float AvgCountColumnWidth = 112f;
    private const float AvgCallColumnWidth = 96f;
    private const float MethodColumnMinWidth = 260f;
    private const float MethodColumnMaxWidth = 1800f;
    private const float WindowHorizontalPadding = 28f;

    private readonly ValheimProfilerApp _app;
    private readonly ManualLogSource _logger;
    private readonly InstrumentationLog _instrumentationLog;
    private readonly WindowManager _windows;
    private readonly ThemeManager _theme;
    private readonly Harmony _harmony;
    private readonly InstrumentationSession _instrumentation;
    private readonly ProfilerWindow _mainWindow;
    private readonly ProfilerWindow _callTreeWindow;
    private readonly InstrumentationReportView _safetyReport = new InstrumentationReportView();
    private readonly SelectionPolicy _selectionPolicy;

    private readonly object _lock = new object();
    private readonly List<MethodTypeEntry> _types = new List<MethodTypeEntry>();
    private readonly Dictionary<string, GeneralMethodEntry> _methodsByKey = new Dictionary<string, GeneralMethodEntry>(StringComparer.Ordinal);
    private readonly Dictionary<MethodBase, GeneralMethodEntry> _runtimeEntries = new Dictionary<MethodBase, GeneralMethodEntry>();
    private readonly List<GeneralMethodEntry> _measuredEntries = new List<GeneralMethodEntry>();
    private readonly HashSet<GeneralMethodEntry> _layoutSampledEntries = new HashSet<GeneralMethodEntry>();

    private readonly Dictionary<string, bool> _groupExpanded = new Dictionary<string, bool>(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _selectionGroupExpanded = new Dictionary<string, bool>(StringComparer.Ordinal);
    private List<FlatRowView> _cachedFlatRows = new List<FlatRowView>();
    private List<GroupRowView> _cachedGroupedRows = new List<GroupRowView>();
    private readonly List<SelectionRow> _selectionRows = new List<SelectionRow>();

    private readonly object _analyticsQueueLock = new object();
    private readonly object _analyticsProcessingLock = new object();
    private readonly Queue<RollingProfilerStat> _dirtyAnalyticsQueue = new Queue<RollingProfilerStat>();
    private readonly HashSet<RollingProfilerStat> _queuedAnalyticsStats = new HashSet<RollingProfilerStat>();
    private readonly HashSet<RollingProfilerStat> _activeAnalyticsStats = new HashSet<RollingProfilerStat>();
    private AutoResetEvent _analyticsWakeEvent;
    private Thread _analyticsThread;
    private volatile bool _analyticsThreadRunning;
    private float _lastAnalyticsSweepRealtime;

    private GUIStyle _labelStyle;
    private GUIStyle _headerLabelStyle;
    private GUIStyle _activeHeaderLabelStyle;
    private GUIStyle _groupLabelStyle;
    private GUIStyle _compactButtonStyle;
    private GUISkin _styleSkin;

    private Vector2 _profilerScroll;
    private Vector2 _selectionScroll;
    private Vector2 _helpScroll;
    private string _search = string.Empty;

    private bool _selectedOnly;
    private bool _showUnavailable;
    private string _profilerSearch = string.Empty;
    private bool _selectionRowsDirty = true;
    private bool _selectionDirty;
    private bool _listReady;
    private volatile bool _profilingActive;

    private enum InstrumentationTransition
    {
        None = 0,
        Installing = 1,
        Removing = 2,
        RollingBack = 3
    }

    private InstrumentationTransition _instrumentationTransition;
    private GeneralMethodEntry[] _installPlan = Array.Empty<GeneralMethodEntry>();
    private InstrumentationCatalog _installCatalog;
    private int _installIndex;
    private int _installInstrumentationFailures;
    private int _installSafetySkipped;
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
    private bool _viewDirty = true;
    private bool _statsFrozen;

    private int _currentFrame;
    private int _currentRealtimeMs;
    private int _frozenFrame;
    private float _frozenRealtime;
    private int _lastObservedGc0;
    private int _lastObservedGc1;
    private int _lastObservedGc2;

    private MainTab _mainTab = MainTab.Profiler;
    private ProfilerView _profilerView = ProfilerView.MaxOver60Sec;
    private TableSortColumn _avgSortColumn = TableSortColumn.AvgMsPerFrame;
    private TableSortColumn _maxSortColumn = TableSortColumn.ThirdMax;
    private ProfilerView _lastRenderedProfilerView = (ProfilerView)(-1);
    private bool _lastRenderedGroupByMod;
    private float _nextViewRefreshTime;

    private float _drawModColumnWidth;
    private float _drawTimeColumnWidth;
    private float _drawCountColumnWidth;
    private float _drawAvgTimeColumnWidth;
    private float _drawAvgCountColumnWidth;
    private float _drawAvgCallColumnWidth;
    private float _drawClassColumnWidth;
    private float _drawMethodColumnWidth;
    private float _drawCallTreeButtonWidth;
    private float _drawContentWidth;
    private bool _layoutMetricsDirty = true;
    private float _lastLayoutWindowWidth = -1f;
    private ProfilerView _lastLayoutProfilerView = (ProfilerView)(-1);
    private bool _lastLayoutGroupByMod;
    private GUISkin _lastLayoutSkin;

    private string _status = "Not loaded.";
    private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
    private static int _mainThreadId;
    private static GeneralMethodProfilerTool _instance;

    internal GeneralMethodProfilerTool(ValheimProfilerApp app)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _logger = app.Logger;
        _instrumentationLog = new InstrumentationLog(_logger, app.Config, DisplayTitle);
        _windows = app.Windows;
        _theme = app.Theme;
        _harmony = new Harmony(HarmonyId);
        _instrumentation = new InstrumentationSession(_harmony, app.InstrumentationSafety, _safetyReport.Report, _instrumentationLog);
        _selectionPolicy = new SelectionPolicy(ProfilerPaths.GetConfigFilePath("GeneralMethodSelection.cfg"));

        _mainThreadId = Thread.CurrentThread.ManagedThreadId;

        ValheimProfilerConfig config = app.Config;
        _avgSortColumn = ParseSortColumn(
            config.GeneralMethodProfilerAvgSortColumn.Value,
            ProfilerView.Avg1s,
            TableSortColumn.AvgMsPerFrame);
        _maxSortColumn = ParseSortColumn(
            config.GeneralMethodProfilerMaxSortColumn.Value,
            ProfilerView.MaxOver60Sec,
            TableSortColumn.ThirdMax);
        _ignoreGcAllocations = config.GeneralMethodProfilerIgnoreGcAllocations.Value;
        _excludeMeasuredNestedCalls = config.GeneralMethodProfilerExcludeMeasuredNestedCalls.Value;
        _gradualProfilingStart = config.GeneralMethodProfilerGradualProfilingStart.Value;
        _targetFpsText = Math.Max(1, config.FrameBudgetTargetFps.Value).ToString();
        _groupByMod = config.GeneralMethodProfilerGroupByMod.Value;
        _profilerView = config.GeneralMethodProfilerView.Value is "Avg1s" or "FrameBudget" ? ProfilerView.Avg1s : ProfilerView.MaxOver60Sec;
        _search = config.GeneralMethodSelectionSearch.Value;
        _profilerSearch = config.GeneralMethodProfilerSearch.Value;
        _selectedOnly = config.GeneralMethodSelectedOnly.Value;
        _showUnavailable = config.GeneralMethodShowUnavailable.Value;
        if (Enum.TryParse(config.GeneralMethodCallTreeSortColumn.Value, true, out TreeSortColumn treeSort) && Enum.IsDefined(typeof(TreeSortColumn), treeSort))
            _treeSortColumn = treeSort;
        _maxTreeNodes = config.GeneralMethodMaxTreeNodes.Value;
        _callStack = new InvocationFrame[config.GeneralMethodMaxCallDepth.Value];

        var minimumSize = new Vector2(760f, 420f);
        Vector2 defaultSize = _windows.GetDefaultToolWindowSize(650f, minimumSize);
        _mainWindow = _windows.Register(new ProfilerWindow(
            "ValheimProfiler.GeneralMethodProfiler",
            DisplayTitle,
            new Rect(ValheimProfilerConfig.DefaultGeneralMethodWindowPosition, defaultSize),
            minimumSize,
            resizable: true,
            requestedVisible: false,
            drawContents: DrawWindow,
            positionConfig: config.GeneralMethodWindowPosition,
            sizeConfig: config.GeneralMethodWindowSize));

        var treeMinimumSize = new Vector2(800f, 520f);
        _callTreeWindow = _windows.Register(new ProfilerWindow(
            "ValheimProfiler.GeneralMethodProfiler.CallTree",
            "General Method Profiler - Call tree",
            new Rect(ValheimProfilerConfig.DefaultGeneralCallTreeWindowPosition,
                _windows.GetDefaultToolWindowSize(650f, treeMinimumSize)),
            treeMinimumSize,
            resizable: true,
            requestedVisible: false,
            drawContents: DrawCallTreeWindow,
            positionConfig: config.GeneralCallTreeWindowPosition,
            sizeConfig: config.GeneralCallTreeWindowSize));

        SetCurrentRealtime(Time.realtimeSinceStartup);
        UpdateGcBaseline();

        _analyticsWakeEvent = new AutoResetEvent(false);
        StartAnalyticsThread();
        _instance = this;
    }

    string IProfilerTool.Id => ToolId;
    string IProfilerTool.DisplayName => DisplayTitle;
    bool IProfilerTool.IsWindowVisible => IsWindowVisible;
    bool IProfilerTool.IsActive => _profilingActive;
    void IProfilerTool.ShowWindow() => ShowWindow();
    void IProfilerTool.ToggleWindow() => ToggleWindow();
    void IProfilerTool.Update() => Update();
    void IProfilerTool.Shutdown() => Shutdown();

    internal bool IsWindowVisible
    {
        get => _mainWindow.RequestedVisible;
        set
        {
            _mainWindow.RequestedVisible = value;
            if (!value)
                _callTreeWindow.RequestedVisible = false;
        }
    }

    private float CurrentRealtime => Volatile.Read(ref _currentRealtimeMs) * 0.001f;
    private int FrameBudgetTargetFps => Math.Max(1, _app.Config.FrameBudgetTargetFps.Value);
    private int DisplayFrame => _statsFrozen ? _frozenFrame : _currentFrame;
    private float DisplayRealtime => _statsFrozen ? _frozenRealtime : CurrentRealtime;
    private float MainWindowWidth => _mainWindow.Rect.width;
    private float MainWindowHeight => _mainWindow.Rect.height;
    private float CurrentRowHeight => Mathf.Max(20f, (_labelStyle?.lineHeight ?? 16f) + 4f);
    private float CurrentHeaderHeight => Mathf.Max(20f, (_headerLabelStyle?.lineHeight ?? 16f) + 4f);
    private float CurrentGroupHeight => Mathf.Max(22f, (_groupLabelStyle?.lineHeight ?? 16f) + 4f);

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

        if (!_listReady)
            RefreshMethodList();
    }

    internal void Update()
    {
        if (IsTransitioning)
            ProcessInstrumentationTransitionSafely();
        if (_profilingActive && _app.InstrumentationSafety.RestartRequired && !IsTransitioning)
            BeginStopProfiling(freezeData: true, restartAfterRemoval: false);
        _currentFrame = Time.frameCount;
        SetCurrentRealtime(Time.realtimeSinceStartup);
        UpdateGcBaseline();
        if (_filtersDirty && Time.realtimeSinceStartup >= _saveFiltersAt)
            PersistFilters();
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
        PersistFilters();
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

    private void MarkViewDirty()
    {
        _viewDirty = true;
        _layoutMetricsDirty = true;
        _nextViewRefreshTime = 0f;
        _treeViewDirty = true;
    }

    private void MarkSelectionRowsDirty() => _selectionRowsDirty = true;
}