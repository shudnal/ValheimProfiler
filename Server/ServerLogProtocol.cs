#nullable disable

using BepInEx.Logging;
using System;
using System.Collections.Generic;

namespace ValheimProfiler.Server;

internal enum ServerLogRequestKind : byte
{
    Probe = 1,
    Subscribe = 2,
    Unsubscribe = 3,
    RequestOlder = 4,
    Resync = 5,
    SetRemoteAccess = 6
}

internal enum ServerLogResponseKind : byte
{
    Capabilities = 1,
    Status = 2,
    Snapshot = 3,
    LiveBatch = 4,
    OlderPage = 5,
    Error = 6
}

internal sealed class ServerLogWireEntry
{
    internal long Sequence;
    internal DateTime Timestamp;
    internal LogLevel Level;
    internal string Source;
    internal string RawMessage;
    internal string Message;
    internal string Details;
    internal string Scene;
    internal int ThreadId;
    internal bool IsHistorical;
    internal long FileOffset;

    internal string Fingerprint => LogMonitorText.BuildFingerprint(Level, Source, Message, Details);
}

internal sealed class ServerLogResponse
{
    internal ServerLogResponseKind Kind;
    internal string SessionId = string.Empty;
    internal string Status = string.Empty;
    internal string ServerVersion = string.Empty;
    internal bool RemoteEnabled;
    internal bool Authorized;
    internal long DroppedCount;
    internal long FirstSequence;
    internal long LastSequence;
    internal long HistoryCursor;
    internal bool HasMoreHistory;
    internal long FileCreationUtcTicks;
    internal bool ResetHistory;
    internal readonly List<ServerLogWireEntry> Entries = new();
}

internal static class ServerLogProtocol
{
    // Internal wire-layout guard. Pre-release builds do not promise backward compatibility;
    // bump only when the serialized request/response layout becomes incompatible.
    internal const int Version = 4;
    internal const string RequestRpc = "shudnal.ValheimProfiler.ServerLog.Request";
    internal const string ResponseRpc = "shudnal.ValheimProfiler.ServerLog.Response";
    internal const int MaxRequestPayloadBytes = 16 * 1024;
    internal const int MaxResponsePayloadBytes = ServerLogTransport.MaxRawPayloadBytes;
    internal const int MaxEntryPayloadBytes = 128 * 1024;
    internal const int MaxWireStringLength = 8192;

    internal static ZPackage CreateRequest(
        ServerLogRequestKind kind,
        string sessionId = "",
        long cursor = 0L,
        long lastSequence = 0L,
        long fileCreationUtcTicks = 0L,
        bool requestedRemoteEnabled = false)
    {
        var package = new ZPackage();
        package.Write(Version);
        package.Write((byte)kind);
        package.Write(LimitTo(sessionId, 128));
        package.Write(cursor);
        package.Write(lastSequence);
        package.Write(fileCreationUtcTicks);
        package.Write(requestedRemoteEnabled);
        return package;
    }

    internal static void ReadRequest(
        ZPackage package,
        out int version,
        out ServerLogRequestKind kind,
        out string sessionId,
        out long cursor,
        out long lastSequence,
        out long fileCreationUtcTicks,
        out bool requestedRemoteEnabled)
    {
        version = package.ReadInt();
        kind = (ServerLogRequestKind)package.ReadByte();
        if (!Enum.IsDefined(typeof(ServerLogRequestKind), kind))
            throw new InvalidOperationException($"Unknown server log request kind {(byte)kind}.");
        sessionId = ReadBoundedString(package, 128, "request session id");
        cursor = package.ReadLong();
        lastSequence = package.ReadLong();
        fileCreationUtcTicks = package.ReadLong();
        requestedRemoteEnabled = package.ReadBool();
        if (cursor < 0L || lastSequence < 0L || fileCreationUtcTicks < 0L)
            throw new InvalidOperationException("Server log request contains a negative cursor, sequence or file timestamp.");
    }

    internal static ZPackage CreateResponse(ServerLogResponse response)
    {
        var package = new ZPackage();
        package.Write(Version);
        package.Write((byte)response.Kind);
        package.Write(LimitTo(response.SessionId, 128));
        package.Write(Limit(response.Status));
        package.Write(LimitTo(response.ServerVersion, 128));
        package.Write(response.RemoteEnabled);
        package.Write(response.Authorized);
        package.Write(response.DroppedCount);
        package.Write(response.FirstSequence);
        package.Write(response.LastSequence);
        package.Write(response.HistoryCursor);
        package.Write(response.HasMoreHistory);
        package.Write(response.FileCreationUtcTicks);
        package.Write(response.ResetHistory);
        package.Write(response.Entries.Count);

        // Each entry is length-prefixed so a future protocol can skip or reject one
        // malformed entry without losing the boundaries of the remaining payload.
        for (int i = 0; i < response.Entries.Count; i++)
        {
            var entryPackage = new ZPackage();
            WriteEntry(entryPackage, response.Entries[i]);
            byte[] entryData = entryPackage.GetArray();
            if (entryData.Length > MaxEntryPayloadBytes)
                throw new InvalidOperationException($"Server log entry payload exceeds {MaxEntryPayloadBytes} bytes.");
            package.Write(entryData);
        }

        return package;
    }

    internal static ServerLogResponse ReadResponse(ZPackage package)
    {
        int version = package.ReadInt();
        if (version != Version)
            throw new InvalidOperationException($"Unsupported server log protocol {version}; expected {Version}.");

        ServerLogResponseKind responseKind = (ServerLogResponseKind)package.ReadByte();
        if (!Enum.IsDefined(typeof(ServerLogResponseKind), responseKind))
            throw new InvalidOperationException($"Unknown server log response kind {(byte)responseKind}.");

        var response = new ServerLogResponse
        {
            Kind = responseKind,
            SessionId = ReadBoundedString(package, 128, "response session id"),
            Status = ReadBoundedString(package, MaxWireStringLength, "response status"),
            ServerVersion = ReadBoundedString(package, 128, "server version"),
            RemoteEnabled = package.ReadBool(),
            Authorized = package.ReadBool(),
            DroppedCount = package.ReadLong(),
            FirstSequence = package.ReadLong(),
            LastSequence = package.ReadLong(),
            HistoryCursor = package.ReadLong(),
            HasMoreHistory = package.ReadBool(),
            FileCreationUtcTicks = package.ReadLong(),
            ResetHistory = package.ReadBool()
        };

        if (response.DroppedCount < 0L ||
            response.FirstSequence < 0L ||
            response.LastSequence < 0L ||
            response.HistoryCursor < 0L ||
            response.FileCreationUtcTicks < 0L ||
            (response.FirstSequence > 0L && response.LastSequence > 0L &&
             response.FirstSequence > response.LastSequence))
        {
            throw new InvalidOperationException("Server log response contains invalid range metadata.");
        }

        int count = package.ReadInt();
        if (count < 0 || count > 10000)
            throw new InvalidOperationException($"Invalid server log entry count {count}.");

        long previousSequence = 0L;
        long previousFileOffset = -1L;
        for (int i = 0; i < count; i++)
        {
            byte[] entryData = package.ReadByteArray();
            if (entryData == null || entryData.Length <= 0 || entryData.Length > MaxEntryPayloadBytes)
                throw new InvalidOperationException($"Invalid server log entry payload size {entryData?.Length ?? 0}.");

            ServerLogWireEntry entry = ReadEntry(new ZPackage(entryData));
            if (response.Kind == ServerLogResponseKind.OlderPage)
            {
                if (!entry.IsHistorical || entry.Sequence != 0L ||
                    (previousFileOffset >= 0L && entry.FileOffset < previousFileOffset))
                {
                    throw new InvalidOperationException("Server log history page contains invalid entry ordering.");
                }
                previousFileOffset = entry.FileOffset;
            }
            else if (response.Kind == ServerLogResponseKind.Snapshot ||
                     response.Kind == ServerLogResponseKind.LiveBatch)
            {
                if (entry.IsHistorical || entry.Sequence <= 0L ||
                    (previousSequence > 0L && entry.Sequence <= previousSequence))
                {
                    throw new InvalidOperationException("Server log live response contains invalid entry ordering.");
                }
                previousSequence = entry.Sequence;
            }

            response.Entries.Add(entry);
        }

        if (response.Entries.Count > 0 &&
            (response.Kind == ServerLogResponseKind.Snapshot ||
             response.Kind == ServerLogResponseKind.LiveBatch) &&
            (response.FirstSequence != response.Entries[0].Sequence ||
             response.LastSequence != response.Entries[response.Entries.Count - 1].Sequence))
        {
            throw new InvalidOperationException("Server log response sequence range does not match its entries.");
        }

        return response;
    }

    internal static int EstimateEntryBytes(ServerLogWireEntry entry)
    {
        return 80 + EstimateString(entry.Source) + EstimateString(entry.RawMessage) +
               EstimateString(entry.Message) + EstimateString(entry.Details) + EstimateString(entry.Scene);
    }

    private static int EstimateString(string value) => Math.Min(MaxWireStringLength, value?.Length ?? 0) * 4 + 8;

    private static void WriteEntry(ZPackage package, ServerLogWireEntry entry)
    {
        package.Write(entry.Sequence);
        package.Write(ToUtcTicks(entry.Timestamp));
        package.Write((int)entry.Level);
        package.Write(Limit(entry.Source));
        package.Write(Limit(entry.RawMessage));
        package.Write(Limit(entry.Message));
        package.Write(Limit(entry.Details));
        package.Write(Limit(entry.Scene));
        package.Write(entry.ThreadId);
        package.Write(entry.IsHistorical);
        package.Write(entry.FileOffset);
    }

    private static ServerLogWireEntry ReadEntry(ZPackage package)
    {
        long sequence = package.ReadLong();
        long ticks = package.ReadLong();
        int rawLevel = package.ReadInt();
        if ((rawLevel & ~(int)LogLevel.All) != 0)
            throw new InvalidOperationException($"Server log entry contains invalid log level {rawLevel}.");

        string source = ReadBoundedString(package, MaxWireStringLength, "source");
        string rawMessage = ReadBoundedString(package, MaxWireStringLength, "raw message");
        string message = ReadBoundedString(package, MaxWireStringLength, "message");
        string details = ReadBoundedString(package, MaxWireStringLength, "details");
        string scene = ReadBoundedString(package, MaxWireStringLength, "scene");
        int threadId = package.ReadInt();
        bool isHistorical = package.ReadBool();
        long fileOffset = package.ReadLong();

        bool invalidIdentity = isHistorical
            ? sequence != 0L || fileOffset < 0L
            : sequence <= 0L || fileOffset != -1L;

        if (ticks < 0L || ticks > DateTime.MaxValue.Ticks || threadId < 0 || invalidIdentity)
        {
            throw new InvalidOperationException(
                $"Server log entry contains invalid numeric metadata: sequence={sequence}, ticks={ticks}, " +
                $"thread={threadId}, historical={isHistorical}, fileOffset={fileOffset}.");
        }

        return new ServerLogWireEntry
        {
            Sequence = sequence,
            Timestamp = ticks > 0L
                ? new DateTime(ticks, DateTimeKind.Utc).ToLocalTime()
                : default,
            Level = (LogLevel)rawLevel,
            Source = source,
            RawMessage = rawMessage,
            Message = message,
            Details = details,
            Scene = scene,
            ThreadId = threadId,
            IsHistorical = isHistorical,
            FileOffset = fileOffset
        };
    }

    private static string ReadBoundedString(ZPackage package, int maximumLength, string fieldName)
    {
        string value = package.ReadString() ?? string.Empty;
        if (value.Length > maximumLength)
            throw new InvalidOperationException($"Server log {fieldName} exceeds {maximumLength} characters.");
        return value;
    }

    private static long ToUtcTicks(DateTime value)
    {
        if (value == default)
            return 0L;

        return value.Kind == DateTimeKind.Utc
            ? value.Ticks
            : value.ToUniversalTime().Ticks;
    }

    private static string Limit(string value) => LimitTo(value, MaxWireStringLength);

    private static string LimitTo(string value, int maximumLength)
    {
        value ??= string.Empty;
        if (value.Length <= maximumLength)
            return value;
        int prefixLength = Math.Max(0, maximumLength - 24);
        return value.Substring(0, prefixLength) + "\n... [text truncated]";
    }
}
