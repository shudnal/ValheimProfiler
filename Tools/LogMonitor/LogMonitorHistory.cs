#nullable disable

using BepInEx;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

namespace ValheimProfiler.Tools.LogMonitor;

internal sealed partial class LogMonitorTool
{
    private enum HistoryLoadMode
    {
        AutomaticBackfill,
        ManualOlder
    }

    private sealed class HistoryLoadResult
    {
        internal HistoryLoadMode Mode;
        internal LogFilePage Page;
    }

    private string LogFilePath => Path.Combine(Paths.BepInExRootPath, "LogOutput.log");

    private void UpdateHistoryLoading()
    {
        if (_automaticBackfillScheduled && !_automaticBackfillAttempted &&
            Time.realtimeSinceStartup >= _automaticBackfillDueRealtime)
        {
            if (QueueHistoryLoad(HistoryLoadMode.AutomaticBackfill, 0L))
                _automaticBackfillAttempted = true;
        }

        HistoryLoadResult result = null;
        lock (_historyResultSync)
        {
            if (_pendingHistoryResult != null)
            {
                result = _pendingHistoryResult;
                _pendingHistoryResult = null;
            }
        }

        if (result != null)
        {
            Interlocked.Exchange(ref _historyLoadInProgress, 0);
            ApplyHistoryResult(result);
        }
    }

    private void RequestOlderHistory()
    {
        if (Volatile.Read(ref _historyLoadInProgress) != 0)
            return;

        long cursor = _historyCursor > 0L ? _historyCursor : 0L;
        QueueHistoryLoad(HistoryLoadMode.ManualOlder, cursor);
    }

    private bool QueueHistoryLoad(HistoryLoadMode mode, long beforeOffset)
    {
        if (Interlocked.CompareExchange(ref _historyLoadInProgress, 1, 0) != 0)
            return false;

        _historyStatus = mode == HistoryLoadMode.AutomaticBackfill
            ? "Loading startup history..."
            : "Loading older entries...";

        int maxEntries = mode == HistoryLoadMode.AutomaticBackfill
            ? Math.Max(2000, _app.Config.LogMonitorHistoryPageEntries.Value * 4)
            : _app.Config.LogMonitorHistoryPageEntries.Value;
        int maxBytes = mode == HistoryLoadMode.AutomaticBackfill ? 16 * 1024 * 1024 : 4 * 1024 * 1024;
        string path = LogFilePath;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            HistoryLoadResult result;
            try
            {
                result = new HistoryLoadResult
                {
                    Mode = mode,
                    Page = LogFileHistoryReader.ReadOlder(path, beforeOffset, maxEntries, maxBytes)
                };
            }
            catch (Exception ex)
            {
                result = new HistoryLoadResult
                {
                    Mode = mode,
                    Page = new LogFilePage { Error = ex.GetType().Name + ": " + ex.Message }
                };
            }

            lock (_historyResultSync)
                _pendingHistoryResult = result;
        });
        return true;
    }

    private void ApplyHistoryResult(HistoryLoadResult result)
    {
        LogFilePage page = result.Page;
        if (!string.IsNullOrEmpty(page.Error))
        {
            _historyStatus = page.Error;
            return;
        }

        if (_historyFileCreationUtcTicks != 0L && page.FileCreationUtcTicks != 0L &&
            _historyFileCreationUtcTicks != page.FileCreationUtcTicks)
        {
            UnloadHistory();
            _historyStatus = "LogOutput.log changed; loaded history was reset.";
        }

        _historyFileCreationUtcTicks = page.FileCreationUtcTicks;
        _historyHasMore = page.HasMore;
        _historyCursor = page.NextCursor;

        if (page.Entries.Count == 0)
        {
            _historyStatus = page.HasMore ? "No complete entries found in this page." : "No older entries.";
            return;
        }

        var loaded = new List<LogEntry>(page.Entries.Count);
        for (int i = 0; i < page.Entries.Count; i++)
        {
            ParsedLogFileEntry parsed = page.Entries[i];
            loaded.Add(new LogEntry
            {
                Sequence = --_nextHistoricalSequence,
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

        int addCount = loaded.Count;
        bool boundaryLoad = result.Mode == HistoryLoadMode.AutomaticBackfill || _loadedHistoryEntries == 0;
        bool overlapFound = false;
        if (boundaryLoad && _entries.Count > 0)
        {
            int overlap = LogHistoryMerge.FindOverlap(
                loaded,
                _entries,
                item => item.Fingerprint,
                item => item.Fingerprint);
            if (overlap >= 0)
            {
                addCount = overlap;
                overlapFound = true;
            }
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

        if (_followStream && result.Mode != HistoryLoadMode.ManualOlder)
            _scrollStreamToEnd = true;

        string overlapText = boundaryLoad && !overlapFound && _entries.Count > addCount
            ? " Startup overlap was not found; repeated file records may remain visible."
            : string.Empty;
        _historyStatus = $"Loaded {addCount} older entries. Total history: {_loadedHistoryEntries}." + overlapText;
    }
}
