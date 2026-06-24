#nullable disable

using BepInEx.Logging;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimProfiler.Tools.ServerLogMonitor;

internal sealed partial class ServerLogMonitorTool
{
    private void ProbeServer()
    {
        if (!IsDedicatedServerConnectionDetected() || _probePending)
            return;

        _availability = AvailabilityState.Detecting;
        _availabilityStatus = "Checking whether the dedicated server supports remote log monitoring...";
        if (!SendRequest(ServerLogRequestKind.Probe))
        {
            _availability = AvailabilityState.ServerModUnavailable;
            _availabilityStatus = "The dedicated server RPC connection is not ready.";
            _nextProbeRealtime = Time.realtimeSinceStartup + ProbeRetrySeconds;
            return;
        }

        _probePending = true;
        _probeDeadlineRealtime = Time.realtimeSinceStartup + ProbeTimeoutSeconds;
    }

    private void Subscribe()
    {
        if (!IsAvailable || _subscribed || _subscriptionRequestPending)
            return;

        if (!SendRequest(ServerLogRequestKind.Subscribe))
            return;

        _subscriptionRequestPending = true;
        _requestDeadlineRealtime = Time.realtimeSinceStartup + RequestTimeoutSeconds;
        _availabilityStatus = "Requesting the recent dedicated-server log snapshot...";
        _mainTab = MainTab.Stream;
    }

    private void Unsubscribe()
    {
        if (_subscribed)
            SendRequest(ServerLogRequestKind.Unsubscribe);

        _subscribed = false;
        _subscriptionRequestPending = false;
        _historyRequestPending = false;
        _availabilityStatus = "Dedicated server log access is available. Subscribe to receive entries.";
    }

    private void RequestOlderHistory(bool startupBackfill = false)
    {
        if (!_subscribed || _historyRequestPending || !_historyHasMore)
            return;

        if (!SendRequest(
                ServerLogRequestKind.RequestOlder,
                _sessionId,
                _historyCursor,
                _lastLiveSequence,
                _historyFileCreationUtcTicks))
            return;

        _historyRequestPending = true;
        _requestDeadlineRealtime = Time.realtimeSinceStartup + RequestTimeoutSeconds;
        _historyStatus = startupBackfill
            ? "Loading server startup history..."
            : "Requesting older server log entries...";
    }

    private bool SendRequest(
        ServerLogRequestKind kind,
        string sessionId = "",
        long cursor = 0L,
        long lastSequence = 0L,
        long fileCreationUtcTicks = 0L)
    {
        try
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null)
                return false;

            long serverPeerId = rpc.GetServerPeerID();
            if (serverPeerId == 0L)
                return false;

            ZPackage payload = ServerLogProtocol.CreateRequest(
                kind,
                sessionId,
                cursor,
                lastSequence,
                fileCreationUtcTicks,
                false);
            if (payload.Size() > ServerLogProtocol.MaxRequestPayloadBytes)
                return false;

            return ServerLogTransport.Send(
                serverPeerId,
                ServerLogProtocol.RequestRpc,
                payload,
                out _);
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

            long serverPeerId = rpc.GetServerPeerID();
            if (serverPeerId != 0L && sender != serverPeerId)
                return;

            ServerLogTransportReceiveResult transportResult = ServerLogTransport.TryReceive(
                sender,
                package,
                out ZPackage responsePayload,
                out string transportError);
            if (transportResult == ServerLogTransportReceiveResult.WaitingForFragments)
                return;
            if (transportResult == ServerLogTransportReceiveResult.Rejected)
                throw new InvalidOperationException("Invalid Server Log Monitor transport payload: " + transportError);
            if (responsePayload.Size() > ServerLogProtocol.MaxResponsePayloadBytes)
                throw new InvalidOperationException("Oversized Server Log Monitor response payload.");

            ServerLogResponse response = ServerLogProtocol.ReadResponse(responsePayload);
            _probePending = false;
            _serverVersion = response.ServerVersion ?? string.Empty;
            _serverDroppedCount = response.DroppedCount;
            _serverAuthorized = response.Authorized;

            ApplyCapabilityState(response);

            switch (response.Kind)
            {
                case ServerLogResponseKind.Capabilities:
                    return;
                case ServerLogResponseKind.Status:
                {
                    bool historyRequestWasPending = _historyRequestPending;
                    _subscriptionRequestPending = false;
                    _historyRequestPending = false;
                    _availabilityStatus = string.IsNullOrWhiteSpace(response.Status)
                        ? _availabilityStatus
                        : response.Status;
                    if (historyRequestWasPending)
                        _historyStatus = _availabilityStatus;
                    return;
                }
                case ServerLogResponseKind.Error:
                    _subscriptionRequestPending = false;
                    _historyRequestPending = false;
                    if (!response.Authorized)
                    {
                        _subscribed = false;
                        if (IsWindowVisible)
                            _mainTab = MainTab.Help;
                    }
                    _availabilityStatus = string.IsNullOrWhiteSpace(response.Status)
                        ? "The dedicated server rejected the log request."
                        : response.Status;
                    _historyStatus = _availabilityStatus;
                    return;
                case ServerLogResponseKind.Snapshot:
                    ApplySnapshot(response);
                    return;
                case ServerLogResponseKind.LiveBatch:
                    ApplyLiveBatch(response);
                    return;
                case ServerLogResponseKind.OlderPage:
                    ApplyOlderPage(response);
                    return;
            }
        }
        catch (Exception ex)
        {
            _probePending = false;
            _subscriptionRequestPending = false;
            _historyRequestPending = false;
            _subscribed = false;
            _availability = AvailabilityState.ProtocolMismatch;
            _availabilityStatus = "Server Log Monitor protocol error: " + ex.Message;
            _nextProbeRealtime = Time.realtimeSinceStartup + ProbeRetrySeconds;
            if (IsWindowVisible)
                _mainTab = MainTab.Help;
        }
    }

    private void ApplyCapabilityState(ServerLogResponse response)
    {
        if (!response.Authorized)
        {
            _subscribed = false;
            _subscriptionRequestPending = false;
            _availability = AvailabilityState.AccessDenied;
            _availabilityStatus = string.IsNullOrWhiteSpace(response.Status)
                ? "Valheim Profiler is installed, but the current player is not in the server admin list."
                : response.Status;
            _nextProbeRealtime = Time.realtimeSinceStartup + ProbeRetrySeconds;
            return;
        }

        _availability = AvailabilityState.Available;
        _availabilityStatus = string.IsNullOrWhiteSpace(response.Status)
            ? "Dedicated server log access is available. Subscribe to start a private stream."
            : response.Status;
        _nextProbeRealtime = float.MaxValue;
    }

    private void ApplySnapshot(ServerLogResponse response)
    {
        _subscriptionRequestPending = false;
        _historyRequestPending = false;

        bool sessionChanged = !string.Equals(_sessionId, response.SessionId, StringComparison.Ordinal);
        bool fileChanged = response.ResetHistory ||
                           (_historyFileCreationUtcTicks != 0L && response.FileCreationUtcTicks != 0L &&
                            _historyFileCreationUtcTicks != response.FileCreationUtcTicks);
        long previousHistoryCursor = _historyCursor;
        long previousHistoryStartCursor = _historyStartCursor;
        bool preserveHistoryProgress = !sessionChanged && !fileChanged &&
                                       (_loadedHistoryEntries > 0 || previousHistoryCursor != previousHistoryStartCursor);

        if (sessionChanged || fileChanged)
            ClearLocalView(resetSession: true);
        else
            RemoveLiveEntries();

        _sessionId = response.SessionId ?? string.Empty;
        _serverDroppedCount = response.DroppedCount;
        _subscribed = true;
        _historyStartCursor = response.HistoryCursor;
        if (preserveHistoryProgress)
        {
            _historyCursor = previousHistoryCursor;
            _historyHasMore = previousHistoryCursor > 0L;
        }
        else
        {
            _historyCursor = response.HistoryCursor;
            _historyHasMore = response.HasMoreHistory;
        }
        _historyFileCreationUtcTicks = response.FileCreationUtcTicks;
        _availabilityStatus = string.IsNullOrWhiteSpace(response.Status)
            ? "Subscribed to the dedicated server log."
            : response.Status;

        for (int i = 0; i < response.Entries.Count; i++)
            AddLiveEntry(Convert(response.Entries[i], historicalOverride: false));

        _lastLiveSequence = response.LastSequence;
        MarkViewsDirty();
        if (_followStream)
            _scrollStreamToEnd = true;

        if (_historyHasMore &&
            !_historyRequestPending &&
            !string.Equals(_startupHistoryRequestedSession, _sessionId, StringComparison.Ordinal))
        {
            _startupHistoryRequestedSession = _sessionId;
            RequestOlderHistory(startupBackfill: true);
        }
    }

    private void ApplyLiveBatch(ServerLogResponse response)
    {
        if (!_subscribed || !string.Equals(_sessionId, response.SessionId, StringComparison.Ordinal))
        {
            _subscribed = false;
            Subscribe();
            return;
        }

        if (response.Entries.Count == 0)
            return;

        long first = response.FirstSequence != 0L ? response.FirstSequence : response.Entries[0].Sequence;
        if (_lastLiveSequence > 0L && first > _lastLiveSequence + 1L)
        {
            if (!_subscriptionRequestPending)
            {
                _detectedGaps++;
                _availabilityStatus = $"Detected a server log sequence gap after {_lastLiveSequence}; requesting a recent snapshot.";
                if (SendRequest(ServerLogRequestKind.Resync, _sessionId, 0L, _lastLiveSequence, _historyFileCreationUtcTicks))
                {
                    _subscriptionRequestPending = true;
                    _requestDeadlineRealtime = Time.realtimeSinceStartup + RequestTimeoutSeconds;
                }
            }
            return;
        }

        bool added = false;
        for (int i = 0; i < response.Entries.Count; i++)
        {
            ServerLogWireEntry wire = response.Entries[i];
            if (wire.Sequence <= _lastLiveSequence)
                continue;

            AddLiveEntry(Convert(wire, historicalOverride: false));
            _lastLiveSequence = wire.Sequence;
            added = true;
        }

        if (!added)
            return;

        MarkViewsDirty();
        if (_followStream)
            _scrollStreamToEnd = true;
    }

    private void ApplyOlderPage(ServerLogResponse response)
    {
        _historyRequestPending = false;

        if (!_subscribed || !string.Equals(_sessionId, response.SessionId, StringComparison.Ordinal))
        {
            _historyStatus = "The server log session changed; subscribe again before loading history.";
            return;
        }

        if (_historyFileCreationUtcTicks != 0L && response.FileCreationUtcTicks != 0L &&
            _historyFileCreationUtcTicks != response.FileCreationUtcTicks)
        {
            UnloadHistory();
            _historyStatus = "The server LogOutput.log file changed; loaded history was removed.";
            return;
        }

        _historyFileCreationUtcTicks = response.FileCreationUtcTicks;
        _historyCursor = response.HistoryCursor;
        _historyHasMore = response.HasMoreHistory;

        if (response.Entries.Count == 0)
        {
            _historyStatus = string.IsNullOrWhiteSpace(response.Status) ? "No older server log entries." : response.Status;
            return;
        }

        var loaded = new List<LogEntry>(response.Entries.Count);
        for (int i = 0; i < response.Entries.Count; i++)
            loaded.Add(Convert(response.Entries[i], historicalOverride: true));

        int addCount = loaded.Count;
        if (_entries.Count > 0)
        {
            int overlap = LogHistoryMerge.FindOverlap(loaded, _entries, item => item.Fingerprint, item => item.Fingerprint);
            if (overlap >= 0)
                addCount = overlap;
        }

        if (addCount > 0)
        {
            if (addCount < loaded.Count)
                loaded.RemoveRange(addCount, loaded.Count - addCount);
            _entries.InsertRange(0, loaded);
            _loadedHistoryEntries += loaded.Count;
            RebuildIssues();
            MarkViewsDirty();
        }

        _historyStatus = $"Loaded {addCount} older server entries. Total history: {_loadedHistoryEntries}.";
    }

    private LogEntry Convert(ServerLogWireEntry wire, bool historicalOverride)
    {
        return new LogEntry
        {
            Sequence = wire.Sequence,
            Timestamp = wire.Timestamp,
            Level = wire.Level,
            Source = wire.Source ?? string.Empty,
            RawMessage = wire.RawMessage ?? string.Empty,
            Message = wire.Message ?? string.Empty,
            Details = wire.Details ?? string.Empty,
            Scene = wire.Scene ?? string.Empty,
            ThreadId = wire.ThreadId,
            IsHistorical = historicalOverride || wire.IsHistorical,
            FileOffset = wire.FileOffset
        };
    }

    private void AddLiveEntry(LogEntry entry)
    {
        _entries.Add(entry);
        bool trimmed = TrimLiveEntriesToLimit();
        if (trimmed)
            RebuildIssues();
        else if (IsIssueLevel(entry.Level))
            AddIssue(entry);
    }

    private bool TrimLiveEntriesToLimit()
    {
        int maxEntries = Math.Max(500, _app.Config.ServerLogClientMaxEntries.Value);
        int liveCount = 0;
        for (int i = 0; i < _entries.Count; i++)
        {
            if (!_entries[i].IsHistorical)
                liveCount++;
        }

        if (liveCount <= maxEntries)
            return false;

        int removeNeeded = Math.Max(liveCount - maxEntries, Math.Max(1, maxEntries / 10));
        int firstLive = 0;
        while (firstLive < _entries.Count && _entries[firstLive].IsHistorical)
            firstLive++;

        int removeCount = Math.Min(removeNeeded, _entries.Count - firstLive);
        if (removeCount <= 0)
            return false;

        for (int i = firstLive; i < firstLive + removeCount; i++)
            RemoveEntryFromStreamSelection(_entries[i]);

        _entries.RemoveRange(firstLive, removeCount);
        return true;
    }

    private void RemoveLiveEntries()
    {
        int firstLive = 0;
        while (firstLive < _entries.Count && _entries[firstLive].IsHistorical)
            firstLive++;

        if (firstLive >= _entries.Count)
            return;

        for (int i = firstLive; i < _entries.Count; i++)
            RemoveEntryFromStreamSelection(_entries[i]);
        _entries.RemoveRange(firstLive, _entries.Count - firstLive);
        RebuildIssues();
    }

    private void AddIssue(LogEntry entry)
    {
        string key = entry.Fingerprint;
        if (_issuesByKey.TryGetValue(key, out IssueGroup group))
        {
            group.Count++;
            if (group.FirstSeen == default || (entry.Timestamp != default && entry.Timestamp < group.FirstSeen))
            {
                group.FirstSeen = entry.Timestamp;
                group.FirstSequence = entry.Sequence;
            }
            if (group.LastSeen == default || entry.Timestamp >= group.LastSeen)
            {
                group.LastSeen = entry.Timestamp;
                group.LastSequence = entry.Sequence;
                group.Scene = entry.Scene;
                group.LastThreadId = entry.ThreadId;
                group.Details = entry.Details;
            }
            return;
        }

        int maxGroups = Math.Max(100, _app.Config.ServerLogMaxIssueGroups.Value);
        if (_issues.Count >= maxGroups)
            RemoveOldestIssueGroup();

        group = new IssueGroup
        {
            Key = key,
            Level = entry.Level,
            Source = entry.Source,
            Message = entry.Message,
            Details = entry.Details,
            Scene = entry.Scene,
            LastThreadId = entry.ThreadId,
            Count = 1,
            FirstSeen = entry.Timestamp,
            LastSeen = entry.Timestamp,
            FirstSequence = entry.Sequence,
            LastSequence = entry.Sequence
        };
        _issuesByKey[key] = group;
        _issues.Add(group);
    }

    private void RebuildIssues()
    {
        _issues.Clear();
        _issuesByKey.Clear();
        _selectedIssue = null;
        for (int i = 0; i < _entries.Count; i++)
        {
            if (IsIssueLevel(_entries[i].Level))
                AddIssue(_entries[i]);
        }
        _issuesViewDirty = true;
    }

    private void RemoveOldestIssueGroup()
    {
        if (_issues.Count == 0)
            return;

        int oldestIndex = 0;
        DateTime oldest = _issues[0].LastSeen;
        for (int i = 1; i < _issues.Count; i++)
        {
            if (_issues[i].LastSeen >= oldest)
                continue;
            oldest = _issues[i].LastSeen;
            oldestIndex = i;
        }

        IssueGroup group = _issues[oldestIndex];
        if (ReferenceEquals(_selectedIssue, group))
            _selectedIssue = null;
        _issues.RemoveAt(oldestIndex);
        _issuesByKey.Remove(group.Key);
    }

    private void ClearLocalView(bool resetSession = false)
    {
        _entries.Clear();
        _issues.Clear();
        _issuesByKey.Clear();
        _filteredStream.Clear();
        _filteredIssues.Clear();
        ClearStreamSelection();
        _selectedIssue = null;
        _streamScroll = default;
        _issuesScroll = default;
        _streamDetailsScroll = default;
        _issueDetailsScroll = default;
        _loadedHistoryEntries = 0;
        _historyStatus = string.Empty;

        if (resetSession)
        {
            _sessionId = string.Empty;
            _lastLiveSequence = 0L;
            _historyCursor = 0L;
            _historyStartCursor = 0L;
            _historyFileCreationUtcTicks = 0L;
            _historyHasMore = false;
            _detectedGaps = 0;
            _serverDroppedCount = 0L;
            _startupHistoryRequestedSession = string.Empty;
        }
        else
        {
            _historyCursor = _historyStartCursor;
            _historyHasMore = _historyCursor > 0L;
        }

        MarkViewsDirty();
    }

    private void UnloadHistory()
    {
        bool removed = false;
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            if (!_entries[i].IsHistorical)
                continue;
            RemoveEntryFromStreamSelection(_entries[i]);
            _entries.RemoveAt(i);
            removed = true;
        }

        _loadedHistoryEntries = 0;
        _historyCursor = _historyStartCursor;
        _historyHasMore = _historyCursor > 0L;
        _historyStatus = removed ? "Loaded server history removed." : "No loaded server history.";
        if (removed)
        {
            RebuildIssues();
            MarkViewsDirty();
        }
    }

    private void MarkViewsDirty()
    {
        _streamViewDirty = true;
        _issuesViewDirty = true;
    }

    private static bool IsIssueLevel(LogLevel level) =>
        (level & (LogLevel.Warning | LogLevel.Error | LogLevel.Fatal)) != 0;

    private static bool IsWarningLevel(LogLevel level) =>
        (level & LogLevel.Warning) != 0 && (level & (LogLevel.Error | LogLevel.Fatal)) == 0;
}
