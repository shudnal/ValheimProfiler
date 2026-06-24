#nullable disable

using BepInEx.Logging;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ValheimProfiler.Tools.ServerLogMonitor;

internal sealed partial class ServerLogMonitorTool : IProfilerTool, IProfilerToolAvailability
{
    internal const string ToolId = "ServerLogMonitor";
    internal const string DisplayTitle = "Server Log Monitor";
    private const float VirtualizationOverscan = 60f;
    private const float ProbeTimeoutSeconds = 3f;
    private const float ProbeRetrySeconds = 10f;
    private const float RequestTimeoutSeconds = 6f;

    private enum AvailabilityState
    {
        NoDedicatedConnection,
        Detecting,
        ServerModUnavailable,
        AccessDenied,
        Available,
        ProtocolMismatch
    }

    private readonly ValheimProfilerApp _app;
    private readonly WindowManager _windows;
    private readonly ThemeManager _theme;
    private readonly ProfilerWindow _mainWindow;

    private readonly List<LogEntry> _entries = new();
    private readonly List<LogEntry> _filteredStream = new();
    private readonly Dictionary<string, IssueGroup> _issuesByKey = new(StringComparer.Ordinal);
    private readonly List<IssueGroup> _issues = new();
    private readonly List<IssueGroup> _filteredIssues = new();

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

    private readonly HashSet<LogEntry> _selectedEntries = new();
    private LogEntry _selectedEntry;
    private LogEntry _selectionAnchorEntry;
    private IssueGroup _selectedIssue;

    private AvailabilityState _availability = AvailabilityState.NoDedicatedConnection;
    private string _availabilityStatus = "Connect to a dedicated server to use Server Log Monitor.";
    private string _serverVersion = string.Empty;
    private string _sessionId = string.Empty;
    private bool _subscribed;
    private bool _subscriptionRequestPending;
    private bool _historyRequestPending;
    private bool _serverAuthorized;
    private bool _probePending;
    private float _probeDeadlineRealtime;
    private float _requestDeadlineRealtime;
    private float _nextProbeRealtime;
    private ZRoutedRpc _networkRpc;
    private long _lastLiveSequence;
    private long _serverDroppedCount;
    private long _historyCursor;
    private long _historyStartCursor;
    private long _historyFileCreationUtcTicks;
    private bool _historyHasMore;
    private int _loadedHistoryEntries;
    private int _detectedGaps;
    private string _historyStatus = string.Empty;
    private string _startupHistoryRequestedSession = string.Empty;

    internal ServerLogMonitorTool(ValheimProfilerApp app)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _windows = app.Windows;
        _theme = app.Theme;
        _issueSortColumn = ParseIssueSortColumn(app.Config.ServerLogIssueSortColumn.Value);

        var minimumSize = new Vector2(760f, 460f);
        Vector2 defaultSize = _windows.GetDefaultToolWindowSize(680f, minimumSize);
        _mainWindow = _windows.Register(new ProfilerWindow(
            "ValheimProfiler.ServerLogMonitor",
            DisplayTitle,
            new Rect(ValheimProfilerConfig.DefaultServerLogMonitorWindowPosition, defaultSize),
            minimumSize,
            resizable: true,
            requestedVisible: false,
            drawContents: DrawWindow,
            positionConfig: app.Config.ServerLogMonitorWindowPosition,
            sizeConfig: app.Config.ServerLogMonitorWindowSize));
    }

    string IProfilerTool.Id => ToolId;
    string IProfilerTool.DisplayName => "Server Log";
    bool IProfilerTool.IsWindowVisible => IsWindowVisible;
    bool IProfilerTool.IsActive => _subscribed;
    void IProfilerTool.ShowWindow() => ShowWindow();
    void IProfilerTool.ToggleWindow() => ToggleWindow();
    void IProfilerTool.Update() => Update();
    void IProfilerTool.Shutdown() => Shutdown();

    bool IProfilerToolAvailability.IsAvailable => IsAvailable;
    bool IProfilerToolAvailability.CanOpenWhenUnavailable => true;
    string IProfilerToolAvailability.AvailabilityTooltip => AvailabilityTooltip;

    internal bool IsWindowVisible
    {
        get => _mainWindow.RequestedVisible;
        set => _mainWindow.RequestedVisible = value;
    }

    internal bool IsAvailable => _availability == AvailabilityState.Available;

    internal string AvailabilityTooltip
    {
        get
        {
            string requirement = "Requires a compatible Valheim Profiler on the connected headless dedicated server and the current player in the server admin list.";
            return string.IsNullOrWhiteSpace(_availabilityStatus)
                ? requirement
                : _availabilityStatus + "\n" + requirement;
        }
    }

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

        if (!IsAvailable)
            _mainTab = MainTab.Help;
    }

    internal void Update()
    {
        UpdateAvailability();
        UpdateRequestTimeouts();
    }

    internal void Shutdown()
    {
        if (_subscribed && IsDedicatedServerConnectionDetected())
            SendRequest(ServerLogRequestKind.Unsubscribe);

        IsWindowVisible = false;
        _subscribed = false;
        ClearLocalView(resetSession: true);
    }

    internal void OnNetworkDestroyed()
    {
        _networkRpc = null;
        _probePending = false;
        _subscriptionRequestPending = false;
        _historyRequestPending = false;
        _serverAuthorized = false;
        _startupHistoryRequestedSession = string.Empty;
        _subscribed = false;
        _sessionId = string.Empty;
        _serverVersion = string.Empty;
        _availability = AvailabilityState.NoDedicatedConnection;
        _availabilityStatus = "Connect to a dedicated server to use Server Log Monitor.";
        _nextProbeRealtime = 0f;
        if (IsWindowVisible)
            _mainTab = MainTab.Help;
        ClearLocalView(resetSession: true);
    }

    internal bool IsDedicatedServerConnectionDetected()
    {
        try
        {
            ZNet znet = ZNet.instance;
            return SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null &&
                   znet != null &&
                   znet.IsServer() != true &&
                   znet.IsDedicated() != true &&
                   znet.IsCurrentServerDedicated() == true &&
                   ZRoutedRpc.instance != null;
        }
        catch
        {
            return false;
        }
    }


    private void UpdateAvailability()
    {
        if (!IsDedicatedServerConnectionDetected())
        {
            if (_availability != AvailabilityState.NoDedicatedConnection || _networkRpc != null)
                OnNetworkDestroyed();
            return;
        }

        ZRoutedRpc currentRpc = ZRoutedRpc.instance;
        if (!ReferenceEquals(_networkRpc, currentRpc))
        {
            _networkRpc = currentRpc;
            _subscribed = false;
            _sessionId = string.Empty;
            _availability = AvailabilityState.Detecting;
            _availabilityStatus = "Checking whether the dedicated server supports remote log monitoring...";
            _probePending = false;
            _nextProbeRealtime = Time.realtimeSinceStartup + 0.15f;
            if (IsWindowVisible)
                _mainTab = MainTab.Help;
            ClearLocalView(resetSession: true);
        }

        float now = Time.realtimeSinceStartup;
        if (_probePending && now >= _probeDeadlineRealtime)
        {
            _probePending = false;
            _availability = AvailabilityState.ServerModUnavailable;
            _availabilityStatus = "The dedicated server did not answer. Valheim Profiler is probably not installed there, is too old, or its RPC backend is unavailable.";
            _nextProbeRealtime = now + ProbeRetrySeconds;
            if (IsWindowVisible)
                _mainTab = MainTab.Help;
        }

        if (!_probePending && _availability != AvailabilityState.Available && now >= _nextProbeRealtime)
            ProbeServer();
    }

    private void UpdateRequestTimeouts()
    {
        float now = Time.realtimeSinceStartup;
        if (_subscriptionRequestPending && now >= _requestDeadlineRealtime)
        {
            _subscriptionRequestPending = false;
            _subscribed = false;
            _availability = AvailabilityState.ServerModUnavailable;
            _availabilityStatus = "The dedicated server stopped responding to Server Log Monitor requests.";
            _nextProbeRealtime = now + ProbeRetrySeconds;
            if (IsWindowVisible)
                _mainTab = MainTab.Help;
        }

        if (_historyRequestPending && now >= _requestDeadlineRealtime)
        {
            _historyRequestPending = false;
            _historyStatus = "The server log history request timed out.";
        }

    }

    private static IssueSortColumn ParseIssueSortColumn(string value)
    {
        return Enum.TryParse(value, true, out IssueSortColumn parsed) && Enum.IsDefined(typeof(IssueSortColumn), parsed)
            ? parsed
            : IssueSortColumn.Count;
    }
}
