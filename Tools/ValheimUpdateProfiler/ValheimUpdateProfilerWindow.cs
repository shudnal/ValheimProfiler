#nullable disable

using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimProfiler.Tools.ValheimUpdateProfiler;

internal sealed partial class ValheimUpdateProfilerTool
{
    private enum MainTab
    {
        Profiler,
        Help
    }

    private MainTab _mainTab;

    private const float ScopeWidth = 420f;
    private const float NumberWidth = 92f;
    private const float WideNumberWidth = 112f;

    private void DrawWindow(int id)
    {
        EnsureStyles();
        Color oldContentColor = GUI.contentColor;
        try
        {
            GUI.contentColor = _theme.TextColor;
            GUILayout.BeginVertical();
            DrawToolbar();
            GUILayout.Space(4f);

            GUILayout.BeginHorizontal();
            MainTab newTab = (MainTab)GUILayout.Toolbar((int)_mainTab, new[] { "Profiler", "Help" }, GUILayout.Width(210f));
            if (newTab != _mainTab)
                _mainTab = newTab;

            if (_mainTab == MainTab.Profiler)
            {
                GUILayout.Space(12f);
                ValheimUpdateView newView = (ValheimUpdateView)GUILayout.Toolbar(
                    (int)_view,
                    new[] { "Over 1 sec", "Max over 60 sec" },
                    GUILayout.Width(290f));
                if (newView != _view)
                    _view = newView;
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(4f);

            if (_mainTab == MainTab.Profiler)
                DrawProfilerTab();
            else
                DrawHelpTab();

            GUILayout.EndVertical();
        }
        finally
        {
            GUI.contentColor = oldContentColor;
        }
    }

    private void DrawToolbar()
    {
        GUILayout.BeginHorizontal();
        string state = _profilingActive ? "Profiling: ON" : _statsFrozen ? "Profiling: PAUSED" : "Profiling: OFF";
        GUILayout.Label($"{state} | Status: {_status}", _labelStyle, GUILayout.ExpandWidth(true));

        string button = _profilingActive ? "Pause profiling" : "Start profiling";
        if (GUILayout.Button(button, GUILayout.Width(125f)))
        {
            if (_profilingActive)
                StopProfiling();
            else
                StartProfiling();
        }

        if (GUILayout.Button("Reset stats", GUILayout.Width(100f)))
        {
            ResetAllStats();
            _status = "Statistics reset.";
        }

        if (GUILayout.Button("Close", GUILayout.Width(70f)))
            IsWindowVisible = false;

        GUILayout.EndHorizontal();
    }

    private void DrawProfilerTab()
    {
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Expand all", GUILayout.Width(88f)))
            SetAllGroupsExpanded(true);
        if (GUILayout.Button("Collapse all", GUILayout.Width(88f)))
            SetAllGroupsExpanded(false);
        GUILayout.Space(12f);
        GUILayout.Label("Search:", _labelStyle, GUILayout.Width(48f));
        string newSearch = GUILayout.TextField(_search ?? string.Empty, GUILayout.Width(260f));
        if (!string.Equals(newSearch, _search, StringComparison.Ordinal))
            _search = newSearch;
        if (GUILayout.Button("Clear", GUILayout.Width(55f)))
            _search = string.Empty;
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(3f);

        DrawWearStatus();
        DrawHeader();

        _scroll = GUILayout.BeginScrollView(_scroll, true, true);
        GUILayout.BeginVertical(GUILayout.Width(GetTableWidth()));
        DrawGroup(ValheimUpdateKind.FixedUpdate, "MonoUpdaters.FixedUpdate");
        DrawGroup(ValheimUpdateKind.Update, "MonoUpdaters.Update");
        DrawGroup(ValheimUpdateKind.LateUpdate, "MonoUpdaters.LateUpdate");
        DrawGroup(ValheimUpdateKind.AI, "MonoUpdaters.UpdateAI");
        DrawGroup(ValheimUpdateKind.WearNTear, "WearNTearUpdater.UpdateWearNTear");
        GUILayout.EndVertical();
        GUILayout.EndScrollView();
    }

    private void DrawWearStatus()
    {
        int instances;
        int updatesPerFrame;
        int index;
        double lastDuration;
        double maxDuration;
        double lastLag;
        double maxLag;

        lock (_sync)
        {
            instances = _wearCurrentInstances;
            updatesPerFrame = _wearCurrentUpdatesPerFrame;
            index = _wearCurrentIndex;
            lastDuration = _wearLastCycleDurationMs;
            maxDuration = _wearMaxCycleDurationMs;
            lastLag = _wearLastCycleLagMs;
            maxLag = _wearMaxCycleLagMs;
        }

        string progress = instances > 0 ? $"{Math.Min(index, instances)}/{instances}" : "0/0";
        string tooltip =
            "WearNTearUpdater performs one Full pass per cycle, then processes incremental Wear batches over subsequent frames.\n" +
            "Instances is the current WearNTear list size. Updates/frame is the adaptive batch limit used by the game.\n" +
            "Cycle progress is the current list index. Positive lag means the completed cycle finished after its scheduled time; negative lag means it finished early.";
        GUILayout.BeginHorizontal(GUI.skin.box);
        GUILayout.Label(
            new GUIContent(
                $"WearNTear state | Instances: {instances} | Updates/frame: {updatesPerFrame} | Cycle progress: {progress} | " +
                $"Last cycle: {FormatMs(lastDuration)} | Max cycle: {FormatMs(maxDuration)} | " +
                $"Last lag: {FormatSignedMs(lastLag)} | Max lag: {FormatSignedMs(maxLag)}",
                tooltip),
            _labelStyle,
            GUILayout.ExpandWidth(true));
        GUILayout.EndHorizontal();
        GUILayout.Space(3f);
    }

    private void DrawHeader()
    {
        GUILayout.BeginHorizontal(GUILayout.Width(GetTableWidth()));
        GUILayout.Label("Scope", _headerStyle, GUILayout.Width(ScopeWidth));

        if (_view == ValheimUpdateView.OverOneSecond)
        {
            HeaderButton("ms / 1 sec", ValheimUpdateSortColumn.MsOneSecond, WideNumberWidth,
                "Total measured milliseconds in the rolling one-second window.");
            HeaderButton("calls / 1 sec", ValheimUpdateSortColumn.CallsOneSecond, WideNumberWidth,
                "Number of batch or phase invocations in the rolling one-second window.");
            HeaderButton("items / 1 sec", ValheimUpdateSortColumn.ItemsOneSecond, WideNumberWidth,
                "Scheduled list items in the rolling one-second window. A scheduled item may be skipped by the game when disabled.");
            HeaderButton("avg batch", ValheimUpdateSortColumn.AverageBatchMs, NumberWidth,
                "Average milliseconds per batch call in the rolling one-second window.");
            HeaderButton("avg items", ValheimUpdateSortColumn.AverageItems, NumberWidth,
                "Average scheduled items per call in the rolling one-second window.");
            HeaderButton("avg ms/item", ValheimUpdateSortColumn.AverageMsPerItem, WideNumberWidth,
                "Average inclusive milliseconds per scheduled item.");
            HeaderButton("last ms", ValheimUpdateSortColumn.LastMs, NumberWidth,
                "Duration of the latest batch or phase call.");
            HeaderButton("last items", ValheimUpdateSortColumn.LastItems, NumberWidth,
                "Scheduled items in the latest call.");
        }
        else
        {
            HeaderButton("raw max", ValheimUpdateSortColumn.RawMax, NumberWidth, "Slowest call in the rolling 60-second window.");
            HeaderButton("2nd max", ValheimUpdateSortColumn.SecondMax, NumberWidth, "Second-slowest call in the rolling 60-second window.");
            HeaderButton("3rd max", ValheimUpdateSortColumn.ThirdMax, NumberWidth, "Third-slowest call in the rolling 60-second window.");
            HeaderButton("> p99", ValheimUpdateSortColumn.AboveP99, NumberWidth, "Number of calls slower than the approximate p99 threshold in the rolling 60-second window.");
            HeaderButton("avg >p99", ValheimUpdateSortColumn.AvgAboveP99, WideNumberWidth, "Average duration of calls slower than the approximate p99 threshold.");
            HeaderButton("p99", ValheimUpdateSortColumn.P99, NumberWidth, "Approximate 99th percentile in the rolling 60-second window.");
            HeaderButton("> p95", ValheimUpdateSortColumn.AboveP95, NumberWidth, "Number of calls slower than the approximate p95 threshold in the rolling 60-second window.");
            HeaderButton("avg >p95", ValheimUpdateSortColumn.AvgAboveP95, WideNumberWidth, "Average duration of calls slower than the approximate p95 threshold.");
            HeaderButton("p95", ValheimUpdateSortColumn.P95, NumberWidth, "Approximate 95th percentile in the rolling 60-second window.");
            HeaderButton("max items", ValheimUpdateSortColumn.MaxItems, NumberWidth, "Largest scheduled batch in the rolling 60-second window.");
            HeaderButton("last ms", ValheimUpdateSortColumn.LastMs, NumberWidth, "Duration of the latest call.");
            HeaderButton("last items", ValheimUpdateSortColumn.LastItems, NumberWidth, "Scheduled items in the latest call.");
        }

        GUILayout.EndHorizontal();
        GUILayout.Space(2f);
    }

    private void HeaderButton(string text, ValheimUpdateSortColumn column, float width, string tooltip)
    {
        GUIStyle style = IsSortColumn(column) ? _activeHeaderStyle : _headerStyle;
        if (GUILayout.Button(new GUIContent(text, tooltip), style, GUILayout.Width(width)))
            SetSortColumn(column);
    }

    private void DrawGroup(ValheimUpdateKind kind, string title)
    {
        List<ValheimUpdateRowSnapshot> rows = BuildRows(kind);
        if (rows.Count == 0 && !string.IsNullOrEmpty(_search))
            return;

        bool expanded = _groupExpanded.TryGetValue(kind, out bool value) && value;
        GUILayout.BeginHorizontal(GUI.skin.box, GUILayout.Width(GetTableWidth()));
        if (GUILayout.Button(expanded ? "▼" : "▶", _compactButtonStyle, GUILayout.Width(16f)))
            expanded = !expanded;
        if (GUILayout.Button($"{title} ({rows.Count})", _groupStyle, GUILayout.Width(ScopeWidth - 16f)))
            expanded = !expanded;

        double groupMs = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Entry.Scope == TotalScope || rows[i].Entry.Scope == WearTotalScope)
            {
                groupMs = rows[i].MsOneSecond;
                break;
            }
            if (rows[i].Entry.FixedOrder >= 100)
                groupMs += rows[i].MsOneSecond;
        }

        GUILayout.Label($"{groupMs:0.000} ms / 1 sec", _groupStyle, GUILayout.Width(150f));
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        _groupExpanded[kind] = expanded;

        if (!expanded)
            return;

        for (int i = 0; i < rows.Count; i++)
            DrawRow(rows[i]);
    }

    private void DrawRow(ValheimUpdateRowSnapshot row)
    {
        ValheimUpdateScopeEntry entry = row.Entry;
        bool modScope = entry.DisplayName.StartsWith("Mod | ", StringComparison.Ordinal);
        string classification = modScope
            ? "Third-party or unknown scope"
            : "Built-in Valheim 0.219.14 scope";
        string tooltip = string.IsNullOrEmpty(entry.RuntimeTypeName)
            ? $"Full profileScope: {entry.Scope}\nClassification: {classification}"
            : $"Full profileScope: {entry.Scope}\nClassification: {classification}\nFirst observed runtime type: {entry.RuntimeTypeName}";

        GUILayout.BeginHorizontal(GUILayout.Width(GetTableWidth()));
        GUILayout.Label(new GUIContent(entry.DisplayName, tooltip), _labelStyle, GUILayout.Width(ScopeWidth));

        if (_view == ValheimUpdateView.OverOneSecond)
        {
            ValueLabel(row.MsOneSecond, WideNumberWidth);
            ValueLabel(row.CallsOneSecond, WideNumberWidth, "0.0");
            ItemLabel(entry, row.ItemsOneSecond, WideNumberWidth);
            ValueLabel(row.AverageBatchMs, NumberWidth);
            ItemValueLabel(entry, row.AverageItems, NumberWidth, "0.0");
            ItemValueLabel(entry, row.AverageMsPerItem, WideNumberWidth, "0.000000");
            ValueLabel(row.LastMs, NumberWidth);
            ItemLabel(entry, row.LastItems, NumberWidth);
        }
        else
        {
            ValueLabel(row.MaxSnapshot.RawMaxMs, NumberWidth);
            ValueLabel(row.MaxSnapshot.SecondMaxMs, NumberWidth);
            ValueLabel(row.MaxSnapshot.ThirdMaxMs, NumberWidth);
            CountLabel(row.MaxSnapshot.AboveP99Count, NumberWidth);
            ValueLabel(row.MaxSnapshot.AvgAboveP99Ms, WideNumberWidth);
            ValueLabel(row.MaxSnapshot.P99Ms, NumberWidth);
            CountLabel(row.MaxSnapshot.AboveP95Count, NumberWidth);
            ValueLabel(row.MaxSnapshot.AvgAboveP95Ms, WideNumberWidth);
            ValueLabel(row.MaxSnapshot.P95Ms, NumberWidth);
            ItemLabel(entry, row.MaxItems, NumberWidth);
            ValueLabel(row.LastMs, NumberWidth);
            ItemLabel(entry, row.LastItems, NumberWidth);
        }

        GUILayout.EndHorizontal();
    }

    private void ValueLabel(double value, float width, string format = "0.000") =>
        GUILayout.Label(value.ToString(format), _labelStyle, GUILayout.Width(width));

    private void ItemValueLabel(ValheimUpdateScopeEntry entry, double value, float width, string format) =>
        GUILayout.Label(entry.ItemsApplicable ? value.ToString(format) : "-", _labelStyle, GUILayout.Width(width));

    private void ItemLabel(ValheimUpdateScopeEntry entry, long value, float width) =>
        GUILayout.Label(entry.ItemsApplicable ? value.ToString() : "-", _labelStyle, GUILayout.Width(width));

    private void CountLabel(long value, float width) =>
        GUILayout.Label(value.ToString(), _labelStyle, GUILayout.Width(width));

    private void SetAllGroupsExpanded(bool expanded)
    {
        foreach (ValheimUpdateKind kind in Enum.GetValues(typeof(ValheimUpdateKind)))
            _groupExpanded[kind] = expanded;
    }

    private float GetTableWidth()
    {
        if (_view == ValheimUpdateView.OverOneSecond)
            return ScopeWidth + WideNumberWidth * 4f + NumberWidth * 4f + 20f;
        return ScopeWidth + NumberWidth * 10f + WideNumberWidth * 2f + 20f;
    }

    private void DrawHelpTab()
    {
        _helpScroll = GUILayout.BeginScrollView(_helpScroll);

        HeaderLabel("Purpose");
        Label("Valheim Update Profiler measures fixed centralized update loops that Valheim uses instead of individual MonoBehaviour callbacks.");
        Label("It is intended for mod developers measuring managed execution time, not as an FPS estimator.");
        GUILayout.Space(6f);

        HeaderLabel("MonoUpdatersExtra");
        Label("The profiler instruments CustomFixedUpdate, CustomUpdate, CustomLateUpdate and UpdateAI once per batch.");
        Label("Rows use the profileScope string passed by Valheim or another mod. Third-party scopes are discovered automatically when they use the same extension methods.");
        Label("Scheduled item counts are container.Count + source.Count before AddRange. They describe attempted list traversal, not guaranteed successful callbacks when an exception occurs.");
        Label("Known Valheim 0.219.14 profileScope values are shown without a prefix. Other scopes are prefixed with Mod |. Runtime type is captured best-effort from the first non-empty batch and is available in the row tooltip.");
        GUILayout.Space(6f);

        HeaderLabel("Top-level phases");
        Label("MonoUpdaters.FixedUpdate, Update and LateUpdate are measured as complete phases.");
        Label("Measured scopes is the sum of MonoUpdatersExtra batches observed inside each top-level invocation.");
        Label("Unattributed remainder is the top-level phase time minus those measured batches. It includes WaterVolume work and other code outside the centralized extension methods.");
        Label("Nested or replacement update loops can produce overlapping inclusive measurements; do not add unrelated profiler rows as exclusive cost.");
        GUILayout.Space(6f);

        HeaderLabel("WearNTear");
        Label("UpdateWearNTear is measured once per invocation and split into Full pass and Wear batch without patching individual building pieces.");
        Label("Full pass contains UpdateCover for enabled pieces and UpdateAshlandsMaterialValues for all pieces. Its item count is the total instance count, not the number of inner method calls.");
        Label("Wear batch reports scheduled list slots, current updates/frame, cycle progress, wall-clock cycle duration and schedule lag.");
        Label("Detailed UpdateCover, UpdateAshlandsMaterialValues or UpdateWear investigation can be performed manually through MonoBehaviour Frame Profiler when necessary.");
        GUILayout.Space(6f);

        HeaderLabel("Views");
        Label("Over 1 sec reports total milliseconds, calls and scheduled items in a rolling one-second window plus per-call and per-item averages.");
        Label("Max over 60 sec reports slowest calls, approximate p95/p99 values, counts above those thresholds and the average duration of the slower samples. All times are inclusive wall-clock measurements on the Unity main thread.");
        Label("Click a column header to sort descending. Sort selection is stored in the BepInEx config.");
        GUILayout.Space(6f);

        HeaderLabel("Limitations");
        Label("Direct calls to IMonoUpdater methods that bypass MonoUpdatersExtra are not visible here.");
        Label("A mod that fully replaces the Valheim loops may also bypass these measurements. Use MonoBehaviour Frame Profiler or Patch Profiler for those cases.");
        Label("The profiler itself adds a small Prefix/Finalizer cost per centralized batch, not per subordinate object.");

        GUILayout.EndScrollView();
    }

    private void EnsureStyles()
    {
        if (_styleSkin == GUI.skin && _labelStyle != null)
            return;

        _styleSkin = GUI.skin;
        _labelStyle = new GUIStyle(GUI.skin.label)
        {
            wordWrap = false,
            clipping = TextClipping.Clip,
            alignment = TextAnchor.MiddleLeft
        };
        _labelStyle.normal.textColor = _theme.TextColor;

        _headerStyle = new GUIStyle(GUI.skin.label)
        {
            wordWrap = false,
            clipping = TextClipping.Clip,
            alignment = TextAnchor.MiddleLeft,
            fontStyle = FontStyle.Bold
        };
        _headerStyle.normal.textColor = _theme.HeaderTextColor;

        _activeHeaderStyle = new GUIStyle(_headerStyle);
        Color activeColor = Color.Lerp(_theme.HeaderTextColor, _theme.AccentColor, 0.5f);
        _activeHeaderStyle.normal.textColor = activeColor;
        _activeHeaderStyle.hover.textColor = activeColor;
        _activeHeaderStyle.active.textColor = activeColor;

        _groupStyle = new GUIStyle(_headerStyle)
        {
            alignment = TextAnchor.MiddleLeft
        };

        _compactButtonStyle = new GUIStyle(GUI.skin.button)
        {
            margin = new RectOffset(1, 1, 1, 1),
            padding = new RectOffset(2, 3, 1, 1)
        };
    }

    private static string FormatMs(double value) => $"{value:0.000} ms";
    private static string FormatSignedMs(double value) => $"{value:+0.000;-0.000;0.000} ms";

    private void Label(string text, params GUILayoutOption[] options) =>
        GUILayout.Label(text ?? string.Empty, _labelStyle, options);

    private void HeaderLabel(string text, params GUILayoutOption[] options) =>
        GUILayout.Label(text ?? string.Empty, _headerStyle, options);
}
