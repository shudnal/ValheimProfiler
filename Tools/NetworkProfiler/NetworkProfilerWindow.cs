#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ValheimProfiler.Tools.NetworkProfiler;

internal sealed partial class NetworkProfilerTool
{
    private void DrawWindow(int id)
    {
        EnsureStyles();
        GUILayout.BeginVertical();
        DrawToolbar();
        GUILayout.Space(3f);

        MainTab next = (MainTab)GUILayout.Toolbar(
            (int)_tab,
            new[] { "RPC", "ZDO", "Peers / Transport", "Routing errors", "Help" },
            GUILayout.Width(620f));
        if (next != _tab)
        {
            _tab = next;
            _scroll = Vector2.zero;
        }

        GUILayout.Space(3f);

        if (_tab != MainTab.Help)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Search:", _labelStyle, GUILayout.Width(48f));
            _search = GUILayout.TextField(_search ?? string.Empty, GUILayout.Width(300f));
            if (GUILayout.Button("Clear", GUILayout.Width(55f)))
                _search = string.Empty;
            if (_tab == MainTab.Zdo)
                _zdoView = GUILayout.Toolbar(_zdoView, new[] { "By prefab", "Top ZDOs", "Keys" }, GUILayout.Width(260f));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(3f);
        }

        switch (_tab)
        {
            case MainTab.Rpc:
                DrawRpc();
                break;
            case MainTab.Zdo:
                DrawZdo();
                break;
            case MainTab.Peers:
                DrawPeers();
                break;
            case MainTab.RoutingErrors:
                DrawErrors();
                break;
            default:
                DrawHelp();
                break;
        }

        GUILayout.EndVertical();
    }

    private void DrawToolbar()
    {
        GUILayout.BeginHorizontal();
        string version = string.IsNullOrEmpty(_serverVersion) ? "unknown" : _serverVersion;
        GUILayout.Label(
            $"Server v{version} | {(_subscribed ? "SUBSCRIBED" : _requestPending ? "CONNECTING" : "OFF")} | " +
            $"Snapshot: {_snapshotSequence} | RPC: {_rpcRows.Count} | ZDO: {_zdoRows.Count} | Errors: {_errorRows.Count}",
            _labelStyle,
            GUILayout.ExpandWidth(true));

        bool old = GUI.enabled;
        GUI.enabled = old && IsAvailable && !_requestPending;
        if (GUILayout.Button(
                new GUIContent(
                    _subscribed ? "Unsubscribe" : "Subscribe",
                    _subscribed
                        ? "Stop this connection-scoped private admin stream. The current local snapshot remains visible until Reset."
                        : "Subscribe this administrator connection to private dedicated-server network snapshots."),
                GUILayout.Width(94f)))
        {
            if (_subscribed)
                Unsubscribe();
            else
                Subscribe();
        }

        GUI.enabled = old && !_requestPending && (_subscribed || HasDataRows);
        if (GUILayout.Button(
                new GUIContent(
                    "Reset",
                    _subscribed
                        ? "Reset the dedicated-server Network Profiler counters and clear the current local snapshot."
                        : "Clear the retained local snapshot after unsubscribing."),
                GUILayout.Width(62f)))
        {
            if (_subscribed)
            {
                if (SendRequest(NetworkProfilerRequestKind.Reset))
                {
                    _requestPending = true;
                    _requestDeadline = Time.realtimeSinceStartup + RequestTimeoutSeconds;
                }
            }
            else
            {
                ClearDataRows();
                _status = "Retained local Network Profiler data cleared.";
            }
        }

        GUI.enabled = old;
        if (GUILayout.Button("Close", GUILayout.Width(62f)))
            IsWindowVisible = false;
        GUILayout.EndHorizontal();

        GUILayout.Label(_status, _labelStyle, GUILayout.ExpandWidth(true));
        if (_compatibilityWarnings.Count > 0)
            GUILayout.Label("Compatibility warning: " + _compatibilityWarnings[0], _detailsStyle, GUILayout.ExpandWidth(true));
    }

    private void DrawRpc()
    {
        const float name = 440f;
        const float small = 78f;
        const float medium = 105f;

        GUILayout.BeginHorizontal();
        SortHeader("Layer", 65f, RpcSortColumn.Layer, "RPC layer: Direct ZRpc, global routed RPC, or object/ZNetView routed RPC.");
        SortHeader("Mod / component / RPC", name, RpcSortColumn.Name, "Registered protocol owner, component, RPC name/hash, handler method and prefab when known. Registration ownership is not always the outgoing call site.");
        SortHeader("in/s", small, RpcSortColumn.IncomingCalls, "Remote calls handled by the server during the latest snapshot interval.");
        SortHeader("local/s", small, RpcSortColumn.LocalCalls, "Calls dispatched locally on the server during the latest interval.");
        SortHeader("out/s", small, RpcSortColumn.OutgoingCalls, "Logical outgoing RPC calls initiated by the server during the latest interval.");
        SortHeader("in data", medium, RpcSortColumn.IncomingBytes, "Logical serialized payload received for this RPC during the latest interval.");
        SortHeader("local data", medium, RpcSortColumn.LocalBytes, "Logical payload dispatched locally on the server.");
        SortHeader("out data", medium, RpcSortColumn.OutgoingBytes, "Logical outgoing payload before routed broadcast fanout.");
        SortHeader("physical out", medium, RpcSortColumn.PhysicalBytes, "Serialized routed payload after actual peer fanout. This is more representative for broadcasts.");
        SortHeader("sends", small, RpcSortColumn.PhysicalSends, "Number of physical peer sends after routed fanout.");
        SortHeader("CPU ms/s", small, RpcSortColumn.HandlerMs, "Inclusive server handler CPU time accumulated in the latest interval.");
        SortHeader("avg ms", small, RpcSortColumn.AverageHandlerMs, "Average server handler duration per invocation.");
        SortHeader("max ms", small, RpcSortColumn.MaxHandlerMs, "Slowest server handler invocation in the latest interval.");
        SortHeader("max payload", medium, RpcSortColumn.MaxPayload, "Largest logical payload observed for one invocation.");
        SortHeader("max/frame", small, RpcSortColumn.MaxCallsFrame, "Largest number of calls observed in one server frame.");
        SortHeader("errors", small, RpcSortColumn.Errors, "Handler exceptions or routing failures associated with this RPC.");
        GUILayout.EndHorizontal();

        IEnumerable<NetworkRpcWireRow> rows = _rpcRows.Where(r => Matches(_search, r.Name, r.Mod, r.Component, r.Handler, r.Prefab));
        rows = SortRpcRows(rows);

        _scroll = GUILayout.BeginScrollView(_scroll, true, true);
        foreach (NetworkRpcWireRow row in rows)
        {
            GUILayout.BeginHorizontal();
            Cell(row.Layer, 65f);
            Cell(BuildRpcName(row), name);
            Cell(row.IncomingCalls.ToString(), small);
            Cell(row.LocalCalls.ToString(), small);
            Cell(row.OutgoingCalls.ToString(), small);
            Cell(Bytes(row.IncomingBytes), medium);
            Cell(Bytes(row.LocalBytes), medium);
            Cell(Bytes(row.OutgoingBytes), medium);
            Cell(Bytes(row.PhysicalBytes), medium);
            Cell(row.PhysicalSends.ToString(), small);
            Cell(Ms(row.HandlerMs), small);
            Cell(Ms(row.AverageHandlerMs), small);
            Cell(Ms(row.MaxHandlerMs), small);
            Cell(Bytes(row.MaxPayloadBytes), medium);
            Cell(row.MaxCallsPerFrame.ToString(), small);
            Cell(row.Errors.ToString(), small);
            GUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();
    }

    private static string BuildRpcName(NetworkRpcWireRow row)
    {
        string value = string.Empty;
        if (!string.IsNullOrEmpty(row.Mod))
            value = row.Mod;
        if (!string.IsNullOrEmpty(row.Component))
            value += (value.Length == 0 ? string.Empty : " | ") + row.Component;
        value += (value.Length == 0 ? string.Empty : " | ") + FormatRpcIdentity(row.Name, row.MethodHash);
        if (!string.IsNullOrEmpty(row.Handler))
            value += " -> " + row.Handler;
        if (!string.IsNullOrEmpty(row.Prefab))
            value += " | prefab " + row.Prefab;
        return value;
    }

    private void DrawZdo()
    {
        if (_zdoView == 2)
        {
            GUILayout.BeginHorizontal();
            SortHeader("Prefab", 250f, ZdoKeySortColumn.Prefab, "Prefab whose ZDO values were mutated.");
            SortHeader("Key", 260f, ZdoKeySortColumn.Key, "Best-effort ZDO key name and stable hash. Runtime string Set overloads and known vanilla ZDOVars improve name resolution.");
            SortHeader("Type", 100f, ZdoKeySortColumn.Type, "Observed value type for this ZDO key.");
            SortHeader("mutations/s", 100f, ZdoKeySortColumn.Mutations, "Successful Set operations that triggered a ZDO revision in the latest interval.");
            SortHeader("affected", 90f, ZdoKeySortColumn.Affected, "Distinct ZDO instances affected by this key in the latest interval. Full payload bytes are intentionally not assigned to one key.");
            GUILayout.EndHorizontal();

            IEnumerable<NetworkZdoKeyWireRow> rows = _zdoKeyRows.Where(r => Matches(_search, r.Prefab, r.KeyName, r.ValueType));
            rows = SortZdoKeyRows(rows);
            _scroll = GUILayout.BeginScrollView(_scroll, true, true);
            foreach (NetworkZdoKeyWireRow row in rows)
            {
                GUILayout.BeginHorizontal();
                Cell(row.Prefab, 250f);
                Cell((string.IsNullOrEmpty(row.KeyName) ? "Unknown" : row.KeyName) + " (#" + row.KeyHash + ")", 260f);
                Cell(row.ValueType, 100f);
                Cell(row.Mutations.ToString(), 100f);
                Cell(row.AffectedZdos.ToString(), 90f);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            return;
        }

        if (_zdoView == 1)
        {
            GUILayout.BeginHorizontal();
            SortHeader("ZDOID", 210f, ZdoInstanceSortColumn.ZdoId, "Specific network object identifier.");
            SortHeader("Prefab", 240f, ZdoInstanceSortColumn.Prefab, "Prefab name/hash for this ZDO.");
            SortHeader("Owner", 125f, ZdoInstanceSortColumn.Owner, "Current ZDO owner peer ID.");
            SortHeader("mut/s", 75f, ZdoInstanceSortColumn.Mutations, "Revision-triggering mutations in the latest interval.");
            SortHeader("sent/s", 75f, ZdoInstanceSortColumn.SentCount, "Full ZDO serializations sent in the latest interval.");
            SortHeader("sent", 95f, ZdoInstanceSortColumn.SentBytes, "Full serialized ZDO bytes sent across peers.");
            SortHeader("recv/s", 75f, ZdoInstanceSortColumn.ReceivedCount, "Full ZDO updates received in the latest interval.");
            SortHeader("recv", 95f, ZdoInstanceSortColumn.ReceivedBytes, "Full serialized ZDO bytes received.");
            SortHeader("max size", 90f, ZdoInstanceSortColumn.MaxSize, "Largest complete serialized form observed for this ZDO.");
            GUILayout.EndHorizontal();

            IEnumerable<NetworkZdoInstanceWireRow> rows = _zdoInstanceRows.Where(r => Matches(_search, r.ZdoId, r.Prefab, r.Owner.ToString()));
            rows = SortZdoInstanceRows(rows);
            _scroll = GUILayout.BeginScrollView(_scroll, true, true);
            foreach (NetworkZdoInstanceWireRow row in rows)
            {
                GUILayout.BeginHorizontal();
                Cell(row.ZdoId, 210f);
                Cell(row.Prefab + " (#" + row.PrefabHash + ")", 240f);
                Cell(row.Owner.ToString(), 125f);
                Cell(row.Mutations.ToString(), 75f);
                Cell(row.SentCount.ToString(), 75f);
                Cell(Bytes(row.SentBytes), 95f);
                Cell(row.ReceivedCount.ToString(), 75f);
                Cell(Bytes(row.ReceivedBytes), 95f);
                Cell(Bytes(row.MaxSize), 90f);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            return;
        }

        GUILayout.BeginHorizontal();
        SortHeader("Prefab", 270f, ZdoPrefabSortColumn.Prefab, "ZDO prefab name and stable hash.");
        SortHeader("mut/s", 75f, ZdoPrefabSortColumn.Mutations, "Successful ZDO Set operations that changed values and incremented revisions.");
        SortHeader("unique", 75f, ZdoPrefabSortColumn.UniqueChanged, "Distinct changed ZDO instances in the latest interval.");
        SortHeader("sent/s", 75f, ZdoPrefabSortColumn.SentCount, "Complete ZDO serializations sent in the latest interval.");
        SortHeader("sent", 95f, ZdoPrefabSortColumn.SentBytes, "Complete serialized ZDO bytes sent to all peers, including amplification.");
        SortHeader("recv/s", 75f, ZdoPrefabSortColumn.ReceivedCount, "Complete ZDO updates received in the latest interval.");
        SortHeader("recv", 95f, ZdoPrefabSortColumn.ReceivedBytes, "Complete serialized ZDO bytes received.");
        SortHeader("ser ms", 75f, ZdoPrefabSortColumn.SerializeMs, "CPU time spent serializing complete ZDO payloads.");
        SortHeader("deser ms", 80f, ZdoPrefabSortColumn.DeserializeMs, "CPU time spent deserializing complete ZDO payloads.");
        SortHeader("avg size", 90f, ZdoPrefabSortColumn.AverageSize, "Average complete serialized ZDO size.");
        SortHeader("max size", 90f, ZdoPrefabSortColumn.MaxSize, "Largest complete serialized ZDO size.");
        SortHeader("create", 70f, ZdoPrefabSortColumn.Creates, "ZDO instances created in the latest interval.");
        SortHeader("destroy", 70f, ZdoPrefabSortColumn.Destroys, "ZDO instances destroyed in the latest interval.");
        SortHeader("owner", 70f, ZdoPrefabSortColumn.Ownership, "Ownership changes in the latest interval.");
        GUILayout.EndHorizontal();

        IEnumerable<NetworkZdoWireRow> prefabRows = _zdoRows.Where(r => Matches(_search, r.Prefab, r.PrefabHash.ToString()));
        prefabRows = SortZdoPrefabRows(prefabRows);
        _scroll = GUILayout.BeginScrollView(_scroll, true, true);
        foreach (NetworkZdoWireRow row in prefabRows)
        {
            GUILayout.BeginHorizontal();
            Cell(row.Prefab + " (#" + row.PrefabHash + ")", 270f);
            Cell(row.Mutations.ToString(), 75f);
            Cell(row.UniqueChanged.ToString(), 75f);
            Cell(row.SentCount.ToString(), 75f);
            Cell(Bytes(row.SentBytes), 95f);
            Cell(row.ReceivedCount.ToString(), 75f);
            Cell(Bytes(row.ReceivedBytes), 95f);
            Cell(Ms(row.SerializeMs), 75f);
            Cell(Ms(row.DeserializeMs), 80f);
            Cell(Bytes(row.AverageSize), 90f);
            Cell(Bytes(row.MaxSize), 90f);
            Cell(row.Creates.ToString(), 70f);
            Cell(row.Destroys.ToString(), 70f);
            Cell(row.OwnershipChanges.ToString(), 70f);
            GUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();
    }

    private void DrawPeers()
    {
        GUILayout.BeginHorizontal();
        SortHeader("Peer", 210f, PeerSortColumn.Peer, "Connected peer name, host and peer ID.");
        SortHeader("Socket", 135f, PeerSortColumn.Socket, "Runtime socket backend. Network mods may replace or wrap the vanilla backend.");
        SortHeader("ready", 55f, PeerSortColumn.Ready, "Whether the peer is ready for normal game traffic.");
        SortHeader("ping", 55f, PeerSortColumn.Ping, "Reported peer ping in milliseconds when available.");
        SortHeader("out/s", 85f, PeerSortColumn.Out, "Transport-reported outgoing bytes per second.");
        SortHeader("in/s", 85f, PeerSortColumn.In, "Transport-reported incoming bytes per second.");
        SortHeader("serialized out", 105f, PeerSortColumn.SerializedOut, "Game package bytes serialized for this peer in the latest interval.");
        SortHeader("serialized in", 105f, PeerSortColumn.SerializedIn, "Game package bytes deserialized from this peer in the latest interval.");
        SortHeader("queue", 80f, PeerSortColumn.SendQueue, "Current game-reported socket send queue. Backend semantics differ; see Help compatibility notes.");
        SortHeader("max queue", 90f, PeerSortColumn.MaxQueue, "Maximum send queue observed during the latest interval.");
        SortHeader("send rate", 85f, PeerSortColumn.SendRate, "Current configured or measured send rate exposed by the socket backend.");
        SortHeader("actual flight", 95f, PeerSortColumn.InFlight, "Best-effort actual in-flight bytes. Primarily available for PlayFab; n/a for unsupported backends.");
        SortHeader("ZDO ms", 75f, PeerSortColumn.SendZdoMs, "CPU time spent in SendZDOs for this peer.");
        SortHeader("sync ms", 75f, PeerSortColumn.SyncMs, "CPU time spent building and sorting the ZDO synchronization candidate list.");
        SortHeader("sent", 65f, PeerSortColumn.Sent, "ZDO records selected and serialized for this peer.");
        SortHeader("candidates", 85f, PeerSortColumn.Candidates, "ZDO candidates considered for this peer before filtering and budget limits.");
        SortHeader("selected", 75f, PeerSortColumn.Selected, "ZDO candidates selected for synchronization.");
        GUILayout.EndHorizontal();

        IEnumerable<NetworkPeerWireRow> rows = _peerRows.Where(r => Matches(_search, r.Name, r.Host, r.Socket, r.PeerId.ToString()));
        rows = SortPeerRows(rows);
        _scroll = GUILayout.BeginScrollView(_scroll, true, true);
        foreach (NetworkPeerWireRow row in rows)
        {
            GUILayout.BeginHorizontal();
            Cell((string.IsNullOrEmpty(row.Name) ? row.PeerId.ToString() : row.Name) + " | " + row.Host, 210f);
            Cell(row.Socket, 135f);
            Cell(row.Ready ? "yes" : "no", 55f);
            Cell(row.Ping.ToString(), 55f);
            Cell(Bytes((long)row.ReportedOutBytesPerSecond), 85f);
            Cell(Bytes((long)row.ReportedInBytesPerSecond), 85f);
            Cell(Bytes(row.SerializedOutBytesPerSecond), 105f);
            Cell(Bytes(row.SerializedInBytesPerSecond), 105f);
            Cell(Bytes(row.SendQueue), 80f);
            Cell(Bytes(row.MaximumSendQueue), 90f);
            Cell(Bytes(row.CurrentSendRate), 85f);
            Cell(row.ActualInFlightBytes < 0 ? "n/a" : Bytes(row.ActualInFlightBytes), 95f);
            Cell(Ms(row.SendZdoMs), 75f);
            Cell(Ms(row.CreateSyncListMs), 75f);
            Cell(row.ZdosSent.ToString(), 65f);
            Cell(row.SyncCandidates.ToString(), 85f);
            Cell(row.SyncSelected.ToString(), 75f);
            GUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();
    }

    private void DrawErrors()
    {
        GUILayout.BeginHorizontal();
        SortHeader("Count", 70f, RoutingErrorSortColumn.Count, "Occurrences of this bounded routing error identity.");
        SortHeader("Kind", 185f, RoutingErrorSortColumn.Kind, "Missing handler/peer/ZDO/ZNetView, handler exception, or another routing failure category.");
        SortHeader("RPC", 230f, RoutingErrorSortColumn.Rpc, "Registered RPC name and stable hash. Unknown hashes are shown once as #hash.");
        SortHeader("Peer", 180f, RoutingErrorSortColumn.Peer, "Source or target peer known at the failing server-side routing point.");
        SortHeader("Component / handler", 300f, RoutingErrorSortColumn.Component, "Component and registered handler resolved from Register calls or the current handler dictionaries.");
        SortHeader("Source caller", 300f, RoutingErrorSortColumn.Caller, "Best-effort server-local initiating method and mod. Incoming client call sites cannot be reconstructed on the server; use Peer plus registered handler context.");
        SortHeader("Target / details", 390f, RoutingErrorSortColumn.Details, "Target ZDO/prefab and the latest error or exception text.");
        GUILayout.EndHorizontal();

        IEnumerable<NetworkRoutingErrorWireRow> rows = _errorRows.Where(r => Matches(
            _search,
            r.Kind,
            r.Rpc,
            r.Peer,
            r.Component,
            r.Handler,
            r.Prefab,
            r.Caller,
            r.CallerMod,
            r.Target,
            r.LastDetails));
        rows = SortErrorRows(rows);

        _scroll = GUILayout.BeginScrollView(_scroll, true, true);
        foreach (NetworkRoutingErrorWireRow row in rows)
        {
            GUILayout.BeginHorizontal();
            Cell(row.Count.ToString(), 70f);
            Cell(row.Kind, 185f);
            Cell(FormatRpcIdentity(row.Rpc, row.MethodHash), 230f);
            Cell(row.Peer + (row.PeerId == 0 ? string.Empty : " | " + row.PeerId), 180f);
            Cell((row.Component + " | " + row.Handler).Trim(' ', '|'), 300f);
            Cell((row.CallerMod + " | " + row.Caller).Trim(' ', '|'), 300f);
            string details = (row.Target + " | " + row.Prefab + " | " + row.LastDetails).Trim(' ', '|');
            Cell(details, 390f);
            GUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();
    }

    private void DrawHelp()
    {
        _helpScroll = GUILayout.BeginScrollView(_helpScroll);
        HeaderLabel("Network Profiler requirements");
        Body("This tool intentionally works only with a remote headless dedicated server. Self-hosted and open-server instances are excluded because host gameplay, rendering and local loopback make the measurements harder to interpret consistently.");
        Body("The same compatible Valheim Profiler build must be installed on the dedicated server and the current player must be in its admin list. Subscribe creates a private connection-scoped stream; no network metrics are broadcast to other administrators.");
        GUILayout.Space(6f);
        HeaderLabel("RPC");
        Body("Registry rows attribute registered handlers to delegate methods, component types, assemblies and BepInEx plugins. Runtime rows show incoming/outgoing calls, logical payload, routed physical fanout, handler CPU time and peaks. Attribution means protocol/handler owner; the exact sender-side call site is not captured in the default low-overhead mode.");
        Body("Routing errors report handler exceptions, missing direct/global/object handlers, unavailable peers, missing ZDOs and missing ZNetViews. Register calls and current handler dictionaries map names/hashes to components and handler methods. For server-local failed sends the initiating caller is captured only when the error occurs; an incoming client RPC can expose the source peer but not its original client-side method without client instrumentation.");
        GUILayout.Space(6f);
        HeaderLabel("ZDO");
        Body("A successful ZDO mutation increments the whole ZDO revision, and vanilla synchronization serializes the complete ZDO. The profiler therefore reports mutation triggers, full serialized bytes and per-prefab traffic separately. Key rows show mutation frequency and must not be interpreted as exact byte ownership.");
        GUILayout.Space(6f);
        HeaderLabel("Compatibility");
        Body(string.IsNullOrEmpty(_compatibilitySummary) ? "No server compatibility report received yet." : _compatibilitySummary);
        for (int i = 0; i < _compatibilityWarnings.Count; i++)
            Body("- " + _compatibilityWarnings[i]);
        GUILayout.Space(6f);
        HeaderLabel("Current state");
        Body(_status);
        GUILayout.EndScrollView();
    }

    private IEnumerable<NetworkRpcWireRow> SortRpcRows(IEnumerable<NetworkRpcWireRow> rows) => _rpcSortColumn switch
    {
        RpcSortColumn.Layer => rows.OrderByDescending(r => r.Layer, StringComparer.OrdinalIgnoreCase),
        RpcSortColumn.Name => rows.OrderByDescending(BuildRpcName, StringComparer.OrdinalIgnoreCase),
        RpcSortColumn.IncomingCalls => rows.OrderByDescending(r => r.IncomingCalls),
        RpcSortColumn.LocalCalls => rows.OrderByDescending(r => r.LocalCalls),
        RpcSortColumn.OutgoingCalls => rows.OrderByDescending(r => r.OutgoingCalls),
        RpcSortColumn.IncomingBytes => rows.OrderByDescending(r => r.IncomingBytes),
        RpcSortColumn.LocalBytes => rows.OrderByDescending(r => r.LocalBytes),
        RpcSortColumn.OutgoingBytes => rows.OrderByDescending(r => r.OutgoingBytes),
        RpcSortColumn.PhysicalBytes => rows.OrderByDescending(r => r.PhysicalBytes),
        RpcSortColumn.PhysicalSends => rows.OrderByDescending(r => r.PhysicalSends),
        RpcSortColumn.AverageHandlerMs => rows.OrderByDescending(r => r.AverageHandlerMs),
        RpcSortColumn.MaxHandlerMs => rows.OrderByDescending(r => r.MaxHandlerMs),
        RpcSortColumn.MaxPayload => rows.OrderByDescending(r => r.MaxPayloadBytes),
        RpcSortColumn.MaxCallsFrame => rows.OrderByDescending(r => r.MaxCallsPerFrame),
        RpcSortColumn.Errors => rows.OrderByDescending(r => r.Errors),
        _ => rows.OrderByDescending(r => r.HandlerMs)
    };

    private IEnumerable<NetworkZdoWireRow> SortZdoPrefabRows(IEnumerable<NetworkZdoWireRow> rows) => _zdoPrefabSortColumn switch
    {
        ZdoPrefabSortColumn.Prefab => rows.OrderByDescending(r => r.Prefab, StringComparer.OrdinalIgnoreCase),
        ZdoPrefabSortColumn.Mutations => rows.OrderByDescending(r => r.Mutations),
        ZdoPrefabSortColumn.UniqueChanged => rows.OrderByDescending(r => r.UniqueChanged),
        ZdoPrefabSortColumn.SentCount => rows.OrderByDescending(r => r.SentCount),
        ZdoPrefabSortColumn.ReceivedCount => rows.OrderByDescending(r => r.ReceivedCount),
        ZdoPrefabSortColumn.ReceivedBytes => rows.OrderByDescending(r => r.ReceivedBytes),
        ZdoPrefabSortColumn.SerializeMs => rows.OrderByDescending(r => r.SerializeMs),
        ZdoPrefabSortColumn.DeserializeMs => rows.OrderByDescending(r => r.DeserializeMs),
        ZdoPrefabSortColumn.AverageSize => rows.OrderByDescending(r => r.AverageSize),
        ZdoPrefabSortColumn.MaxSize => rows.OrderByDescending(r => r.MaxSize),
        ZdoPrefabSortColumn.Creates => rows.OrderByDescending(r => r.Creates),
        ZdoPrefabSortColumn.Destroys => rows.OrderByDescending(r => r.Destroys),
        ZdoPrefabSortColumn.Ownership => rows.OrderByDescending(r => r.OwnershipChanges),
        _ => rows.OrderByDescending(r => r.SentBytes)
    };

    private IEnumerable<NetworkZdoInstanceWireRow> SortZdoInstanceRows(IEnumerable<NetworkZdoInstanceWireRow> rows) => _zdoInstanceSortColumn switch
    {
        ZdoInstanceSortColumn.ZdoId => rows.OrderByDescending(r => r.ZdoId, StringComparer.OrdinalIgnoreCase),
        ZdoInstanceSortColumn.Prefab => rows.OrderByDescending(r => r.Prefab, StringComparer.OrdinalIgnoreCase),
        ZdoInstanceSortColumn.Owner => rows.OrderByDescending(r => r.Owner),
        ZdoInstanceSortColumn.Mutations => rows.OrderByDescending(r => r.Mutations),
        ZdoInstanceSortColumn.SentCount => rows.OrderByDescending(r => r.SentCount),
        ZdoInstanceSortColumn.SentBytes => rows.OrderByDescending(r => r.SentBytes),
        ZdoInstanceSortColumn.ReceivedCount => rows.OrderByDescending(r => r.ReceivedCount),
        ZdoInstanceSortColumn.ReceivedBytes => rows.OrderByDescending(r => r.ReceivedBytes),
        ZdoInstanceSortColumn.MaxSize => rows.OrderByDescending(r => r.MaxSize),
        _ => rows.OrderByDescending(r => r.SentBytes + r.ReceivedBytes + r.Mutations * 16L)
    };

    private IEnumerable<NetworkZdoKeyWireRow> SortZdoKeyRows(IEnumerable<NetworkZdoKeyWireRow> rows) => _zdoKeySortColumn switch
    {
        ZdoKeySortColumn.Prefab => rows.OrderByDescending(r => r.Prefab, StringComparer.OrdinalIgnoreCase),
        ZdoKeySortColumn.Key => rows.OrderByDescending(r => r.KeyName, StringComparer.OrdinalIgnoreCase),
        ZdoKeySortColumn.Type => rows.OrderByDescending(r => r.ValueType, StringComparer.OrdinalIgnoreCase),
        ZdoKeySortColumn.Affected => rows.OrderByDescending(r => r.AffectedZdos),
        _ => rows.OrderByDescending(r => r.Mutations)
    };

    private IEnumerable<NetworkPeerWireRow> SortPeerRows(IEnumerable<NetworkPeerWireRow> rows) => _peerSortColumn switch
    {
        PeerSortColumn.Peer => rows.OrderByDescending(r => r.Name + "|" + r.Host, StringComparer.OrdinalIgnoreCase),
        PeerSortColumn.Socket => rows.OrderByDescending(r => r.Socket, StringComparer.OrdinalIgnoreCase),
        PeerSortColumn.Ready => rows.OrderByDescending(r => r.Ready),
        PeerSortColumn.Ping => rows.OrderByDescending(r => r.Ping),
        PeerSortColumn.Out => rows.OrderByDescending(r => r.ReportedOutBytesPerSecond),
        PeerSortColumn.In => rows.OrderByDescending(r => r.ReportedInBytesPerSecond),
        PeerSortColumn.SerializedOut => rows.OrderByDescending(r => r.SerializedOutBytesPerSecond),
        PeerSortColumn.SerializedIn => rows.OrderByDescending(r => r.SerializedInBytesPerSecond),
        PeerSortColumn.MaxQueue => rows.OrderByDescending(r => r.MaximumSendQueue),
        PeerSortColumn.SendRate => rows.OrderByDescending(r => r.CurrentSendRate),
        PeerSortColumn.InFlight => rows.OrderByDescending(r => r.ActualInFlightBytes),
        PeerSortColumn.SendZdoMs => rows.OrderByDescending(r => r.SendZdoMs),
        PeerSortColumn.SyncMs => rows.OrderByDescending(r => r.CreateSyncListMs),
        PeerSortColumn.Sent => rows.OrderByDescending(r => r.ZdosSent),
        PeerSortColumn.Candidates => rows.OrderByDescending(r => r.SyncCandidates),
        PeerSortColumn.Selected => rows.OrderByDescending(r => r.SyncSelected),
        _ => rows.OrderByDescending(r => r.SendQueue)
    };

    private IEnumerable<NetworkRoutingErrorWireRow> SortErrorRows(IEnumerable<NetworkRoutingErrorWireRow> rows) => _routingErrorSortColumn switch
    {
        RoutingErrorSortColumn.Kind => rows.OrderByDescending(r => r.Kind, StringComparer.OrdinalIgnoreCase),
        RoutingErrorSortColumn.Rpc => rows.OrderByDescending(r => FormatRpcIdentity(r.Rpc, r.MethodHash), StringComparer.OrdinalIgnoreCase),
        RoutingErrorSortColumn.Peer => rows.OrderByDescending(r => r.Peer + r.PeerId, StringComparer.OrdinalIgnoreCase),
        RoutingErrorSortColumn.Component => rows.OrderByDescending(r => r.Component + r.Handler, StringComparer.OrdinalIgnoreCase),
        RoutingErrorSortColumn.Caller => rows.OrderByDescending(r => r.CallerMod + r.Caller, StringComparer.OrdinalIgnoreCase),
        RoutingErrorSortColumn.Details => rows.OrderByDescending(r => r.Target + r.LastDetails, StringComparer.OrdinalIgnoreCase),
        _ => rows.OrderByDescending(r => r.Count)
    };

    private void SetSortColumn(RpcSortColumn column)
    {
        _rpcSortColumn = column;
        _app.Config.NetworkRpcSortColumn.Value = column.ToString();
    }

    private void SetSortColumn(ZdoPrefabSortColumn column)
    {
        _zdoPrefabSortColumn = column;
        _app.Config.NetworkZdoPrefabSortColumn.Value = column.ToString();
    }

    private void SetSortColumn(ZdoInstanceSortColumn column)
    {
        _zdoInstanceSortColumn = column;
        _app.Config.NetworkZdoInstanceSortColumn.Value = column.ToString();
    }

    private void SetSortColumn(ZdoKeySortColumn column)
    {
        _zdoKeySortColumn = column;
        _app.Config.NetworkZdoKeySortColumn.Value = column.ToString();
    }

    private void SetSortColumn(PeerSortColumn column)
    {
        _peerSortColumn = column;
        _app.Config.NetworkPeerSortColumn.Value = column.ToString();
    }

    private void SetSortColumn(RoutingErrorSortColumn column)
    {
        _routingErrorSortColumn = column;
        _app.Config.NetworkRoutingErrorSortColumn.Value = column.ToString();
    }

    private void SortHeader(string text, float width, RpcSortColumn column, string tooltip) =>
        SortHeaderCore(text, width, _rpcSortColumn.Equals(column), tooltip, () => SetSortColumn(column));

    private void SortHeader(string text, float width, ZdoPrefabSortColumn column, string tooltip) =>
        SortHeaderCore(text, width, _zdoPrefabSortColumn.Equals(column), tooltip, () => SetSortColumn(column));

    private void SortHeader(string text, float width, ZdoInstanceSortColumn column, string tooltip) =>
        SortHeaderCore(text, width, _zdoInstanceSortColumn.Equals(column), tooltip, () => SetSortColumn(column));

    private void SortHeader(string text, float width, ZdoKeySortColumn column, string tooltip) =>
        SortHeaderCore(text, width, _zdoKeySortColumn.Equals(column), tooltip, () => SetSortColumn(column));

    private void SortHeader(string text, float width, PeerSortColumn column, string tooltip) =>
        SortHeaderCore(text, width, _peerSortColumn.Equals(column), tooltip, () => SetSortColumn(column));

    private void SortHeader(string text, float width, RoutingErrorSortColumn column, string tooltip) =>
        SortHeaderCore(text, width, _routingErrorSortColumn.Equals(column), tooltip, () => SetSortColumn(column));

    private void SortHeaderCore(string text, float width, bool active, string tooltip, Action setSort)
    {
        GUIStyle style = active ? _activeHeaderStyle : _headerStyle;
        if (GUILayout.Button(new GUIContent(text, tooltip + " Click to sort descending."), style, GUILayout.Width(width)))
            setSort();
    }

    private void EnsureStyles()
    {
        if (_styleSkin == GUI.skin && _labelStyle != null)
            return;

        _styleSkin = GUI.skin;
        _labelStyle = new GUIStyle(GUI.skin.label)
        {
            wordWrap = false,
            clipping = TextClipping.Clip,
            alignment = TextAnchor.MiddleLeft
        };
        _labelStyle.normal.textColor = _theme.TextColor;

        _headerStyle = new GUIStyle(_labelStyle)
        {
            fontStyle = FontStyle.Bold
        };
        _headerStyle.normal.textColor = _theme.HeaderTextColor;

        _activeHeaderStyle = new GUIStyle(_headerStyle);
        Color activeColor = Color.Lerp(_theme.HeaderTextColor, _theme.AccentColor, 0.5f);
        _activeHeaderStyle.normal.textColor = activeColor;
        _activeHeaderStyle.hover.textColor = activeColor;
        _activeHeaderStyle.active.textColor = activeColor;
        _activeHeaderStyle.focused.textColor = activeColor;

        _detailsStyle = new GUIStyle(GUI.skin.label)
        {
            wordWrap = true,
            clipping = TextClipping.Clip,
            alignment = TextAnchor.UpperLeft
        };
        _detailsStyle.normal.textColor = _theme.TextColor;
    }

    private void Cell(string text, float width) => GUILayout.Label(text ?? string.Empty, _labelStyle, GUILayout.Width(width));
    private void HeaderLabel(string text) => GUILayout.Label(text, _headerStyle);
    private void Body(string text) => GUILayout.Label(text ?? string.Empty, _detailsStyle);
}
