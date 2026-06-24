#nullable disable

using System;
using System.Collections.Generic;

namespace ValheimProfiler.Server;

internal enum NetworkProfilerRequestKind : byte
{
    Probe = 1,
    Subscribe = 2,
    Unsubscribe = 3,
    Reset = 4
}

internal enum NetworkProfilerResponseKind : byte
{
    Capabilities = 1,
    Snapshot = 2,
    Status = 3,
    Error = 4
}

internal sealed class NetworkRpcWireRow
{
    internal string Layer = string.Empty;
    internal string Name = string.Empty;
    internal int MethodHash;
    internal string Component = string.Empty;
    internal string Handler = string.Empty;
    internal string Mod = string.Empty;
    internal string Prefab = string.Empty;
    internal long Registrations;
    internal long IncomingCalls;
    internal long LocalCalls;
    internal long OutgoingCalls;
    internal long IncomingBytes;
    internal long LocalBytes;
    internal long OutgoingBytes;
    internal long PhysicalSends;
    internal long PhysicalBytes;
    internal double HandlerMs;
    internal double AverageHandlerMs;
    internal double MaxHandlerMs;
    internal int MaxPayloadBytes;
    internal int MaxCallsPerFrame;
    internal long Errors;
}

internal sealed class NetworkZdoWireRow
{
    internal int PrefabHash;
    internal string Prefab = string.Empty;
    internal long Mutations;
    internal long UniqueChanged;
    internal long SentCount;
    internal long SentBytes;
    internal long ReceivedCount;
    internal long ReceivedBytes;
    internal double SerializeMs;
    internal double DeserializeMs;
    internal int AverageSize;
    internal int MaxSize;
    internal long Creates;
    internal long Destroys;
    internal long OwnershipChanges;
}


internal sealed class NetworkZdoInstanceWireRow
{
    internal string ZdoId = string.Empty;
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

internal sealed class NetworkZdoKeyWireRow
{
    internal int KeyHash;
    internal string KeyName = string.Empty;
    internal string ValueType = string.Empty;
    internal string Prefab = string.Empty;
    internal long Mutations;
    internal long AffectedZdos;
}

internal sealed class NetworkPeerWireRow
{
    internal long PeerId;
    internal string Name = string.Empty;
    internal string Host = string.Empty;
    internal string Socket = string.Empty;
    internal bool Ready;
    internal int Ping;
    internal float LocalQuality;
    internal float RemoteQuality;
    internal float ReportedOutBytesPerSecond;
    internal float ReportedInBytesPerSecond;
    internal int SerializedOutBytesPerSecond;
    internal int SerializedInBytesPerSecond;
    internal int SendQueue;
    internal int MaximumSendQueue;
    internal int CurrentSendRate;
    internal long ActualInFlightBytes;
    internal float LastReceiveAge;
    internal double SendZdoMs;
    internal double CreateSyncListMs;
    internal int ZdosSent;
    internal int SyncCandidates;
    internal int SyncSelected;
}

internal sealed class NetworkRoutingErrorWireRow
{
    internal string Kind = string.Empty;
    internal string Rpc = string.Empty;
    internal int MethodHash;
    internal string Component = string.Empty;
    internal string Handler = string.Empty;
    internal string Prefab = string.Empty;
    internal string Caller = string.Empty;
    internal string CallerMod = string.Empty;
    internal long PeerId;
    internal string Peer = string.Empty;
    internal string Target = string.Empty;
    internal long Count;
    internal string LastDetails = string.Empty;
}

internal sealed class NetworkProfilerResponse
{
    internal NetworkProfilerResponseKind Kind;
    internal string SessionId = string.Empty;
    internal string Status = string.Empty;
    internal string ServerVersion = string.Empty;
    internal bool Authorized;
    internal long SnapshotSequence;
    internal bool FullRegistry;
    internal string CompatibilitySummary = string.Empty;
    internal readonly List<string> CompatibilityWarnings = new();
    internal readonly List<NetworkRpcWireRow> RpcRows = new();
    internal readonly List<NetworkZdoWireRow> ZdoRows = new();
    internal readonly List<NetworkZdoInstanceWireRow> ZdoInstanceRows = new();
    internal readonly List<NetworkZdoKeyWireRow> ZdoKeyRows = new();
    internal readonly List<NetworkPeerWireRow> PeerRows = new();
    internal readonly List<NetworkRoutingErrorWireRow> ErrorRows = new();
}

internal static class NetworkProfilerProtocol
{
    internal const int Version = 1;
    internal const string RequestRpc = "shudnal.ValheimProfiler.Network.Request";
    internal const string ResponseRpc = "shudnal.ValheimProfiler.Network.Response";
    internal const int MaxRequestPayloadBytes = 8 * 1024;
    internal const int MaxResponsePayloadBytes = NetworkProfilerTransport.MaxRawPayloadBytes;
    internal const int MaxStringLength = 4096;
    internal const int MaxRowsPerSection = 2000;

    internal static ZPackage CreateRequest(NetworkProfilerRequestKind kind)
    {
        var package = new ZPackage();
        package.Write(Version);
        package.Write((byte)kind);
        return package;
    }

    internal static void ReadRequest(ZPackage package, out int version, out NetworkProfilerRequestKind kind)
    {
        version = package.ReadInt();
        kind = (NetworkProfilerRequestKind)package.ReadByte();
        if (!Enum.IsDefined(typeof(NetworkProfilerRequestKind), kind))
            throw new InvalidOperationException($"Unknown Network Profiler request kind {(byte)kind}.");
    }

    internal static ZPackage CreateResponse(NetworkProfilerResponse response)
    {
        var package = new ZPackage();
        package.Write(Version);
        package.Write((byte)response.Kind);
        WriteString(package, response.SessionId, 128);
        WriteString(package, response.Status, MaxStringLength);
        WriteString(package, response.ServerVersion, 128);
        package.Write(response.Authorized);
        package.Write(response.SnapshotSequence);
        package.Write(response.FullRegistry);
        WriteString(package, response.CompatibilitySummary, MaxStringLength);

        WriteStringList(package, response.CompatibilityWarnings, 128);
        WriteRows(package, response.RpcRows, WriteRpcRow);
        WriteRows(package, response.ZdoRows, WriteZdoRow);
        WriteRows(package, response.ZdoInstanceRows, WriteZdoInstanceRow);
        WriteRows(package, response.ZdoKeyRows, WriteZdoKeyRow);
        WriteRows(package, response.PeerRows, WritePeerRow);
        WriteRows(package, response.ErrorRows, WriteErrorRow);
        return package;
    }

    internal static NetworkProfilerResponse ReadResponse(ZPackage package)
    {
        int version = package.ReadInt();
        if (version != Version)
            throw new InvalidOperationException($"Unsupported Network Profiler protocol {version}; expected {Version}.");

        NetworkProfilerResponseKind kind = (NetworkProfilerResponseKind)package.ReadByte();
        if (!Enum.IsDefined(typeof(NetworkProfilerResponseKind), kind))
            throw new InvalidOperationException($"Unknown Network Profiler response kind {(byte)kind}.");

        var response = new NetworkProfilerResponse
        {
            Kind = kind,
            SessionId = ReadString(package, 128, "session id"),
            Status = ReadString(package, MaxStringLength, "status"),
            ServerVersion = ReadString(package, 128, "server version"),
            Authorized = package.ReadBool(),
            SnapshotSequence = package.ReadLong(),
            FullRegistry = package.ReadBool(),
            CompatibilitySummary = ReadString(package, MaxStringLength, "compatibility summary")
        };
        if (response.SnapshotSequence < 0L)
            throw new InvalidOperationException("Network Profiler response contains a negative snapshot sequence.");

        ReadStringList(package, response.CompatibilityWarnings, 128);
        ReadRows(package, response.RpcRows, ReadRpcRow);
        ReadRows(package, response.ZdoRows, ReadZdoRow);
        ReadRows(package, response.ZdoInstanceRows, ReadZdoInstanceRow);
        ReadRows(package, response.ZdoKeyRows, ReadZdoKeyRow);
        ReadRows(package, response.PeerRows, ReadPeerRow);
        ReadRows(package, response.ErrorRows, ReadErrorRow);
        return response;
    }

    private delegate void RowWriter<T>(ZPackage package, T row);
    private delegate T RowReader<T>(ZPackage package);

    private static void WriteRows<T>(ZPackage package, List<T> rows, RowWriter<T> writer)
    {
        int count = Math.Min(rows?.Count ?? 0, MaxRowsPerSection);
        package.Write(count);
        for (int i = 0; i < count; i++)
            writer(package, rows[i]);
    }

    private static void ReadRows<T>(ZPackage package, List<T> rows, RowReader<T> reader)
    {
        int count = package.ReadInt();
        if (count < 0 || count > MaxRowsPerSection)
            throw new InvalidOperationException($"Invalid Network Profiler row count {count}.");
        for (int i = 0; i < count; i++)
            rows.Add(reader(package));
    }

    private static void WriteStringList(ZPackage package, List<string> values, int maximumCount)
    {
        int count = Math.Min(values?.Count ?? 0, maximumCount);
        package.Write(count);
        for (int i = 0; i < count; i++)
            WriteString(package, values[i], MaxStringLength);
    }

    private static void ReadStringList(ZPackage package, List<string> values, int maximumCount)
    {
        int count = package.ReadInt();
        if (count < 0 || count > maximumCount)
            throw new InvalidOperationException($"Invalid Network Profiler string list count {count}.");
        for (int i = 0; i < count; i++)
            values.Add(ReadString(package, MaxStringLength, "list item"));
    }

    private static void WriteRpcRow(ZPackage p, NetworkRpcWireRow r)
    {
        WriteString(p, r.Layer, 64); WriteString(p, r.Name, 512); p.Write(r.MethodHash);
        WriteString(p, r.Component, 512); WriteString(p, r.Handler, 1024); WriteString(p, r.Mod, 512); WriteString(p, r.Prefab, 512);
        p.Write(r.Registrations); p.Write(r.IncomingCalls); p.Write(r.LocalCalls); p.Write(r.OutgoingCalls); p.Write(r.IncomingBytes); p.Write(r.LocalBytes); p.Write(r.OutgoingBytes); p.Write(r.PhysicalSends); p.Write(r.PhysicalBytes);
        p.Write(r.HandlerMs); p.Write(r.AverageHandlerMs); p.Write(r.MaxHandlerMs); p.Write(r.MaxPayloadBytes); p.Write(r.MaxCallsPerFrame); p.Write(r.Errors);
    }

    private static NetworkRpcWireRow ReadRpcRow(ZPackage p) => new()
    {
        Layer = ReadString(p, 64, "rpc layer"), Name = ReadString(p, 512, "rpc name"), MethodHash = p.ReadInt(),
        Component = ReadString(p, 512, "rpc component"), Handler = ReadString(p, 1024, "rpc handler"), Mod = ReadString(p, 512, "rpc mod"), Prefab = ReadString(p, 512, "rpc prefab"),
        Registrations = ReadNonNegativeLong(p, "registrations"), IncomingCalls = ReadNonNegativeLong(p, "incoming calls"), LocalCalls = ReadNonNegativeLong(p, "local calls"), OutgoingCalls = ReadNonNegativeLong(p, "outgoing calls"),
        IncomingBytes = ReadNonNegativeLong(p, "incoming bytes"), LocalBytes = ReadNonNegativeLong(p, "local bytes"), OutgoingBytes = ReadNonNegativeLong(p, "outgoing bytes"),
        PhysicalSends = ReadNonNegativeLong(p, "physical sends"), PhysicalBytes = ReadNonNegativeLong(p, "physical bytes"),
        HandlerMs = ReadNonNegativeDouble(p, "handler ms"), AverageHandlerMs = ReadNonNegativeDouble(p, "average handler ms"), MaxHandlerMs = ReadNonNegativeDouble(p, "max handler ms"),
        MaxPayloadBytes = ReadNonNegativeInt(p, "max payload"), MaxCallsPerFrame = ReadNonNegativeInt(p, "max calls per frame"), Errors = ReadNonNegativeLong(p, "errors")
    };

    private static void WriteZdoRow(ZPackage p, NetworkZdoWireRow r)
    {
        p.Write(r.PrefabHash); WriteString(p, r.Prefab, 512); p.Write(r.Mutations); p.Write(r.UniqueChanged); p.Write(r.SentCount); p.Write(r.SentBytes);
        p.Write(r.ReceivedCount); p.Write(r.ReceivedBytes); p.Write(r.SerializeMs); p.Write(r.DeserializeMs); p.Write(r.AverageSize); p.Write(r.MaxSize);
        p.Write(r.Creates); p.Write(r.Destroys); p.Write(r.OwnershipChanges);
    }

    private static NetworkZdoWireRow ReadZdoRow(ZPackage p) => new()
    {
        PrefabHash = p.ReadInt(), Prefab = ReadString(p, 512, "prefab"), Mutations = ReadNonNegativeLong(p, "mutations"), UniqueChanged = ReadNonNegativeLong(p, "unique changed"),
        SentCount = ReadNonNegativeLong(p, "sent count"), SentBytes = ReadNonNegativeLong(p, "sent bytes"), ReceivedCount = ReadNonNegativeLong(p, "received count"), ReceivedBytes = ReadNonNegativeLong(p, "received bytes"),
        SerializeMs = ReadNonNegativeDouble(p, "serialize ms"), DeserializeMs = ReadNonNegativeDouble(p, "deserialize ms"), AverageSize = ReadNonNegativeInt(p, "average size"), MaxSize = ReadNonNegativeInt(p, "max size"),
        Creates = ReadNonNegativeLong(p, "creates"), Destroys = ReadNonNegativeLong(p, "destroys"), OwnershipChanges = ReadNonNegativeLong(p, "ownership changes")
    };

    private static void WriteZdoInstanceRow(ZPackage p, NetworkZdoInstanceWireRow r)
    {
        WriteString(p, r.ZdoId, 256); p.Write(r.PrefabHash); WriteString(p, r.Prefab, 512); p.Write(r.Owner); p.Write(r.Mutations);
        p.Write(r.SentCount); p.Write(r.SentBytes); p.Write(r.ReceivedCount); p.Write(r.ReceivedBytes); p.Write(r.MaxSize);
    }

    private static NetworkZdoInstanceWireRow ReadZdoInstanceRow(ZPackage p) => new()
    {
        ZdoId = ReadString(p, 256, "zdo id"), PrefabHash = p.ReadInt(), Prefab = ReadString(p, 512, "zdo prefab"), Owner = p.ReadLong(),
        Mutations = ReadNonNegativeLong(p, "zdo mutations"), SentCount = ReadNonNegativeLong(p, "zdo sent count"), SentBytes = ReadNonNegativeLong(p, "zdo sent bytes"),
        ReceivedCount = ReadNonNegativeLong(p, "zdo received count"), ReceivedBytes = ReadNonNegativeLong(p, "zdo received bytes"), MaxSize = ReadNonNegativeInt(p, "zdo max size")
    };

    private static void WriteZdoKeyRow(ZPackage p, NetworkZdoKeyWireRow r)
    {
        p.Write(r.KeyHash); WriteString(p, r.KeyName, 512); WriteString(p, r.ValueType, 128); WriteString(p, r.Prefab, 512); p.Write(r.Mutations); p.Write(r.AffectedZdos);
    }

    private static NetworkZdoKeyWireRow ReadZdoKeyRow(ZPackage p) => new()
    {
        KeyHash = p.ReadInt(), KeyName = ReadString(p, 512, "key name"), ValueType = ReadString(p, 128, "value type"), Prefab = ReadString(p, 512, "key prefab"),
        Mutations = ReadNonNegativeLong(p, "key mutations"), AffectedZdos = ReadNonNegativeLong(p, "affected zdos")
    };

    private static void WritePeerRow(ZPackage p, NetworkPeerWireRow r)
    {
        p.Write(r.PeerId); WriteString(p, r.Name, 512); WriteString(p, r.Host, 512); WriteString(p, r.Socket, 512); p.Write(r.Ready); p.Write(r.Ping);
        p.Write(r.LocalQuality); p.Write(r.RemoteQuality); p.Write(r.ReportedOutBytesPerSecond); p.Write(r.ReportedInBytesPerSecond);
        p.Write(r.SerializedOutBytesPerSecond); p.Write(r.SerializedInBytesPerSecond); p.Write(r.SendQueue); p.Write(r.MaximumSendQueue); p.Write(r.CurrentSendRate);
        p.Write(r.ActualInFlightBytes); p.Write(r.LastReceiveAge); p.Write(r.SendZdoMs); p.Write(r.CreateSyncListMs); p.Write(r.ZdosSent); p.Write(r.SyncCandidates); p.Write(r.SyncSelected);
    }

    private static NetworkPeerWireRow ReadPeerRow(ZPackage p) => new()
    {
        PeerId = p.ReadLong(), Name = ReadString(p, 512, "peer name"), Host = ReadString(p, 512, "peer host"), Socket = ReadString(p, 512, "socket"), Ready = p.ReadBool(), Ping = p.ReadInt(),
        LocalQuality = p.ReadSingle(), RemoteQuality = p.ReadSingle(), ReportedOutBytesPerSecond = p.ReadSingle(), ReportedInBytesPerSecond = p.ReadSingle(),
        SerializedOutBytesPerSecond = p.ReadInt(), SerializedInBytesPerSecond = p.ReadInt(), SendQueue = p.ReadInt(), MaximumSendQueue = p.ReadInt(), CurrentSendRate = p.ReadInt(),
        ActualInFlightBytes = p.ReadLong(), LastReceiveAge = p.ReadSingle(), SendZdoMs = ReadNonNegativeDouble(p, "send zdo ms"), CreateSyncListMs = ReadNonNegativeDouble(p, "sync list ms"),
        ZdosSent = ReadNonNegativeInt(p, "zdos sent"), SyncCandidates = ReadNonNegativeInt(p, "sync candidates"), SyncSelected = ReadNonNegativeInt(p, "sync selected")
    };

    private static void WriteErrorRow(ZPackage p, NetworkRoutingErrorWireRow r)
    {
        WriteString(p, r.Kind, 512); WriteString(p, r.Rpc, 512); p.Write(r.MethodHash); WriteString(p, r.Component, 512); WriteString(p, r.Handler, 1024);
        WriteString(p, r.Prefab, 512); WriteString(p, r.Caller, 1024); WriteString(p, r.CallerMod, 512); p.Write(r.PeerId); WriteString(p, r.Peer, 512); WriteString(p, r.Target, 512); p.Write(r.Count); WriteString(p, r.LastDetails, MaxStringLength);
    }

    private static NetworkRoutingErrorWireRow ReadErrorRow(ZPackage p) => new()
    {
        Kind = ReadString(p, 512, "error kind"), Rpc = ReadString(p, 512, "error rpc"), MethodHash = p.ReadInt(), Component = ReadString(p, 512, "error component"),
        Handler = ReadString(p, 1024, "error handler"), Prefab = ReadString(p, 512, "error prefab"), Caller = ReadString(p, 1024, "error caller"),
        CallerMod = ReadString(p, 512, "error caller mod"), PeerId = p.ReadLong(), Peer = ReadString(p, 512, "error peer"),
        Target = ReadString(p, 512, "error target"), Count = ReadNonNegativeLong(p, "error count"), LastDetails = ReadString(p, MaxStringLength, "error details")
    };

    private static void WriteString(ZPackage package, string value, int maximumLength) => package.Write(Limit(value, maximumLength));

    private static string ReadString(ZPackage package, int maximumLength, string field)
    {
        string value = package.ReadString() ?? string.Empty;
        if (value.Length > maximumLength)
            throw new InvalidOperationException($"Network Profiler {field} exceeds {maximumLength} characters.");
        return value;
    }

    private static string Limit(string value, int maximumLength)
    {
        value ??= string.Empty;
        return value.Length <= maximumLength ? value : value.Substring(0, maximumLength);
    }

    private static long ReadNonNegativeLong(ZPackage p, string field)
    {
        long value = p.ReadLong();
        if (value < 0L) throw new InvalidOperationException($"Network Profiler {field} is negative.");
        return value;
    }

    private static int ReadNonNegativeInt(ZPackage p, string field)
    {
        int value = p.ReadInt();
        if (value < 0) throw new InvalidOperationException($"Network Profiler {field} is negative.");
        return value;
    }

    private static double ReadNonNegativeDouble(ZPackage p, string field)
    {
        double value = p.ReadDouble();
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0d)
            throw new InvalidOperationException($"Network Profiler {field} is invalid.");
        return value;
    }
}
