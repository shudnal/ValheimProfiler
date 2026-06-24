#nullable disable

using System.Linq;
using UnityEngine;

namespace ValheimProfiler.Tools.MonoBehaviourCallProfiler;

internal sealed partial class MonoBehaviourCallProfilerTool
{
    private void DrawWindow(int id)
    {
        EnsureStyles();

        Color oldContentColor = GUI.contentColor;
        GUI.contentColor = _theme.TextColor;

        try
        {
            GUILayout.BeginVertical();

            DrawToolbar();
            GUILayout.Space(2f);

            GUILayout.BeginHorizontal();

            MainTab newTab = (MainTab)GUILayout.Toolbar(
                (int)_mainTab,
                new[] { "Profiler", "Calls to profile", "Help" },
                GUILayout.Width(470f));

            if (newTab != _mainTab)
            {
                _mainTab = newTab;
                MarkViewDirty();

                if (_mainTab == MainTab.CallsToProfile)
                {
                    ExpandPathsToSelected(collapseUnselected: false);
                    _selectionExpansionInitialized = true;
                    MarkSelectionRowsDirty();
                }
            }

            if (_mainTab == MainTab.Profiler)
            {
                GUILayout.Space(12f);

                bool newGroup = ProfilerGui.ToggleLayout(
                    _theme,
                    _groupByMod,
                    new GUIContent(
                        "Group by Mod",
                        "Group lifetime call statistics by their BepInEx mod or assembly.\nGroup totals are inclusive and can overlap when inherited methods are selected for multiple runtime types."),
                    135f,
                    _labelStyle,
                    0f);

                if (newGroup != _groupByMod)
                {
                    _groupByMod = newGroup;
                    MarkViewDirty();
                }
            }

            GUILayout.EndHorizontal();
            GUILayout.Space(3f);

            switch (_mainTab)
            {
                case MainTab.Profiler:
                    DrawProfilerTab();
                    break;
                case MainTab.CallsToProfile:
                    DrawSelectionTab();
                    break;
                default:
                    DrawHelpTab();
                    break;
            }

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

        string listState = _listReady ? $"List: ready ({_methodsByKey.Count})" : "List: empty";
        string profilingState = _profilingActive ? "Profiling: ON" : "Profiling: OFF";
        Label($"{listState} | {profilingState} | Status: {_status}", GUILayout.ExpandWidth(true));

        string profilingButton = _profilingActive ? "Pause profiling" : "Start profiling";
        if (GUILayout.Button(profilingButton, GUILayout.Width(125f)))
        {
            if (_profilingActive)
                StopProfiling();
            else
                StartProfiling();
        }

        if (GUILayout.Button("Reset stats", GUILayout.Width(100f)))
        {
            ResetAllStats();
            _status = "Lifetime statistics reset.";
        }

        if (GUILayout.Button("Close", GUILayout.Width(70f)))
            IsWindowVisible = false;

        GUILayout.EndHorizontal();
    }

    private void DrawProfilerTab()
    {
        RefreshCachedViewIfNeeded();
        PrepareTableLayout();
        DrawTableHeader();

        bool hasRows = _groupByMod ? _cachedGroupedRows.Count > 0 : _cachedFlatRows.Count > 0;
        if (!hasRows)
        {
            Label("No data yet. Start profiling and trigger selected lifecycle or declared MonoBehaviour methods.");
            return;
        }

        if (_groupByMod)
        {
            GUILayout.BeginHorizontal();

            if (GUILayout.Button("Expand all", GUILayout.Width(88f)))
            {
                lock (_lock)
                {
                    foreach (string key in _groupExpanded.Keys.ToList())
                        _groupExpanded[key] = true;
                }

                _layoutMetricsDirty = true;
            }

            if (GUILayout.Button("Collapse all", GUILayout.Width(88f)))
            {
                lock (_lock)
                {
                    foreach (string key in _groupExpanded.Keys.ToList())
                        _groupExpanded[key] = false;
                }

                _layoutMetricsDirty = true;
            }

            GUILayout.EndHorizontal();
            GUILayout.Space(2f);
        }

        float contentHeight = CalculateTableContentHeight();
        _profilerScroll = GUILayout.BeginScrollView(_profilerScroll);

        Rect contentRect = GUILayoutUtility.GetRect(
            _drawContentWidth,
            contentHeight,
            GUILayout.Width(_drawContentWidth),
            GUILayout.Height(contentHeight));

        DrawTableContent(contentRect);
        GUILayout.EndScrollView();
    }

    private void DrawHelpTab()
    {
        _helpScroll = GUILayout.BeginScrollView(_helpScroll);

        HeaderLabel("Purpose");
        Label("MonoBehaviour Call Profiler measures selected rare, lifecycle and manually chosen managed instance methods on the Unity main thread.");
        Label("It uses lifetime statistics. Samples are retained until Reset stats, selection changes, or profiling is started again.");
        GUILayout.Space(6f);

        HeaderLabel("Default lifecycle selection");
        Label("Synchronous mod Awake, Start, OnEnable, OnDisable and OnDestroy methods are selected by default.");
        Label("Valheim and Other methods are disabled by default. Declared methods are always opt-in.");
        GUILayout.Space(6f);

        HeaderLabel("Timing limits");
        Label("All times are inclusive and include nested calls and Harmony patches executed inside the measured method.");
        Label("Only calls that occur after instrumentation starts are visible. Awake and Start calls that already happened cannot be reconstructed.");
        Label("Coroutine and async entries measure only the synchronous call that creates or advances the state machine, not the complete asynchronous lifetime.");
        GUILayout.Space(6f);

        HeaderLabel("Statistics");
        Label("calls, total, average, max and last are exact lifetime counters for the current profiling session.");
        Label("p95 and p99 are approximate lifetime percentiles maintained in a fixed logarithmic histogram without time-based expiration.");
        Label("first at and last at are elapsed times relative to the current profiling session start.");
        Label("Grouped p95 and p99 values show the highest child percentile rather than a mathematically merged percentile.");
        Label("Click a table column header to sort descending by that column. The active sort column is highlighted and persisted in config.");
        GUILayout.Space(6f);

        HeaderLabel("Selection");
        Label("Calls to profile reuses the Frame Profiler source, search, grouping and scene-filter workflow.");
        Label("Show declared methods reveals manually selectable methods declared directly by each MonoBehaviour type. Selected declared methods remain visible when the filter is off.");
        Label("Selection changes take effect after Apply selection. Active instrumentation is restarted and lifetime statistics are reset.");
        Label("Selection overrides are stored in BepInEx/config/shudnal.ValheimProfiler/MonoBehaviourCallSelection.cfg.");
        GUILayout.Space(6f);

        HeaderLabel("Overhead");
        Label("The profiler adds a Harmony prefix and finalizer to every selected method. Select only methods relevant to the current investigation.");
        Label("Regular Update, FixedUpdate, LateUpdate and OnGUI callbacks are intentionally excluded here and belong in MonoBehaviour Frame Profiler.");

        GUILayout.EndScrollView();
    }

    private void EnsureStyles()
    {
        if (_styleSkin == GUI.skin && _labelStyle != null)
            return;

        _styleSkin = GUI.skin;
        _layoutMetricsDirty = true;

        _labelStyle = new GUIStyle(GUI.skin.label)
        {
            wordWrap = false,
            clipping = TextClipping.Clip,
            alignment = TextAnchor.MiddleLeft
        };
        _labelStyle.normal.textColor = _theme.TextColor;

        _headerLabelStyle = new GUIStyle(GUI.skin.label)
        {
            wordWrap = false,
            clipping = TextClipping.Clip,
            alignment = TextAnchor.MiddleLeft,
            fontStyle = FontStyle.Bold
        };
        _headerLabelStyle.normal.textColor = _theme.HeaderTextColor;

        _activeHeaderLabelStyle = new GUIStyle(_headerLabelStyle);
        Color activeSortColor = Color.Lerp(_theme.HeaderTextColor, _theme.AccentColor, 0.5f);
        _activeHeaderLabelStyle.normal.textColor = activeSortColor;
        _activeHeaderLabelStyle.hover.textColor = activeSortColor;
        _activeHeaderLabelStyle.active.textColor = activeSortColor;
        _activeHeaderLabelStyle.focused.textColor = activeSortColor;
        _activeHeaderLabelStyle.onNormal.textColor = activeSortColor;
        _activeHeaderLabelStyle.onHover.textColor = activeSortColor;
        _activeHeaderLabelStyle.onActive.textColor = activeSortColor;
        _activeHeaderLabelStyle.onFocused.textColor = activeSortColor;

        _groupLabelStyle = new GUIStyle(_headerLabelStyle)
        {
            alignment = TextAnchor.MiddleLeft
        };

        _compactButtonStyle = new GUIStyle(GUI.skin.button)
        {
            margin = new RectOffset(1, 1, 1, 1),
            padding = new RectOffset(2, 2, 0, 0)
        };
    }

    private void Label(string text, params GUILayoutOption[] options) =>
        GUILayout.Label(text ?? string.Empty, _labelStyle, options);

    private void HeaderLabel(string text, params GUILayoutOption[] options) =>
        GUILayout.Label(text ?? string.Empty, _headerLabelStyle, options);
}
