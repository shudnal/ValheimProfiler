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

namespace ValheimProfiler.Tools.MonoBehaviourCallProfiler;

internal sealed partial class MonoBehaviourCallProfilerTool : IProfilerTool
{
    internal const string ToolId = "MonoBehaviourCallProfiler";
    internal const string HarmonyId = ValheimProfilerPlugin.PluginGuid + ".MonoBehaviourCallProfiler";
    internal const string DisplayTitle = "MonoBehaviour Call Profiler";

    private const float VirtualizationOverscan = 80f;
    private const float GroupToggleWidth = 14f;
    private const int MaxDisplayedCount = 999999999;
    private const double MaxDisplayedMs = 999999.999;

    private const float MinSourceColumnWidth = 170f;
    private const float MaxSourceColumnWidth = 900f;
    private const float TimeColumnWidth = 86f;
    private const float CountColumnWidth = 78f;
    private const float TimelineColumnWidth = 82f;
    private const float TypeColumnMinWidth = 280f;
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
    private bool _showDeclaredMethods;
    private bool _presentInActiveScene;
    private int _activeSceneHandle = int.MinValue;
    private bool _selectionRowsDirty = true;
    private bool _selectionDirty;
    private bool _selectionExpansionInitialized;
    private bool _listReady;
    private volatile bool _profilingActive;
    private bool _groupByMod = true;
    private bool _viewDirty = true;

    private MainTab _mainTab = MainTab.Profiler;
    private TableSortColumn _sortColumn = TableSortColumn.Total;
    private float _nextViewRefreshTime;
    private float _sessionStartRealtime;

    private float _drawSourceColumnWidth;
    private float _drawTimeColumnWidth;
    private float _drawCountColumnWidth;
    private float _drawTimelineColumnWidth;
    private float _drawTypeColumnWidth;
    private float _drawContentWidth;
    private bool _layoutMetricsDirty = true;
    private float _lastLayoutWindowWidth = -1f;
    private bool _lastLayoutGroupByMod;
    private GUISkin _lastLayoutSkin;

    private string _status = "Not loaded.";
    private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
    private static int _mainThreadId;
    private static MonoBehaviourCallProfilerTool _instance;

    internal MonoBehaviourCallProfilerTool(ValheimProfilerApp app)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _logger = app.Logger;
        _windows = app.Windows;
        _theme = app.Theme;
        _harmony = new Harmony(HarmonyId);
        _selectionPolicy = new SelectionPolicy(ProfilerPaths.GetConfigFilePath("MonoBehaviourCallSelection.cfg"));

        _mainThreadId = Thread.CurrentThread.ManagedThreadId;

        ValheimProfilerConfig config = app.Config;
        _sortColumn = ParseSortColumn(config.MonoBehaviourCallProfilerSortColumn.Value, TableSortColumn.Total);

        var minimumSize = new Vector2(800f, 440f);
        Vector2 defaultSize = _windows.GetDefaultToolWindowSize(660f, minimumSize);
        _mainWindow = _windows.Register(new ProfilerWindow(
            "ValheimProfiler.MonoBehaviourCallProfiler",
            DisplayTitle,
            new Rect(ValheimProfilerConfig.DefaultMonoBehaviourCallWindowPosition, defaultSize),
            minimumSize,
            resizable: true,
            requestedVisible: false,
            drawContents: DrawWindow,
            positionConfig: config.MonoBehaviourCallWindowPosition,
            sizeConfig: config.MonoBehaviourCallWindowSize));

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
            StopProfilingInternal();
        }
        catch
        {
        }

        IsWindowVisible = false;
    }

    private void MarkViewDirty()
    {
        _viewDirty = true;
        _layoutMetricsDirty = true;
        _nextViewRefreshTime = 0f;
    }

    private void MarkSelectionRowsDirty() => _selectionRowsDirty = true;
}
