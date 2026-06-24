#nullable disable

using System.Linq;
using UnityEngine;

namespace ValheimProfiler.Tools.MonoBehaviourProfiler;

internal sealed partial class MonoBehaviourProfilerTool
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
                new[] { "Profiler", "Behaviours to profile", "Help" },
                GUILayout.Width(470f));

            if (newTab != _mainTab)
            {
                _mainTab = newTab;
                MarkViewDirty();

                if (_mainTab == MainTab.BehavioursToProfile)
                {
                    ExpandPathsToSelected(collapseUnselected: false);
                    _selectionExpansionInitialized = true;
                    MarkSelectionRowsDirty();
                }
            }

            if (_mainTab == MainTab.Profiler)
            {
                GUILayout.Space(10f);
                Label("View:", GUILayout.Width(40f));

                ProfilerView newView = (ProfilerView)GUILayout.Toolbar(
                    (int)_profilerView,
                    new[] { "Over 1 sec", "Max over 60 sec" },
                    GUILayout.Width(290f));

                if (newView != _profilerView)
                {
                    _profilerView = newView;
                    MarkViewDirty();
                }

                GUILayout.Space(10f);

                bool newGroup = ProfilerGui.ToggleLayout(
                    _theme,
                    _groupByMod,
                    new GUIContent(
                        "Group by Mod",
                        "Group callbacks by their BepInEx mod or assembly.\nGroup summaries contain inclusive values and can overlap when inherited callbacks are selected for multiple runtime types."),
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
                case MainTab.BehavioursToProfile:
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
        string profilingState = _profilingActive ? "Profiling: ON" : _statsFrozen ? "Profiling: PAUSED" : "Profiling: OFF";
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
            _status = "Statistics reset.";
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
            Label("No data yet. Start profiling and trigger selected MonoBehaviour frame callbacks.");
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
            }

            if (GUILayout.Button("Collapse all", GUILayout.Width(88f)))
            {
                lock (_lock)
                {
                    foreach (string key in _groupExpanded.Keys.ToList())
                        _groupExpanded[key] = false;
                }
            }

            GUILayout.EndHorizontal();
            GUILayout.Space(2f);
        }

        float contentHeight = CalculateTableContentHeight();
        _profilerScroll = GUILayout.BeginScrollView(_profilerScroll, false, true);

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
        Label("MonoBehaviour Frame Profiler measures selected managed frame callbacks on the Unity main thread.");
        Label("Mod Update, FixedUpdate, LateUpdate and OnGUI callbacks are selected by default. Valheim and Other callbacks are disabled by default.");
        GUILayout.Space(6f);

        HeaderLabel("Inclusive time");
        Label("All reported times are inclusive: a callback includes time spent in methods and Harmony patches called from inside it.");
        Label("Inherited callbacks can be represented by more than one selected runtime type, and nested work can overlap with measurements from other profiler tools.");
        Label("Do not add arbitrary rows together as exclusive frame cost. Group summaries are diagnostic aggregates and can contain overlapping inclusive time.");
        GUILayout.Space(6f);

        HeaderLabel("Over 1 sec");
        Label("\"ms per frame\" is the average inclusive callback time per frame in a rolling one-second window.");
        Label("\"calls per frame\" is the average number of callback invocations per frame in the same window.");
        GUILayout.Space(6f);

        HeaderLabel("Max over 60 sec");
        Label("raw max, 2nd max and 3rd max are the three slowest callback invocations in the rolling 60-second window.");
        Label("p95 and p99 are approximate histogram percentiles. GC samples mark slow calls observed in a frame where a GC collection counter changed.");
        Label("A trailing ! marks an isolated high spike or a high GC-associated sample.");
        Label("Click a table column header to sort descending by that column. The active sort column is highlighted.");
        GUILayout.Space(6f);

        HeaderLabel("Selection");
        Label("Use Behaviours to profile to enable or disable individual frame callbacks.");
        Label("Mods, Valheim and Other are exclusive view tabs. Present in active scene is an optional view filter and never deletes saved selections.");
        Label("Selection changes take effect after Apply selection. Active profiling is restarted and statistics are reset.");
        Label("Selection overrides are stored in BepInEx/config/shudnal.ValheimProfiler/MonoBehaviourFrameSelection.cfg.");
        Label("The Include Valheim Profiler callbacks config option makes this profiler available in its own behaviour list for development measurements.");
        GUILayout.Space(6f);

        HeaderLabel("Notes");
        Label("Pause profiling freezes rolling windows so the captured values can be inspected.");
        Label("The profiler adds overhead. Profile only the callbacks needed for the current investigation.");
        Label("Rare lifecycle and one-time methods will be handled by a separate Call/Lifecycle Profiler in a future version.");

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