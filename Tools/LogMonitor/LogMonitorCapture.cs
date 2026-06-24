#nullable disable

using BepInEx.Logging;
using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ValheimProfiler.Tools.LogMonitor;

internal sealed partial class LogMonitorTool
{
    private const int MaxPendingEntries = 20000;
    private const int MaxDrainPerFrame = 1000;
    private const int MaxStoredTextLength = 65536;

    private sealed class LogMonitorListener : ILogListener
    {
        private LogMonitorTool _owner;

        internal LogMonitorListener(LogMonitorTool owner)
        {
            _owner = owner;
        }

        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            _owner?.Enqueue(eventArgs);
        }

        public void Dispose()
        {
            _owner = null;
        }
    }

    private void Enqueue(LogEventArgs eventArgs)
    {
        if (!_captureEnabled || eventArgs == null)
            return;

        int pending = Interlocked.Increment(ref _pendingCount);
        if (pending > MaxPendingEntries)
        {
            Interlocked.Decrement(ref _pendingCount);
            Interlocked.Increment(ref _droppedEntries);
            return;
        }

        try
        {
            ParseData(eventArgs.Data, out string rawMessage, out string details);
            string source = LimitText(eventArgs.Source?.SourceName ?? "Unknown", 256);
            string message = LogMonitorText.NormalizeMessage(source, rawMessage, out DateTime unityTimestamp);
            DateTime timestamp = unityTimestamp != default ? unityTimestamp : DateTime.Now;

            _pending.Enqueue(new PendingLogEvent
            {
                Sequence = Interlocked.Increment(ref _nextSequence),
                Timestamp = timestamp,
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
            Interlocked.Increment(ref _droppedEntries);
        }
    }

    private void DrainPending()
    {
        string scene = GetActiveSceneName();
        int drained = 0;

        while (drained < MaxDrainPerFrame && _pending.TryDequeue(out PendingLogEvent pending))
        {
            Interlocked.Decrement(ref _pendingCount);

            var entry = new LogEntry
            {
                Sequence = pending.Sequence,
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
            };

            AddLiveEntry(entry);
            if (!_automaticBackfillScheduled && LogMonitorText.IsChainloaderStartupComplete(entry.Source, entry.Message))
            {
                _automaticBackfillScheduled = true;
                _automaticBackfillDueRealtime = Time.realtimeSinceStartup + 0.5f;
            }

            drained++;
        }

        if (drained <= 0)
            return;

        MarkViewsDirty();
        if (_followStream)
            _scrollStreamToEnd = true;
    }

    private void AddLiveEntry(LogEntry entry)
    {
        _entries.Add(entry);
        _capturedEntries++;

        bool trimmed = TrimLiveEntriesToLimit();
        if (trimmed)
            RebuildIssues();
        else if (IsIssueLevel(entry.Level))
            AddIssue(entry);
    }

    private bool TrimLiveEntriesToLimit()
    {
        int maxEntries = Math.Max(500, _app.Config.LogMonitorMaxEntries.Value);
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

    private void AddIssue(LogEntry entry)
    {
        string key = BuildIssueKey(entry);
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

        int maxGroups = Math.Max(100, _app.Config.LogMonitorMaxIssueGroups.Value);
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
            LogEntry entry = _entries[i];
            if (IsIssueLevel(entry.Level))
                AddIssue(entry);
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

        IssueGroup oldestGroup = _issues[oldestIndex];
        if (ReferenceEquals(_selectedIssue, oldestGroup))
            _selectedIssue = null;

        _issues.RemoveAt(oldestIndex);
        _issuesByKey.Remove(oldestGroup.Key);
    }

    private void ClearCapturedData()
    {
        while (_pending.TryDequeue(out _))
            Interlocked.Decrement(ref _pendingCount);

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
        _capturedEntries = 0;
        _historyCursor = -1L;
        _historyHasMore = false;
        _loadedHistoryEntries = 0;
        _historyStatus = string.Empty;
        Interlocked.Exchange(ref _droppedEntries, 0);
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
        _historyCursor = -1L;
        _historyHasMore = true;
        _historyStatus = removed ? "Loaded history removed." : "No loaded history.";
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

    private static bool IsIssueLevel(LogLevel level) =>
        (level & (LogLevel.Warning | LogLevel.Error | LogLevel.Fatal)) != 0;

    private static bool IsWarningLevel(LogLevel level) =>
        (level & LogLevel.Warning) != 0 && (level & (LogLevel.Error | LogLevel.Fatal)) == 0;

    private static string BuildIssueKey(LogEntry entry) => entry.Fingerprint;

    private static string GetLevelText(LogLevel level) => LogMonitorText.GetLevelText(level);
}
