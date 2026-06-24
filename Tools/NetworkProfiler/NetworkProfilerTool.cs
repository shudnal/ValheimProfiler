#nullable disable

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ValheimProfiler.Tools.NetworkProfiler;

internal sealed partial class NetworkProfilerTool : IProfilerTool, IProfilerToolAvailability
{
    internal const string ToolId = "NetworkProfiler";
    internal const string DisplayTitle = "Network Profiler";
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

    private enum MainTab
    {
        Rpc,
        Zdo,
        Peers,
        RoutingErrors,
        Help
    }


    private enum RpcSortColumn
    {
        Layer, Name, IncomingCalls, LocalCalls, OutgoingCalls, IncomingBytes, LocalBytes,
        OutgoingBytes, PhysicalBytes, PhysicalSends, HandlerMs, AverageHandlerMs,
        MaxHandlerMs, MaxPayload, MaxCallsFrame, Errors
    }

    private enum ZdoPrefabSortColumn
    {
        Prefab, Mutations, UniqueChanged, SentCount, SentBytes, ReceivedCount, ReceivedBytes,
        SerializeMs, DeserializeMs, AverageSize, MaxSize, Creates, Destroys, Ownership
    }

    private enum ZdoInstanceSortColumn
    {
        ZdoId, Prefab, Owner, Mutations, SentCount, SentBytes, ReceivedCount, ReceivedBytes, MaxSize, Traffic
    }

    private enum ZdoKeySortColumn
    {
        Prefab, Key, Type, Mutations, Affected
    }

    private enum PeerSortColumn
    {
        Peer, Socket, Ready, Ping, Out, In, SerializedOut, SerializedIn, SendQueue,
        MaxQueue, SendRate, InFlight, SendZdoMs, SyncMs, Sent, Candidates, Selected
    }

    private enum RoutingErrorSortColumn
    {
        Count, Kind, Rpc, Peer, Component, Caller, Details
    }

    private readonly ValheimProfilerApp _app;
    private readonly WindowManager _windows;
    private readonly ThemeManager _theme;
    private readonly ProfilerWindow _window;
    private readonly List<NetworkRpcWireRow> _rpcRows = new();
    private readonly List<NetworkZdoWireRow> _zdoRows = new();
    private readonly List<NetworkZdoInstanceWireRow> _zdoInstanceRows = new();
    private readonly List<NetworkZdoKeyWireRow> _zdoKeyRows = new();
    private readonly List<NetworkPeerWireRow> _peerRows = new();
    private readonly List<NetworkRoutingErrorWireRow> _errorRows = new();
    private readonly List<string> _compatibilityWarnings = new();

    private AvailabilityState _availability = AvailabilityState.NoDedicatedConnection;
    private string _status = "Connect to a headless dedicated server to use Network Profiler.";
    private string _serverVersion = string.Empty;
    private string _sessionId = string.Empty;
    private string _compatibilitySummary = string.Empty;
    private bool _authorized;
    private bool _subscribed;
    private bool _probePending;
    private bool _requestPending;
    private float _probeDeadline;
    private float _requestDeadline;
    private float _nextProbe;
    private long _snapshotSequence;
    private ZRoutedRpc _networkRpc;
    private MainTab _tab;
    private int _zdoView;
    private string _search = string.Empty;
    private Vector2 _scroll;
    private Vector2 _helpScroll;
    private GUIStyle _labelStyle;
    private GUIStyle _headerStyle;
    private GUIStyle _activeHeaderStyle;
    private GUIStyle _detailsStyle;
    private GUISkin _styleSkin;
    private RpcSortColumn _rpcSortColumn;
    private ZdoPrefabSortColumn _zdoPrefabSortColumn;
    private ZdoInstanceSortColumn _zdoInstanceSortColumn;
    private ZdoKeySortColumn _zdoKeySortColumn;
    private PeerSortColumn _peerSortColumn;
    private RoutingErrorSortColumn _routingErrorSortColumn;

    internal NetworkProfilerTool(ValheimProfilerApp app)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _windows = app.Windows;
        _theme = app.Theme;
        _rpcSortColumn = ParseSort(app.Config.NetworkRpcSortColumn.Value, RpcSortColumn.HandlerMs);
        _zdoPrefabSortColumn = ParseSort(app.Config.NetworkZdoPrefabSortColumn.Value, ZdoPrefabSortColumn.SentBytes);
        _zdoInstanceSortColumn = ParseSort(app.Config.NetworkZdoInstanceSortColumn.Value, ZdoInstanceSortColumn.SentBytes);
        _zdoKeySortColumn = ParseSort(app.Config.NetworkZdoKeySortColumn.Value, ZdoKeySortColumn.Mutations);
        _peerSortColumn = ParseSort(app.Config.NetworkPeerSortColumn.Value, PeerSortColumn.SendQueue);
        _routingErrorSortColumn = ParseSort(app.Config.NetworkRoutingErrorSortColumn.Value, RoutingErrorSortColumn.Count);
        var minimumSize = new Vector2(900f, 500f);
        Vector2 defaultSize = _windows.GetDefaultToolWindowSize(700f, minimumSize);
        _window = _windows.Register(new ProfilerWindow(
            "ValheimProfiler.NetworkProfiler",
            DisplayTitle,
            new Rect(ValheimProfilerConfig.DefaultNetworkProfilerWindowPosition, defaultSize),
            minimumSize,
            resizable: true,
            requestedVisible: false,
            drawContents: DrawWindow,
            positionConfig: app.Config.NetworkProfilerWindowPosition,
            sizeConfig: app.Config.NetworkProfilerWindowSize));
    }

    string IProfilerTool.Id => ToolId;
    string IProfilerTool.DisplayName => "Network";
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
        get => _window.RequestedVisible;
        set => _window.RequestedVisible = value;
    }

    internal bool IsAvailable => _availability == AvailabilityState.Available;
    internal string AvailabilityTooltip => _status + "\nRequires Valheim Profiler on a headless dedicated server and server admin access.";

    internal void ShowWindow()
    {
        IsWindowVisible = true;
        _app.ShowUi();
        _windows.BringToFront(_window);
        if (!IsAvailable)
            _tab = MainTab.Help;
    }

    internal void ToggleWindow()
    {
        IsWindowVisible = !IsWindowVisible;
        if (IsWindowVisible)
            ShowWindow();
    }

    internal void Update()
    {
        UpdateAvailability();
        float now = Time.realtimeSinceStartup;
        if (_requestPending && now >= _requestDeadline)
        {
            _requestPending = false;
            _status = "Network Profiler request timed out.";
        }
    }

    internal void Shutdown()
    {
        if (_subscribed && IsDedicatedServerConnectionDetected())
            SendRequest(NetworkProfilerRequestKind.Unsubscribe);
        _subscribed = false;
        IsWindowVisible = false;
        ClearRows();
    }

    internal void OnNetworkDestroyed()
    {
        _networkRpc = null;
        _probePending = false;
        _requestPending = false;
        _authorized = false;
        _subscribed = false;
        _sessionId = string.Empty;
        _serverVersion = string.Empty;
        _snapshotSequence = 0L;
        _availability = AvailabilityState.NoDedicatedConnection;
        _status = "Connect to a headless dedicated server to use Network Profiler.";
        _nextProbe = 0f;
        ClearRows();
        if (IsWindowVisible)
            _tab = MainTab.Help;
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

        ZRoutedRpc current = ZRoutedRpc.instance;
        if (!ReferenceEquals(current, _networkRpc))
        {
            _networkRpc = current;
            _subscribed = false;
            _availability = AvailabilityState.Detecting;
            _status = "Checking whether the dedicated server supports Network Profiler...";
            _probePending = false;
            _nextProbe = Time.realtimeSinceStartup + 0.15f;
            ClearRows();
        }

        float now = Time.realtimeSinceStartup;
        if (_probePending && now >= _probeDeadline)
        {
            _probePending = false;
            _availability = AvailabilityState.ServerModUnavailable;
            _status = "The dedicated server did not answer. Valheim Profiler is probably missing, too old, or its Network Profiler backend is unavailable.";
            _nextProbe = now + ProbeRetrySeconds;
            if (IsWindowVisible)
                _tab = MainTab.Help;
        }

        if (!_probePending && _availability != AvailabilityState.Available && now >= _nextProbe)
            Probe();
    }

    private void Probe()
    {
        if (!SendRequest(NetworkProfilerRequestKind.Probe))
        {
            _availability = AvailabilityState.ServerModUnavailable;
            _status = "The dedicated-server RPC connection is not ready.";
            _nextProbe = Time.realtimeSinceStartup + ProbeRetrySeconds;
            return;
        }
        _availability = AvailabilityState.Detecting;
        _status = "Checking dedicated-server Network Profiler capabilities...";
        _probePending = true;
        _probeDeadline = Time.realtimeSinceStartup + ProbeTimeoutSeconds;
    }

    private void Subscribe()
    {
        if (!IsAvailable || _subscribed || _requestPending)
            return;
        if (!SendRequest(NetworkProfilerRequestKind.Subscribe))
            return;
        _requestPending = true;
        _requestDeadline = Time.realtimeSinceStartup + RequestTimeoutSeconds;
        _status = "Requesting the first dedicated-server network snapshot...";
        _tab = MainTab.Rpc;
    }

    private void Unsubscribe()
    {
        if (_subscribed)
            SendRequest(NetworkProfilerRequestKind.Unsubscribe);
        _subscribed = false;
        _requestPending = false;
        _status = "Unsubscribed. The retained local snapshot remains visible; use Reset to clear it or Subscribe to resume private admin snapshots.";
    }

    private bool SendRequest(NetworkProfilerRequestKind kind)
    {
        try
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null)
                return false;
            long server = rpc.GetServerPeerID();
            if (server == 0L)
                return false;
            ZPackage payload = NetworkProfilerProtocol.CreateRequest(kind);
            return NetworkProfilerTransport.Send(server, NetworkProfilerProtocol.RequestRpc, payload, out _);
        }
        catch
        {
            return false;
        }
    }

    internal void HandleResponse(long sender, ZPackage package)
    {
        try
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null)
                return;
            long server = rpc.GetServerPeerID();
            if (server != 0L && sender != server)
                return;

            NetworkProfilerTransportReceiveResult transport = NetworkProfilerTransport.TryReceive(sender, package, out ZPackage payload, out string transportError);
            if (transport == NetworkProfilerTransportReceiveResult.WaitingForFragments)
                return;
            if (transport == NetworkProfilerTransportReceiveResult.Rejected)
                throw new InvalidOperationException("Invalid Network Profiler transport payload: " + transportError);

            NetworkProfilerResponse response = NetworkProfilerProtocol.ReadResponse(payload);
            _probePending = false;
            _requestPending = false;
            _serverVersion = response.ServerVersion ?? string.Empty;
            _authorized = response.Authorized;
            _compatibilitySummary = response.CompatibilitySummary ?? string.Empty;
            _compatibilityWarnings.Clear();
            _compatibilityWarnings.AddRange(response.CompatibilityWarnings);

            if (!response.Authorized)
            {
                _subscribed = false;
                _availability = AvailabilityState.AccessDenied;
                _status = string.IsNullOrWhiteSpace(response.Status) ? "The server rejected Network Profiler access." : response.Status;
                _nextProbe = Time.realtimeSinceStartup + ProbeRetrySeconds;
                if (IsWindowVisible)
                    _tab = MainTab.Help;
                return;
            }

            _availability = AvailabilityState.Available;
            _nextProbe = float.MaxValue;
            _status = string.IsNullOrWhiteSpace(response.Status) ? "Dedicated-server Network Profiler is available." : response.Status;

            if (response.Kind == NetworkProfilerResponseKind.Error)
            {
                _subscribed = false;
                return;
            }
            if (response.Kind == NetworkProfilerResponseKind.Status)
            {
                if (_status.IndexOf("Unsubscribed", StringComparison.OrdinalIgnoreCase) >= 0)
                    _subscribed = false;
                return;
            }
            if (response.Kind == NetworkProfilerResponseKind.Capabilities)
                return;

            _subscribed = true;
            _sessionId = response.SessionId ?? string.Empty;
            _snapshotSequence = response.SnapshotSequence;
            ReplaceRows(response);
        }
        catch (Exception ex)
        {
            _probePending = false;
            _requestPending = false;
            _subscribed = false;
            _availability = AvailabilityState.ProtocolMismatch;
            _status = "Network Profiler protocol error: " + ex.Message;
            _nextProbe = Time.realtimeSinceStartup + ProbeRetrySeconds;
            if (IsWindowVisible)
                _tab = MainTab.Help;
        }
    }

    private void ReplaceRows(NetworkProfilerResponse response)
    {
        if (response.FullRegistry)
        {
            _rpcRows.Clear();
            _rpcRows.AddRange(response.RpcRows);
        }
        else
        {
            for (int i = 0; i < _rpcRows.Count; i++)
                ResetRpcInterval(_rpcRows[i]);

            var existing = new Dictionary<string, NetworkRpcWireRow>(StringComparer.Ordinal);
            for (int i = 0; i < _rpcRows.Count; i++)
                existing[RpcIdentity(_rpcRows[i])] = _rpcRows[i];

            for (int i = 0; i < response.RpcRows.Count; i++)
            {
                NetworkRpcWireRow incoming = response.RpcRows[i];
                string key = RpcIdentity(incoming);
                if (existing.TryGetValue(key, out NetworkRpcWireRow target))
                    CopyRpcInterval(incoming, target);
                else
                    _rpcRows.Add(incoming);
            }
        }

        _zdoRows.Clear(); _zdoRows.AddRange(response.ZdoRows);
        _zdoInstanceRows.Clear(); _zdoInstanceRows.AddRange(response.ZdoInstanceRows);
        _zdoKeyRows.Clear(); _zdoKeyRows.AddRange(response.ZdoKeyRows);
        _peerRows.Clear(); _peerRows.AddRange(response.PeerRows);
        _errorRows.Clear(); _errorRows.AddRange(response.ErrorRows);
    }

    private static string RpcIdentity(NetworkRpcWireRow row) =>
        row.Layer + "|" + row.MethodHash + "|" + row.Component + "|" + row.Handler + "|" + row.Prefab;

    private static void ResetRpcInterval(NetworkRpcWireRow row)
    {
        row.IncomingCalls = 0L;
        row.LocalCalls = 0L;
        row.OutgoingCalls = 0L;
        row.IncomingBytes = 0L;
        row.LocalBytes = 0L;
        row.OutgoingBytes = 0L;
        row.PhysicalSends = 0L;
        row.PhysicalBytes = 0L;
        row.HandlerMs = 0d;
        row.AverageHandlerMs = 0d;
        row.MaxHandlerMs = 0d;
        row.MaxPayloadBytes = 0;
        row.MaxCallsPerFrame = 0;
        row.Errors = 0L;
    }

    private static void CopyRpcInterval(NetworkRpcWireRow source, NetworkRpcWireRow target)
    {
        target.Name = source.Name;
        target.Mod = source.Mod;
        target.Component = source.Component;
        target.Handler = source.Handler;
        target.Prefab = source.Prefab;
        target.Registrations = source.Registrations;
        target.IncomingCalls = source.IncomingCalls;
        target.LocalCalls = source.LocalCalls;
        target.OutgoingCalls = source.OutgoingCalls;
        target.IncomingBytes = source.IncomingBytes;
        target.LocalBytes = source.LocalBytes;
        target.OutgoingBytes = source.OutgoingBytes;
        target.PhysicalSends = source.PhysicalSends;
        target.PhysicalBytes = source.PhysicalBytes;
        target.HandlerMs = source.HandlerMs;
        target.AverageHandlerMs = source.AverageHandlerMs;
        target.MaxHandlerMs = source.MaxHandlerMs;
        target.MaxPayloadBytes = source.MaxPayloadBytes;
        target.MaxCallsPerFrame = source.MaxCallsPerFrame;
        target.Errors = source.Errors;
    }

    private bool HasDataRows =>
        _rpcRows.Count > 0 || _zdoRows.Count > 0 || _zdoInstanceRows.Count > 0 ||
        _zdoKeyRows.Count > 0 || _peerRows.Count > 0 || _errorRows.Count > 0;

    private void ClearDataRows()
    {
        _rpcRows.Clear();
        _zdoRows.Clear();
        _zdoInstanceRows.Clear();
        _zdoKeyRows.Clear();
        _peerRows.Clear();
        _errorRows.Clear();
        _snapshotSequence = 0L;
    }

    private void ClearRows()
    {
        ClearDataRows();
        _compatibilityWarnings.Clear();
        _compatibilitySummary = string.Empty;
    }

    private static T ParseSort<T>(string value, T fallback) where T : struct, Enum =>
        Enum.TryParse(value, true, out T parsed) ? parsed : fallback;

    private static string FormatRpcIdentity(string name, int hash)
    {
        string hashText = hash.ToString();
        string hashToken = "#" + hashText;
        string value = (name ?? string.Empty).Trim();

        if (value.Length == 0 ||
            string.Equals(value, hashText, StringComparison.Ordinal) ||
            string.Equals(value, hashToken, StringComparison.Ordinal) ||
            string.Equals(value, "Unknown RPC", StringComparison.OrdinalIgnoreCase))
            return hashToken;

        if (value.IndexOf("(#" + hashText + ")", StringComparison.Ordinal) >= 0 ||
            value.EndsWith(hashToken, StringComparison.Ordinal))
            return value;

        return value + " (" + hashToken + ")";
    }

    private static bool Matches(string search, params string[] values)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;
        for (int i = 0; i < values.Length; i++)
            if (!string.IsNullOrEmpty(values[i]) && values[i].IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        return false;
    }

    private static string Bytes(long value)
    {
        if (value >= 1024 * 1024) return (value / 1048576d).ToString("0.00") + " MB";
        if (value >= 1024) return (value / 1024d).ToString("0.0") + " KB";
        return value + " B";
    }

    private static string Ms(double value) => value < 0.001d ? "0" : value.ToString("0.###");
}
