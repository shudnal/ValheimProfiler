#nullable disable

using UnityEngine;

namespace ValheimProfiler.Tools.PatchProfiler;

internal sealed partial class PatchProfilerTool
{
    private void DrawProfilerVirtualContent(Rect contentRect)
    {
        float visibleTop = _scroll.y - VirtualizationOverscan;
        float visibleBottom = _scroll.y + Mathf.Max(100f, MainWindowHeight - 150f) + VirtualizationOverscan;
        float y = 0f;

        if (_groupByMod)
        {
            foreach (ModGroupView group in _cachedGroupedRows)
            {
                DrawVirtualGroupRow(contentRect, ref y, group, visibleTop, visibleBottom);

                bool expanded;
                lock (_lock)
                    expanded = _modExpanded.TryGetValue(group.ModGuid, out bool value) && value;

                if (!expanded)
                    continue;

                foreach (FlatRowView row in group.Rows)
                    DrawVirtualGroupedPatchRow(contentRect, ref y, row, visibleTop, visibleBottom);
            }
        }
        else
        {
            foreach (FlatRowView row in _cachedFlatRows)
                DrawVirtualFlatPatchRow(contentRect, ref y, row, visibleTop, visibleBottom);
        }
    }

    private static bool IsVirtualRowVisible(float y, float height, float visibleTop, float visibleBottom) =>
        y + height >= visibleTop && y <= visibleBottom;

    private void DrawVirtualGroupRow(Rect contentRect, ref float y, ModGroupView group, float visibleTop, float visibleBottom)
    {
        float height = CurrentGroupRowHeight;

        if (!IsVirtualRowVisible(y, height, visibleTop, visibleBottom))
        {
            y += height;
            return;
        }

        bool expanded;
        lock (_lock)
            expanded = _modExpanded.TryGetValue(group.ModGuid, out bool value) && value;

        float x = contentRect.x;
        float rowY = contentRect.y + y;
        Rect toggleRect = InsetGroupToggleRect(new Rect(x, rowY, GroupToggleWidth, height));
        bool toggleRequested = GUI.Button(
            toggleRect,
            expanded ? "▼" : "▶",
            _compactButtonStyle);

        x += GroupToggleWidth;

        string modName = group.ModName ?? "Unknown";
        if (_profilerView == ProfilerView.MaxOver60Sec)
            modName = Truncate(modName, MaxGroupedModNameCharsOnMax60);

        float nameWidth = _drawModColumnWidth - GroupToggleWidth;
        toggleRequested |= GUI.Button(
            new Rect(x, rowY, nameWidth, height),
            modName,
            _groupLabelStyle);
        x += nameWidth;

        if (toggleRequested)
        {
            lock (_lock)
                _modExpanded[group.ModGuid] = !expanded;

            MarkViewDirty();
        }
        DrawGuiGroupSummaryColumns(ref x, rowY, height, group.Summary);
        y += height;
    }

    private void DrawVirtualGroupedPatchRow(Rect contentRect, ref float y, FlatRowView row, float visibleTop, float visibleBottom)
    {
        float height = CurrentRowHeight;

        if (!IsVirtualRowVisible(y, height, visibleTop, visibleBottom))
        {
            y += height;
            return;
        }

        var patchMethod = row.InstrumentedMethod;
        var snapshot = row.Snapshot;
        var context = row.Context;

        string patchType = context.PatchType ?? "?";
        string target = context.TargetDisplay ?? "(unknown target)";
        string patchDisplay = GetMethodDisplay(patchMethod);

        float x = contentRect.x;
        float rowY = contentRect.y + y;

        DrawGuiLabel(ref x, rowY, _drawModColumnWidth, height, string.Empty, _labelStyle);
        DrawGuiStatColumns(ref x, rowY, height, snapshot);
        DrawGuiLabel(ref x, rowY, _drawPatchTypeColumnWidth, height, patchType, _labelStyle);
        DrawGuiMethodColumns(ref x, rowY, height, target, patchDisplay, context);

        y += height;
    }

    private void DrawVirtualFlatPatchRow(Rect contentRect, ref float y, FlatRowView row, float visibleTop, float visibleBottom)
    {
        float height = CurrentRowHeight;

        if (!IsVirtualRowVisible(y, height, visibleTop, visibleBottom))
        {
            y += height;
            return;
        }

        var patchMethod = row.InstrumentedMethod;
        var snapshot = row.Snapshot;
        var context = row.Context;

        string modName = context.ModName ?? GuessModName(patchMethod, context.ModGuid);
        string patchType = context.PatchType ?? "?";
        string target = context.TargetDisplay ?? "(unknown target)";
        string patchDisplay = GetMethodDisplay(patchMethod);

        float x = contentRect.x;
        float rowY = contentRect.y + y;

        DrawGuiLabel(ref x, rowY, _drawModColumnWidth, height, modName, _labelStyle);
        DrawGuiStatColumns(ref x, rowY, height, snapshot);
        DrawGuiLabel(ref x, rowY, _drawPatchTypeColumnWidth, height, patchType, _labelStyle);
        DrawGuiMethodColumns(ref x, rowY, height, target, patchDisplay, context);

        y += height;
    }

    private void DrawGuiMethodColumns(ref float x, float y, float height, string target, string patchDisplay, PatchContext context)
    {
        bool showDetailsButton = context != null && context.IsTranspiledTargetEntry && context.TranspilerDetails.Count > 0;
        const float detailsButtonWidth = 64f;
        const float detailsGap = 4f;

        if (_profilerView == ProfilerView.Avg1s)
        {
            if (showDetailsButton && _drawTargetColumnWidth > detailsButtonWidth + detailsGap + 40f)
            {
                bool detailsVisible = _detailsWindow.RequestedVisible && ReferenceEquals(_selectedTranspilerDetailsContext, context);
                Rect detailsRect = InsetButtonRect(new Rect(x, y, detailsButtonWidth, height));
                bool newDetailsVisible = GUI.Toggle(
                    detailsRect,
                    detailsVisible,
                    new GUIContent("Calls", "Show transpilers and runtime calls detected in the transpiled method."),
                    _compactButtonStyle);

                if (newDetailsVisible != detailsVisible)
                    ToggleTranspilerDetails(context);

                x += detailsButtonWidth + detailsGap;
                DrawGuiLabel(ref x, y, _drawTargetColumnWidth - detailsButtonWidth - detailsGap, height, target, _labelStyle);
            }
            else
            {
                DrawGuiLabel(ref x, y, _drawTargetColumnWidth, height, target, _labelStyle);
            }

            DrawGuiLabel(ref x, y, _drawPatchMethodColumnWidth, height, patchDisplay, _labelStyle);
        }
        else
        {
            string combined = $"{target}  │  {patchDisplay}";

            if (showDetailsButton && _drawMethodColumnWidth > detailsButtonWidth + detailsGap + 80f)
            {
                bool detailsVisible = _detailsWindow.RequestedVisible && ReferenceEquals(_selectedTranspilerDetailsContext, context);
                Rect detailsRect = InsetButtonRect(new Rect(x, y, detailsButtonWidth, height));
                bool newDetailsVisible = GUI.Toggle(
                    detailsRect,
                    detailsVisible,
                    new GUIContent("Calls", "Show transpilers and runtime calls detected in the transpiled method."),
                    _compactButtonStyle);

                if (newDetailsVisible != detailsVisible)
                    ToggleTranspilerDetails(context);

                x += detailsButtonWidth + detailsGap;
                DrawGuiLabel(ref x, y, _drawMethodColumnWidth - detailsButtonWidth - detailsGap, height, combined, _labelStyle);
            }
            else
            {
                DrawGuiLabel(ref x, y, _drawMethodColumnWidth, height, combined, _labelStyle);
            }
        }
    }

    private void DrawGuiStatColumns(ref float x, float y, float height, PatchStatSnapshot snapshot)
    {
        if (_profilerView == ProfilerView.Avg1s)
        {
            DrawGuiLabel(ref x, y, _drawAvgTimeColumnWidth, height, FormatMs(snapshot.Avg1sMsPerFrame), _labelStyle);
            DrawGuiLabel(ref x, y, _drawAvgCountColumnWidth, height, FormatCount(snapshot.Avg1sCallsPerFrame), _labelStyle);
        }
        else
        {
            DrawGuiMaxColumns(ref x, y, height, snapshot.MaxSnapshot);
        }
    }

    private void DrawGuiGroupSummaryColumns(ref float x, float y, float height, GroupSummary summary)
    {
        if (_profilerView == ProfilerView.Avg1s)
        {
            DrawGuiLabel(ref x, y, _drawAvgTimeColumnWidth, height, FormatMs(summary.Avg1sMsPerFrame), _labelStyle);
            DrawGuiLabel(ref x, y, _drawAvgCountColumnWidth, height, FormatCount(summary.Avg1sCallsPerFrame), _labelStyle);
        }
        else
        {
            DrawGuiMaxColumns(ref x, y, height, summary.Max);
        }

        DrawGuiLabel(ref x, y, _drawPatchTypeColumnWidth, height, string.Empty, _labelStyle);

        if (_profilerView == ProfilerView.Avg1s)
        {
            DrawGuiLabel(ref x, y, _drawTargetColumnWidth, height, string.Empty, _labelStyle);
            DrawGuiLabel(ref x, y, _drawPatchMethodColumnWidth, height, string.Empty, _labelStyle);
        }
        else
        {
            DrawGuiLabel(ref x, y, _drawMethodColumnWidth, height, string.Empty, _labelStyle);
        }
    }

    private void DrawGuiMaxColumns(ref float x, float y, float height, MaxAnalyticsSnapshot max)
    {
        DrawGuiLabel(ref x, y, _drawTimeColumnWidth, height, FormatRawMax(max), _labelStyle);
        DrawGuiLabel(ref x, y, _drawTimeColumnWidth, height, FormatMs(max.SecondMaxMs), _labelStyle);
        DrawGuiLabel(ref x, y, _drawTimeColumnWidth, height, FormatMs(max.ThirdMaxMs), _labelStyle);

        DrawGuiLabel(ref x, y, _drawCountColumnWidth, height, FormatCount(max.AboveP99Count), _labelStyle);
        DrawGuiLabel(ref x, y, _drawTimeColumnWidth, height, FormatMs(max.AvgAboveP99Ms), _labelStyle);
        DrawGuiLabel(ref x, y, _drawTimeColumnWidth, height, FormatMs(max.P99Ms), _labelStyle);

        DrawGuiLabel(ref x, y, _drawCountColumnWidth, height, FormatCount(max.AboveP95Count), _labelStyle);
        DrawGuiLabel(ref x, y, _drawTimeColumnWidth, height, FormatMs(max.AvgAboveP95Ms), _labelStyle);
        DrawGuiLabel(ref x, y, _drawTimeColumnWidth, height, FormatMs(max.P95Ms), _labelStyle);

        DrawGuiLabel(ref x, y, _drawCountColumnWidth, height, FormatCount(max.WindowSampleCount), _labelStyle);
        DrawGuiLabel(ref x, y, _drawCountColumnWidth, height, FormatCount(max.GcSampleCount), _labelStyle);
    }

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

    private static void DrawGuiLabel(
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