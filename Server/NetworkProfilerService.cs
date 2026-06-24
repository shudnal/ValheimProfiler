#nullable disable

using BepInEx.Bootstrap;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace ValheimProfiler.Server;

internal sealed class NetworkProfilerService
{
    private const int MaxSubscribers = 8;
    private const int MaxRpcRows = 600;
    private const int MaxZdoRows = 400;
    private const int MaxZdoInstanceRows = 500;
    private const int MaxTrackedZdoInstancesPerInterval = 10000;
    private const int MaxKeyRows = 400;
    private const int MaxTrackedZdoKeyAggregates = 4096;
    private const int MaxErrorRows = 400;
    private const int MaxTrackedRoutingErrors = 2048;
    private const int MaxKnownKeyNames = 4096;
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SubscriberValidationInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RegistryScanInterval = TimeSpan.FromSeconds(15);

    private sealed class Subscriber
    {
        internal long PeerId;
    }

    private sealed class SeenRpcHandler { }

    internal sealed class RpcRegistrationInfo
    {
        internal string Layer = string.Empty;
        internal string Name = string.Empty;
        internal int Hash;
        internal string Component = string.Empty;
        internal string Handler = string.Empty;
        internal string Mod = string.Empty;
        internal string Prefab = string.Empty;
        internal long Registrations;
        internal string Key = string.Empty;
    }

    private sealed class RpcAggregate
    {
        internal RpcRegistrationInfo Registration = new();
        internal long IncomingCalls;
        internal long LocalCalls;
        internal long OutgoingCalls;
        internal long IncomingBytes;
        internal long LocalBytes;
        internal long OutgoingBytes;
        internal long PhysicalSends;
        internal long PhysicalBytes;
        internal double HandlerMs;
        internal double MaxHandlerMs;
        internal int MaxPayloadBytes;
        internal long Errors;
        internal int Frame = -1;
        internal int CallsThisFrame;
        internal int MaxCallsPerFrame;

        internal void CountFrame(int frame)
        {
            if (Frame != frame)
            {
                Frame = frame;
                CallsThisFrame = 0;
            }
            CallsThisFrame++;
            if (CallsThisFrame > MaxCallsPerFrame)
                MaxCallsPerFrame = CallsThisFrame;
        }

        internal void ResetInterval()
        {
            IncomingCalls = 0L;
            LocalCalls = 0L;
            OutgoingCalls = 0L;
            IncomingBytes = 0L;
            LocalBytes = 0L;
            OutgoingBytes = 0L;
            PhysicalSends = 0L;
            PhysicalBytes = 0L;
            HandlerMs = 0d;
            MaxHandlerMs = 0d;
            MaxPayloadBytes = 0;
            Errors = 0L;
            Frame = -1;
            CallsThisFrame = 0;
            MaxCallsPerFrame = 0;
        }
    }

    private sealed class ZdoAggregate
    {
        internal int PrefabHash;
        internal string Prefab = string.Empty;
        internal long Mutations;
        internal readonly HashSet<ZDOID> UniqueChanged = new();
        internal long SentCount;
        internal long SentBytes;
        internal long ReceivedCount;
        internal long ReceivedBytes;
        internal double SerializeMs;
        internal double DeserializeMs;
        internal long SizeSamples;
        internal long SizeTotal;
        internal int MaxSize;
        internal long Creates;
        internal long Destroys;
        internal long OwnershipChanges;

        internal void ResetInterval()
        {
            Mutations = 0L;
            UniqueChanged.Clear();
            SentCount = 0L;
            SentBytes = 0L;
            ReceivedCount = 0L;
            ReceivedBytes = 0L;
            SerializeMs = 0d;
            DeserializeMs = 0d;
            SizeSamples = 0L;
            SizeTotal = 0L;
            MaxSize = 0;
            Creates = 0L;
            Destroys = 0L;
            OwnershipChanges = 0L;
        }
    }

    private readonly struct ZdoKeyId : IEquatable<ZdoKeyId>
    {
        internal readonly int PrefabHash;
        internal readonly int KeyHash;
        internal readonly string ValueType;

        internal ZdoKeyId(int prefabHash, int keyHash, string valueType)
        {
            PrefabHash = prefabHash;
            KeyHash = keyHash;
            ValueType = valueType ?? string.Empty;
        }

        public bool Equals(ZdoKeyId other) => PrefabHash == other.PrefabHash && KeyHash == other.KeyHash && string.Equals(ValueType, other.ValueType, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is ZdoKeyId other && Equals(other);
        public override int GetHashCode() => ((PrefabHash * 397) ^ KeyHash) * 397 ^ StringComparer.Ordinal.GetHashCode(ValueType);
    }

    private sealed class ZdoInstanceAggregate
    {
        internal ZDOID Id;
        internal int PrefabHash;
        internal string Prefab = string.Empty;
        internal long Owner;
        internal long Mutations;
        internal long SentCount;
        internal long SentBytes;
        internal long ReceivedCount;
        internal long ReceivedBytes;
        internal int MaxSize;
    }

    private sealed class ZdoKeyAggregate
    {
        internal int Hash;
        internal string Name = string.Empty;
        internal string ValueType = string.Empty;
        internal string Prefab = string.Empty;
        internal long Mutations;
        internal readonly HashSet<ZDOID> Affected = new();

        internal void ResetInterval()
        {
            Mutations = 0L;
            Affected.Clear();
        }
    }

    private sealed class PeerAggregate
    {
        internal long PeerId;
        internal int MaximumSendQueue;
        internal double SendZdoMs;
        internal double CreateSyncListMs;
        internal int ZdosSent;
        internal int SyncCandidates;
        internal int SyncSelected;
        internal int PreviousSentData;
        internal int PreviousRecvData;
        internal bool CountersInitialized;

        internal void ResetInterval()
        {
            SendZdoMs = 0d;
            CreateSyncListMs = 0d;
            ZdosSent = 0;
            SyncCandidates = 0;
            SyncSelected = 0;
        }
    }

    private sealed class RoutingErrorAggregate
    {
        internal NetworkRoutingErrorWireRow Row = new();
    }

    private readonly object _sync = new();
    private readonly Harmony _harmony;
    private readonly Dictionary<long, Subscriber> _subscribers = new();
    private readonly Dictionary<string, RpcAggregate> _rpc = new(StringComparer.Ordinal);
    private readonly Dictionary<int, List<RpcRegistrationInfo>> _rpcByHash = new();
    private readonly Dictionary<int, ZdoAggregate> _zdo = new();
    private readonly Dictionary<ZDOID, ZdoInstanceAggregate> _zdoInstances = new();
    private readonly Dictionary<ZdoKeyId, ZdoKeyAggregate> _zdoKeys = new();
    private readonly Dictionary<long, PeerAggregate> _peers = new();
    private readonly Dictionary<string, RoutingErrorAggregate> _errors = new(StringComparer.Ordinal);
    private readonly Dictionary<Assembly, string> _assemblyMods = new();
    private readonly Dictionary<int, string> _knownKeyNames = new();
    private readonly Dictionary<int, string> _knownRpcNames = new();
    private readonly Dictionary<int, string> _prefabNames = new();
    private readonly ConditionalWeakTable<object, SeenRpcHandler> _seenRpcHandlers = new();
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly List<string> _compatibilityWarnings = new();
    private string _compatibilitySummary = "Vanilla network stack detected.";
    private DateTime _nextSnapshotUtc;
    private DateTime _nextSubscriberValidationUtc;
    private DateTime _nextRegistryScanUtc;
    private long _snapshotSequence;
    private long _untrackedZdoKeyMutations;
    private long _collapsedRoutingErrors;
    private volatile bool _collecting;
    private bool _shutdown;

    internal NetworkProfilerService()
    {
        BuildAssemblyMap();
        BuildKnownZdoKeyMap();
        DetectNetworkMods();
        _harmony = new Harmony(ValheimProfilerPlugin.PluginGuid + ".NetworkProfiler.Server");
        try
        {
            NetworkProfilerInstrumentation.Install(this, _harmony);
        }
        catch (Exception ex)
        {
            ReportInstrumentationWarning("Network instrumentation initialization failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    internal string SessionId => _sessionId;
    internal bool IsCollecting => _collecting;

    internal void Update()
    {
        if (_shutdown)
            return;

        DateTime now = DateTime.UtcNow;
        if (now >= _nextSubscriberValidationUtc)
        {
            _nextSubscriberValidationUtc = now + SubscriberValidationInterval;
            ValidateSubscribers();
        }

        if (_subscribers.Count > 0 && now >= _nextRegistryScanUtc)
        {
            _nextRegistryScanUtc = now + RegistryScanInterval;
            ScanRpcRegistrations();
        }

        if (now < _nextSnapshotUtc)
            return;

        _nextSnapshotUtc = now + SnapshotInterval;
        if (_subscribers.Count == 0)
            return;

        NetworkProfilerResponse response = BuildSnapshot("Dedicated-server network snapshot.", includeRegistry: false);
        foreach (long peerId in _subscribers.Keys.ToArray())
            Send(peerId, response);
    }

    internal void HandleRequest(long sender, ZPackage envelope)
    {
        try
        {
            NetworkProfilerTransportReceiveResult result = NetworkProfilerTransport.TryReceive(sender, envelope, out ZPackage payload, out string transportError);
            if (result == NetworkProfilerTransportReceiveResult.WaitingForFragments)
                return;
            if (result == NetworkProfilerTransportReceiveResult.Rejected)
            {
                SendError(sender, "Invalid Network Profiler transport payload: " + transportError);
                return;
            }
            if (payload.Size() > NetworkProfilerProtocol.MaxRequestPayloadBytes)
            {
                SendError(sender, "Oversized Network Profiler request.");
                return;
            }

            NetworkProfilerProtocol.ReadRequest(payload, out int version, out NetworkProfilerRequestKind kind);
            if (version != NetworkProfilerProtocol.Version)
            {
                SendError(sender, $"Unsupported Network Profiler protocol {version}; server uses {NetworkProfilerProtocol.Version}.");
                return;
            }

            if (kind == NetworkProfilerRequestKind.Probe)
            {
                SendCapabilities(sender);
                return;
            }

            if (!IsAdmin(sender))
            {
                _subscribers.Remove(sender);
                _collecting = _subscribers.Count > 0;
                SendError(sender, "Network Profiler access denied. The connected peer is not a server administrator.");
                return;
            }

            switch (kind)
            {
                case NetworkProfilerRequestKind.Subscribe:
                    if (!_subscribers.ContainsKey(sender) && _subscribers.Count >= MaxSubscribers)
                    {
                        SendError(sender, $"Network Profiler subscriber limit ({MaxSubscribers}) reached.");
                        return;
                    }
                    bool wasCollecting = _collecting;
                    _subscribers[sender] = new Subscriber { PeerId = sender };
                    BuildAssemblyMap();
                    DetectNetworkMods();
                    ScanRpcRegistrations();
                    if (!wasCollecting)
                        ResetIntervalMetrics();
                    _collecting = true;
                    if (!wasCollecting)
                        InitializePeerCounters();
                    _nextSnapshotUtc = DateTime.UtcNow + SnapshotInterval;
                    Send(sender, BuildSnapshot(
                        "Subscribed to the dedicated-server Network Profiler. The first complete one-second interval follows.",
                        includeRegistry: true,
                        includeDynamic: false,
                        consumeInterval: false));
                    break;
                case NetworkProfilerRequestKind.Unsubscribe:
                    _subscribers.Remove(sender);
                    _collecting = _subscribers.Count > 0;
                    SendStatus(sender, "Unsubscribed from Network Profiler updates.");
                    break;
                case NetworkProfilerRequestKind.Reset:
                    ResetMetrics();
                    InitializePeerCounters();
                    Send(sender, BuildSnapshot("Network Profiler interval and error metrics were reset.", includeRegistry: true, includeDynamic: false, consumeInterval: false));
                    break;
                default:
                    SendError(sender, $"Unknown Network Profiler request {kind}.");
                    break;
            }
        }
        catch (Exception ex)
        {
            SendError(sender, "Invalid Network Profiler request: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    internal void OnNetworkDestroyed()
    {
        _subscribers.Clear();
        _collecting = false;
    }

    internal void Shutdown()
    {
        if (_shutdown)
            return;
        _shutdown = true;
        _subscribers.Clear();
        _collecting = false;
        NetworkProfilerInstrumentation.Uninstall(this);
        try { _harmony.UnpatchSelf(); } catch { }
    }

    internal void ReportInstrumentationWarning(string warning)
    {
        if (string.IsNullOrWhiteSpace(warning))
            return;
        lock (_sync)
        {
            string full = "Instrumentation: " + warning;
            if (!_compatibilityWarnings.Contains(full))
                _compatibilityWarnings.Add(full);
            if (_compatibilitySummary.IndexOf("instrumentation warning", StringComparison.OrdinalIgnoreCase) < 0)
                _compatibilitySummary += " One or more instrumentation warnings were detected; see Help.";
        }
    }

    internal string ResolveMod(Assembly assembly)
    {
        if (assembly == null)
            return string.Empty;
        lock (_sync)
        {
            if (_assemblyMods.TryGetValue(assembly, out string mod))
                return mod;
        }
        return assembly.GetName().Name ?? string.Empty;
    }

    internal void RegisterRpc(object handlerObject, string layer, string name, Delegate handler, string prefab, int? explicitHash = null)
    {
        if (string.IsNullOrEmpty(name))
            return;

        int hash = explicitHash ?? name.GetStableHashCode();
        lock (_sync) _knownRpcNames[hash] = name;
        if (IsInternalProfilerRpcName(name) || IsInternalProfilerRpcHash(hash) || IsRoutedCarrier(layer, name))
            return;

        if (handlerObject != null)
        {
            lock (_sync)
            {
                if (_seenRpcHandlers.TryGetValue(handlerObject, out _))
                    return;
                _seenRpcHandlers.Add(handlerObject, new SeenRpcHandler());
            }
        }

        Type targetType = handler?.Target?.GetType();
        Type componentType = handler?.Target is Component
            ? targetType
            : handler?.Method?.DeclaringType ?? targetType;
        string component = componentType?.FullName ?? string.Empty;
        string handlerName = handler?.Method == null ? string.Empty : $"{handler.Method.DeclaringType?.FullName}.{handler.Method.Name}";
        string mod = ResolveMod(handler?.Method?.DeclaringType?.Assembly ?? componentType?.Assembly);
        var registration = new RpcRegistrationInfo
        {
            Layer = layer ?? string.Empty,
            Name = name,
            Hash = hash,
            Component = component,
            Handler = handlerName,
            Mod = mod,
            Prefab = prefab ?? string.Empty,
            Registrations = 1L
        };
        registration.Key = RpcKey(registration);

        lock (_sync)
        {
            string key = registration.Key;
            if (!_rpc.TryGetValue(key, out RpcAggregate aggregate))
            {
                aggregate = new RpcAggregate { Registration = registration };
                _rpc[key] = aggregate;
            }
            else
            {
                aggregate.Registration.Registrations++;
            }

            if (!_rpcByHash.TryGetValue(hash, out List<RpcRegistrationInfo> list))
            {
                list = new List<RpcRegistrationInfo>();
                _rpcByHash[hash] = list;
            }
            if (!list.Any(item => item.Layer == registration.Layer && item.Handler == registration.Handler && item.Prefab == registration.Prefab))
                list.Add(registration);
        }

        NetworkProfilerInstrumentation.BindHandler(handlerObject, registration);
    }

    internal void RecordRpcHandler(RpcRegistrationInfo registration, long sender, int payloadBytes, double elapsedMs)
    {
        if (!_collecting) return;
        if (registration == null)
            return;
        lock (_sync)
        {
            RpcAggregate aggregate = GetRpcAggregate(registration);
            bool local = sender != 0L && ZRoutedRpc.instance != null && sender == ZRoutedRpc.instance.m_id;
            if (local)
            {
                aggregate.LocalCalls++;
                aggregate.LocalBytes += Math.Max(0, payloadBytes);
            }
            else
            {
                aggregate.IncomingCalls++;
                aggregate.IncomingBytes += Math.Max(0, payloadBytes);
            }
            aggregate.HandlerMs += Math.Max(0d, elapsedMs);
            if (elapsedMs > aggregate.MaxHandlerMs) aggregate.MaxHandlerMs = elapsedMs;
            if (payloadBytes > aggregate.MaxPayloadBytes) aggregate.MaxPayloadBytes = payloadBytes;
            aggregate.CountFrame(Time.frameCount);
        }
    }

    internal void RecordRpcHandlerException(RpcRegistrationInfo registration, long sender, Exception exception)
    {
        if (!_collecting || registration == null || exception == null)
            return;
        Exception reported = exception is TargetInvocationException invocation && invocation.InnerException != null
            ? invocation.InnerException
            : exception;
        string details = reported.GetType().FullName + ": " + reported.Message;
        RecordRoutingError(
            "Handler exception",
            registration.Hash,
            sender,
            ZDOID.None,
            details,
            registration.Component,
            registration.Handler,
            registration.Prefab);
    }

    internal void RecordDirectOutgoing(string name, int payloadBytes, ZRpc rpc)
    {
        if (!_collecting) return;
        if (IsInternalProfilerRpcName(name))
            return;
        var registration = FindRegistration(name?.GetStableHashCode() ?? 0, "Direct");
        if (registration == null)
            registration = UnknownRegistration("Direct", name, string.Empty);
        lock (_sync)
        {
            RpcAggregate aggregate = GetRpcAggregate(registration);
            aggregate.OutgoingCalls++;
            aggregate.OutgoingBytes += Math.Max(0, payloadBytes);
            aggregate.PhysicalSends++;
            aggregate.PhysicalBytes += Math.Max(0, payloadBytes);
            if (payloadBytes > aggregate.MaxPayloadBytes) aggregate.MaxPayloadBytes = payloadBytes;
            aggregate.CountFrame(Time.frameCount);
        }
    }

    internal void RecordRoutedOutgoing(int hash, int logicalBytes, int physicalSends, int physicalBytes, ZDOID targetZdo)
    {
        if (!_collecting) return;
        if (IsInternalProfilerRpcHash(hash))
            return;
        string layer = targetZdo.IsNone() ? "Global" : "Object";
        string prefab = targetZdo.IsNone() ? string.Empty : ResolveTargetPrefabName(targetZdo);
        RpcRegistrationInfo registration = FindRegistration(hash, layer, prefab) ??
                                           FindRegistration(hash, layer) ??
                                           FindRegistration(hash, null) ??
                                           UnknownRegistration(layer, ResolveRpcName(hash), prefab, hash);
        lock (_sync)
        {
            RpcAggregate aggregate = GetRpcAggregate(registration);
            aggregate.OutgoingCalls++;
            aggregate.OutgoingBytes += Math.Max(0, logicalBytes);
            aggregate.PhysicalSends += Math.Max(0, physicalSends);
            aggregate.PhysicalBytes += Math.Max(0, physicalBytes);
            if (logicalBytes > aggregate.MaxPayloadBytes) aggregate.MaxPayloadBytes = logicalBytes;
            aggregate.CountFrame(Time.frameCount);
        }
    }

    internal void RecordRoutingError(string kind, int hash, long peerId, ZDOID targetZdo, string details, string component = "", string handler = "", string prefab = "", string caller = "", string callerMod = "")
    {
        if (!_collecting) return;
        if (IsInternalProfilerRpcHash(hash))
            return;
        string rpcName = ResolveRpcName(hash);
        string peer = DescribePeer(peerId);
        string target = targetZdo.IsNone() ? string.Empty : targetZdo.ToString();
        RpcRegistrationInfo registration = FindRegistrationForError(hash, kind, targetZdo, component, handler, prefab);
        component = string.IsNullOrEmpty(component) ? registration?.Component ?? string.Empty : component;
        handler = string.IsNullOrEmpty(handler) ? registration?.Handler ?? string.Empty : handler;
        prefab = string.IsNullOrEmpty(prefab) ? registration?.Prefab ?? string.Empty : prefab;
        string key = kind + "|" + hash + "|" + peerId + "|" + target + "|" + component + "|" + handler + "|" + prefab + "|" + caller;
        lock (_sync)
        {
            if (!_errors.TryGetValue(key, out RoutingErrorAggregate aggregate))
            {
                if (_errors.Count >= MaxTrackedRoutingErrors)
                {
                    const string overflowKey = "__overflow__";
                    if (!_errors.TryGetValue(overflowKey, out aggregate))
                    {
                        aggregate = new RoutingErrorAggregate
                        {
                            Row = new NetworkRoutingErrorWireRow
                            {
                                Kind = "Tracking limit reached",
                                Rpc = "multiple",
                                Count = 0L,
                                LastDetails = "Additional unique routing-error identities were collapsed to keep memory bounded."
                            }
                        };
                        _errors[overflowKey] = aggregate;
                    }
                    _collapsedRoutingErrors++;
                    aggregate.Row.Count++;
                    aggregate.Row.LastDetails = $"Collapsed {_collapsedRoutingErrors} additional unique routing-error identities.";
                    RpcRegistrationInfo overflowRegistration = registration ?? UnknownRegistration("Unknown", rpcName, prefab, hash);
                    GetRpcAggregate(overflowRegistration).Errors++;
                    return;
                }

                aggregate = new RoutingErrorAggregate
                {
                    Row = new NetworkRoutingErrorWireRow
                    {
                        Kind = kind ?? string.Empty,
                        Rpc = rpcName,
                        MethodHash = hash,
                        Component = component,
                        Handler = handler,
                        Prefab = prefab,
                        Caller = caller ?? string.Empty,
                        CallerMod = callerMod ?? string.Empty,
                        PeerId = peerId,
                        Peer = peer,
                        Target = target,
                        Count = 0L
                    }
                };
                _errors[key] = aggregate;
            }
            aggregate.Row.Count++;
            aggregate.Row.LastDetails = details ?? string.Empty;

            RpcRegistrationInfo reg = registration ?? UnknownRegistration("Unknown", rpcName, prefab, hash);
            GetRpcAggregate(reg).Errors++;
        }
    }

    internal void RememberKeyName(int hash, string name)
    {
        if (!_collecting) return;
        if (string.IsNullOrEmpty(name)) return;
        lock (_sync)
        {
            if (_knownKeyNames.ContainsKey(hash) || _knownKeyNames.Count < MaxKnownKeyNames)
                _knownKeyNames[hash] = name;
        }
    }

    internal void RecordZdoMutation(ZDO zdo, int keyHash, string valueType)
    {
        if (!_collecting) return;
        if (zdo == null) return;
        int prefabHash = zdo.GetPrefab();
        string prefab = ResolvePrefabName(prefabHash);
        lock (_sync)
        {
            ZdoAggregate aggregate = GetZdoAggregate(prefabHash, prefab);
            aggregate.Mutations++;
            aggregate.UniqueChanged.Add(zdo.m_uid);
            ZdoInstanceAggregate instance = GetZdoInstanceAggregate(zdo, prefabHash, prefab);
            if (instance != null) instance.Mutations++;

            string keyName = keyHash == 0 ? valueType : (_knownKeyNames.TryGetValue(keyHash, out string known) ? known : keyHash.ToString());
            var key = new ZdoKeyId(prefabHash, keyHash, valueType);
            if (!_zdoKeys.TryGetValue(key, out ZdoKeyAggregate keyAggregate))
            {
                if (_zdoKeys.Count >= MaxTrackedZdoKeyAggregates)
                {
                    _untrackedZdoKeyMutations++;
                    return;
                }
                keyAggregate = new ZdoKeyAggregate { Hash = keyHash, Name = keyName, ValueType = valueType ?? string.Empty, Prefab = prefab };
                _zdoKeys[key] = keyAggregate;
            }
            keyAggregate.Mutations++;
            keyAggregate.Affected.Add(zdo.m_uid);
        }
    }

    internal void RecordZdoOwnershipChange(ZDO zdo)
    {
        if (!_collecting) return;
        if (zdo == null) return;
        lock (_sync) GetZdoAggregate(zdo.GetPrefab(), ResolvePrefabName(zdo.GetPrefab())).OwnershipChanges++;
    }

    internal void RecordZdoCreate(ZDO zdo)
    {
        if (!_collecting) return;
        if (zdo == null) return;
        lock (_sync) GetZdoAggregate(zdo.GetPrefab(), ResolvePrefabName(zdo.GetPrefab())).Creates++;
    }

    internal void RecordZdoDestroy(ZDO zdo)
    {
        if (!_collecting) return;
        if (zdo == null) return;
        lock (_sync) GetZdoAggregate(zdo.GetPrefab(), ResolvePrefabName(zdo.GetPrefab())).Destroys++;
    }

    internal void RecordZdoSerialize(ZDO zdo, int bytes, double elapsedMs, long peerId, bool receiving)
    {
        if (!_collecting) return;
        if (zdo == null) return;
        lock (_sync)
        {
            ZdoAggregate aggregate = GetZdoAggregate(zdo.GetPrefab(), ResolvePrefabName(zdo.GetPrefab()));
            ZdoInstanceAggregate instance = GetZdoInstanceAggregate(zdo, zdo.GetPrefab(), aggregate.Prefab);
            if (receiving)
            {
                aggregate.ReceivedCount++;
                aggregate.ReceivedBytes += Math.Max(0, bytes);
                aggregate.DeserializeMs += Math.Max(0d, elapsedMs);
                if (instance != null)
                {
                    instance.ReceivedCount++;
                    instance.ReceivedBytes += Math.Max(0, bytes);
                }
            }
            else
            {
                aggregate.SentCount++;
                aggregate.SentBytes += Math.Max(0, bytes);
                aggregate.SerializeMs += Math.Max(0d, elapsedMs);
                if (instance != null)
                {
                    instance.SentCount++;
                    instance.SentBytes += Math.Max(0, bytes);
                }
            }
            if (instance != null && bytes > instance.MaxSize) instance.MaxSize = bytes;
            aggregate.SizeSamples++;
            aggregate.SizeTotal += Math.Max(0, bytes);
            if (bytes > aggregate.MaxSize) aggregate.MaxSize = bytes;
        }
    }

    internal void RecordSendZdos(long peerId, double elapsedMs, int zdoCount, int queue)
    {
        if (!_collecting) return;
        lock (_sync)
        {
            PeerAggregate peer = GetPeerAggregate(peerId);
            peer.SendZdoMs += Math.Max(0d, elapsedMs);
            peer.ZdosSent += Math.Max(0, zdoCount);
            if (queue > peer.MaximumSendQueue) peer.MaximumSendQueue = queue;
        }
    }

    internal void RecordCreateSyncList(long peerId, double elapsedMs, int candidates, int selected)
    {
        if (!_collecting) return;
        lock (_sync)
        {
            PeerAggregate peer = GetPeerAggregate(peerId);
            peer.CreateSyncListMs += Math.Max(0d, elapsedMs);
            peer.SyncCandidates += Math.Max(0, candidates);
            peer.SyncSelected += Math.Max(0, selected);
        }
    }

    private static bool IsInternalProfilerRpcName(string name) =>
        !string.IsNullOrEmpty(name) && name.StartsWith(ValheimProfilerPlugin.PluginGuid + ".", StringComparison.Ordinal);

    private static bool IsRoutedCarrier(string layer, string name) =>
        string.Equals(layer, "Direct", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(name, "RoutedRPC", StringComparison.Ordinal);

    private bool IsInternalProfilerRpcHash(int hash)
    {
        if (hash == NetworkProfilerProtocol.RequestRpc.GetStableHashCode() ||
            hash == NetworkProfilerProtocol.ResponseRpc.GetStableHashCode() ||
            hash == ServerLogProtocol.RequestRpc.GetStableHashCode() ||
            hash == ServerLogProtocol.ResponseRpc.GetStableHashCode())
            return true;
        lock (_sync)
            return _knownRpcNames.TryGetValue(hash, out string name) && IsInternalProfilerRpcName(name);
    }

    internal string ResolveRpcName(int hash)
    {
        lock (_sync) return _knownRpcNames.TryGetValue(hash, out string name) ? name : "Unknown RPC";
    }

    internal RpcRegistrationInfo FindRegistration(int hash, string layer, string prefab = null)
    {
        lock (_sync)
        {
            if (!_rpcByHash.TryGetValue(hash, out List<RpcRegistrationInfo> list) || list.Count == 0)
                return null;

            if (!string.IsNullOrEmpty(layer) && !string.IsNullOrEmpty(prefab))
            {
                RpcRegistrationInfo exact = list.FirstOrDefault(item =>
                    string.Equals(item.Layer, layer, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Prefab, prefab, StringComparison.Ordinal));
                if (exact != null)
                    return exact;
            }

            if (string.IsNullOrEmpty(layer))
                return list[0];
            return list.FirstOrDefault(item => string.Equals(item.Layer, layer, StringComparison.OrdinalIgnoreCase)) ?? list[0];
        }
    }

    private RpcRegistrationInfo FindRegistrationForError(int hash, string kind, ZDOID targetZdo, string component, string handler, string prefab)
    {
        lock (_sync)
        {
            if (!_rpcByHash.TryGetValue(hash, out List<RpcRegistrationInfo> list) || list.Count == 0)
                return null;

            if (!string.IsNullOrEmpty(handler) || !string.IsNullOrEmpty(component) || !string.IsNullOrEmpty(prefab))
            {
                RpcRegistrationInfo exact = list.FirstOrDefault(item =>
                    (string.IsNullOrEmpty(handler) || string.Equals(item.Handler, handler, StringComparison.Ordinal)) &&
                    (string.IsNullOrEmpty(component) || string.Equals(item.Component, component, StringComparison.Ordinal)) &&
                    (string.IsNullOrEmpty(prefab) || string.Equals(item.Prefab, prefab, StringComparison.Ordinal)));
                if (exact != null)
                    return exact;
            }

            string layer = !targetZdo.IsNone()
                ? "Object"
                : kind?.IndexOf("Direct", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "Direct"
                    : "Global";
            return list.FirstOrDefault(item => string.Equals(item.Layer, layer, StringComparison.OrdinalIgnoreCase)) ?? list[0];
        }
    }

    private NetworkProfilerResponse BuildSnapshot(string status, bool includeRegistry, bool includeDynamic = true, bool consumeInterval = true)
    {
        var response = new NetworkProfilerResponse
        {
            Kind = NetworkProfilerResponseKind.Snapshot,
            SessionId = _sessionId,
            Status = status,
            ServerVersion = ValheimProfilerPlugin.PluginVersion,
            Authorized = true,
            SnapshotSequence = ++_snapshotSequence,
            FullRegistry = includeRegistry,
            CompatibilitySummary = _compatibilitySummary
        };
        response.CompatibilityWarnings.AddRange(_compatibilityWarnings);

        lock (_sync)
        {
            RpcAggregate[] rpcRows = _rpc.Values
                .Where(item => item.IncomingCalls + item.LocalCalls + item.OutgoingCalls + item.Errors > 0 || (includeRegistry && item.Registration.Registrations > 0))
                .OrderByDescending(item => item.HandlerMs + item.PhysicalBytes / 1024d)
                .Take(MaxRpcRows)
                .ToArray();
            foreach (RpcAggregate a in rpcRows)
            {
                long calls = a.IncomingCalls + a.LocalCalls;
                response.RpcRows.Add(new NetworkRpcWireRow
                {
                    Layer = a.Registration.Layer,
                    Name = _knownRpcNames.TryGetValue(a.Registration.Hash, out string knownRpcName) &&
                           !string.Equals(knownRpcName, "Unknown RPC", StringComparison.OrdinalIgnoreCase)
                        ? knownRpcName
                        : a.Registration.Name,
                    MethodHash = a.Registration.Hash, Component = a.Registration.Component,
                    Handler = a.Registration.Handler, Mod = a.Registration.Mod, Prefab = a.Registration.Prefab, Registrations = a.Registration.Registrations,
                    IncomingCalls = includeDynamic ? a.IncomingCalls : 0L, LocalCalls = includeDynamic ? a.LocalCalls : 0L, OutgoingCalls = includeDynamic ? a.OutgoingCalls : 0L,
                    IncomingBytes = includeDynamic ? a.IncomingBytes : 0L, LocalBytes = includeDynamic ? a.LocalBytes : 0L, OutgoingBytes = includeDynamic ? a.OutgoingBytes : 0L,
                    PhysicalSends = includeDynamic ? a.PhysicalSends : 0L,
                    PhysicalBytes = includeDynamic ? a.PhysicalBytes : 0L, HandlerMs = includeDynamic ? a.HandlerMs : 0d,
                    AverageHandlerMs = includeDynamic && calls > 0 ? a.HandlerMs / calls : 0d,
                    MaxHandlerMs = includeDynamic ? a.MaxHandlerMs : 0d, MaxPayloadBytes = includeDynamic ? a.MaxPayloadBytes : 0,
                    MaxCallsPerFrame = includeDynamic ? a.MaxCallsPerFrame : 0, Errors = includeDynamic ? a.Errors : 0L
                });
            }
            if (consumeInterval)
            {
                foreach (RpcAggregate a in _rpc.Values)
                    a.ResetInterval();
            }

            if (!includeDynamic)
                return response;

            ZdoAggregate[] zdoRows = _zdo.Values
                .Where(item => item.Mutations + item.SentCount + item.ReceivedCount + item.Creates + item.Destroys + item.OwnershipChanges > 0)
                .OrderByDescending(item => item.SentBytes + item.ReceivedBytes)
                .Take(MaxZdoRows)
                .ToArray();
            foreach (ZdoAggregate a in zdoRows)
            {
                response.ZdoRows.Add(new NetworkZdoWireRow
                {
                    PrefabHash = a.PrefabHash, Prefab = a.Prefab, Mutations = a.Mutations, UniqueChanged = a.UniqueChanged.Count,
                    SentCount = a.SentCount, SentBytes = a.SentBytes, ReceivedCount = a.ReceivedCount, ReceivedBytes = a.ReceivedBytes,
                    SerializeMs = a.SerializeMs, DeserializeMs = a.DeserializeMs, AverageSize = a.SizeSamples > 0 ? (int)Math.Min(int.MaxValue, a.SizeTotal / a.SizeSamples) : 0,
                    MaxSize = a.MaxSize, Creates = a.Creates, Destroys = a.Destroys, OwnershipChanges = a.OwnershipChanges
                });
            }
            if (consumeInterval)
            {
                foreach (ZdoAggregate a in _zdo.Values)
                    a.ResetInterval();
            }

            foreach (ZdoInstanceAggregate a in _zdoInstances.Values
                         .OrderByDescending(item => item.SentBytes + item.ReceivedBytes + item.Mutations * 16L)
                         .Take(MaxZdoInstanceRows))
            {
                response.ZdoInstanceRows.Add(new NetworkZdoInstanceWireRow
                {
                    ZdoId = a.Id.ToString(), PrefabHash = a.PrefabHash, Prefab = a.Prefab, Owner = a.Owner, Mutations = a.Mutations,
                    SentCount = a.SentCount, SentBytes = a.SentBytes, ReceivedCount = a.ReceivedCount, ReceivedBytes = a.ReceivedBytes, MaxSize = a.MaxSize
                });
            }
            if (consumeInterval)
                _zdoInstances.Clear();

            ZdoKeyAggregate[] keyRows = _zdoKeys.Values
                .Where(item => item.Mutations > 0)
                .OrderByDescending(item => item.Mutations)
                .Take(MaxKeyRows)
                .ToArray();
            foreach (ZdoKeyAggregate a in keyRows)
            {
                response.ZdoKeyRows.Add(new NetworkZdoKeyWireRow
                {
                    KeyHash = a.Hash, KeyName = a.Name, ValueType = a.ValueType, Prefab = a.Prefab, Mutations = a.Mutations, AffectedZdos = a.Affected.Count
                });
            }
            if (_untrackedZdoKeyMutations > 0L)
            {
                response.ZdoKeyRows.Add(new NetworkZdoKeyWireRow
                {
                    KeyName = "(tracking limit reached)",
                    ValueType = "Multiple",
                    Prefab = "Multiple",
                    Mutations = _untrackedZdoKeyMutations
                });
            }
            if (consumeInterval)
            {
                foreach (ZdoKeyAggregate a in _zdoKeys.Values)
                    a.ResetInterval();
                _untrackedZdoKeyMutations = 0L;
            }

            SamplePeers(response, consumeInterval);

            foreach (RoutingErrorAggregate a in _errors.Values.OrderByDescending(item => item.Row.Count).Take(MaxErrorRows))
            {
                if (_knownRpcNames.TryGetValue(a.Row.MethodHash, out string knownRpcName) &&
                    !string.Equals(knownRpcName, "Unknown RPC", StringComparison.OrdinalIgnoreCase))
                    a.Row.Rpc = knownRpcName;
                response.ErrorRows.Add(a.Row);
            }
        }

        return response;
    }

    private void SamplePeers(NetworkProfilerResponse response, bool consumeInterval)
    {
        List<ZNetPeer> peers;
        try { peers = ZNet.instance?.GetPeers(); } catch { peers = null; }
        if (peers == null) return;

        for (int i = 0; i < peers.Count; i++)
        {
            ZNetPeer peer = peers[i];
            if (peer?.m_socket == null || peer.m_rpc == null) continue;
            long id = peer.m_uid;
            PeerAggregate aggregate = GetPeerAggregate(id);
            int serializedOut = 0;
            int serializedIn = 0;
            if (aggregate.CountersInitialized)
            {
                serializedOut = Math.Max(0, peer.m_rpc.m_sentData - aggregate.PreviousSentData);
                serializedIn = Math.Max(0, peer.m_rpc.m_recvData - aggregate.PreviousRecvData);
            }
            aggregate.PreviousSentData = peer.m_rpc.m_sentData;
            aggregate.PreviousRecvData = peer.m_rpc.m_recvData;
            aggregate.CountersInitialized = true;

            int queue = Safe(() => peer.m_socket.GetSendQueueSize(), -1);
            if (queue > aggregate.MaximumSendQueue) aggregate.MaximumSendQueue = queue;
            float localQuality = 0f, remoteQuality = 0f, outRate = 0f, inRate = 0f;
            int ping = 0;
            try { peer.m_socket.GetConnectionQuality(out localQuality, out remoteQuality, out ping, out outRate, out inRate); } catch { }

            response.PeerRows.Add(new NetworkPeerWireRow
            {
                PeerId = id, Name = peer.m_playerName ?? string.Empty, Host = Safe(() => peer.m_socket.GetHostName(), string.Empty),
                Socket = DescribeSocket(peer.m_socket), Ready = peer.IsReady(), Ping = ping, LocalQuality = localQuality, RemoteQuality = remoteQuality,
                ReportedOutBytesPerSecond = Math.Max(0f, outRate), ReportedInBytesPerSecond = Math.Max(0f, inRate), SerializedOutBytesPerSecond = serializedOut,
                SerializedInBytesPerSecond = serializedIn, SendQueue = queue, MaximumSendQueue = Math.Max(queue, aggregate.MaximumSendQueue),
                CurrentSendRate = Safe(() => peer.m_socket.GetCurrentSendRate(), 0), ActualInFlightBytes = TryGetActualInFlight(peer.m_socket),
                LastReceiveAge = Math.Max(0f, peer.m_rpc.GetTimeSinceLastPing()), SendZdoMs = aggregate.SendZdoMs, CreateSyncListMs = aggregate.CreateSyncListMs,
                ZdosSent = aggregate.ZdosSent, SyncCandidates = aggregate.SyncCandidates, SyncSelected = aggregate.SyncSelected
            });
            if (consumeInterval) aggregate.ResetInterval();
        }
    }

    private void SendCapabilities(long target)
    {
        bool authorized = IsAdmin(target);
        Send(target, CreateStateResponse(
            NetworkProfilerResponseKind.Capabilities,
            target,
            authorized ? "Dedicated-server Network Profiler is available. Subscribe to start private admin snapshots." : "Valheim Profiler is installed, but the connected peer is not a server administrator."));
    }

    private void SendStatus(long target, string status) =>
        Send(target, CreateStateResponse(NetworkProfilerResponseKind.Status, target, status));

    private void SendError(long target, string status) =>
        Send(target, CreateStateResponse(NetworkProfilerResponseKind.Error, target, status));

    private NetworkProfilerResponse CreateStateResponse(NetworkProfilerResponseKind kind, long target, string status)
    {
        var response = new NetworkProfilerResponse
        {
            Kind = kind,
            SessionId = _sessionId,
            Status = status ?? string.Empty,
            ServerVersion = ValheimProfilerPlugin.PluginVersion,
            Authorized = IsAdmin(target),
            SnapshotSequence = _snapshotSequence,
            CompatibilitySummary = _compatibilitySummary
        };
        response.CompatibilityWarnings.AddRange(_compatibilityWarnings);
        return response;
    }

    private static bool Send(long target, NetworkProfilerResponse response)
    {
        try
        {
            if (!TryCreateBoundedPayload(response, out ZPackage payload))
                return false;
            return NetworkProfilerTransport.Send(target, NetworkProfilerProtocol.ResponseRpc, payload, out _);
        }
        catch { return false; }
    }

    private static bool TryCreateBoundedPayload(NetworkProfilerResponse response, out ZPackage payload)
    {
        payload = NetworkProfilerProtocol.CreateResponse(response);
        if (payload.Size() <= NetworkProfilerProtocol.MaxResponsePayloadBytes)
            return true;

        const string trimWarning = "Snapshot rows were trimmed to fit the Network Profiler payload limit.";
        if (!response.CompatibilityWarnings.Contains(trimWarning))
            response.CompatibilityWarnings.Add(trimWarning);

        for (int pass = 0; pass < 128; pass++)
        {
            if (!TrimLowestPriorityRows(response))
                return false;

            payload = NetworkProfilerProtocol.CreateResponse(response);
            if (payload.Size() <= NetworkProfilerProtocol.MaxResponsePayloadBytes)
                return true;
        }

        return false;
    }

    private static bool TrimLowestPriorityRows(NetworkProfilerResponse response)
    {
        if (TrimTail(response.ZdoInstanceRows, 100)) return true;
        if (TrimTail(response.ZdoKeyRows, 100)) return true;
        if (TrimTail(response.RpcRows, 100)) return true;
        if (TrimTail(response.ZdoRows, 100)) return true;
        if (TrimTail(response.ErrorRows, 100)) return true;
        if (TrimTail(response.ZdoInstanceRows, 0)) return true;
        if (TrimTail(response.ZdoKeyRows, 0)) return true;
        if (TrimTail(response.RpcRows, 0)) return true;
        if (TrimTail(response.ZdoRows, 0)) return true;
        if (TrimTail(response.ErrorRows, 0)) return true;
        if (response.CompatibilityWarnings.Count > 1)
        {
            response.CompatibilityWarnings.RemoveAt(response.CompatibilityWarnings.Count - 2);
            return true;
        }
        return TrimTail(response.PeerRows, 0);
    }

    private static bool TrimTail<T>(List<T> rows, int minimumToKeep)
    {
        if (rows == null || rows.Count <= minimumToKeep)
            return false;

        int removable = rows.Count - minimumToKeep;
        int removeCount = Math.Max(1, Math.Min(removable, Math.Max(1, rows.Count / 4)));
        rows.RemoveRange(rows.Count - removeCount, removeCount);
        return true;
    }

    private void ValidateSubscribers()
    {
        foreach (long peerId in _subscribers.Keys.ToArray())
        {
            ZNetPeer peer = ZRoutedRpc.instance?.GetPeer(peerId);
            if (peer == null || !peer.IsReady() || !IsAdmin(peerId))
                _subscribers.Remove(peerId);
        }
        _collecting = _subscribers.Count > 0;
    }

    private static bool IsAdmin(long sender)
    {
        try
        {
            ZNetPeer peer = ZRoutedRpc.instance?.GetPeer(sender);
            string host = peer?.m_socket?.GetHostName();
            return ZNet.instance != null && !string.IsNullOrEmpty(host) && ZNet.instance.IsAdmin(host);
        }
        catch { return false; }
    }

    private void InitializePeerCounters()
    {
        List<ZNetPeer> peers;
        try { peers = ZNet.instance?.GetPeers(); } catch { peers = null; }
        if (peers == null)
            return;

        lock (_sync)
        {
            for (int i = 0; i < peers.Count; i++)
            {
                ZNetPeer peer = peers[i];
                if (peer?.m_rpc == null)
                    continue;
                PeerAggregate aggregate = GetPeerAggregate(peer.m_uid);
                aggregate.PreviousSentData = peer.m_rpc.m_sentData;
                aggregate.PreviousRecvData = peer.m_rpc.m_recvData;
                aggregate.CountersInitialized = true;
            }
        }
    }

    private void ResetIntervalMetrics()
    {
        lock (_sync)
        {
            foreach (RpcAggregate a in _rpc.Values) a.ResetInterval();
            foreach (ZdoAggregate a in _zdo.Values) a.ResetInterval();
            foreach (ZdoKeyAggregate a in _zdoKeys.Values) a.ResetInterval();
            _zdoInstances.Clear();
            foreach (PeerAggregate a in _peers.Values) a.ResetInterval();
        }
    }

    private void ResetMetrics()
    {
        ResetIntervalMetrics();
        lock (_sync)
        {
            foreach (PeerAggregate a in _peers.Values) a.MaximumSendQueue = 0;
            _errors.Clear();
            _collapsedRoutingErrors = 0L;
        }
    }

    private RpcAggregate GetRpcAggregate(RpcRegistrationInfo registration)
    {
        string key = string.IsNullOrEmpty(registration.Key) ? (registration.Key = RpcKey(registration)) : registration.Key;
        if (!_rpc.TryGetValue(key, out RpcAggregate aggregate))
        {
            aggregate = new RpcAggregate { Registration = registration };
            _rpc[key] = aggregate;
        }
        return aggregate;
    }

    private static string RpcKey(RpcRegistrationInfo r) => r.Layer + "|" + r.Hash + "|" + r.Component + "|" + r.Handler + "|" + r.Prefab;

    private RpcRegistrationInfo UnknownRegistration(string layer, string name, string prefab, int? explicitHash = null)
    {
        var registration = new RpcRegistrationInfo
        {
            Layer = layer ?? "Unknown",
            Name = string.IsNullOrEmpty(name) ? "unknown" : name,
            Hash = explicitHash ?? (string.IsNullOrEmpty(name) ? 0 : name.GetStableHashCode()),
            Component = string.Empty,
            Handler = string.Empty,
            Mod = string.Empty,
            Prefab = prefab ?? string.Empty,
            Registrations = 0L
        };
        registration.Key = RpcKey(registration);
        return registration;
    }

    private ZdoAggregate GetZdoAggregate(int hash, string name)
    {
        if (!_zdo.TryGetValue(hash, out ZdoAggregate aggregate))
        {
            aggregate = new ZdoAggregate { PrefabHash = hash, Prefab = name ?? hash.ToString() };
            _zdo[hash] = aggregate;
        }
        return aggregate;
    }

    private ZdoInstanceAggregate GetZdoInstanceAggregate(ZDO zdo, int prefabHash, string prefab)
    {
        if (zdo == null) return null;
        if (_zdoInstances.TryGetValue(zdo.m_uid, out ZdoInstanceAggregate aggregate))
        {
            aggregate.Owner = zdo.GetOwner();
            return aggregate;
        }
        if (_zdoInstances.Count >= MaxTrackedZdoInstancesPerInterval)
            return null;
        aggregate = new ZdoInstanceAggregate
        {
            Id = zdo.m_uid,
            PrefabHash = prefabHash,
            Prefab = prefab ?? prefabHash.ToString(),
            Owner = zdo.GetOwner()
        };
        _zdoInstances[zdo.m_uid] = aggregate;
        return aggregate;
    }

    private PeerAggregate GetPeerAggregate(long peerId)
    {
        if (!_peers.TryGetValue(peerId, out PeerAggregate aggregate))
        {
            aggregate = new PeerAggregate { PeerId = peerId };
            _peers[peerId] = aggregate;
        }
        return aggregate;
    }

    private string ResolvePrefabName(int hash)
    {
        lock (_sync)
        {
            if (_prefabNames.TryGetValue(hash, out string cached)) return cached;
        }
        string name = hash == 0 ? "None" : hash.ToString();
        try
        {
            GameObject prefab = ZNetScene.instance?.GetPrefab(hash);
            if (prefab != null) name = prefab.name;
        }
        catch { }
        lock (_sync) _prefabNames[hash] = name;
        return name;
    }

    private string ResolveTargetPrefabName(ZDOID targetZdo)
    {
        if (targetZdo.IsNone())
            return string.Empty;
        try
        {
            ZDO zdo = ZDOMan.instance?.GetZDO(targetZdo);
            return zdo == null ? string.Empty : ResolvePrefabName(zdo.GetPrefab());
        }
        catch
        {
            return string.Empty;
        }
    }

    private string DescribePeer(long peerId)
    {
        try
        {
            ZNetPeer peer = ZRoutedRpc.instance?.GetPeer(peerId);
            if (peer == null) return peerId.ToString();
            string name = string.IsNullOrEmpty(peer.m_playerName) ? "unnamed" : peer.m_playerName;
            string host = peer.m_socket?.GetHostName() ?? string.Empty;
            return $"{name} ({peerId}, {host})";
        }
        catch { return peerId.ToString(); }
    }

    private void ScanRpcRegistrations()
    {
        try
        {
            ZRoutedRpc routed = ZRoutedRpc.instance;
            if (routed != null)
            {
                foreach (KeyValuePair<int, RoutedMethodBase> pair in routed.m_functions)
                    RegisterScanned(pair.Value, "Global", pair.Key, string.Empty);

                foreach (ZNetPeer peer in routed.m_peers)
                {
                    if (peer?.m_rpc?.m_functions == null) continue;
                    foreach (KeyValuePair<int, ZRpc.RpcMethodBase> pair in peer.m_rpc.m_functions)
                        RegisterScanned(pair.Value, "Direct", pair.Key, string.Empty);
                }
            }

            ZNetScene scene = ZNetScene.instance;
            if (scene != null)
            {
                foreach (ZNetView view in scene.m_instances.Values)
                {
                    if (view?.m_functions == null) continue;
                    string prefab = string.Empty;
                    try { prefab = view.GetPrefabName() ?? string.Empty; } catch { }
                    foreach (KeyValuePair<int, RoutedMethodBase> pair in view.m_functions)
                        RegisterScanned(pair.Value, "Object", pair.Key, prefab);
                }
            }
        }
        catch
        {
        }
    }

    private bool IsRpcHandlerSeen(object handlerObject)
    {
        if (handlerObject == null)
            return false;
        lock (_sync)
            return _seenRpcHandlers.TryGetValue(handlerObject, out _);
    }

    private void RegisterScanned(object wrapper, string layer, int hash, string prefab)
    {
        if (wrapper == null || IsRpcHandlerSeen(wrapper)) return;
        Delegate action = null;
        try
        {
            FieldInfo actionField = wrapper.GetType().GetField("m_action", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            action = actionField?.GetValue(wrapper) as Delegate;
        }
        catch { }
        string name = ResolveRpcName(hash);
        RegisterRpc(wrapper, layer, name, action, prefab, hash);
    }

    private void BuildKnownZdoKeyMap()
    {
        try
        {
            FieldInfo[] fields = typeof(ZDOVars).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (field.FieldType == typeof(int))
                {
                    int hash = (int)field.GetValue(null);
                    if (!_knownKeyNames.ContainsKey(hash) && _knownKeyNames.Count < MaxKnownKeyNames)
                        _knownKeyNames[hash] = field.Name;
                }
                else if (field.FieldType == typeof(KeyValuePair<int, int>))
                {
                    KeyValuePair<int, int> pair = (KeyValuePair<int, int>)field.GetValue(null);
                    if (!_knownKeyNames.ContainsKey(pair.Key) && _knownKeyNames.Count < MaxKnownKeyNames)
                        _knownKeyNames[pair.Key] = field.Name + "_u";
                    if (!_knownKeyNames.ContainsKey(pair.Value) && _knownKeyNames.Count < MaxKnownKeyNames)
                        _knownKeyNames[pair.Value] = field.Name + "_i";
                }
            }
        }
        catch
        {
        }
    }

    private void BuildAssemblyMap()
    {
        foreach (var pair in Chainloader.PluginInfos)
        {
            try
            {
                Assembly assembly = pair.Value?.Instance?.GetType().Assembly;
                if (assembly != null) _assemblyMods[assembly] = pair.Key;
            }
            catch { }
        }
    }

    private void DetectNetworkMods()
    {
        var detected = new List<string>();
        // Keep the most invasive compatibility warning first because the window surfaces the first warning on every tab.
        AddCompatibility("com.Fire.FiresGhettoNetworkMod", "VAGhettoNetworking", "Experimental compatibility: custom routed-RPC processing, ZDO delta serialization, ownership and throttling can bypass vanilla paths. RPC registrations, handler timing, physical traffic and peer queues remain useful; ZDO byte/candidate columns may be partial.", detected);
        AddCompatibility("VitByr.VBNetTweaks", "VBNetTweaks", "Partial compatibility: custom routed-RPC filtering changes broadcast fanout and ZDO scheduling. Physical sends remain measured, but vanilla candidate/selection semantics may differ.", detected);
        AddCompatibility("Searica.Valheim.NetworkTweaks", "NetworkTweaks", "Compatible: it changes peers-per-update and Steam limits. Per-peer intervals and queue values reflect the modified runtime.", detected);
        AddCompatibility("CW_Jesse.BetterNetworking", "Better Networking", "Compatible: runtime limits and compression can change queue/transport values; logical RPC and ZDO measurements remain usable.", detected);
        AddCompatibility("org.bepinex.plugins.network", "Network", "Compatible with caveat: it buffers early ZDOData registration; registrations and runtime traffic are still measured after the final handler is installed.", detected);
        _compatibilitySummary = detected.Count == 0 ? "Vanilla network stack detected." : "Detected network stack mods: " + string.Join(", ", detected) + ". See Help for caveats.";
        if (_compatibilityWarnings.Any(item => item.StartsWith("Instrumentation:", StringComparison.Ordinal)))
            _compatibilitySummary += " One or more instrumentation warnings were detected; see Help.";
    }

    private void AddCompatibility(string guid, string name, string warning, List<string> detected)
    {
        if (!Chainloader.PluginInfos.ContainsKey(guid)) return;
        if (!detected.Contains(name))
            detected.Add(name);
        string full = name + ": " + warning;
        if (!_compatibilityWarnings.Contains(full))
            _compatibilityWarnings.Add(full);
    }

    private static string DescribeSocket(ISocket socket)
    {
        if (socket == null) return "null";
        string result = socket.GetType().Name;
        object current = socket;
        for (int i = 0; i < 8 && current != null; i++)
        {
            Type type = current.GetType();
            if (!string.Equals(type.Name, "BufferingSocket", StringComparison.Ordinal)) break;
            FieldInfo field = type.GetField("Original", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object next = field?.GetValue(current);
            if (next == null || ReferenceEquals(next, current)) break;
            result += " -> " + next.GetType().Name;
            current = next;
        }
        return result;
    }

    private static long TryGetActualInFlight(ISocket socket)
    {
        try
        {
            object current = socket;
            for (int i = 0; i < 8 && current != null; i++)
            {
                Type type = current.GetType();
                if (type.Name == "ZPlayFabSocket")
                {
                    FieldInfo queueField = type.GetField("m_inFlightQueue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    object queue = queueField?.GetValue(current);
                    if (queue == null) return -1L;
                    PropertyInfo bytesProperty = queue.GetType().GetProperty("Bytes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    FieldInfo bytesField = queue.GetType().GetField("Bytes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    object value = bytesProperty?.GetValue(queue) ?? bytesField?.GetValue(queue);
                    return value == null ? -1L : Convert.ToInt64(value);
                }
                FieldInfo original = type.GetField("Original", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object next = original?.GetValue(current);
                if (next == null || ReferenceEquals(next, current)) break;
                current = next;
            }
        }
        catch { }
        return -1L;
    }

    private static T Safe<T>(Func<T> action, T fallback)
    {
        try { return action(); } catch { return fallback; }
    }
}
