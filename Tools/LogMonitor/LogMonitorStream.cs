#nullable disable

using BepInEx.Logging;
using System;
using System.Text;
using System.Threading;
using UnityEngine;

namespace ValheimProfiler.Tools.LogMonitor;

internal sealed partial class LogMonitorTool
{
    private void DrawStreamTab()
    {
        DrawStreamFilters();
        DrawHistoryControls();
        EnsureStreamView();

        float detailsHeight = _selectedEntries.Count == 1 && _selectedEntry != null ? 132f : 0f;
        float listHeight = Mathf.Max(130f, MainWindowHeight - 212f - detailsHeight);

        DrawStreamHeader();
        DrawStreamRows(listHeight);

        if (_selectedEntries.Count == 1 && _selectedEntry != null)
        {
            GUILayout.Space(3f);
            DrawStreamDetails(detailsHeight);
        }
    }

    private void DrawStreamFilters()
    {
        GUILayout.BeginHorizontal();

        DrawLevelFilterButton("D", "Debug", LogLevel.Debug);
        DrawLevelFilterButton("I", "Info", LogLevel.Info);
        DrawLevelFilterButton("M", "Message", LogLevel.Message);
        DrawLevelFilterButton("W", "Warning", LogLevel.Warning);
        DrawLevelFilterButton("E", "Error", LogLevel.Error);
        DrawLevelFilterButton("F", "Fatal", LogLevel.Fatal);

        GUILayout.Space(6f);

        bool newFollow = ProfilerGui.ToggleLayout(
            _theme,
            _followStream,
            new GUIContent("Follow", "Keep the stream scrolled to the newest matching entry."),
            82f,
            _labelStyle);
        if (newFollow != _followStream)
        {
            _followStream = newFollow;
            if (_followStream)
                _scrollStreamToEnd = true;
        }

        GUILayout.Space(6f);
        Label("Search:", GUILayout.Width(50f));

        string newSearch = GUILayout.TextField(
            _streamSearch ?? string.Empty,
            GUILayout.MinWidth(130f),
            GUILayout.MaxWidth(360f),
            GUILayout.ExpandWidth(true));
        if (!string.Equals(newSearch, _streamSearch, StringComparison.Ordinal))
        {
            _streamSearch = newSearch;
            _streamViewDirty = true;
            if (_followStream)
                _scrollStreamToEnd = true;
        }

        GUI.enabled = !string.IsNullOrEmpty(_streamSearch);
        if (GUILayout.Button("Clear", GUILayout.Width(54f)))
        {
            _streamSearch = string.Empty;
            _streamViewDirty = true;
        }
        GUI.enabled = true;

        GUI.enabled = _filteredStream.Count > 0 || _entries.Count > 0;
        if (GUILayout.Button(new GUIContent("Copy filtered", "Copy all currently filtered Stream rows in chronological order."), GUILayout.Width(94f)))
        {
            EnsureStreamView();
            CopyFilteredStream();
        }
        GUI.enabled = true;

        GUI.enabled = _selectedEntries.Count > 0;
        if (GUILayout.Button(
                new GUIContent(
                    "Copy selected",
                    $"Copy {_selectedEntries.Count} selected Stream row(s) in chronological order. Use Ctrl-click to toggle rows and Shift-click to select a range."),
                GUILayout.Width(100f)))
        {
            CopySelectedStream();
        }
        GUI.enabled = true;

        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        GUILayout.Label(
            new GUIContent(
                "Selection: Shift-click selects a range | Ctrl-click toggles individual rows",
                "Click a row for a single selection. Hold Shift to select a continuous range, Ctrl to add or remove individual rows, or Ctrl+Shift to add a range."),
            _labelStyle,
            GUILayout.ExpandWidth(true));
        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && _selectedEntries.Count > 0;
        if (GUILayout.Button("Clear selection", GUILayout.Width(104f)))
            ClearStreamSelection();
        GUI.enabled = oldEnabled;

        bool includeMetadata = ProfilerGui.ToggleLayout(
            _theme,
            _app.Config.LogMonitorCopyMetadata.Value,
            new GUIContent(
                "Copy metadata",
                "Include timestamp, thread, scene and history/sequence metadata and use the expanded two-line clipboard format. Disabled preserves the compact BepInEx LogOutput-style header and raw message."),
            125f,
            _labelStyle);
        if (includeMetadata != _app.Config.LogMonitorCopyMetadata.Value)
            _app.Config.LogMonitorCopyMetadata.Value = includeMetadata;
        GUILayout.EndHorizontal();
        GUILayout.Space(2f);
    }

    private void DrawHistoryControls()
    {
        GUILayout.BeginHorizontal();

        bool loading = Volatile.Read(ref _historyLoadInProgress) != 0;
        GUI.enabled = !loading && (_historyHasMore || _historyCursor < 0L);
        if (GUILayout.Button(loading ? "Loading..." : "Load older", GUILayout.Width(88f)))
            RequestOlderHistory();
        GUI.enabled = true;

        GUI.enabled = _loadedHistoryEntries > 0 && !loading;
        if (GUILayout.Button("Unload history", GUILayout.Width(104f)))
            UnloadHistory();
        GUI.enabled = true;

        Label($"History: {_loadedHistoryEntries} | More: {(_historyHasMore ? "yes" : "no")}", GUILayout.Width(150f));
        Label(_historyStatus, GUILayout.ExpandWidth(true));

        GUILayout.EndHorizontal();
        GUILayout.Space(2f);
    }

    private void DrawLevelFilterButton(string text, string levelName, LogLevel level)
    {
        bool enabled = (_streamLevelFilter & level) != 0;
        GUIStyle style = enabled ? _theme.AccentButtonStyle : GUI.skin.button;
        if (!GUILayout.Button(new GUIContent(text, $"Show or hide {levelName} log entries."), style, GUILayout.Width(30f)))
            return;

        if (enabled)
            _streamLevelFilter &= ~level;
        else
            _streamLevelFilter |= level;

        _streamViewDirty = true;
    }

    private void EnsureStreamView()
    {
        if (!_streamViewDirty)
            return;

        _filteredStream.Clear();

        for (int i = 0; i < _entries.Count; i++)
        {
            LogEntry entry = _entries[i];
            if ((_streamLevelFilter & entry.Level) == 0)
                continue;
            if (!MatchesSearch(entry, _streamSearch))
                continue;

            _filteredStream.Add(entry);
        }

        _streamViewDirty = false;
        PruneStreamSelectionToFilteredView();
    }

    private void CopyFilteredStream()
    {
        var builder = new StringBuilder(Math.Min(1024 * 1024, Math.Max(256, _filteredStream.Count * 128)));
        for (int i = 0; i < _filteredStream.Count; i++)
        {
            if (i > 0)
            {
                builder.AppendLine();
                if (_app.Config.LogMonitorCopyMetadata.Value)
                    builder.AppendLine();
            }
            builder.Append(_filteredStream[i].GetClipboardText(_app.Config.LogMonitorCopyMetadata.Value));
        }

        GUIUtility.systemCopyBuffer = builder.ToString();
    }

    private void CopySelectedStream()
    {
        EnsureStreamView();

        var builder = new StringBuilder(Math.Min(1024 * 1024, Math.Max(256, _selectedEntries.Count * 128)));
        int copied = 0;
        for (int i = 0; i < _filteredStream.Count; i++)
        {
            LogEntry entry = _filteredStream[i];
            if (!_selectedEntries.Contains(entry))
                continue;

            if (copied++ > 0)
            {
                builder.AppendLine();
                if (_app.Config.LogMonitorCopyMetadata.Value)
                    builder.AppendLine();
            }
            builder.Append(entry.GetClipboardText(_app.Config.LogMonitorCopyMetadata.Value));
        }

        if (copied > 0)
            GUIUtility.systemCopyBuffer = builder.ToString();
    }

    private void HandleStreamRowClick(int index, LogEntry entry, bool control, bool shift)
    {
        if (shift)
        {
            int anchorIndex = _selectionAnchorEntry == null
                ? -1
                : _filteredStream.IndexOf(_selectionAnchorEntry);
            if (anchorIndex < 0)
                anchorIndex = index;

            if (!control)
                _selectedEntries.Clear();

            int first = Math.Min(anchorIndex, index);
            int last = Math.Max(anchorIndex, index);
            for (int i = first; i <= last; i++)
                _selectedEntries.Add(_filteredStream[i]);

            _selectedEntry = entry;
            if (_selectionAnchorEntry == null)
                _selectionAnchorEntry = entry;
            return;
        }

        if (control)
        {
            if (!_selectedEntries.Add(entry))
                _selectedEntries.Remove(entry);

            _selectionAnchorEntry = entry;
            _selectedEntry = _selectedEntries.Contains(entry)
                ? entry
                : FindFirstSelectedEntryInView();
            if (_selectedEntries.Count == 0)
                ClearStreamSelection();
            return;
        }

        if (_selectedEntries.Count == 1 && _selectedEntries.Contains(entry))
        {
            ClearStreamSelection();
            return;
        }

        _selectedEntries.Clear();
        _selectedEntries.Add(entry);
        _selectedEntry = entry;
        _selectionAnchorEntry = entry;
    }

    private void ClearStreamSelection()
    {
        _selectedEntries.Clear();
        _selectedEntry = null;
        _selectionAnchorEntry = null;
        _streamDetailsScroll = default;
    }

    private void RemoveEntryFromStreamSelection(LogEntry entry)
    {
        if (entry == null)
            return;

        _selectedEntries.Remove(entry);
        if (ReferenceEquals(_selectionAnchorEntry, entry))
            _selectionAnchorEntry = null;
        if (ReferenceEquals(_selectedEntry, entry))
            _selectedEntry = FindFirstSelectedEntryInView();

        if (_selectedEntries.Count == 0)
            ClearStreamSelection();
        else if (_selectionAnchorEntry == null)
            _selectionAnchorEntry = _selectedEntry;
    }

    private LogEntry FindFirstSelectedEntryInView()
    {
        for (int i = 0; i < _filteredStream.Count; i++)
        {
            if (_selectedEntries.Contains(_filteredStream[i]))
                return _filteredStream[i];
        }

        return null;
    }

    private void PruneStreamSelectionToFilteredView()
    {
        if (_selectedEntries.Count == 0)
            return;

        var visible = new System.Collections.Generic.HashSet<LogEntry>(_filteredStream);
        _selectedEntries.RemoveWhere(entry => !visible.Contains(entry));

        if (_selectedEntries.Count == 0)
        {
            ClearStreamSelection();
            return;
        }

        if (_selectedEntry == null || !_selectedEntries.Contains(_selectedEntry))
            _selectedEntry = FindFirstSelectedEntryInView();
        if (_selectionAnchorEntry == null || !_selectedEntries.Contains(_selectionAnchorEntry))
            _selectionAnchorEntry = _selectedEntry;
    }

    private void DrawStreamHeader()
    {
        Rect rect = GUILayoutUtility.GetRect(1f, HeaderHeight, GUILayout.ExpandWidth(true), GUILayout.Height(HeaderHeight));
        GetStreamColumnWidths(rect.width, out float timeWidth, out float levelWidth, out float sourceWidth, out float messageWidth);

        float x = rect.x;
        DrawHeaderCell(ref x, rect.y, timeWidth, rect.height, "Time", "Local event time. Unity timestamps are removed from Message and kept here.");
        DrawHeaderCell(ref x, rect.y, levelWidth, rect.height, "Level", "BepInEx log level.");
        DrawHeaderCell(ref x, rect.y, sourceWidth, rect.height, "Source", "BepInEx logger source. Unity messages are normally forwarded through a Unity Log source.");
        DrawHeaderCell(ref x, rect.y, messageWidth, rect.height, "Message", "First line of the captured log event. Click to select one row, Ctrl-click to toggle rows, or Shift-click to select a range.");
    }

    private void DrawStreamRows(float height)
    {
        if (_scrollStreamToEnd)
            _streamScroll.y = float.MaxValue;

        _streamScroll = GUILayout.BeginScrollView(
            _streamScroll,
            false,
            false,
            GUILayout.Height(height));

        float contentHeight = Mathf.Max(1f, _filteredStream.Count * RowHeight);
        Rect contentRect = GUILayoutUtility.GetRect(
            1f,
            contentHeight,
            GUILayout.ExpandWidth(true),
            GUILayout.Height(contentHeight));

        float visibleTop = _streamScroll.y - VirtualizationOverscan;
        float visibleBottom = _streamScroll.y + height + VirtualizationOverscan;
        int start = Mathf.Clamp(Mathf.FloorToInt(visibleTop / RowHeight), 0, _filteredStream.Count);
        int end = Mathf.Clamp(Mathf.CeilToInt(visibleBottom / RowHeight), 0, _filteredStream.Count);

        GetStreamColumnWidths(contentRect.width, out float timeWidth, out float levelWidth, out float sourceWidth, out float messageWidth);

        for (int i = start; i < end; i++)
        {
            LogEntry entry = _filteredStream[i];
            float y = contentRect.y + i * RowHeight;
            Rect rowRect = new(contentRect.x, y, contentRect.width, RowHeight);

            if (_selectedEntries.Contains(entry))
            {
                GUIStyle selectionStyle = ReferenceEquals(_selectedEntry, entry)
                    ? _theme.AccentButtonStyle
                    : GUI.skin.box;
                GUI.Box(rowRect, GUIContent.none, selectionStyle);
            }

            Event currentEvent = Event.current;
            bool control = currentEvent.control || currentEvent.command;
            bool shift = currentEvent.shift;
            if (GUI.Button(rowRect, GUIContent.none, GUIStyle.none))
                HandleStreamRowClick(i, entry, control, shift);

            float x = rowRect.x;
            DrawCell(ref x, y, timeWidth, RowHeight, entry.TimeText, _labelStyle);

            Color previous = GUI.contentColor;
            GUI.contentColor = GetLevelColor(entry.Level);
            DrawCell(ref x, y, levelWidth, RowHeight, entry.LevelText, _labelStyle);
            GUI.contentColor = previous;

            DrawCell(ref x, y, sourceWidth, RowHeight, entry.Source, _labelStyle);
            DrawCell(ref x, y, messageWidth, RowHeight, entry.Message, _labelStyle);
        }

        GUILayout.EndScrollView();

        if (_scrollStreamToEnd && Event.current.type == EventType.Repaint)
            _scrollStreamToEnd = false;
    }

    private void DrawStreamDetails(float height)
    {
        LogEntry entry = _selectedEntry;
        if (entry == null)
            return;

        GUILayout.BeginVertical(GUI.skin.box, GUILayout.Height(height));
        GUILayout.BeginHorizontal();

        string selectionPrefix = _selectedEntries.Count > 1 ? $"{_selectedEntries.Count} selected | " : string.Empty;
        HeaderLabel($"{selectionPrefix}{entry.TimeText} [{entry.LevelText}:{entry.Source}]", GUILayout.ExpandWidth(true));
        if (GUILayout.Button(_selectedEntries.Count > 1 ? "Copy selected" : "Copy", GUILayout.Width(_selectedEntries.Count > 1 ? 100f : 58f)))
        {
            if (_selectedEntries.Count > 1)
                CopySelectedStream();
            else
                GUIUtility.systemCopyBuffer = entry.GetClipboardText(_app.Config.LogMonitorCopyMetadata.Value);
        }
        if (GUILayout.Button("Close", GUILayout.Width(58f)))
            ClearStreamSelection();

        GUILayout.EndHorizontal();

        string origin = entry.IsHistorical ? $"History offset: {entry.FileOffset}" : $"Sequence: {entry.Sequence}";
        Label($"{origin} | Thread: {entry.ThreadId} | Scene: {(string.IsNullOrEmpty(entry.Scene) ? "(none)" : entry.Scene)}");

        _streamDetailsScroll = GUILayout.BeginScrollView(_streamDetailsScroll, GUILayout.ExpandHeight(true));
        GUILayout.Label(BuildFullText(entry.Message, entry.Details), _detailsStyle, GUILayout.ExpandWidth(true));
        GUILayout.EndScrollView();

        GUILayout.EndVertical();
    }

    private static bool MatchesSearch(LogEntry entry, string search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;

        return Contains(entry.Source, search) ||
               Contains(entry.Message, search) ||
               Contains(entry.RawMessage, search) ||
               Contains(entry.Details, search) ||
               Contains(entry.Scene, search) ||
               Contains(entry.LevelText, search);
    }

    private static string BuildFullText(string message, string details)
    {
        if (string.IsNullOrEmpty(details))
            return message ?? string.Empty;
        if (string.IsNullOrEmpty(message))
            return details;
        return message + Environment.NewLine + details;
    }

    private static void GetStreamColumnWidths(
        float availableWidth,
        out float timeWidth,
        out float levelWidth,
        out float sourceWidth,
        out float messageWidth)
    {
        timeWidth = 92f;
        levelWidth = 72f;
        sourceWidth = Mathf.Clamp(availableWidth * 0.24f, 130f, 260f);
        messageWidth = Mathf.Max(160f, availableWidth - timeWidth - levelWidth - sourceWidth);
    }
}
