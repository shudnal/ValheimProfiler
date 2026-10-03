#nullable disable

using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimProfiler.Tools.GeneralMethodProfiler;

internal sealed partial class GeneralMethodProfilerTool
{
    private const float CallTreeButtonGap = 4f;

    private void PrepareTableLayout()
    {
        if (!_layoutMetricsDirty &&
            Mathf.Abs(_lastLayoutWindowWidth - MainWindowWidth) < 0.1f &&
            _lastLayoutProfilerView == _profilerView &&
            _lastLayoutGroupByMod == _groupByMod &&
            _lastLayoutSkin == GUI.skin)
        {
            return;
        }

        _drawCallTreeButtonWidth = Mathf.Ceil(Mathf.Max(72f,
            _compactButtonStyle.CalcSize(new GUIContent("Call tree")).x + 4f));
        _drawTimeColumnWidth = CalculateHeaderAwareWidth(
            TimeColumnWidth,
            "raw max ▼",
            "2nd max ▼",
            "3rd max ▼",
            "avg >p99 ▼",
            "p99 ▼",
            "avg >p95 ▼",
            "p95 ▼",
            "000.000!");

        _drawCountColumnWidth = CalculateHeaderAwareWidth(
            CountColumnWidth,
            "> p99 ▼",
            "> p95 ▼",
            "samples ▼",
            "gc samples ▼",
            "9999999+");

        _drawAvgTimeColumnWidth = CalculateHeaderAwareWidth(
            AvgTimeColumnWidth,
            "ms per frame ▼",
            "000.000");

        _drawAvgCountColumnWidth = CalculateHeaderAwareWidth(
            AvgCountColumnWidth,
            "calls per frame ▼",
            "9999999+");

        _drawAvgCallColumnWidth = CalculateHeaderAwareWidth(
            AvgCallColumnWidth,
            "avg call ▼",
            "999.99 us");

        List<FlatRowView> layoutRows = GetLayoutRows();
        _drawModColumnWidth = CalculateModColumnWidth();
        _drawClassColumnWidth = CalculateClassColumnWidth(layoutRows);

        float statsWidth = _profilerView == ProfilerView.Avg1s
            ? _drawAvgTimeColumnWidth + _drawAvgCountColumnWidth + _drawAvgCallColumnWidth
            : _drawTimeColumnWidth * 7f + _drawCountColumnWidth * 4f;

        float fixedWidth = _drawModColumnWidth + statsWidth + _drawClassColumnWidth;
        float availableWidth = Mathf.Max(300f, MainWindowWidth - WindowHorizontalPadding);
        float remainingWidth = Mathf.Max(MethodColumnMinWidth, availableWidth - fixedWidth);
        _drawMethodColumnWidth = Mathf.Max(remainingWidth, CalculateMethodColumnWidth(layoutRows));
        _drawContentWidth = fixedWidth + _drawMethodColumnWidth;
        _lastLayoutWindowWidth = MainWindowWidth;
        _lastLayoutProfilerView = _profilerView;
        _lastLayoutGroupByMod = _groupByMod;
        _lastLayoutSkin = GUI.skin;
        _layoutMetricsDirty = false;
    }

    private float CalculateHeaderAwareWidth(float minimum, params string[] values)
    {
        float width = minimum;
        if (_headerLabelStyle == null)
            return width;

        foreach (string value in values)
            width = Mathf.Max(width, _headerLabelStyle.CalcSize(new GUIContent(value)).x + 6f);

        return Mathf.Ceil(width);
    }

    private float CalculateModColumnWidth()
    {
        string header = _groupByMod ? "Mod" : "BepInEx mod";
        float width = _headerLabelStyle.CalcSize(new GUIContent(header + " ▲")).x + 16f;
        if (_groupByMod)
        {
            foreach (GroupRowView group in _cachedGroupedRows)
                width = Mathf.Max(width, GroupToggleWidth +
                    _groupLabelStyle.CalcSize(new GUIContent(GetGroupDisplayName(group))).x + 16f);
        }
        else
        {
            foreach (FlatRowView row in _cachedFlatRows)
                width = Mathf.Max(width, _labelStyle.CalcSize(new GUIContent(GetModName(row.Entry))).x + 16f);
        }
        return Mathf.Clamp(Mathf.Ceil(width), MinModColumnWidth, MaxModColumnWidth);
    }

    private float CalculateClassColumnWidth(List<FlatRowView> rows)
    {
        float width = _headerLabelStyle.CalcSize(new GUIContent("Class ▲")).x + 16f;
        foreach (FlatRowView row in rows)
            width = Mathf.Max(width, _labelStyle.CalcSize(new GUIContent(GetClassName(row.Entry))).x + 16f);
        // Fit the actual longest class, including namespaces and nested types. Wide tables scroll.
        return Mathf.Ceil(width);
    }

    private float CalculateMethodColumnWidth(List<FlatRowView> rows)
    {
        float width = _headerLabelStyle.CalcSize(new GUIContent("Class:Method ▲")).x + 16f;
        foreach (FlatRowView row in rows)
            width = Mathf.Max(width, _drawCallTreeButtonWidth + CallTreeButtonGap +
                _labelStyle.CalcSize(new GUIContent(GetMethodDisplayName(row.Entry))).x + 16f);
        return Mathf.Clamp(Mathf.Ceil(width), MethodColumnMinWidth, MethodColumnMaxWidth);
    }

    private List<FlatRowView> GetLayoutRows()
    {
        if (!_groupByMod)
            return _cachedFlatRows;

        var rows = new List<FlatRowView>();
        foreach (GroupRowView group in _cachedGroupedRows)
        {
            if (_groupExpanded.TryGetValue(group.GroupId, out bool expanded) && expanded)
                rows.AddRange(group.Rows);
        }
        return rows;
    }

    private string GetGroupDisplayName(GroupRowView group)
    {
        string name = group.GroupName ?? "Unknown";
        return _profilerView == ProfilerView.MaxOver60Sec && name.Length > MaxGroupedModNameCharsOnMax60
            ? name.Substring(0, MaxGroupedModNameCharsOnMax60 - 1) + "…"
            : name;
    }

    private static string GetModName(GeneralMethodEntry entry) =>
        entry?.TypeEntry?.GroupName ?? entry?.TypeEntry?.AssemblyName ?? "Unknown";

    private static string GetClassName(GeneralMethodEntry entry) =>
        entry?.TypeEntry?.TypeName ?? "Unknown";

    private static string GetMethodDisplayName(GeneralMethodEntry entry) =>
        entry?.TableDisplayName ?? "Unknown:Unknown()";

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
                _drawModColumnWidth,
                height,
                _groupByMod ? "Mod" : "BepInEx mod",
                "BepInEx mod associated with the method. Grouped child rows do not repeat the mod name.\nClick to sort ascending by this text column.",
                TableSortColumn.Mod);

            if (_profilerView == ProfilerView.Avg1s)
            {
                DrawSortableHeaderCell(ref x, y, _drawAvgTimeColumnWidth, height, "ms per frame",
                    "Accepted measured time during the rolling one-second window divided by Target FPS. This estimates the method's contribution to one target frame even when the game's current FPS is different.\nClick to sort descending by this column.",
                    TableSortColumn.AvgMsPerFrame);
                DrawSortableHeaderCell(ref x, y, _drawAvgCountColumnWidth, height, "calls per frame",
                    "Accepted calls during the rolling one-second window divided by Target FPS. Empty target frames are included in this fixed denominator.\nClick to sort descending by this column.",
                    TableSortColumn.AvgCallsPerFrame);
                DrawSortableHeaderCell(ref x, y, _drawAvgCallColumnWidth, height, "avg call",
                    "Average duration of one accepted measured call during the rolling one-second window. Units automatically switch between ms, us and ns. This value does not depend on Target FPS.\nClick to sort descending by this column.",
                    TableSortColumn.AvgCall);
            }
            else
            {
                DrawSortableHeaderCell(ref x, y, _drawTimeColumnWidth, height, "raw max",
                    "Slowest individual method invocation in the rolling 60-second window.\nA trailing ! marks an isolated spike or a high GC-associated sample.\nClick to sort descending by this column.",
                    TableSortColumn.RawMax);
                DrawSortableHeaderCell(ref x, y, _drawTimeColumnWidth, height, "2nd max",
                    "Second-slowest individual method invocation in the rolling 60-second window.\nClick to sort descending by this column.",
                    TableSortColumn.SecondMax);
                DrawSortableHeaderCell(ref x, y, _drawTimeColumnWidth, height, "3rd max",
                    "Third-slowest individual method invocation in the rolling 60-second window.\nClick to sort descending by this column.",
                    TableSortColumn.ThirdMax);

                DrawSortableHeaderCell(ref x, y, _drawCountColumnWidth, height, "> p99",
                    "Number of samples above the approximate p99 histogram boundary.\nClick to sort descending by this column.",
                    TableSortColumn.AboveP99);
                DrawSortableHeaderCell(ref x, y, _drawTimeColumnWidth, height, "avg >p99",
                    "Average execution time of samples above the approximate p99 boundary.\nClick to sort descending by this column.",
                    TableSortColumn.AvgAboveP99);
                DrawSortableHeaderCell(ref x, y, _drawTimeColumnWidth, height, "p99",
                    "Approximate 99th percentile: about 99% of method invocations completed at or below this duration.\nClick to sort descending by this column.",
                    TableSortColumn.P99);

                DrawSortableHeaderCell(ref x, y, _drawCountColumnWidth, height, "> p95",
                    "Number of samples above the approximate p95 histogram boundary.\nClick to sort descending by this column.",
                    TableSortColumn.AboveP95);
                DrawSortableHeaderCell(ref x, y, _drawTimeColumnWidth, height, "avg >p95",
                    "Average execution time of samples above the approximate p95 boundary.\nClick to sort descending by this column.",
                    TableSortColumn.AvgAboveP95);
                DrawSortableHeaderCell(ref x, y, _drawTimeColumnWidth, height, "p95",
                    "Approximate 95th percentile: about 95% of method invocations completed at or below this duration.\nClick to sort descending by this column.",
                    TableSortColumn.P95);

                DrawSortableHeaderCell(ref x, y, _drawCountColumnWidth, height, "samples",
                    "Number of method invocations currently retained in the rolling 60-second window.\nClick to sort descending by this column.",
                    TableSortColumn.Samples);
                DrawSortableHeaderCell(ref x, y, _drawCountColumnWidth, height, "gc samples",
                    "Slow method invocations observed in a frame where a managed GC collection counter changed.\nThis is correlation, not proof that the method caused the collection.\nClick to sort descending by this column.",
                    TableSortColumn.GcSamples);
            }

            DrawSortableHeaderCell(ref x, y, _drawClassColumnWidth, height, "Class",
                "Full declaring-class name. The column fits the longest displayed class, including namespaces and nested types.\nClick to sort ascending by this text column.",
                TableSortColumn.Class);
            DrawSortableHeaderCell(ref x, y, _drawMethodColumnWidth, height, "Class:Method",
                "Short declaring-class name followed by the measured method and its parameters. Call tree opens recorded runtime contexts.\nClick to sort ascending by this text column.",
                TableSortColumn.Method);
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
        Rect rect = new(x, y, width, height);
        bool active = CurrentSortColumn == column;
        GUIStyle style = active ? _activeHeaderLabelStyle : _headerLabelStyle;
        string displayText = text ?? string.Empty;
        if (active)
            displayText += IsTextSortColumn(column) ? " ▲" : " ▼";
        if (GUI.Button(rect, new GUIContent(displayText, tooltip ?? string.Empty), style))
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
        Rect buttonRect = InsetGroupToggleRect(new Rect(x, rowY, GroupToggleWidth, height));
        bool toggleRequested = GUI.Button(buttonRect, expanded ? "▼" : "▶", _compactButtonStyle);

        x += GroupToggleWidth;
        float nameWidth = _drawModColumnWidth - GroupToggleWidth;
        toggleRequested |= GUI.Button(
            new Rect(x, rowY, nameWidth, height),
            new GUIContent(GetGroupDisplayName(group), group.GroupName),
            _groupLabelStyle);
        x += nameWidth;

        if (toggleRequested)
        {
            lock (_lock)
                _groupExpanded[group.GroupId] = !expanded;

            MarkViewDirty();
        }

        if (_profilerView == ProfilerView.Avg1s)
        {
            DrawCell(ref x, rowY, _drawAvgTimeColumnWidth, height, FormatMs(group.Summary.Avg1sMsPerFrame), _labelStyle);
            DrawCell(ref x, rowY, _drawAvgCountColumnWidth, height, FormatCount(group.Summary.Avg1sCallsPerFrame), _labelStyle);
            DrawCell(ref x, rowY, _drawAvgCallColumnWidth, height, FormatAverageCall(group.Summary.Avg1sAverageCallMs), _labelStyle);
        }
        else
        {
            DrawMaxColumns(ref x, rowY, height, group.Summary.Max);
        }

        DrawCell(ref x, rowY, _drawClassColumnWidth, height, string.Empty, _labelStyle);
        DrawCell(ref x, rowY, _drawMethodColumnWidth, height, string.Empty, _labelStyle);
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

        GeneralMethodEntry entry = row.Entry;
        float x = contentRect.x;
        float rowY = contentRect.y + y;
        DrawCell(ref x, rowY, _drawModColumnWidth, height,
            grouped ? string.Empty : GetModName(entry), _labelStyle, entry.Tooltip);

        if (_profilerView == ProfilerView.Avg1s)
        {
            DrawCell(ref x, rowY, _drawAvgTimeColumnWidth, height, FormatMs(row.Snapshot.Avg1sMsPerFrame), _labelStyle);
            DrawCell(ref x, rowY, _drawAvgCountColumnWidth, height, FormatCount(row.Snapshot.Avg1sCallsPerFrame), _labelStyle);
            DrawCell(ref x, rowY, _drawAvgCallColumnWidth, height, FormatAverageCall(row.Snapshot.Avg1sAverageCallMs), _labelStyle);
        }
        else
        {
            DrawMaxColumns(ref x, rowY, height, row.Snapshot.MaxSnapshot);
        }

        DrawCell(ref x, rowY, _drawClassColumnWidth, height, GetClassName(entry), _labelStyle, entry.Tooltip);
        bool treeVisible = _callTreeWindow.RequestedVisible && ReferenceEquals(_treeSelectedMethod, entry);
        bool showTree = GUI.Toggle(InsetButtonRect(new Rect(x, rowY, _drawCallTreeButtonWidth, height)),
            treeVisible,
            new GUIContent("Call tree", "Show or hide recorded calling contexts, then navigate to their root, parent or children."),
            _compactButtonStyle);
        if (showTree != treeVisible)
        {
            if (showTree)
                ShowInCallTree(entry);
            else
                _callTreeWindow.RequestedVisible = false;
        }
        x += _drawCallTreeButtonWidth + CallTreeButtonGap;
        DrawCell(ref x, rowY, _drawMethodColumnWidth - _drawCallTreeButtonWidth - CallTreeButtonGap,
            height, GetMethodDisplayName(entry), _labelStyle, entry.Tooltip);
        y += height;
    }

    private void DrawMaxColumns(ref float x, float y, float height, RollingMaxSnapshot max)
    {
        DrawCell(ref x, y, _drawTimeColumnWidth, height, FormatRawMax(max), _labelStyle);
        DrawCell(ref x, y, _drawTimeColumnWidth, height, FormatMs(max.SecondMaxMs), _labelStyle);
        DrawCell(ref x, y, _drawTimeColumnWidth, height, FormatMs(max.ThirdMaxMs), _labelStyle);

        DrawCell(ref x, y, _drawCountColumnWidth, height, FormatCount(max.AboveP99Count), _labelStyle);
        DrawCell(ref x, y, _drawTimeColumnWidth, height, FormatMs(max.AvgAboveP99Ms), _labelStyle);
        DrawCell(ref x, y, _drawTimeColumnWidth, height, FormatMs(max.P99Ms), _labelStyle);

        DrawCell(ref x, y, _drawCountColumnWidth, height, FormatCount(max.AboveP95Count), _labelStyle);
        DrawCell(ref x, y, _drawTimeColumnWidth, height, FormatMs(max.AvgAboveP95Ms), _labelStyle);
        DrawCell(ref x, y, _drawTimeColumnWidth, height, FormatMs(max.P95Ms), _labelStyle);

        DrawCell(ref x, y, _drawCountColumnWidth, height, FormatCount(max.WindowSampleCount), _labelStyle);
        DrawCell(ref x, y, _drawCountColumnWidth, height, FormatCount(max.GcSampleCount), _labelStyle);
    }

    private static bool RowVisible(float y, float height, float visibleTop, float visibleBottom) =>
        y + height >= visibleTop && y <= visibleBottom;

    private static Rect InsetGroupToggleRect(Rect rect)
    {
        rect.x += 1f;
        rect.y += 3f;
        rect.width = Mathf.Max(1f, rect.width - 3f);
        rect.height = Mathf.Max(1f, rect.height - 6f);
        return rect;
    }

    private static Rect InsetButtonRect(Rect rect)
    {
        rect.x += 1f;
        rect.y += 1f;
        rect.width = Mathf.Max(1f, rect.width - 2f);
        rect.height = Mathf.Max(1f, rect.height - 2f);
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
