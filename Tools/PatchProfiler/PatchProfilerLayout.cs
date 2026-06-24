#nullable disable

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ValheimProfiler.Tools.PatchProfiler;

internal sealed partial class PatchProfilerTool
{
    private void PrepareProfilerLayoutWidths()
    {
        if (!_layoutMetricsDirty &&
            Mathf.Abs(_lastLayoutWindowWidth - MainWindowWidth) < 0.1f &&
            _lastLayoutProfilerView == _profilerView &&
            _lastLayoutGroupByMod == _groupByMod &&
            _lastLayoutSkin == GUI.skin)
        {
            return;
        }

        _drawTimeColumnWidth = CalculateHeaderAwareColumnWidth(TimeColumnWidth, "raw max", "2nd max", "3rd max", "avg >p99", "p99", "avg >p95", "p95", "000.000!");
        _drawCountColumnWidth = CalculateHeaderAwareColumnWidth(CountColumnWidth, "> p99", "> p95", "samples", "gc samples", "9999999+");
        _drawAvgTimeColumnWidth = CalculateHeaderAwareColumnWidth(AvgTimeColumnWidth, "ms per frame", "000.000");
        _drawAvgCountColumnWidth = CalculateHeaderAwareColumnWidth(AvgCountColumnWidth, "calls per frame", "9999999+");

        List<FlatRowView> layoutRows = GetLayoutRows();
        _drawModColumnWidth = CalculateModColumnWidth(layoutRows);
        _drawPatchTypeColumnWidth = CalculatePatchTypeColumnWidth();

        float fixedWidth = CalculateFixedProfilerColumnsWidth(_drawModColumnWidth, _drawPatchTypeColumnWidth);
        float availableWidth = Mathf.Max(300f, MainWindowWidth - WindowHorizontalPadding);
        float remainingMethodWidth = Mathf.Max(MethodColumnMinWidth, availableWidth - fixedWidth);

        if (_profilerView == ProfilerView.Avg1s)
        {
            _drawTargetColumnWidth = CalculateTargetColumnWidth(layoutRows);
            _drawPatchMethodColumnWidth = CalculatePatchMethodColumnWidth(layoutRows);

            float desiredTotal = _drawTargetColumnWidth + _drawPatchMethodColumnWidth;
            if (desiredTotal < remainingMethodWidth)
                _drawPatchMethodColumnWidth += remainingMethodWidth - desiredTotal;

            _drawMethodColumnWidth = _drawTargetColumnWidth + _drawPatchMethodColumnWidth;
        }
        else
        {
            _drawTargetColumnWidth = 0f;
            _drawPatchMethodColumnWidth = 0f;
            _drawMethodColumnWidth = Mathf.Max(remainingMethodWidth, CalculateCombinedMethodColumnWidth(layoutRows));
        }

        _drawContentWidth = fixedWidth + _drawMethodColumnWidth;
        _lastLayoutWindowWidth = MainWindowWidth;
        _lastLayoutProfilerView = _profilerView;
        _lastLayoutGroupByMod = _groupByMod;
        _lastLayoutSkin = GUI.skin;
        _layoutMetricsDirty = false;
    }

    private float CalculateHeaderAwareColumnWidth(float nominalWidth, params string[] texts)
    {
        if (_headerLabelStyle == null)
            return nominalWidth;

        float width = nominalWidth;

        foreach (string text in texts)
            width = Mathf.Max(width, _headerLabelStyle.CalcSize(new GUIContent(text)).x + 6f);

        return Mathf.Ceil(width);
    }

    private void DrawHeader()
    {
        Rect rect = GUILayoutUtility.GetRect(
            1f,
            CurrentHeaderHeight,
            GUILayout.ExpandWidth(true),
            GUILayout.Height(CurrentHeaderHeight));

        GUI.BeginGroup(rect);
        try
        {
            float x = -_scroll.x;
            float y = 0f;
            float height = rect.height;

            DrawSortableHeaderCell(
                ref x,
                y,
                _drawModColumnWidth,
                height,
                _groupByMod ? "Mod" : "BepInEx mod",
                "BepInEx mod associated with the patch entry. Transpiled target rows are grouped under Transpiled methods.\nClick to sort ascending by this text column.",
                TableSortColumn.Mod);

            if (_profilerView == ProfilerView.Avg1s)
            {
                DrawSortableHeaderCell(ref x, y, _drawAvgTimeColumnWidth, height, "ms per frame",
                    "Average measured patch execution time contributed per rendered frame in the rolling one-second window.\nClick to sort descending by this column.",
                    TableSortColumn.AvgMsPerFrame);
                DrawSortableHeaderCell(ref x, y, _drawAvgCountColumnWidth, height, "calls per frame",
                    "Average number of measured patch invocations per rendered frame in the rolling one-second window.\nClick to sort descending by this column.",
                    TableSortColumn.AvgCallsPerFrame);
            }
            else
            {
                DrawSortableHeaderCell(ref x, y, _drawTimeColumnWidth, height, "raw max",
                    "Slowest individual measured call in the rolling 60-second window.\nA trailing ! marks an isolated spike or a high GC-associated sample.\nClick to sort descending by this column.",
                    TableSortColumn.RawMax);
                DrawSortableHeaderCell(ref x, y, _drawTimeColumnWidth, height, "2nd max",
                    "Second-slowest individual measured call in the rolling 60-second window.\nClick to sort descending by this column.",
                    TableSortColumn.SecondMax);
                DrawSortableHeaderCell(ref x, y, _drawTimeColumnWidth, height, "3rd max",
                    "Third-slowest individual measured call in the rolling 60-second window.\nClick to sort descending by this column.",
                    TableSortColumn.ThirdMax);

                DrawSortableHeaderCell(ref x, y, _drawCountColumnWidth, height, "> p99",
                    "Number of samples above the approximate p99 histogram boundary.\nClick to sort descending by this column.",
                    TableSortColumn.AboveP99);
                DrawSortableHeaderCell(ref x, y, _drawTimeColumnWidth, height, "avg >p99",
                    "Average execution time of samples above the approximate p99 boundary.\nClick to sort descending by this column.",
                    TableSortColumn.AvgAboveP99);
                DrawSortableHeaderCell(ref x, y, _drawTimeColumnWidth, height, "p99",
                    "Approximate 99th percentile: about 99% of samples completed at or below this duration.\nClick to sort descending by this column.",
                    TableSortColumn.P99);

                DrawSortableHeaderCell(ref x, y, _drawCountColumnWidth, height, "> p95",
                    "Number of samples above the approximate p95 histogram boundary.\nClick to sort descending by this column.",
                    TableSortColumn.AboveP95);
                DrawSortableHeaderCell(ref x, y, _drawTimeColumnWidth, height, "avg >p95",
                    "Average execution time of samples above the approximate p95 boundary.\nClick to sort descending by this column.",
                    TableSortColumn.AvgAboveP95);
                DrawSortableHeaderCell(ref x, y, _drawTimeColumnWidth, height, "p95",
                    "Approximate 95th percentile: about 95% of samples completed at or below this duration.\nClick to sort descending by this column.",
                    TableSortColumn.P95);

                DrawSortableHeaderCell(ref x, y, _drawCountColumnWidth, height, "samples",
                    "Number of measured calls retained in the rolling 60-second window.\nClick to sort descending by this column.",
                    TableSortColumn.Samples);
                DrawSortableHeaderCell(ref x, y, _drawCountColumnWidth, height, "gc samples",
                    "Slow measured calls observed in a frame where a managed GC collection counter changed.\nThis is correlation, not proof that the patch caused the collection.\nClick to sort descending by this column.",
                    TableSortColumn.GcSamples);
            }

            DrawSortableHeaderCell(ref x, y, _drawPatchTypeColumnWidth, height, "PatchType",
                "Harmony patch role or transpiler-derived runtime entry type.\nClick to sort ascending by this text column.",
                TableSortColumn.PatchType);

            if (_profilerView == ProfilerView.Avg1s)
            {
                DrawSortableHeaderCell(ref x, y, _drawTargetColumnWidth, height, "Patched target method",
                    "Original game or mod method whose Harmony patch chain contains this entry.\nClick to sort ascending by this text column.",
                    TableSortColumn.Target);
                DrawSortableHeaderCell(ref x, y, _drawPatchMethodColumnWidth, height, "Patch method",
                    "Managed patch method or transpiler-injected call being measured.\nClick to sort ascending by this text column.",
                    TableSortColumn.PatchMethod);
            }
            else
            {
                DrawSortableHeaderCell(ref x, y, _drawMethodColumnWidth, height, "Patched target method │ Patch method",
                    "Original patched target followed by the measured patch method or transpiler-injected call.\nClick to sort ascending by this text column.",
                    TableSortColumn.CombinedMethod);
            }
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

    private void DrawTotalSummaryRow()
    {
        Rect rect = GUILayoutUtility.GetRect(
            1f,
            CurrentTotalRowHeight,
            GUILayout.ExpandWidth(true),
            GUILayout.Height(CurrentTotalRowHeight));

        GUI.BeginGroup(rect);
        try
        {
            float x = -_scroll.x;
            float y = 0f;
            float height = rect.height;

            DrawGuiLabel(ref x, y, _drawModColumnWidth, height, "Total", _groupLabelStyle);
            DrawGuiLabel(ref x, y, _drawAvgTimeColumnWidth, height, FormatMs(_cachedTotalSummary.Avg1sMsPerFrame), _labelStyle);
            DrawGuiLabel(ref x, y, _drawAvgCountColumnWidth, height, FormatCount(_cachedTotalSummary.Avg1sCallsPerFrame), _labelStyle);
            DrawGuiLabel(ref x, y, _drawPatchTypeColumnWidth, height, string.Empty, _labelStyle);
            DrawGuiLabel(ref x, y, _drawTargetColumnWidth, height, string.Empty, _labelStyle);
            DrawGuiLabel(ref x, y, _drawPatchMethodColumnWidth, height, string.Empty, _labelStyle);
        }
        finally
        {
            GUI.EndGroup();
        }
    }

    private float CalculateModColumnWidth(List<FlatRowView> layoutRows)
    {
        if (_labelStyle == null)
            return 240f;

        string header = _groupByMod ? "Mod" : "BepInEx mod";
        float width = _headerLabelStyle.CalcSize(new GUIContent(header)).x + 16f;

        if (_groupByMod)
        {
            foreach (ModGroupView group in _cachedGroupedRows)
            {
                string name = group.ModName ?? "Unknown";
                if (_profilerView == ProfilerView.MaxOver60Sec)
                    name = Truncate(name, MaxGroupedModNameCharsOnMax60);

                float nameWidth = GroupToggleWidth +
                    _groupLabelStyle.CalcSize(new GUIContent(name)).x + 16f;
                width = Mathf.Max(width, nameWidth);
            }
        }
        else
        {
            foreach (FlatRowView row in layoutRows)
            {
                string name = GetRowModName(row);
                float nameWidth = _labelStyle.CalcSize(new GUIContent(name)).x + 16f;
                width = Mathf.Max(width, nameWidth);
            }
        }

        return Mathf.Clamp(Mathf.Ceil(width), MinModColumnWidth, MaxModColumnWidth);
    }

    private float CalculatePatchTypeColumnWidth()
    {
        if (_labelStyle == null)
            return 70f;

        float header = _headerLabelStyle.CalcSize(new GUIContent("PatchType")).x + 12f;
        float transpiledTarget = _labelStyle.CalcSize(new GUIContent("Transpiled target")).x + 12f;
        float transpilerCall = _labelStyle.CalcSize(new GUIContent("Transpiler call")).x + 12f;
        return Mathf.Ceil(Mathf.Max(header, Mathf.Max(transpiledTarget, transpilerCall)));
    }

    private float CalculateTargetColumnWidth(List<FlatRowView> layoutRows)
    {
        if (_labelStyle == null)
            return TargetColumnMinWidth;

        float width = _headerLabelStyle.CalcSize(new GUIContent("Patched target method")).x + 16f;

        foreach (FlatRowView row in layoutRows)
        {
            string target = row.Context?.TargetDisplay ?? "(unknown target)";
            width = Mathf.Max(width, _labelStyle.CalcSize(new GUIContent(target)).x + 16f);
        }

        return Mathf.Clamp(Mathf.Ceil(width), TargetColumnMinWidth, TargetColumnMaxWidth);
    }

    private float CalculatePatchMethodColumnWidth(List<FlatRowView> layoutRows)
    {
        if (_labelStyle == null)
            return MethodColumnMinWidth;

        float width = _headerLabelStyle.CalcSize(new GUIContent("Patch method")).x + 16f;

        foreach (FlatRowView row in layoutRows)
        {
            string method = GetMethodDisplay(row.InstrumentedMethod);
            width = Mathf.Max(width, _labelStyle.CalcSize(new GUIContent(method)).x + 16f);
        }

        return Mathf.Clamp(Mathf.Ceil(width), MethodColumnMinWidth, PatchMethodColumnMaxWidth);
    }

    private float CalculateCombinedMethodColumnWidth(List<FlatRowView> layoutRows)
    {
        if (_labelStyle == null)
            return MethodColumnMinWidth;

        float width = _headerLabelStyle.CalcSize(new GUIContent("Patched target method │ Patch method")).x + 16f;

        foreach (FlatRowView row in layoutRows)
        {
            PatchContext context = row.Context;
            string target = context?.TargetDisplay ?? "(unknown target)";
            string method = GetMethodDisplay(row.InstrumentedMethod);
            string combined = $"{target}  │  {method}";
            float detailsWidth = context != null && context.IsTranspiledTargetEntry && context.TranspilerDetails.Count > 0 ? 68f : 0f;
            width = Mathf.Max(width, detailsWidth + _labelStyle.CalcSize(new GUIContent(combined)).x + 16f);
        }

        return Mathf.Clamp(Mathf.Ceil(width), MethodColumnMinWidth, CombinedMethodColumnMaxWidth);
    }

    private List<FlatRowView> GetLayoutRows()
    {
        if (!_groupByMod)
            return _cachedFlatRows;

        var rows = new List<FlatRowView>();

        foreach (ModGroupView group in _cachedGroupedRows)
        {
            bool expanded;
            lock (_lock)
                expanded = _modExpanded.TryGetValue(group.ModGuid, out bool value) && value;

            if (expanded)
                rows.AddRange(group.Rows);
        }

        return rows;
    }

    private float CalculateFixedProfilerColumnsWidth(float modColumnWidth, float patchTypeColumnWidth)
    {
        float width = modColumnWidth;

        if (_profilerView == ProfilerView.Avg1s)
        {
            width += _drawAvgTimeColumnWidth;
            width += _drawAvgCountColumnWidth;
        }
        else
        {
            width += _drawTimeColumnWidth * 7f;
            width += _drawCountColumnWidth * 4f;
        }

        width += patchTypeColumnWidth;
        return width;
    }

    private float CalculateProfilerContentWidth() => _drawContentWidth;

    private float CalculateProfilerContentHeight()
    {
        if (!_groupByMod)
            return _cachedFlatRows.Count * CurrentRowHeight;

        float height = 0f;

        foreach (ModGroupView group in _cachedGroupedRows)
        {
            height += CurrentGroupRowHeight;

            bool expanded;
            lock (_lock)
                expanded = _modExpanded.TryGetValue(group.ModGuid, out bool value) && value;

            if (expanded)
                height += group.Rows.Count * CurrentRowHeight;
        }

        return height;
    }
}