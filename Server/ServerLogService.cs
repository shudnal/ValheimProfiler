#nullable disable

using BepInEx;
using BepInEx.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine.SceneManagement;

namespace ValheimProfiler.Server;

internal sealed class ServerLogService
{
    private const int MaxPendingEntries = 20000;
    private const int MaxDrainPerFrame = 2000;
    private const int MaxStoredTextLength = ServerLogProtocol.MaxWireStringLength;
    private const int MaxLiveBatchEntries = 256;
    private const int MaxSubscribers = 8;
    private static readonly TimeSpan LiveBatchInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan HistoryRequestInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SubscriberValidationInterval = TimeSpan.FromSeconds(1);

    private sealed class PendingLogEvent
    {
        internal DateTime Timestamp;
        internal LogLevel Level;
        internal string Source;
        internal string RawMessage;
        internal string Message;
        internal string Details;
        internal int ThreadId;
    }

    private sealed class Subscriber
    {
        internal long PeerId;
        internal long LastSentSequence;
        internal DateTime LastHistoryRequestUtc;
        internal bool HistoryRequestPending;
    }

    private sealed class HistoryResult
    {
        internal long PeerId;
        internal string RequestedSessionId;
        internal LogFilePage Page;
    }

    private sealed class Listener : ILogListener
    {
        private ServerLogService _owner;

        internal Listener(ServerLogService owner) => _owner = owner;

        public void LogEvent(object sender, LogEventArgs eventArgs) => _owner?.Enqueue(eventArgs);

        public void Dispose() => _owner = null;
    }

    private readonly ValheimProfilerConfig _config;
    private readonly Listener _listener;
    private readonly ConcurrentQueue<PendingLogEvent> _pending = new();
    private readonly ConcurrentQueue<HistoryResult> _historyResults = new();
    private readonly List<ServerLogWireEntry> _recent = new();
    private readonly Dictionary<long, Subscriber> _subscribers = new();
    private readonly string _sessionId = Guid.NewGuid().ToString("N");

    private int _pendingCount;
    private long _droppedCount;
    private long _nextSequence;
    private DateTime _nextLiveSendUtc;
    private DateTime _nextSubscriberValidationUtc;
    private bool _shutdown;

    internal ServerLogService(ValheimProfilerConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _listener = new Listener(this);
        BepInEx.Logging.Logger.Listeners.Add(_listener);
    }

    internal string SessionId => _sessionId;

    internal void Update()
    {
        if (_shutdown)
            return;

        DrainPending();
        DrainHistoryResults();

        DateTime now = DateTime.UtcNow;
        if (now >= _nextSubscriberValidationUtc)
        {
            _nextSubscriberValidationUtc = now + SubscriberValidationInterval;
            ValidateSubscribers();
        }

        if (now < _nextLiveSendUtc)
            return;

        _nextLiveSendUtc = now + LiveBatchInterval;
        SendLiveBatches();
    }

    internal void HandleRequest(long sender, ZPackage package)
    {
        try
        {
            ServerLogTransportReceiveResult transportResult = ServerLogTransport.TryReceive(
                sender,
                package,
                out ZPackage requestPayload,
                out string transportError);
            if (transportResult == ServerLogTransportReceiveResult.WaitingForFragments)
                return;
            if (transportResult == ServerLogTransportReceiveResult.Rejected)
            {
                SendError(sender, "Invalid server log transport payload: " + transportError);
                return;
            }

            if (requestPayload.Size() > ServerLogProtocol.MaxRequestPayloadBytes)
            {
                SendError(sender, "Oversized server log request payload.");
                return;
            }

            ServerLogProtocol.ReadRequest(
                requestPayload,
                out int protocol,
                out ServerLogRequestKind kind,
                out string requestedSession,
                out long cursor,
                out long lastSequence,
                out long expectedFileCreationUtcTicks,
                out _);

            if (protocol != ServerLogProtocol.Version)
            {
                SendError(sender, $"Unsupported server log protocol {protocol}; server uses {ServerLogProtocol.Version}.");
                return;
            }

            if (kind == ServerLogRequestKind.Probe)
            {
                SendCapabilities(sender);
                return;
            }

            if (!IsAdmin(sender))
            {
                SendError(sender, "Server log access denied. The connected peer is not a server administrator.");
                _subscribers.Remove(sender);
                return;
            }

            if (kind == ServerLogRequestKind.SetRemoteAccess)
            {
                SendError(sender, "The remote access toggle was removed. Use Subscribe to start a private admin log stream.");
                return;
            }

            switch (kind)
            {
                case ServerLogRequestKind.Subscribe:
                    Subscribe(sender);
                    break;
                case ServerLogRequestKind.Unsubscribe:
                    _subscribers.Remove(sender);
                    SendStatus(sender, "Unsubscribed from server log updates.");
                    break;
                case ServerLogRequestKind.RequestOlder:
                    RequestOlder(sender, requestedSession, cursor, expectedFileCreationUtcTicks);
                    break;
                case ServerLogRequestKind.Resync:
                    if (!_subscribers.TryGetValue(sender, out Subscriber subscriber))
                    {
                        if (_subscribers.Count >= MaxSubscribers)
                        {
                            SendError(sender, $"Server log subscriber limit ({MaxSubscribers}) reached.");
                            return;
                        }
                        subscriber = new Subscriber { PeerId = sender };
                        _subscribers[sender] = subscriber;
                    }
                    SendSnapshot(subscriber, $"Server log stream resynchronized after client sequence {lastSequence}.");
                    break;
                default:
                    SendError(sender, $"Unknown server log request {kind}.");
                    break;
            }
        }
        catch (Exception ex)
        {
            SendError(sender, "Invalid server log request: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    internal void OnNetworkDestroyed()
    {
        _subscribers.Clear();
    }

    internal void Shutdown()
    {
        if (_shutdown)
            return;

        _shutdown = true;
        try
        {
            BepInEx.Logging.Logger.Listeners.Remove(_listener);
            _listener.Dispose();
        }
        catch
        {
        }

        _subscribers.Clear();
        _recent.Clear();
        while (_pending.TryDequeue(out _))
        {
        }
        while (_historyResults.TryDequeue(out _))
        {
        }
    }


    private void Subscribe(long sender)
    {
        if (!_subscribers.TryGetValue(sender, out Subscriber subscriber))
        {
            if (_subscribers.Count >= MaxSubscribers)
            {
                SendError(sender, $"Server log subscriber limit ({MaxSubscribers}) reached.");
                return;
            }

            subscriber = new Subscriber { PeerId = sender };
            _subscribers.Add(sender, subscriber);
        }

        SendSnapshot(subscriber, "Subscribed to a private dedicated-server log stream for this admin connection.");
    }

    private void RequestOlder(long sender, string requestedSession, long cursor, long expectedFileCreationUtcTicks)
    {
        if (!_subscribers.TryGetValue(sender, out Subscriber subscriber))
        {
            SendError(sender, "Subscribe to the server log before requesting history.");
            return;
        }

        if (!string.Equals(requestedSession, _sessionId, StringComparison.Ordinal))
        {
            SendSnapshot(subscriber, "The server log session changed; a fresh recent snapshot was sent.", resetHistory: true);
            return;
        }

        long currentFileLength = GetLogFileLength(out long currentFileCreationUtcTicks);
        if ((expectedFileCreationUtcTicks != 0L && currentFileCreationUtcTicks != 0L &&
             expectedFileCreationUtcTicks != currentFileCreationUtcTicks) ||
            cursor > currentFileLength)
        {
            SendSnapshot(subscriber, "The server LogOutput.log file changed; loaded history was invalidated and a fresh snapshot was sent.", resetHistory: true);
            return;
        }

        DateTime now = DateTime.UtcNow;
        if (subscriber.HistoryRequestPending || now - subscriber.LastHistoryRequestUtc < HistoryRequestInterval)
        {
            SendStatus(sender, "A server log history request is already pending or was requested too quickly.");
            return;
        }

        subscriber.HistoryRequestPending = true;
        subscriber.LastHistoryRequestUtc = now;
        int maxEntries = Math.Max(100, _config.ServerLogHistoryPageEntries.Value);
        string path = LogFilePath;
        string session = _sessionId;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            LogFilePage page;
            try
            {
                page = LogFileHistoryReader.ReadOlder(path, cursor, maxEntries, 4 * 1024 * 1024);
            }
            catch (Exception ex)
            {
                page = new LogFilePage { Error = ex.GetType().Name + ": " + ex.Message };
            }

            _historyResults.Enqueue(new HistoryResult
            {
                PeerId = sender,
                RequestedSessionId = session,
                Page = page
            });
        });
    }

    private void DrainHistoryResults()
    {
        while (_historyResults.TryDequeue(out HistoryResult result))
        {
            if (_subscribers.TryGetValue(result.PeerId, out Subscriber subscriber))
                subscriber.HistoryRequestPending = false;

            if (_shutdown || !string.Equals(result.RequestedSessionId, _sessionId, StringComparison.Ordinal))
                continue;
            if (!_subscribers.ContainsKey(result.PeerId) || !IsAdmin(result.PeerId))
                continue;

            LogFilePage page = result.Page;
            if (!string.IsNullOrEmpty(page.Error))
            {
                if (page.Error.StartsWith("LogOutput.log changed", StringComparison.OrdinalIgnoreCase) &&
                    _subscribers.TryGetValue(result.PeerId, out Subscriber activeSubscriber))
                {
                    SendSnapshot(activeSubscriber, "The server LogOutput.log file changed; loaded history was invalidated and a fresh snapshot was sent.", resetHistory: true);
                }
                else
                {
                    SendError(result.PeerId, page.Error);
                }
                continue;
            }

            var response = new ServerLogResponse
            {
                Kind = ServerLogResponseKind.OlderPage,
                SessionId = _sessionId,
                ServerVersion = ValheimProfilerPlugin.PluginVersion,
                RemoteEnabled = true,
                Authorized = true,
                Status = page.Entries.Count == 0 ? "No older server log entries." : $"Loaded {page.Entries.Count} older server log entries.",
                DroppedCount = Interlocked.Read(ref _droppedCount),
                HistoryCursor = page.NextCursor,
                HasMoreHistory = page.HasMore,
                FileCreationUtcTicks = page.FileCreationUtcTicks
            };

            List<ServerLogWireEntry> candidates = new(page.Entries.Count);
            for (int i = 0; i < page.Entries.Count; i++)
            {
                ParsedLogFileEntry parsed = page.Entries[i];
                candidates.Add(new ServerLogWireEntry
                {
                    Sequence = 0L,
                    Timestamp = parsed.Timestamp,
                    Level = parsed.Level,
                    Source = parsed.Source,
                    RawMessage = parsed.RawMessage,
                    Message = parsed.Message,
                    Details = parsed.Details,
                    Scene = parsed.Scene,
                    ThreadId = parsed.ThreadId,
                    IsHistorical = true,
                    FileOffset = parsed.FileOffset
                });
            }

            AddNewestEntriesWithinBudget(response, candidates, candidates.Count);
            if (response.Entries.Count > 0)
            {
                response.HistoryCursor = response.Entries[0].FileOffset;
                response.HasMoreHistory = response.HistoryCursor > 0L;
            }
            Send(result.PeerId, response);
        }
    }

    private void SendSnapshot(Subscriber subscriber, string status, bool resetHistory = false)
    {
        long historyCursor = GetLogFileLength(out long fileCreationTicks);
        var response = new ServerLogResponse
        {
            Kind = ServerLogResponseKind.Snapshot,
            SessionId = _sessionId,
            ServerVersion = ValheimProfilerPlugin.PluginVersion,
            RemoteEnabled = true,
            Authorized = true,
            Status = status,
            DroppedCount = Interlocked.Read(ref _droppedCount),
            HistoryCursor = historyCursor,
            HasMoreHistory = historyCursor > 0L,
            FileCreationUtcTicks = fileCreationTicks,
            ResetHistory = resetHistory
        };

        int max = Math.Max(100, _config.ServerLogInitialEntries.Value);
        AddNewestEntriesWithinBudget(response, _recent, max);
        long sentThroughSequence;
        if (response.Entries.Count > 0)
        {
            response.FirstSequence = response.Entries[0].Sequence;
            response.LastSequence = response.Entries[response.Entries.Count - 1].Sequence;
            sentThroughSequence = response.LastSequence;
        }
        else
        {
            response.FirstSequence = 0L;
            response.LastSequence = _nextSequence;
            sentThroughSequence = _nextSequence;
        }

        if (Send(subscriber.PeerId, response))
            subscriber.LastSentSequence = sentThroughSequence;
    }

    private void SendLiveBatches()
    {
        if (_subscribers.Count == 0 || _recent.Count == 0)
            return;

        long earliest = _recent[0].Sequence;
        long latest = _recent[_recent.Count - 1].Sequence;

        foreach (Subscriber subscriber in _subscribers.Values.ToArray())
        {
            if (subscriber.LastSentSequence >= latest)
                continue;

            if (subscriber.LastSentSequence > 0L && subscriber.LastSentSequence < earliest - 1L)
            {
                SendSnapshot(subscriber, "Live server log entries were dropped before delivery; a recent snapshot was sent.");
                continue;
            }

            var response = new ServerLogResponse
            {
                Kind = ServerLogResponseKind.LiveBatch,
                SessionId = _sessionId,
                ServerVersion = ValheimProfilerPlugin.PluginVersion,
                RemoteEnabled = true,
                Authorized = true,
                Status = string.Empty,
                DroppedCount = Interlocked.Read(ref _droppedCount)
            };

            int estimated = 256;
            for (int i = 0; i < _recent.Count && response.Entries.Count < MaxLiveBatchEntries; i++)
            {
                ServerLogWireEntry entry = _recent[i];
                if (entry.Sequence <= subscriber.LastSentSequence)
                    continue;

                int entryBytes = ServerLogProtocol.EstimateEntryBytes(entry);
                if (response.Entries.Count > 0 && estimated + entryBytes > ServerLogProtocol.MaxResponsePayloadBytes)
                    break;

                response.Entries.Add(entry);
                estimated += entryBytes;
            }

            if (response.Entries.Count == 0)
                continue;

            response.FirstSequence = response.Entries[0].Sequence;
            response.LastSequence = response.Entries[response.Entries.Count - 1].Sequence;
            if (Send(subscriber.PeerId, response))
                subscriber.LastSentSequence = response.LastSequence;
        }
    }

    private static void AddNewestEntriesWithinBudget(
        ServerLogResponse response,
        IReadOnlyList<ServerLogWireEntry> source,
        int maximumCount)
    {
        if (source == null || source.Count == 0 || maximumCount <= 0)
            return;

        var reverse = new List<ServerLogWireEntry>();
        int estimated = 256;
        for (int i = source.Count - 1; i >= 0 && reverse.Count < maximumCount; i--)
        {
            ServerLogWireEntry entry = source[i];
            int entryBytes = ServerLogProtocol.EstimateEntryBytes(entry);
            if (reverse.Count > 0 && estimated + entryBytes > ServerLogProtocol.MaxResponsePayloadBytes)
                break;

            reverse.Add(entry);
            estimated += entryBytes;
        }

        for (int i = reverse.Count - 1; i >= 0; i--)
            response.Entries.Add(reverse[i]);
    }

    private void SendCapabilities(long target)
    {
        bool authorized = IsAdmin(target);
        string status = authorized
            ? "Dedicated server log access is available. Subscribe to start a private stream for this connection."
            : "Valheim Profiler is installed, but the connected peer is not a server administrator.";

        Send(target, new ServerLogResponse
        {
            Kind = ServerLogResponseKind.Capabilities,
            SessionId = _sessionId,
            Status = status,
            ServerVersion = ValheimProfilerPlugin.PluginVersion,
            RemoteEnabled = true,
            Authorized = authorized,
            DroppedCount = Interlocked.Read(ref _droppedCount)
        });
    }

    private void SendStatus(long target, string status)
    {
        Send(target, new ServerLogResponse
        {
            Kind = ServerLogResponseKind.Status,
            SessionId = _sessionId,
            ServerVersion = ValheimProfilerPlugin.PluginVersion,
            RemoteEnabled = true,
            Authorized = IsAdmin(target),
            Status = status,
            DroppedCount = Interlocked.Read(ref _droppedCount)
        });
    }

    private void SendError(long target, string status)
    {
        Send(target, new ServerLogResponse
        {
            Kind = ServerLogResponseKind.Error,
            SessionId = _sessionId,
            ServerVersion = ValheimProfilerPlugin.PluginVersion,
            RemoteEnabled = true,
            Authorized = IsAdmin(target),
            Status = status,
            DroppedCount = Interlocked.Read(ref _droppedCount)
        });
    }

    private static bool Send(long target, ServerLogResponse response)
    {
        try
        {
            bool originalResponsePreserved = true;
            ZPackage payload = ServerLogProtocol.CreateResponse(response);

            while (payload.Size() > ServerLogProtocol.MaxResponsePayloadBytes && response.Entries.Count > 1)
            {
                int removeCount = Math.Max(1, response.Entries.Count / 8);
                if (response.Kind == ServerLogResponseKind.LiveBatch)
                {
                    response.Entries.RemoveRange(response.Entries.Count - removeCount, removeCount);
                }
                else
                {
                    response.Entries.RemoveRange(0, removeCount);
                }

                RefreshResponseRange(response);
                payload = ServerLogProtocol.CreateResponse(response);
            }

            if (payload.Size() > ServerLogProtocol.MaxResponsePayloadBytes)
            {
                originalResponsePreserved = false;
                response.Entries.Clear();
                response.Kind = ServerLogResponseKind.Error;
                response.Status = "The requested server log response exceeded the transport limit. Reduce page or snapshot entry limits.";
                response.FirstSequence = 0L;
                response.LastSequence = 0L;
                payload = ServerLogProtocol.CreateResponse(response);
            }

            bool sent = ServerLogTransport.Send(
                target,
                ServerLogProtocol.ResponseRpc,
                payload,
                out _);
            return sent && originalResponsePreserved;
        }
        catch
        {
            return false;
        }
    }

    private static void RefreshResponseRange(ServerLogResponse response)
    {
        if (response.Entries.Count == 0)
            return;

        if (response.Kind == ServerLogResponseKind.Snapshot || response.Kind == ServerLogResponseKind.LiveBatch)
        {
            response.FirstSequence = response.Entries[0].Sequence;
            response.LastSequence = response.Entries[response.Entries.Count - 1].Sequence;
        }

        if (response.Kind == ServerLogResponseKind.OlderPage)
        {
            response.HistoryCursor = response.Entries[0].FileOffset;
            response.HasMoreHistory = response.HistoryCursor > 0L;
        }
    }

    private void Enqueue(LogEventArgs eventArgs)
    {
        if (_shutdown || eventArgs == null)
            return;

        int pending = Interlocked.Increment(ref _pendingCount);
        if (pending > MaxPendingEntries)
        {
            Interlocked.Decrement(ref _pendingCount);
            Interlocked.Increment(ref _droppedCount);
            return;
        }

        try
        {
            ParseData(eventArgs.Data, out string rawMessage, out string details);
            string source = LimitText(eventArgs.Source?.SourceName ?? "Unknown", 256);
            string message = LogMonitorText.NormalizeMessage(source, rawMessage, out DateTime unityTimestamp);
            _pending.Enqueue(new PendingLogEvent
            {
                Timestamp = unityTimestamp != default ? unityTimestamp : DateTime.Now,
                Level = eventArgs.Level,
                Source = source,
                RawMessage = rawMessage,
                Message = message,
                Details = details,
                ThreadId = Thread.CurrentThread.ManagedThreadId
            });
        }
        catch
        {
            Interlocked.Decrement(ref _pendingCount);
            Interlocked.Increment(ref _droppedCount);
        }
    }

    private void DrainPending()
    {
        string scene = GetActiveSceneName();
        int drained = 0;
        while (drained < MaxDrainPerFrame && _pending.TryDequeue(out PendingLogEvent pending))
        {
            Interlocked.Decrement(ref _pendingCount);
            _recent.Add(new ServerLogWireEntry
            {
                Sequence = ++_nextSequence,
                Timestamp = pending.Timestamp,
                Level = pending.Level,
                Source = pending.Source,
                RawMessage = pending.RawMessage,
                Message = pending.Message,
                Details = pending.Details,
                Scene = scene,
                ThreadId = pending.ThreadId,
                IsHistorical = false,
                FileOffset = -1L
            });
            drained++;
        }

        int max = Math.Max(500, _config.ServerLogRecentEntries.Value);
        if (_recent.Count > max)
        {
            int remove = Math.Max(_recent.Count - max, Math.Max(1, max / 10));
            _recent.RemoveRange(0, Math.Min(remove, _recent.Count));
        }
    }

    private void ValidateSubscribers()
    {
        if (_subscribers.Count == 0)
            return;

        foreach (long peerId in _subscribers.Keys.ToArray())
        {
            ZNetPeer peer = ZRoutedRpc.instance?.GetPeer(peerId);
            if (peer == null || !peer.IsReady())
            {
                _subscribers.Remove(peerId);
                continue;
            }

            if (!IsAdmin(peerId))
            {
                SendError(peerId, "Server log access was revoked because the connected peer is no longer a server administrator.");
                _subscribers.Remove(peerId);
            }
        }
    }

    private static bool IsAdmin(long sender)
    {
        try
        {
            if (ZNet.instance == null || ZRoutedRpc.instance == null)
                return false;

            ZNetPeer peer = ZRoutedRpc.instance.GetPeer(sender);
            string hostName = peer?.m_socket?.GetHostName();
            return !string.IsNullOrEmpty(hostName) && ZNet.instance.IsAdmin(hostName);
        }
        catch
        {
            return false;
        }
    }

    private static string GetActiveSceneName()
    {
        try
        {
            return SceneManager.GetActiveScene().name ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string LogFilePath => Path.Combine(Paths.BepInExRootPath, "LogOutput.log");

    private static long GetLogFileLength(out long creationUtcTicks)
    {
        creationUtcTicks = 0L;
        try
        {
            var info = new FileInfo(LogFilePath);
            if (!info.Exists)
                return 0L;
            creationUtcTicks = info.CreationTimeUtc.Ticks;
            return info.Length;
        }
        catch
        {
            return 0L;
        }
    }

    private static void ParseData(object data, out string message, out string details)
    {
        if (data is Exception exception)
        {
            message = LimitText($"{exception.GetType().FullName}: {exception.Message}", 4096);
            details = LimitText(exception.ToString(), MaxStoredTextLength);
            return;
        }

        string raw = LimitText(data?.ToString() ?? "NULL", MaxStoredTextLength);
        int firstBreak = raw.IndexOfAny(new[] { '\r', '\n' });
        if (firstBreak < 0)
        {
            message = raw;
            details = string.Empty;
            return;
        }

        message = raw.Substring(0, firstBreak).TrimEnd();
        int detailsStart = firstBreak;
        while (detailsStart < raw.Length && (raw[detailsStart] == '\r' || raw[detailsStart] == '\n'))
            detailsStart++;
        details = detailsStart < raw.Length ? raw.Substring(detailsStart) : string.Empty;
        if (string.IsNullOrEmpty(message))
            message = details.Length > 0 ? FirstLine(details) : "(empty message)";
    }

    private static string FirstLine(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        int firstBreak = value.IndexOfAny(new[] { '\r', '\n' });
        return firstBreak >= 0 ? value.Substring(0, firstBreak) : value;
    }

    private static string LimitText(string value, int maxLength)
    {
        value ??= string.Empty;
        if (value.Length <= maxLength)
            return value;
        return value.Substring(0, Math.Max(0, maxLength - 24)) + "\n... [text truncated]";
    }
}
