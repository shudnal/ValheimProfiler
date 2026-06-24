#nullable disable

using BepInEx.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace ValheimProfiler.Tools.LogMonitor;

internal sealed partial class LogMonitorTool : IProfilerTool
{
    internal const string ToolId = "LogMonitor";
    internal const string DisplayTitle = "Client Log Monitor";

    private const float VirtualizationOverscan = 60f;

    private readonly ValheimProfilerApp _app;
    private readonly WindowManager _windows;
    private readonly ThemeManager _theme;
    private readonly ProfilerWindow _mainWindow;
    private readonly LogMonitorListener _listener;

    private readonly ConcurrentQueue<PendingLogEvent> _pending = new();
    private readonly List<LogEntry> _entries = new();
    private readonly List<LogEntry> _filteredStream = new();
    private readonly Dictionary<string, IssueGroup> _issuesByKey = new(StringComparer.Ordinal);
    private readonly List<IssueGroup> _issues = new();
    private readonly List<IssueGroup> _filteredIssues = new();
    private readonly object _historyResultSync = new();

    private GUIStyle _labelStyle;
    private GUIStyle _headerLabelStyle;
    private GUIStyle _activeHeaderLabelStyle;
    private GUIStyle _detailsStyle;
    private GUISkin _styleSkin;

    private Vector2 _streamScroll;
    private Vector2 _issuesScroll;
    private Vector2 _streamDetailsScroll;
    private Vector2 _issueDetailsScroll;
    private Vector2 _helpScroll;

    private MainTab _mainTab;
    private LogLevel _streamLevelFilter = LogLevel.All;
    private IssueSortColumn _issueSortColumn = IssueSortColumn.Count;
    private string _streamSearch = string.Empty;
    private string _issuesSearch = string.Empty;
    private bool _includeWarningsInIssues;
    private bool _followStream = true;
    private bool _scrollStreamToEnd;
    private bool _streamViewDirty = true;
    private bool _issuesViewDirty = true;
    private volatile bool _captureEnabled = true;

    private readonly HashSet<LogEntry> _selectedEntries = new();
    private LogEntry _selectedEntry;
    private LogEntry _selectionAnchorEntry;
    private IssueGroup _selectedIssue;

    private int _pendingCount;
    private long _nextSequence;
    private long _nextHistoricalSequence;
    private long _capturedEntries;
    private long _droppedEntries;

    private bool _automaticBackfillScheduled;
    private bool _automaticBackfillAttempted;
    private float _automaticBackfillDueRealtime;
    private int _historyLoadInProgress;
    private HistoryLoadResult _pendingHistoryResult;
    private long _historyCursor = -1L;
    private long _historyFileCreationUtcTicks;
    private bool _historyHasMore = true;
    private int _loadedHistoryEntries;
    private string _historyStatus = string.Empty;

    internal LogMonitorTool(ValheimProfilerApp app)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _windows = app.Windows;
        _theme = app.Theme;

        ValheimProfilerConfig config = app.Config;
        _issueSortColumn = ParseIssueSortColumn(config.LogMonitorIssueSortColumn.Value);

        var minimumSize = new Vector2(760f, 460f);
        Vector2 defaultSize = _windows.GetDefaultToolWindowSize(680f, minimumSize);

        _mainWindow = _windows.Register(new ProfilerWindow(
            "ValheimProfiler.LogMonitor",
            DisplayTitle,
            new Rect(ValheimProfilerConfig.DefaultLogMonitorWindowPosition, defaultSize),
            minimumSize,
            resizable: true,
            requestedVisible: false,
            drawContents: DrawWindow,
            positionConfig: config.LogMonitorWindowPosition,
            sizeConfig: config.LogMonitorWindowSize));

        _listener = new LogMonitorListener(this);
        BepInEx.Logging.Logger.Listeners.Add(_listener);
    }

    string IProfilerTool.Id => ToolId;
    string IProfilerTool.DisplayName => "Client Log";
    bool IProfilerTool.IsWindowVisible => IsWindowVisible;
    bool IProfilerTool.IsActive => _captureEnabled;
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
    private float RowHeight => Mathf.Max(20f, (_labelStyle?.lineHeight ?? 16f) + 4f);
    private float HeaderHeight => Mathf.Max(20f, (_headerLabelStyle?.lineHeight ?? 16f) + 4f);

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
        DrainPending();
        UpdateHistoryLoading();
    }

    internal void Shutdown()
    {
        _captureEnabled = false;

        try
        {
            BepInEx.Logging.Logger.Listeners.Remove(_listener);
            _listener.Dispose();
        }
        catch
        {
        }

        IsWindowVisible = false;
        ClearCapturedData();
    }

    private void ToggleCapture()
    {
        _captureEnabled = !_captureEnabled;
        if (_captureEnabled && _followStream)
            _scrollStreamToEnd = true;
    }

    private long DroppedEntries => Interlocked.Read(ref _droppedEntries);

    private static IssueSortColumn ParseIssueSortColumn(string value)
    {
        return Enum.TryParse(value, ignoreCase: true, out IssueSortColumn parsed) &&
               Enum.IsDefined(typeof(IssueSortColumn), parsed)
            ? parsed
            : IssueSortColumn.Count;
    }
}
