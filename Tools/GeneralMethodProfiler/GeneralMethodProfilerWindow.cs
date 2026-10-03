#nullable disable

using System.Linq;
using UnityEngine;

namespace ValheimProfiler.Tools.GeneralMethodProfiler;

internal sealed partial class GeneralMethodProfilerTool
{
    private GUIStyle _helpLabelStyle;

    private void DrawWindow(int id)
    {
        EnsureStyles();
        Color oldContentColor = GUI.contentColor;
        GUI.contentColor = _theme.TextColor;

        try
        {
            GUILayout.BeginVertical();
            DrawToolbar();
            _safetyReport.DrawRecoveryBanner(_app.InstrumentationSafety);
            GUILayout.Space(2f);

            GUILayout.BeginHorizontal();
            MainTab tab = (MainTab)_safetyReport.DrawTabs((int)_mainTab, "Methods to profile", 520f,
                _app.InstrumentationSafety.RestartRequired);
            if (tab != _mainTab)
            {
                _mainTab = tab;
                if (_mainTab == MainTab.MethodsToProfile && !_listReady)
                    RefreshMethodList();
                MarkViewDirty();
                MarkSelectionRowsDirty();
            }
            if (_mainTab == MainTab.Profiler)
                DrawProfilerControls();
            GUILayout.EndHorizontal();
            GUILayout.Space(3f);

            switch (_mainTab)
            {
                case MainTab.Profiler:
                    DrawProfilerTab();
                    break;
                case MainTab.MethodsToProfile:
                    DrawSelectionTab();
                    break;
                case MainTab.Safety:
                    _safetyReport.Draw(MainWindowHeight);
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
        string state = _app.InstrumentationSafety.RestartRequired ? "RECOVERY UNCERTAIN" :
            _instrumentationTransition == InstrumentationTransition.Installing ? "INSTALLING" :
            _instrumentationTransition == InstrumentationTransition.Removing ? "REMOVING" :
            _instrumentationTransition == InstrumentationTransition.RollingBack ? "ROLLING BACK" :
            _profilingActive ? "ON" : _statsFrozen ? "PAUSED" : "OFF";
        GUILayout.Label(new GUIContent("Profiling: " + state + " | " + _status, _status), _labelStyle, GUILayout.ExpandWidth(true));
        bool controlsEnabled = GUI.enabled;
        GUI.enabled = controlsEnabled && !IsTransitioning;
        bool gradualStart = ProfilerGui.ToggleLayout(_theme, _gradualProfilingStart,
            new GUIContent("Gradual start", "Spread Harmony install/remove work across frames using a 400 ms / 2560 operation budget. Disable to process the complete transition in one Unity frame (one long freeze)."), 115f, _labelStyle);
        if (gradualStart != _gradualProfilingStart)
            SetGradualProfilingStart(gradualStart);
        GUILayout.Space(6f);
        if (_instrumentationTransition == InstrumentationTransition.Installing)
        {
            GUI.enabled = controlsEnabled;
            if (GUILayout.Button("Cancel start", GUILayout.Width(125f)))
                RequestCancelStart();
        }
        else
        {
            GUI.enabled = controlsEnabled && !IsTransitioning && (_profilingActive || !_app.InstrumentationSafety.RestartRequired);
            bool toggleProfiling = GUILayout.Button(new GUIContent(_profilingActive ? "Pause profiling" : "Start profiling",
                _app.InstrumentationSafety.RestartRequired ? _app.InstrumentationSafety.RestartMessage : "Install or remove this profiler's measurement hooks."), GUILayout.Width(125f));
            if (toggleProfiling)
            {
                if (_profilingActive)
                    StopProfiling();
                else
                    StartProfiling();
            }
        }
        GUI.enabled = controlsEnabled && !IsTransitioning;
        if (GUILayout.Button(new GUIContent("Reset stats", "Clear all method and context statistics. Active instrumentation remains installed."), GUILayout.Width(100f)))
        {
            ResetAllStats();
            _status = "Statistics and call contexts reset.";
        }
        GUI.enabled = controlsEnabled;
        if (GUILayout.Button("Close", GUILayout.Width(70f)))
            IsWindowVisible = false;
        GUILayout.EndHorizontal();
        if (IsTransitioning)
            ProfilerGui.ProgressBar(_theme, TransitionProgress, TransitionProgressText);
        if (_treeLimitHits > 0 || _depthLimitHits > 0 || _stackMismatches > 0 || _hookFailures > 0)
            DrawCaptureDiagnostics();
    }

    private void DrawProfilerControls()
    {
        GUILayout.Space(10f);
        Label("View:", GUILayout.Width(40f));
        ProfilerView view = (ProfilerView)GUILayout.Toolbar((int)_profilerView,
            new[] { "Frame budget", "Max over 60 sec" }, GUILayout.Width(290f));
        if (view != _profilerView)
        {
            _profilerView = view;
            _app.Config.GeneralMethodProfilerView.Value = view == ProfilerView.Avg1s ? "FrameBudget" : view.ToString();
            MarkViewDirty();
        }
        GUILayout.Space(10f);
        DrawTargetFpsField();
        GUILayout.Space(10f);
        bool group = ProfilerGui.ToggleLayout(_theme, _groupByMod,
            new GUIContent("Group by Mod", "Group methods by their owning BepInEx plugin assembly. Inclusive rows can overlap; group totals are not exclusive CPU cost."), 135f, _labelStyle);
        if (group != _groupByMod)
        {
            _groupByMod = group;
            _app.Config.GeneralMethodProfilerGroupByMod.Value = group;
            MarkViewDirty();
        }
        GUILayout.Space(6f);
        bool ignoreGc = ProfilerGui.ToggleLayout(_theme, _ignoreGcAllocations,
            new GUIContent("Ignore gc allocations", "Discard samples potentially contaminated by a GC collection in the current frame before any method or context statistics are updated. The heuristic does not attribute the collection to one method. Changing this option resets all statistics."), 165f, _labelStyle);
        if (ignoreGc != _ignoreGcAllocations)
            SetIgnoreGcAllocations(ignoreGc);
        GUILayout.Space(6f);
        bool excludeNested = ProfilerGui.ToggleLayout(_theme, _excludeMeasuredNestedCalls,
            new GUIContent("Exclude nested calls", "Subtract nested calls measured by General Method Profiler and the profiler bookkeeping used to measure those nested calls from parent method/context timings. Unmeasured nested calls remain included. Changing this option resets all statistics."), 145f, _labelStyle);
        if (excludeNested != _excludeMeasuredNestedCalls)
            SetExcludeMeasuredNestedCalls(excludeNested);
        GUILayout.Space(10f);
        DrawProfilerFilter();
    }

    private void DrawTargetFpsField()
    {
        GUILayout.Label(new GUIContent("Target FPS:", "Fixed FPS target used by Frame budget. The last one second of accepted measured time and calls is divided by this value; current game FPS is not used."), _labelStyle, GUILayout.Width(72f));
        const string controlName = "ValheimProfiler.General.TargetFps";
        if (GUI.GetNameOfFocusedControl() != controlName)
            _targetFpsText = FrameBudgetTargetFps.ToString();
        GUI.SetNextControlName(controlName);
        string value = GUILayout.TextField(_targetFpsText ?? FrameBudgetTargetFps.ToString(), GUILayout.Width(48f));
        if (value != _targetFpsText)
        {
            _targetFpsText = value;
            if (int.TryParse(value, out int parsed) && parsed >= 1 && parsed <= 1000 && parsed != _app.Config.FrameBudgetTargetFps.Value)
            {
                _app.Config.FrameBudgetTargetFps.Value = parsed;
                MarkViewDirty();
            }
        }
    }

    private void DrawProfilerFilter()
    {
        Label("Search:", GUILayout.Width(55f));
        string search = GUILayout.TextField(_profilerSearch, GUILayout.ExpandWidth(true));
        if (search != _profilerSearch)
        {
            _profilerSearch = search;
            FiltersChanged();
        }
        if (GUILayout.Button("Clear", GUILayout.Width(65f)))
        {
            _profilerSearch = string.Empty;
            FiltersChanged();
            GUI.FocusControl(null);
        }
        if (_groupByMod)
        {
            if (GUILayout.Button("Expand all", GUILayout.Width(85f)))
            {
                foreach (string key in _groupExpanded.Keys.ToArray())
                    _groupExpanded[key] = true;
                MarkViewDirty();
            }
            if (GUILayout.Button("Collapse all", GUILayout.Width(85f)))
            {
                foreach (string key in _groupExpanded.Keys.ToArray())
                    _groupExpanded[key] = false;
                MarkViewDirty();
            }
        }
    }

    private void SetIgnoreGcAllocations(bool value)
    {
        _ignoreGcAllocations = value;
        _app.Config.GeneralMethodProfilerIgnoreGcAllocations.Value = value;
        ResetAllStats();
        _status = value ? "GC-associated samples are discarded. Statistics reset." : "GC-associated samples are included. Statistics reset.";
    }

    private void SetExcludeMeasuredNestedCalls(bool value)
    {
        if (_excludeMeasuredNestedCalls == value)
            return;

        _excludeMeasuredNestedCalls = value;
        _app.Config.GeneralMethodProfilerExcludeMeasuredNestedCalls.Value = value;
        ResetAllStats();
        _status = value
            ? "Measured nested calls are excluded from method/context timings. Statistics reset."
            : "Measured nested calls are included in method/context timings. Statistics reset.";
    }

    private void SetGradualProfilingStart(bool value)
    {
        if (_gradualProfilingStart == value)
            return;

        _gradualProfilingStart = value;
        _app.Config.GeneralMethodProfilerGradualProfilingStart.Value = value;
        _status = value
            ? "Gradual instrumentation transitions use the 400 ms frame budget."
            : "Instrumentation transitions run in one Unity frame.";
    }

    private void DrawProfilerTab()
    {
        RefreshCachedViewIfNeeded();
        PrepareTableLayout();
        DrawTableHeader();
        if ((_groupByMod ? _cachedGroupedRows.Count : _cachedFlatRows.Count) == 0)
        {
            Label("No matching samples. Select methods, start profiling and trigger their execution on the main thread.");
            return;
        }
        float height = CalculateTableContentHeight();
        _profilerScroll = GUILayout.BeginScrollView(_profilerScroll, false, false);
        Rect content = GUILayoutUtility.GetRect(_drawContentWidth, height,
            GUILayout.Width(_drawContentWidth), GUILayout.Height(height));
        DrawTableContent(content);
        GUILayout.EndScrollView();
    }

    private void DrawHelpTab()
    {
        _helpScroll = GUILayout.BeginScrollView(_helpScroll);
        HelpSection("Purpose",
            "General Method Profiler measures synchronous managed methods declared in loaded BepInEx plugin assemblies, only on the Unity main thread. It covers ordinary calls, delegates, events, synchronous RPC handlers, managers and services.",
            "Registered Harmony callbacks and MonoBehaviour messages belong in their dedicated profilers. Plugin dependencies, game, Unity and system assemblies are not scanned. A method called from a worker is ignored before any Unity API or call-stack access.");
        HelpSection("Selection",
            "Methods are disabled by default. Expand a mod, module and class, then select individual methods or use a container checkbox. Bulk selection affects only methods matching the current filters, even when their classes are collapsed. Top-level mod names use normal text by default and become bold when that mod has selected methods, making the profiling set easier to scan.",
            "Show unavailable exposes exclusion reasons. Search and Selected only change the view, not the stored selection. Apply selection saves it and restarts active profiling with fresh data. Start also applies the current selection.",
            "Selection is stored in BepInEx/config/shudnal.ValheimProfiler/GeneralMethodSelection.cfg. Stable keys include plugin GUIDs, assembly/module names and full method signatures. Selections for temporarily absent mods are retained.");
        HelpSection("Timing and sorting",
            "The table follows Patch Profiler: Mod, timing statistics, Class, then Class:Method with a Call tree button. Class width fits the longest displayed declaring-class name. Grouped child rows leave the Mod cell empty.",
            "The table uses the shared rolling statistics engine: Frame budget, raw/2nd/3rd maximum, approximate p95/p99, counts and average durations above those boundaries, samples and GC samples. Text columns sort ascending and numeric columns descending; the active header shows the direction. Sort order is saved separately for each view.",
            "Frame budget uses the last one second of accepted measurements but divides measured milliseconds and calls by the configured Target FPS, not by the game's current FPS. This gives every row the same fixed denominator and includes target frames with no call. avg call is total measured time divided by accepted calls and is independent of Target FPS.",
            "Main-table durations are inclusive by default. Enable Exclude nested calls to subtract nested invocations measured by General Method Profiler and the timing-bookkeeping performed around those nested measurements; unselected, unavailable and otherwise uninstrumented calls remain included. Changing the mode resets statistics. A trailing ! marks an isolated high maximum or a high GC-associated sample.",
            "Ignore gc allocations keeps the existing frame-level GC-collection heuristic, not an allocated-byte measurement. A slow sample is associated with GC when collection counters differ from the last frame baseline; the profiler does not try to identify which method triggered the collection. Discarded samples do not affect any method/context timing, count, maximum, percentile or self-time statistic.");
        HelpSection("Call tree",
            "Call tree opens the contexts observed for that method. A context is the entire runtime path. Calls under different parents and recursive levels remain separate; overloads are distinct methods. Choose a context, then navigate to its parent, root or a direct child.",
            "Root and Current describe the real recorded root and selected node. The view also exposes other contexts of the originally selected method. Context totals and self-time summaries cover accepted calls since the last reset; rolling figures use the same 60-second window as the main table.",
            "Self time subtracts measured direct-child durations from the invocation. It still contains uninstrumented work, skipped/capped descendants and profiler overhead; it is not native exclusive CPU time. A rejected GC child is still subtracted from a retained parent's self time.",
            "Only successfully instrumented methods are visible. Calls through unselected code connect the nearest instrumented ancestors; this is not a complete program call graph. A discarded parent can remain as a zero-sample structural node containing retained child samples.");
        HelpSection("Bounds, lifecycle and safety",
            "Call-tree nodes and stack depth are bounded by configuration. At the node cap, existing paths continue to collect and method aggregates remain available for new paths. At the depth cap, deeper calls are omitted from both tree and aggregates and counted in the diagnostics line.",
            "With Gradual start enabled, Start and Pause spread per-method Harmony rebuilds across Unity frames with a 400 ms / 2560-operation backend budget and visible progress. Disable it to process the complete transition in one Unity frame, causing one long freeze instead of incremental progress. One rebuild is indivisible and may exceed either budget. Sampling becomes active only after the full install plan completes; Pause disables sampling before reverse-order removal. Cancel during installation reverses the current install progress back toward zero while rolling back the installed subset. Reset keeps active instrumentation but clears statistics and topology, not safety failures. Session generations invalidate in-flight samples across reset, pause, reconfiguration and shutdown. Recursive calls and exceptions are tracked without replacing or suppressing application exceptions.",
            "The shared safety guard rejects unsupported signatures and profiler/runtime infrastructure. The same MethodBase cannot be instrumented twice across tools. Equal names or Harmony owners in different assemblies are not exclusions. The MonoMod resolver cache is cleared before and after each installation, rollback and removal operation; a reset failure stops further instrumentation.",
            "Methods declared on value types (structs), including static methods and operators, are excluded before any Harmony mutation. This is a conservative policy after a reproduced HSLColor instrumentation failure, not a ban on class methods that return or accept structs. Show unavailable exposes the reason; previously selected excluded methods are also reported in Skipped methods at capture start. Prevented attempts are ordinary exclusions, not installation failures. Their work remains inside an eligible caller's inclusive time.",
            "Skipped methods is yellow for real failures and lists installation/rollback/removal problems first, expected exclusions afterward. Failures only and search filter this report without erasing it. Diagnostics remain available regardless of logging. General / Logging enabled adds compact method failures and grouped safety skips to the ordinary log, with full method details and exceptions in a separate report under BepInEx/config/shudnal.ValheimProfiler. Each operation displays at most 10 failure identities and 8 skip groups; omitted details remain in the report. The latest and previous report are retained per operation. When disabled, normal operations log aggregate counts only; uncertain recovery always emits a restart warning. Patch Profiler's separate crash trace setting remains independent.",
            "Failed methods are not retried in this process. Uncertain recovery stops further installation and blocks new Patch/General captures until game restart; Reset stats cannot clear the warning. A successful registration check does not restore scene state or undo early static initialization. The guard reduces identified risks; it cannot make arbitrary runtime detouring risk-free. Inlined calls can remain invisible. Broad selections add substantial instrumentation and observer overhead; narrow the selection to the code under investigation.");
        GUILayout.EndScrollView();
    }

    private void HelpSection(string title, params string[] paragraphs)
    {
        HeaderLabel(title);
        foreach (string paragraph in paragraphs)
            GUILayout.Label(paragraph, _helpLabelStyle, GUILayout.ExpandWidth(true));
        GUILayout.Space(8f);
    }

    private void EnsureStyles()
    {
        if (_styleSkin == GUI.skin && _labelStyle != null)
            return;

        _styleSkin = GUI.skin;
        _layoutMetricsDirty = true;
        MarkSelectionRowsDirty();

        _labelStyle = new GUIStyle(GUI.skin.label)
        {
            wordWrap = false,
            clipping = TextClipping.Clip,
            alignment = TextAnchor.UpperLeft
        };
        _labelStyle.normal.textColor = _theme.TextColor;

        _headerLabelStyle = new GUIStyle(GUI.skin.label)
        {
            wordWrap = false,
            clipping = TextClipping.Clip,
            alignment = TextAnchor.UpperLeft,
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

        _helpLabelStyle = new GUIStyle(_labelStyle) { wordWrap = true };

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