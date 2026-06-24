#nullable disable

using System;
using UnityEngine;

namespace ValheimProfiler.Tools.MonoBehaviourProfiler;

internal sealed partial class MonoBehaviourProfilerTool
{
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

        _drawTimeColumnWidth = CalculateHeaderAwareWidth(
            TimeColumnWidth,
            "raw max",
            "2nd max",
            "3rd max",
            "avg >p99",
            "p99",
            "avg >p95",
            "p95",
            "000.000!");

        _drawCountColumnWidth = CalculateHeaderAwareWidth(
            CountColumnWidth,
            "> p99",
            "> p95",
            "samples",
            "gc samples",
            "9999999+");

        _drawAvgTimeColumnWidth = CalculateHeaderAwareWidth(
            AvgTimeColumnWidth,
            "ms per frame",
            "000.000");

        _drawAvgCountColumnWidth = CalculateHeaderAwareWidth(
            AvgCountColumnWidth,
            "calls per frame",
            "9999999+");

        _drawSourceColumnWidth = CalculateSourceColumnWidth();
        _drawTypeColumnWidth = CalculateTypeColumnWidth();

        float statsWidth = _profilerView == ProfilerView.Avg1s
            ? _drawAvgTimeColumnWidth + _drawAvgCountColumnWidth
            : _drawTimeColumnWidth * 7f + _drawCountColumnWidth * 4f;

        _drawContentWidth = _drawSourceColumnWidth + statsWidth + _drawTypeColumnWidth;
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

    private float CalculateSourceColumnWidth()
    {
        string header = _groupByMod ? "Mod / callback" : "Mod:MonoBehaviour:Callback";
        float width = _headerLabelStyle != null
            ? _headerLabelStyle.CalcSize(new GUIContent(header)).x + 12f
            : MinSourceColumnWidth;

        lock (_lock)
        {
            foreach (BehaviourTypeEntry typeEntry in _types)
            {
                if (_groupByMod)
                {
                    float groupWidth = GroupToggleWidth +
                        _groupLabelStyle.CalcSize(new GUIContent(typeEntry.GroupName ?? "Unknown")).x + 12f;
                    width = Mathf.Max(width, groupWidth);

                    foreach (BehaviourMethodEntry method in typeEntry.Methods)
                    {
                        string entryName = BuildGroupedEntryName(method);
                        float methodWidth = _labelStyle.CalcSize(new GUIContent(entryName)).x + 12f;
                        width = Mathf.Max(width, methodWidth);
                    }
                }
                else
                {
                    foreach (BehaviourMethodEntry method in typeEntry.Methods)
                    {
                        string entryName = BuildUngroupedEntryName(method);
                        float entryWidth = _labelStyle.CalcSize(new GUIContent(entryName)).x + 12f;
                        width = Mathf.Max(width, entryWidth);
                    }
                }
            }
        }

        float maximum = MaxSourceColumnWidth;
        if (_groupByMod)
            maximum = Mathf.Min(maximum, CalculateUngroupedSourceColumnWidth());

        return Mathf.Clamp(Mathf.Ceil(width), MinSourceColumnWidth, maximum);
    }

    private float CalculateUngroupedSourceColumnWidth()
    {
        float width = _headerLabelStyle != null
            ? _headerLabelStyle.CalcSize(new GUIContent("Mod:MonoBehaviour:Callback")).x + 12f
            : MinSourceColumnWidth;

        lock (_lock)
        {
            foreach (BehaviourTypeEntry typeEntry in _types)
            {
                foreach (BehaviourMethodEntry method in typeEntry.Methods)
                {
                    string entryName = BuildUngroupedEntryName(method);
                    float entryWidth = _labelStyle.CalcSize(new GUIContent(entryName)).x + 12f;
                    width = Mathf.Max(width, entryWidth);
                }
            }
        }

        return Mathf.Clamp(Mathf.Ceil(width), MinSourceColumnWidth, MaxSourceColumnWidth);
    }

    private float CalculateTypeColumnWidth()
    {
        float width = _headerLabelStyle != null
            ? _headerLabelStyle.CalcSize(new GUIContent("MonoBehaviour")).x + 12f
            : TypeColumnMinWidth;

        lock (_lock)
        {
            foreach (BehaviourTypeEntry typeEntry in _types)
                width = Mathf.Max(width, _labelStyle.CalcSize(new GUIContent(typeEntry.TypeName ?? "Unknown")).x + 12f);
        }

        return Mathf.Clamp(Mathf.Ceil(width), TypeColumnMinWidth, 720f);
    }

    private static string BuildGroupedEntryName(BehaviourMethodEntry entry)
    {
        if (entry == null)
            return "Unknown.Unknown";

        string type = entry.TypeEntry?.Type?.Name ?? GetShortTypeName(entry.TypeEntry?.TypeName);
        string method = entry.DisplayName ?? "Unknown";
        return $"{type}.{method}";
    }

    private static string BuildUngroupedEntryName(BehaviourMethodEntry entry)
    {
        if (entry == null)
            return "Unknown:Unknown:Unknown";

        string mod = entry.TypeEntry?.GroupName ?? "Unknown";
        string type = entry.TypeEntry?.Type?.Name ?? GetShortTypeName(entry.TypeEntry?.TypeName);
        string method = entry.DisplayName ?? "Unknown";
        return $"{mod}:{type}:{method}";
    }

    private static string GetShortTypeName(string fullName)
    {
        if (string.IsNullOrEmpty(fullName))
            return "Unknown";

        int dot = fullName.LastIndexOf('.');
        int plus = fullName.LastIndexOf('+');
        int separator = Math.Max(dot, plus);
        return separator >= 0 && separator + 1 < fullName.Length
            ? fullName.Substring(separator + 1)
            : fullName;
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
                _groupByMod ? "Mod / callback" : "Mod:MonoBehaviour:Callback",
                _groupByMod
                    ? "Grouped view: mod or assembly names are shown in group rows, and short MonoBehaviour type plus callback are shown in child rows.\nClick to sort descending by this column."
                    : "Ungrouped view: Mod:short MonoBehaviour name:callback name.\nClick to sort descending by this column.",
                TableSortColumn.Source);

            if (_profilerView == ProfilerView.Avg1s)
            {
                DrawSortableHeaderCell(ref x, y, _drawAvgTimeColumnWidth, height, "ms per frame",
                    "Average inclusive execution time contributed by this callback per rendered frame in the rolling one-second window.\nClick to sort descending by this column.",
                    TableSortColumn.AvgMsPerFrame);
                DrawSortableHeaderCell(ref x, y, _drawAvgCountColumnWidth, height, "calls per frame",
                    "Average number of callback invocations per rendered frame in the rolling one-second window.\nClick to sort descending by this column.",
                    TableSortColumn.AvgCallsPerFrame);
            }
            else
            {
                DrawSortableHeaderCell(ref x, y, _drawTimeColumnWidth, height, "raw max",
                    "Slowest individual callback invocation in the rolling 60-second window.\nA trailing ! marks an isolated spike or a high GC-associated sample.\nClick to sort descending by this column.",
                    TableSortColumn.RawMax);
                DrawSortableHeaderCell(ref x, y, _drawTimeColumnWidth, height, "2nd max",
                    "Second-slowest individual callback invocation in the rolling 60-second window.\nClick to sort descending by this column.",
                    TableSortColumn.SecondMax);
                DrawSortableHeaderCell(ref x, y, _drawTimeColumnWidth, height, "3rd max",
                    "Third-slowest individual callback invocation in the rolling 60-second window.\nClick to sort descending by this column.",
                    TableSortColumn.ThirdMax);

                DrawSortableHeaderCell(ref x, y, _drawCountColumnWidth, height, "> p99",
                    "Number of samples above the approximate p99 histogram boundary.\nClick to sort descending by this column.",
                    TableSortColumn.AboveP99);
                DrawSortableHeaderCell(ref x, y, _drawTimeColumnWidth, height, "avg >p99",
                    "Average execution time of samples above the approximate p99 boundary.\nClick to sort descending by this column.",
                    TableSortColumn.AvgAboveP99);
                DrawSortableHeaderCell(ref x, y, _drawTimeColumnWidth, height, "p99",
                    "Approximate 99th percentile: about 99% of callback invocations completed at or below this duration.\nClick to sort descending by this column.",
                    TableSortColumn.P99);

                DrawSortableHeaderCell(ref x, y, _drawCountColumnWidth, height, "> p95",
                    "Number of samples above the approximate p95 histogram boundary.\nClick to sort descending by this column.",
                    TableSortColumn.AboveP95);
                DrawSortableHeaderCell(ref x, y, _drawTimeColumnWidth, height, "avg >p95",
                    "Average execution time of samples above the approximate p95 boundary.\nClick to sort descending by this column.",
                    TableSortColumn.AvgAboveP95);
                DrawSortableHeaderCell(ref x, y, _drawTimeColumnWidth, height, "p95",
                    "Approximate 95th percentile: about 95% of callback invocations completed at or below this duration.\nClick to sort descending by this column.",
                    TableSortColumn.P95);

                DrawSortableHeaderCell(ref x, y, _drawCountColumnWidth, height, "samples",
                    "Number of callback invocations currently retained in the rolling 60-second window.\nClick to sort descending by this column.",
                    TableSortColumn.Samples);
                DrawSortableHeaderCell(ref x, y, _drawCountColumnWidth, height, "gc samples",
                    "Slow callback invocations observed in a frame where a managed GC collection counter changed.\nThis is correlation, not proof that the callback caused the collection.\nClick to sort descending by this column.",
                    TableSortColumn.GcSamples);
            }

            DrawSortableHeaderCell(ref x, y, _drawTypeColumnWidth, height, "MonoBehaviour",
                "Managed MonoBehaviour runtime type being measured.\nClick to sort descending by this column.",
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
        Rect rect = new(x, y, width, height);
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

            MarkViewDirty();
        }

        if (_profilerView == ProfilerView.Avg1s)
        {
            DrawCell(ref x, rowY, _drawAvgTimeColumnWidth, height, FormatMs(group.Summary.Avg1sMsPerFrame), _labelStyle);
            DrawCell(ref x, rowY, _drawAvgCountColumnWidth, height, FormatCount(group.Summary.Avg1sCallsPerFrame), _labelStyle);
        }
        else
        {
            DrawMaxColumns(ref x, rowY, height, group.Summary.Max);
        }

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
        string entryName = grouped
            ? BuildGroupedEntryName(entry)
            : BuildUngroupedEntryName(entry);

        DrawCell(ref x, rowY, _drawSourceColumnWidth, height, entryName, _labelStyle);

        if (_profilerView == ProfilerView.Avg1s)
        {
            DrawCell(ref x, rowY, _drawAvgTimeColumnWidth, height, FormatMs(row.Snapshot.Avg1sMsPerFrame), _labelStyle);
            DrawCell(ref x, rowY, _drawAvgCountColumnWidth, height, FormatCount(row.Snapshot.Avg1sCallsPerFrame), _labelStyle);
        }
        else
        {
            DrawMaxColumns(ref x, rowY, height, row.Snapshot.MaxSnapshot);
        }

        DrawCell(ref x, rowY, _drawTypeColumnWidth, height, entry.TypeEntry.TypeName, _labelStyle);
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
