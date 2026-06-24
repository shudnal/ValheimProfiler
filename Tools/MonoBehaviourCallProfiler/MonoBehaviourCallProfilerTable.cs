#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ValheimProfiler.Tools.MonoBehaviourCallProfiler;

internal sealed partial class MonoBehaviourCallProfilerTool
{
    private void PrepareTableLayout()
    {
        bool layoutChanged =
            _layoutMetricsDirty ||
            _lastLayoutSkin != GUI.skin ||
            Mathf.Abs(_lastLayoutWindowWidth - MainWindowWidth) > 0.01f ||
            _lastLayoutGroupByMod != _groupByMod;

        if (!layoutChanged)
            return;

        _lastLayoutSkin = GUI.skin;
        _lastLayoutWindowWidth = MainWindowWidth;
        _lastLayoutGroupByMod = _groupByMod;
        _layoutMetricsDirty = false;

        _drawTimeColumnWidth = TimeColumnWidth;
        _drawCountColumnWidth = CountColumnWidth;
        _drawTimelineColumnWidth = TimelineColumnWidth;
        _drawSourceColumnWidth = CalculateSourceColumnWidth();
        _drawTypeColumnWidth = CalculateTypeColumnWidth();

        float fixedColumns =
            _drawCountColumnWidth +
            _drawTimeColumnWidth * 6f +
            _drawTimelineColumnWidth * 2f;

        float availableTypeWidth =
            MainWindowWidth -
            WindowHorizontalPadding -
            _drawSourceColumnWidth -
            fixedColumns;

        _drawTypeColumnWidth = Mathf.Max(_drawTypeColumnWidth, availableTypeWidth);
        _drawContentWidth =
            _drawSourceColumnWidth +
            fixedColumns +
            _drawTypeColumnWidth;
    }

    private float CalculateSourceColumnWidth()
    {
        if (_labelStyle == null)
            return 240f;

        string header = _groupByMod ? "Method" : "Mod │ Method";
        float width = _headerLabelStyle.CalcSize(new GUIContent(header)).x + 16f;

        if (_groupByMod)
        {
            foreach (GroupRowView group in _cachedGroupedRows)
            {
                float groupWidth = GroupToggleWidth +
                    _groupLabelStyle.CalcSize(new GUIContent(group.GroupName ?? "Unknown")).x + 16f;
                width = Mathf.Max(width, groupWidth);

                bool expanded;
                lock (_lock)
                    expanded = _groupExpanded.TryGetValue(group.GroupId, out bool value) && value;

                if (!expanded)
                    continue;

                foreach (FlatRowView row in group.Rows)
                {
                    float methodWidth = _labelStyle.CalcSize(new GUIContent(BuildEntryMethodName(row.Entry))).x + 12f;
                    width = Mathf.Max(width, methodWidth);
                }
            }
        }
        else
        {
            foreach (FlatRowView row in _cachedFlatRows)
            {
                float entryWidth = _labelStyle.CalcSize(new GUIContent(BuildUngroupedEntryName(row.Entry))).x + 12f;
                width = Mathf.Max(width, entryWidth);
            }
        }

        return Mathf.Clamp(Mathf.Ceil(width), MinSourceColumnWidth, MaxSourceColumnWidth);
    }

    private float CalculateTypeColumnWidth()
    {
        if (_labelStyle == null)
            return TypeColumnMinWidth;

        float width = _headerLabelStyle.CalcSize(new GUIContent("MonoBehaviour")).x + 16f;
        IEnumerable<FlatRowView> rows = _groupByMod
            ? _cachedGroupedRows.SelectMany(group => group.Rows)
            : _cachedFlatRows;

        foreach (FlatRowView row in rows)
        {
            string typeName = row.Entry?.TypeEntry?.TypeName ?? "Unknown";
            width = Mathf.Max(width, _labelStyle.CalcSize(new GUIContent(typeName)).x + 12f);
        }

        return Mathf.Max(TypeColumnMinWidth, Mathf.Ceil(width));
    }

    private void DrawTableHeader()
    {
        Rect rect = GUILayoutUtility.GetRect(
            1f,
            CurrentHeaderHeight,
            GUILayout.ExpandWidth(true),
            GUILayout.Height(CurrentHeaderHeight));

        GUI.BeginGroup(rect);
        try
        {
            float x = -_profilerScroll.x;
            float y = 0f;
            float height = rect.height;

            DrawSortableHeaderCell(
                ref x,
                y,
                _drawSourceColumnWidth,
                height,
                _groupByMod ? "Method" : "Mod │ Method",
                "Measured method. In grouped mode the group name is shown in its own expandable row.\nClick to sort descending by this column.",
                TableSortColumn.Source);
            DrawSortableHeaderCell(ref x, y, _drawCountColumnWidth, height, "calls",
                "Total calls observed since the current profiling session started.\nClick to sort descending by this column.",
                TableSortColumn.Calls);
            DrawSortableHeaderCell(ref x, y, _drawTimeColumnWidth, height, "total ms",
                "Total inclusive CPU time accumulated by this method during the current profiling session.\nClick to sort descending by this column.",
                TableSortColumn.Total);
            DrawSortableHeaderCell(ref x, y, _drawTimeColumnWidth, height, "avg ms",
                "Average inclusive duration per call over the complete profiling session.\nClick to sort descending by this column.",
                TableSortColumn.Average);
            DrawSortableHeaderCell(ref x, y, _drawTimeColumnWidth, height, "max ms",
                "Slowest individual call observed during the complete profiling session.\nClick to sort descending by this column.",
                TableSortColumn.Max);
            DrawSortableHeaderCell(ref x, y, _drawTimeColumnWidth, height, "last ms",
                "Duration of the most recently observed call.\nClick to sort descending by this column.",
                TableSortColumn.Last);
            DrawSortableHeaderCell(ref x, y, _drawTimeColumnWidth, height, "p95",
                "Approximate lifetime 95th percentile based on a logarithmic histogram.\nGrouped rows show the highest child percentile, not a combined percentile.\nClick to sort descending by this column.",
                TableSortColumn.P95);
            DrawSortableHeaderCell(ref x, y, _drawTimeColumnWidth, height, "p99",
                "Approximate lifetime 99th percentile based on a logarithmic histogram.\nGrouped rows show the highest child percentile, not a combined percentile.\nClick to sort descending by this column.",
                TableSortColumn.P99);
            DrawSortableHeaderCell(ref x, y, _drawTimelineColumnWidth, height, "first at",
                "Elapsed profiling-session time when the first call was observed.\nClick to sort descending by this column.",
                TableSortColumn.FirstAt);
            DrawSortableHeaderCell(ref x, y, _drawTimelineColumnWidth, height, "last at",
                "Elapsed profiling-session time when the latest call was observed.\nClick to sort descending by this column.",
                TableSortColumn.LastAt);
            DrawSortableHeaderCell(ref x, y, _drawTypeColumnWidth, height, "MonoBehaviour",
                "Managed MonoBehaviour runtime type represented by the row.\nClick to sort descending by this column.",
                TableSortColumn.MonoBehaviour);
        }
        finally
        {
            GUI.EndGroup();
        }
    }

    private void DrawSortableHeaderCell(
        ref float x,
        float y,
        float width,
        float height,
        string text,
        string tooltip,
        TableSortColumn column)
    {
        Rect rect = new Rect(x, y, width, height);
        GUIStyle style = CurrentSortColumn == column ? _activeHeaderLabelStyle : _headerLabelStyle;
        if (GUI.Button(rect, new GUIContent(text ?? string.Empty, tooltip ?? string.Empty), style))
            SetSortColumn(column);

        x += width;
    }

    private float CalculateTableContentHeight()
    {
        if (!_groupByMod)
            return Mathf.Max(1f, _cachedFlatRows.Count * CurrentRowHeight);

        float height = 0f;

        lock (_lock)
        {
            foreach (GroupRowView group in _cachedGroupedRows)
            {
                height += CurrentGroupHeight;

                if (_groupExpanded.TryGetValue(group.GroupId, out bool expanded) && expanded)
                    height += group.Rows.Count * CurrentRowHeight;
            }
        }

        return Mathf.Max(1f, height);
    }

    private void DrawTableContent(Rect contentRect)
    {
        float visibleTop = _profilerScroll.y - VirtualizationOverscan;
        float visibleBottom = _profilerScroll.y + Mathf.Max(100f, MainWindowHeight - 145f) + VirtualizationOverscan;
        float y = 0f;

        if (_groupByMod)
        {
            foreach (GroupRowView group in _cachedGroupedRows)
            {
                DrawGroupRow(contentRect, ref y, group, visibleTop, visibleBottom);

                bool expanded;
                lock (_lock)
                    expanded = _groupExpanded.TryGetValue(group.GroupId, out bool value) && value;

                if (!expanded)
                    continue;

                foreach (FlatRowView row in group.Rows)
                    DrawDataRow(contentRect, ref y, row, true, visibleTop, visibleBottom);
            }
        }
        else
        {
            foreach (FlatRowView row in _cachedFlatRows)
                DrawDataRow(contentRect, ref y, row, false, visibleTop, visibleBottom);
        }
    }

    private void DrawGroupRow(
        Rect contentRect,
        ref float y,
        GroupRowView group,
        float visibleTop,
        float visibleBottom)
    {
        float height = CurrentGroupHeight;

        if (!RowVisible(y, height, visibleTop, visibleBottom))
        {
            y += height;
            return;
        }

        bool expanded;
        lock (_lock)
            expanded = _groupExpanded.TryGetValue(group.GroupId, out bool value) && value;

        float x = contentRect.x;
        float rowY = contentRect.y + y;
        Rect buttonRect = InsetButtonRect(new Rect(x, rowY, GroupToggleWidth, height));
        bool toggleRequested = GUI.Button(buttonRect, expanded ? "▼" : "▶", _compactButtonStyle);

        x += GroupToggleWidth;
        float nameWidth = _drawSourceColumnWidth - GroupToggleWidth;
        toggleRequested |= GUI.Button(
            new Rect(x, rowY, nameWidth, height),
            group.GroupName,
            _groupLabelStyle);
        x += nameWidth;

        if (toggleRequested)
        {
            lock (_lock)
                _groupExpanded[group.GroupId] = !expanded;

            _layoutMetricsDirty = true;
        }

        DrawSummaryCells(ref x, rowY, height, group.Summary);
        DrawCell(ref x, rowY, _drawTypeColumnWidth, height, string.Empty, _labelStyle);
        y += height;
    }

    private void DrawDataRow(
        Rect contentRect,
        ref float y,
        FlatRowView row,
        bool grouped,
        float visibleTop,
        float visibleBottom)
    {
        float height = CurrentRowHeight;

        if (!RowVisible(y, height, visibleTop, visibleBottom))
        {
            y += height;
            return;
        }

        BehaviourMethodEntry entry = row.Entry;
        float x = contentRect.x;
        float rowY = contentRect.y + y;
        string entryName = grouped ? BuildEntryMethodName(entry) : BuildUngroupedEntryName(entry);

        DrawCell(ref x, rowY, _drawSourceColumnWidth, height, entryName, _labelStyle, entry.Tooltip);
        DrawSnapshotCells(ref x, rowY, height, row.Snapshot);
        DrawCell(ref x, rowY, _drawTypeColumnWidth, height, entry.TypeEntry.TypeName, _labelStyle);
        y += height;
    }

    private void DrawSummaryCells(ref float x, float y, float height, GroupSummary summary)
    {
        DrawCell(ref x, y, _drawCountColumnWidth, height, FormatCount(summary.Calls), _labelStyle);
        DrawCell(ref x, y, _drawTimeColumnWidth, height, FormatMs(summary.TotalMs), _labelStyle);
        DrawCell(ref x, y, _drawTimeColumnWidth, height, FormatMs(summary.AverageMs), _labelStyle);
        DrawCell(ref x, y, _drawTimeColumnWidth, height, FormatMs(summary.MaxMs), _labelStyle);
        DrawCell(ref x, y, _drawTimeColumnWidth, height, FormatMs(summary.LastMs), _labelStyle);
        DrawCell(ref x, y, _drawTimeColumnWidth, height, FormatMs(summary.P95Ms), _labelStyle);
        DrawCell(ref x, y, _drawTimeColumnWidth, height, FormatMs(summary.P99Ms), _labelStyle);
        DrawCell(ref x, y, _drawTimelineColumnWidth, height, FormatTimeline(summary.FirstCallAtSeconds), _labelStyle);
        DrawCell(ref x, y, _drawTimelineColumnWidth, height, FormatTimeline(summary.LastCallAtSeconds), _labelStyle);
    }

    private void DrawSnapshotCells(ref float x, float y, float height, LifetimeProfilerSnapshot snapshot)
    {
        DrawCell(ref x, y, _drawCountColumnWidth, height, FormatCount(snapshot.Calls), _labelStyle);
        DrawCell(ref x, y, _drawTimeColumnWidth, height, FormatMs(snapshot.TotalMs), _labelStyle);
        DrawCell(ref x, y, _drawTimeColumnWidth, height, FormatMs(snapshot.AverageMs), _labelStyle);
        DrawCell(ref x, y, _drawTimeColumnWidth, height, FormatMs(snapshot.MaxMs), _labelStyle);
        DrawCell(ref x, y, _drawTimeColumnWidth, height, FormatMs(snapshot.LastMs), _labelStyle);
        DrawCell(ref x, y, _drawTimeColumnWidth, height, FormatMs(snapshot.P95Ms), _labelStyle);
        DrawCell(ref x, y, _drawTimeColumnWidth, height, FormatMs(snapshot.P99Ms), _labelStyle);
        DrawCell(ref x, y, _drawTimelineColumnWidth, height, FormatTimeline(snapshot.FirstCallAtSeconds), _labelStyle);
        DrawCell(ref x, y, _drawTimelineColumnWidth, height, FormatTimeline(snapshot.LastCallAtSeconds), _labelStyle);
    }

    private static bool RowVisible(float y, float height, float visibleTop, float visibleBottom) =>
        y + height >= visibleTop && y <= visibleBottom;

    private static Rect InsetButtonRect(Rect rect)
    {
        rect.x += 1f;
        rect.y += 3f;
        rect.width = Mathf.Max(1f, rect.width - 3f);
        rect.height = Mathf.Max(1f, rect.height - 6f);
        return rect;
    }

    private static void DrawCell(
        ref float x,
        float y,
        float width,
        float height,
        string text,
        GUIStyle style,
        string tooltip = null)
    {
        GUI.Label(
            new Rect(x, y, width, height),
            new GUIContent(text ?? string.Empty, tooltip ?? string.Empty),
            style);
        x += width;
    }
}
