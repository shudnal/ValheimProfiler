#nullable disable

using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ValheimProfiler.Tools.MonoBehaviourProfiler;

internal sealed partial class MonoBehaviourProfilerTool : IProfilerTool
{
    internal const string ToolId = "MonoBehaviourFrameProfiler";
    internal const string HarmonyId = ValheimProfilerPlugin.PluginGuid + ".MonoBehaviourFrameProfiler";
    internal const string DisplayTitle = "MonoBehaviour Frame Profiler";

    private const float GcCheckMinSampleMs = 1.0f;
    private const float VirtualizationOverscan = 80f;
    private const float GroupToggleWidth = 14f;
    private const int MaxDisplayedCount = 9999999;
    private const double MaxDisplayedMs = 999.999;
    private const double IsolatedRawMaxMinMs = 8.0;
    private const double IsolatedRawMaxMultiplier = 4.0;

    private const float MinSourceColumnWidth = 150f;
    private const float MaxSourceColumnWidth = 900f;
    private const float TimeColumnWidth = 82f;
    private const float CountColumnWidth = 82f;
    private const float AvgTimeColumnWidth = 104f;
    private const float AvgCountColumnWidth = 112f;
    private const float TypeColumnMinWidth = 260f;
    private const float WindowHorizontalPadding = 18f;

    private readonly ValheimProfilerApp _app;
    private readonly ManualLogSource _logger;
    private readonly WindowManager _windows;
    private readonly ThemeManager _theme;
    private readonly Harmony _harmony;
    private readonly ProfilerWindow _mainWindow;
    private readonly SelectionPolicy _selectionPolicy;

    private readonly object _lock = new object();
    private readonly List<BehaviourTypeEntry> _types = new List<BehaviourTypeEntry>();
    private readonly Dictionary<string, BehaviourMethodEntry> _methodsByKey = new Dictionary<string, BehaviourMethodEntry>(StringComparer.Ordinal);
    private readonly HashSet<MethodBase> _instrumentedMethods = new HashSet<MethodBase>();
    private Dictionary<MethodBase, RuntimeBehaviourEntry[]> _runtimeEntries = new Dictionary<MethodBase, RuntimeBehaviourEntry[]>();

    private readonly Dictionary<string, bool> _groupExpanded = new Dictionary<string, bool>(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _selectionGroupExpanded = new Dictionary<string, bool>(StringComparer.Ordinal);
    private readonly HashSet<Type> _typesPresentInActiveScene = new HashSet<Type>();
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
    private string _lastSelectionSearch = string.Empty;

    private BehaviourSource _selectionSource = BehaviourSource.Mod;
    private bool _presentInActiveScene;
    private int _activeSceneHandle = int.MinValue;
    private bool _selectionRowsDirty = true;
    private bool _selectionDirty;
    private bool _selectionExpansionInitialized;
    private bool _listReady;
    private volatile bool _profilingActive;
    private bool _groupByMod = true;
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

    private float _drawSourceColumnWidth;
    private float _drawTimeColumnWidth;
    private float _drawCountColumnWidth;
    private float _drawAvgTimeColumnWidth;
    private float _drawAvgCountColumnWidth;
    private float _drawTypeColumnWidth;
    private float _drawContentWidth;
    private bool _layoutMetricsDirty = true;
    private float _lastLayoutWindowWidth = -1f;
    private ProfilerView _lastLayoutProfilerView = (ProfilerView)(-1);
    private bool _lastLayoutGroupByMod;
    private GUISkin _lastLayoutSkin;

    private string _status = "Not loaded.";
    private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
    private static int _mainThreadId;
    private static MonoBehaviourProfilerTool _instance;

    internal MonoBehaviourProfilerTool(ValheimProfilerApp app)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _logger = app.Logger;
        _windows = app.Windows;
        _theme = app.Theme;
        _harmony = new Harmony(HarmonyId);
        _selectionPolicy = new SelectionPolicy(ProfilerPaths.GetConfigFilePath("MonoBehaviourFrameSelection.cfg"));

        _mainThreadId = Thread.CurrentThread.ManagedThreadId;

        ValheimProfilerConfig config = app.Config;
        _avgSortColumn = ParseSortColumn(
            config.MonoBehaviourProfilerAvgSortColumn.Value,
            ProfilerView.Avg1s,
            TableSortColumn.AvgMsPerFrame);
        _maxSortColumn = ParseSortColumn(
            config.MonoBehaviourProfilerMaxSortColumn.Value,
            ProfilerView.MaxOver60Sec,
            TableSortColumn.ThirdMax);

        var minimumSize = new Vector2(760f, 420f);
        Vector2 defaultSize = _windows.GetDefaultToolWindowSize(650f, minimumSize);
        _mainWindow = _windows.Register(new ProfilerWindow(
            "ValheimProfiler.MonoBehaviourFrameProfiler",
            DisplayTitle,
            new Rect(ValheimProfilerConfig.DefaultMonoBehaviourWindowPosition, defaultSize),
            minimumSize,
            resizable: true,
            requestedVisible: false,
            drawContents: DrawWindow,
            positionConfig: config.MonoBehaviourWindowPosition,
            sizeConfig: config.MonoBehaviourWindowSize));

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
        set => _mainWindow.RequestedVisible = value;
    }

    private float CurrentRealtime => Volatile.Read(ref _currentRealtimeMs) * 0.001f;
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
            RefreshBehaviourList();
    }

    internal void Update()
    {
        _currentFrame = Time.frameCount;
        SetCurrentRealtime(Time.realtimeSinceStartup);
        UpdateGcBaseline();

        if (_presentInActiveScene)
        {
            int sceneHandle = SceneManager.GetActiveScene().handle;
            if (sceneHandle != _activeSceneHandle)
            {
                RefreshActiveScenePresence();
                MarkSelectionRowsDirty();
            }
        }
    }

    internal void Shutdown()
    {
        if (_instance == this)
            _instance = null;

        try
        {
            StopProfilingInternal(false);
        }
        catch
        {
        }

        StopAnalyticsThread();
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
    }

    private void MarkSelectionRowsDirty() => _selectionRowsDirty = true;
}