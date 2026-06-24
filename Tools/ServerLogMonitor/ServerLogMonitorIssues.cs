#nullable disable

using BepInEx.Logging;
using System;
using System.Linq;
using UnityEngine;

namespace ValheimProfiler.Tools.ServerLogMonitor;

internal sealed partial class ServerLogMonitorTool
{
    private void DrawIssuesTab()
    {
        DrawIssueFilters();
        EnsureIssueView();

        float detailsHeight = _selectedIssue != null ? 146f : 0f;
        float listHeight = Mathf.Max(130f, MainWindowHeight - 166f - detailsHeight);

        DrawIssueHeader();
        DrawIssueRows(listHeight);

        if (_selectedIssue != null)
        {
            GUILayout.Space(3f);
            DrawIssueDetails(detailsHeight);
        }
    }

    private void DrawIssueFilters()
    {
        GUILayout.BeginHorizontal();

        bool newWarnings = ProfilerGui.ToggleLayout(
            _theme,
            _includeWarningsInIssues,
            new GUIContent(
                "Include warnings",
                "Include Warning groups alongside Error and Fatal groups. Warning entries are always retained in the raw Stream while their level filter is enabled."),
            135f,
            _labelStyle);
        if (newWarnings != _includeWarningsInIssues)
        {
            _includeWarningsInIssues = newWarnings;
            _issuesViewDirty = true;
        }

        GUILayout.Space(8f);
        Label("Search:", GUILayout.Width(50f));

        string newSearch = GUILayout.TextField(
            _issuesSearch ?? string.Empty,
            GUILayout.MinWidth(160f),
            GUILayout.MaxWidth(440f),
            GUILayout.ExpandWidth(true));
        if (!string.Equals(newSearch, _issuesSearch, StringComparison.Ordinal))
        {
            _issuesSearch = newSearch;
            _issuesViewDirty = true;
        }

        bool controlsEnabled = GUI.enabled;
        GUI.enabled = controlsEnabled && !string.IsNullOrEmpty(_issuesSearch);
        if (GUILayout.Button("Clear", GUILayout.Width(54f)))
        {
            _issuesSearch = string.Empty;
            _issuesViewDirty = true;
        }
        GUI.enabled = controlsEnabled;

        GUILayout.EndHorizontal();
        GUILayout.Space(2f);
    }

    private void EnsureIssueView()
    {
        if (!_issuesViewDirty)
            return;

        var filtered = _issues.Where(group =>
            (_includeWarningsInIssues || !IsWarningLevel(group.Level)) &&
            MatchesSearch(group, _issuesSearch));

        _filteredIssues.Clear();
        _filteredIssues.AddRange(_issueSortColumn switch
        {
            IssueSortColumn.FirstSeen => filtered.OrderByDescending(group => group.FirstSeen),
            IssueSortColumn.LastSeen => filtered.OrderByDescending(group => group.LastSeen),
            IssueSortColumn.Level => filtered.OrderByDescending(group => SeverityRank(group.Level))
                .ThenByDescending(group => group.LastSeen),
            IssueSortColumn.Source => filtered.OrderByDescending(group => group.Source, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(group => group.Count),
            IssueSortColumn.Message => filtered.OrderByDescending(group => group.Message, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(group => group.Count),
            _ => filtered.OrderByDescending(group => group.Count)
                .ThenByDescending(group => group.LastSeen)
        });

        _issuesViewDirty = false;
    }

    private void DrawIssueHeader()
    {
        Rect rect = GUILayoutUtility.GetRect(1f, HeaderHeight, GUILayout.ExpandWidth(true), GUILayout.Height(HeaderHeight));
        GetIssueColumnWidths(
            rect.width,
            out float levelWidth,
            out float countWidth,
            out float firstWidth,
            out float lastWidth,
            out float sourceWidth,
            out float messageWidth);

        float x = rect.x;
        DrawIssueHeaderCell(ref x, rect.y, levelWidth, rect.height, "Level", "Highest severity represented by this exact issue group.", IssueSortColumn.Level);
        DrawIssueHeaderCell(ref x, rect.y, countWidth, rect.height, "Count", "Number of identical captured events since Clear or group creation.", IssueSortColumn.Count);
        DrawIssueHeaderCell(ref x, rect.y, firstWidth, rect.height, "First", "Time this exact group was first observed.", IssueSortColumn.FirstSeen);
        DrawIssueHeaderCell(ref x, rect.y, lastWidth, rect.height, "Last", "Time this exact group was most recently observed.", IssueSortColumn.LastSeen);
        DrawIssueHeaderCell(ref x, rect.y, sourceWidth, rect.height, "Source", "BepInEx logger source.", IssueSortColumn.Source);
        DrawIssueHeaderCell(ref x, rect.y, messageWidth, rect.height, "Message", "First line of the grouped event.", IssueSortColumn.Message);
    }

    private void DrawIssueRows(float height)
    {
        _issuesScroll = GUILayout.BeginScrollView(
            _issuesScroll,
            false,
            false,
            GUILayout.Height(height));

        float contentHeight = Mathf.Max(1f, _filteredIssues.Count * RowHeight);
        Rect contentRect = GUILayoutUtility.GetRect(
            1f,
            contentHeight,
            GUILayout.ExpandWidth(true),
            GUILayout.Height(contentHeight));

        float visibleTop = _issuesScroll.y - VirtualizationOverscan;
        float visibleBottom = _issuesScroll.y + height + VirtualizationOverscan;
        int start = Mathf.Clamp(Mathf.FloorToInt(visibleTop / RowHeight), 0, _filteredIssues.Count);
        int end = Mathf.Clamp(Mathf.CeilToInt(visibleBottom / RowHeight), 0, _filteredIssues.Count);

        GetIssueColumnWidths(
            contentRect.width,
            out float levelWidth,
            out float countWidth,
            out float firstWidth,
            out float lastWidth,
            out float sourceWidth,
            out float messageWidth);

        for (int i = start; i < end; i++)
        {
            IssueGroup group = _filteredIssues[i];
            float y = contentRect.y + i * RowHeight;
            Rect rowRect = new(contentRect.x, y, contentRect.width, RowHeight);

            if (ReferenceEquals(_selectedIssue, group))
                GUI.Box(rowRect, GUIContent.none, GUI.skin.box);

            if (GUI.Button(rowRect, GUIContent.none, GUIStyle.none))
                _selectedIssue = ReferenceEquals(_selectedIssue, group) ? null : group;

            float x = rowRect.x;
            Color previous = GUI.contentColor;
            GUI.contentColor = GetLevelColor(group.Level);
            DrawCell(ref x, y, levelWidth, RowHeight, group.LevelText, _labelStyle);
            GUI.contentColor = previous;

            DrawCell(ref x, y, countWidth, RowHeight, group.Count.ToString(), _labelStyle);
            DrawCell(ref x, y, firstWidth, RowHeight, group.FirstSeen == default ? "--:--:--" : group.FirstSeen.ToString("HH:mm:ss"), _labelStyle);
            DrawCell(ref x, y, lastWidth, RowHeight, group.LastSeen == default ? "--:--:--" : group.LastSeen.ToString("HH:mm:ss"), _labelStyle);
            DrawCell(ref x, y, sourceWidth, RowHeight, group.Source, _labelStyle);
            DrawCell(ref x, y, messageWidth, RowHeight, group.Message, _labelStyle);
        }

        GUILayout.EndScrollView();
    }

    private void DrawIssueDetails(float height)
    {
        IssueGroup group = _selectedIssue;
        if (group == null)
            return;

        GUILayout.BeginVertical(GUI.skin.box, GUILayout.Height(height));
        GUILayout.BeginHorizontal();

        HeaderLabel($"[{group.LevelText}:{group.Source}] Count: {group.Count}", GUILayout.ExpandWidth(true));
        if (GUILayout.Button("Copy", GUILayout.Width(58f)))
            GUIUtility.systemCopyBuffer = group.GetClipboardText();
        if (GUILayout.Button("Close", GUILayout.Width(58f)))
            _selectedIssue = null;

        GUILayout.EndHorizontal();

        Label(
            $"First: {(group.FirstSeen == default ? "unknown" : group.FirstSeen.ToString("HH:mm:ss.fff"))} | Last: {(group.LastSeen == default ? "unknown" : group.LastSeen.ToString("HH:mm:ss.fff"))} | " +
            $"Last thread: {group.LastThreadId} | Last scene: {(string.IsNullOrEmpty(group.Scene) ? "(none)" : group.Scene)}");

        _issueDetailsScroll = GUILayout.BeginScrollView(_issueDetailsScroll, GUILayout.ExpandHeight(true));
        GUILayout.Label(BuildFullText(group.Message, group.Details), _detailsStyle, GUILayout.ExpandWidth(true));
        GUILayout.EndScrollView();

        GUILayout.EndVertical();
    }

    private void DrawIssueHeaderCell(
        ref float x,
        float y,
        float width,
        float height,
        string text,
        string tooltip,
        IssueSortColumn column)
    {
        Rect rect = new(x, y, width, height);
        GUIStyle style = _issueSortColumn == column ? _activeHeaderLabelStyle : _headerLabelStyle;
        if (GUI.Button(rect, new GUIContent(text ?? string.Empty, tooltip ?? string.Empty), style))
        {
            _issueSortColumn = column;
            _app.Config.ServerLogIssueSortColumn.Value = column.ToString();
            _issuesViewDirty = true;
        }

        x += width;
    }

    private static bool MatchesSearch(IssueGroup group, string search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;

        return Contains(group.Source, search) ||
               Contains(group.Message, search) ||
               Contains(group.Details, search) ||
               Contains(group.Scene, search) ||
               Contains(group.LevelText, search);
    }

    private static int SeverityRank(LogLevel level)
    {
        if ((level & LogLevel.Fatal) != 0)
            return 3;
        if ((level & LogLevel.Error) != 0)
            return 2;
        if ((level & LogLevel.Warning) != 0)
            return 1;
        return 0;
    }

    private static void GetIssueColumnWidths(
        float availableWidth,
        out float levelWidth,
        out float countWidth,
        out float firstWidth,
        out float lastWidth,
        out float sourceWidth,
        out float messageWidth)
    {
        levelWidth = 72f;
        countWidth = 66f;
        firstWidth = 76f;
        lastWidth = 76f;
        sourceWidth = Mathf.Clamp(availableWidth * 0.22f, 125f, 240f);
        messageWidth = Mathf.Max(160f, availableWidth - levelWidth - countWidth - firstWidth - lastWidth - sourceWidth);
    }
}
