#nullable disable

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ValheimProfiler.Tools.PatchProfiler;

internal sealed partial class PatchProfilerTool
{
    private void DrawProfilerTab()
    {
        RefreshCachedViewIfNeeded();
        PrepareProfilerLayoutWidths();

        DrawHeader();

        if (_profilerView == ProfilerView.Avg1s)
            DrawTotalSummaryRow();

        GUILayout.Space(2f);
        Label("The table shows sampled entries only. Selected targets and detected calls stay hidden until their code path executes after profiling starts.");
        GUILayout.Space(2f);

        bool hasRows = _groupByMod ? _cachedGroupedRows.Count > 0 : _cachedFlatRows.Count > 0;
        if (!hasRows)
        {
            Label("No data yet. Run profiling and trigger patched code paths.");
            return;
        }

        if (_groupByMod)
        {
            GUILayout.BeginHorizontal();

            if (GUILayout.Button("Expand all", GUILayout.Width(88f)))
            {
                lock (_lock)
                {
                    foreach (string key in _modExpanded.Keys.ToList())
                        _modExpanded[key] = true;
                }

                MarkViewDirty();
            }

            if (GUILayout.Button("Collapse all", GUILayout.Width(88f)))
            {
                lock (_lock)
                {
                    foreach (string key in _modExpanded.Keys.ToList())
                        _modExpanded[key] = false;
                }

                MarkViewDirty();
            }

            GUILayout.EndHorizontal();
            GUILayout.Space(2f);
        }

        float contentHeight = CalculateProfilerContentHeight();
        float contentWidth = CalculateProfilerContentWidth();

        _scroll = GUILayout.BeginScrollView(_scroll, false, false);

        Rect contentRect = GUILayoutUtility.GetRect(
            contentWidth,
            contentHeight,
            GUILayout.Width(contentWidth),
            GUILayout.Height(contentHeight));

        DrawProfilerVirtualContent(contentRect);

        GUILayout.EndScrollView();
    }

    private void DrawModsTab()
    {
        if (!_listReady)
        {
            Label("Patch list could not be loaded. See the status line for details.");
            return;
        }

        GUILayout.BeginHorizontal();

        if (GUILayout.Button("Enable all", GUILayout.Width(88f)))
        {
            bool changed = false;
            lock (_lock)
            {
                foreach (string key in _modsToProfile.Keys.ToList())
                {
                    if (_modsToProfile[key])
                        continue;

                    _modsToProfile[key] = true;
                    changed = true;
                }
            }

            _modsSelectionDirty |= changed;
        }

        if (GUILayout.Button("Disable all", GUILayout.Width(88f)))
        {
            bool changed = false;
            lock (_lock)
            {
                foreach (string key in _modsToProfile.Keys.ToList())
                {
                    if (!_modsToProfile[key])
                        continue;

                    _modsToProfile[key] = false;
                    changed = true;
                }
            }

            _modsSelectionDirty |= changed;
        }

        GUI.enabled = _modsSelectionDirty;
        GUIStyle applyStyle = _modsSelectionDirty ? _theme.AccentButtonStyle : GUI.skin.button;
        if (GUILayout.Button("Apply selection", applyStyle, GUILayout.Width(110f)))
        {
            ResetProfilingSelection();
        }
        GUI.enabled = true;

        if (_modsSelectionDirty)
        {
            GUILayout.Space(6f);
            GUILayout.Label("Selection has changed", _theme.AccentLabelStyle, GUILayout.ExpandWidth(true));
        }
        else
        {
            GUILayout.FlexibleSpace();
        }

        GUILayout.EndHorizontal();
        GUILayout.Space(3f);

        bool transpiledTargetsEnabled;
        lock (_lock)
            transpiledTargetsEnabled = _modsToProfile.TryGetValue(TranspiledTargetsGuid, out bool targetTimingEnabled) && targetTimingEnabled;

        GUILayout.BeginVertical(GUI.skin.box);
        if (!transpiledTargetsEnabled)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Whole transpiled-target timing is disabled", _theme.AccentLabelStyle, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("Enable Transpiled methods", _theme.AccentButtonStyle, GUILayout.Width(190f)))
            {
                lock (_lock)
                    _modsToProfile[TranspiledTargetsGuid] = true;

                _modsSelectionDirty = true;
                transpiledTargetsEnabled = true;
            }
            GUILayout.EndHorizontal();
        }
        else
        {
            HeaderLabel("Whole transpiled-target timing is enabled");
        }

        Label("The separate Transpiled methods entry controls timing of complete transpiled target methods. Enabling a source mod does not enable it automatically. Transpiler call rows still follow their source mod selection.");
        GUILayout.EndVertical();
        GUILayout.Space(3f);

        List<string> modGuids;
        Dictionary<string, bool> selectionSnapshot;
        Dictionary<string, int> countSnapshot;
        Dictionary<string, string> nameSnapshot;

        lock (_lock)
        {
            modGuids = _modsToProfile.Keys
                .OrderBy(value => value == TranspiledTargetsGuid ? 0 : 1)
                .ThenBy(value => _modGuidToName.TryGetValue(value, out string name) ? name : value, StringComparer.OrdinalIgnoreCase)
                .ToList();
            selectionSnapshot = _modsToProfile.ToDictionary(pair => pair.Key, pair => pair.Value);
            countSnapshot = _modPatchCounts.ToDictionary(pair => pair.Key, pair => pair.Value);
            nameSnapshot = _modGuidToName.ToDictionary(pair => pair.Key, pair => pair.Value);
        }

        _modsScroll = GUILayout.BeginScrollView(_modsScroll);

        foreach (string modGuid in modGuids)
        {
            bool enabled = selectionSnapshot.TryGetValue(modGuid, out bool selected) && selected;
            int count = countSnapshot.TryGetValue(modGuid, out int entryCount) ? entryCount : 0;
            string modName = nameSnapshot.TryGetValue(modGuid, out string name) ? name : "Unknown";

            GUILayout.BeginHorizontal();

            GUILayout.Space(8f);

            string selectionTooltip = modGuid == TranspiledTargetsGuid
                ? "Separate switch for measuring complete transpiled target methods. Source-mod selection controls Transpiler call rows, not this switch."
                : "Enable Patch Profiler entries attributed to this BepInEx mod.";

            bool newValue = ProfilerGui.ToggleLayout(
                _theme,
                enabled,
                new GUIContent(modGuid, selectionTooltip),
                390f,
                _labelStyle);

            if (modGuid == TranspiledTargetsGuid)
                GUILayout.Label("Transpiled methods (whole targets, separate switch)", _theme.AccentLabelStyle, GUILayout.Width(300f));
            else
                Label(modName, GUILayout.Width(300f));
            Label($"entries: {count}", GUILayout.Width(110f));

            GUILayout.EndHorizontal();

            if (newValue != enabled)
            {
                lock (_lock)
                    _modsToProfile[modGuid] = newValue;

                _modsSelectionDirty = true;
            }
        }

        GUILayout.EndScrollView();
    }

    private void DrawHelpTab()
    {
        _modsScroll = GUILayout.BeginScrollView(_modsScroll);

        Label("Patch Profiler measures execution time of Harmony patch methods on the main thread.");
        Label("It instruments patch methods with a timing prefix and finalizer, then aggregates measured wall-clock duration in milliseconds.");
        Label("Hover important controls and column headers for contextual explanations.");
        GUILayout.Space(6f);

        GroupLabel("Transpilers");
        Label("Transpiler methods themselves usually run while Harmony applies patches, not every frame.");
        Label("For every transpiled target method, the profiler can measure the whole modified target method as a Transpiled target row.");
        Label("Whole-target timing is controlled by the separate Transpiled methods selection entry and is not enabled automatically when a source mod is selected.");
        Label("The profiler compares the original target IL with the final IL after Harmony transpilers and registers net-new direct call/callvirt instructions as Transpiler call rows.");
        Label("Duplicate call sites to the same method are aggregated into one row for the target and mod, while the details window lists the associated source transpilers.");
        Label("Transpiler call rows are timed directly around the detected net-new call/callvirt instructions after all source transpilers have run.");
        Label("The timing remains inclusive of work executed inside the called method, but unrelated nested calls to the same method are no longer counted.");
        Label("The main table shows only entries that received samples. A detected target or call remains absent until that code path executes after profiling starts.");
        GUILayout.Space(6f);

        GroupLabel("Over 1 sec");
        Label("This view uses a rolling 1 second window.");
        Label("\"ms per frame\" is the average amount of frame time spent inside measured patch methods.");
        Label("\"calls per frame\" is the average number of calls per frame during the same window.");
        Label("The Total row is the sum for all currently measured patch methods.");
        Label("Click a table column header to sort. Text columns use ascending order; numeric columns use descending order. The active header shows ▲ or ▼.");
        GUILayout.Space(6f);

        GroupLabel("Max over 60 sec");
        Label("This view uses a rolling 60 second window.");
        Label("\"raw max\" is the single slowest measured call in the window. A trailing ! means it looks like an isolated spike or a GC-contaminated spike.");
        Label("\"2nd max\" and \"3rd max\" are the second and third slowest measured calls. They help confirm whether a spike repeated.");
        Label("If raw max is high but 2nd/3rd max are low, the row is likely affected by a one-off stall, GC, OS scheduling, Unity pause, or measurement contamination.");
        GUILayout.Space(6f);

        GroupLabel("Percentiles");
        Label("p99 ms means that 99% of calls completed faster than the shown time.");
        Label("For example, p99 ms = 0.010 means almost all calls completed faster than 0.010 ms.");
        Label("p95 ms means that 95% of calls completed faster than the shown time.");
        Label("\"> p99\" and \"> p95\" show how many samples were slower than the corresponding percentile threshold.");
        Label("\"avg >p99\" and \"avg >p95\" show the average duration of those slower samples.");
        GUILayout.Space(6f);

        GroupLabel("GC samples");
        Label("\"gc samples\" counts slow measured calls where a GC collection counter changed during the current frame before the sample ended.");
        Label("Only samples >= 1 ms are checked to keep profiler overhead low.");
        Label("Such samples are not automatically fake: the profiled code may allocate enough to trigger GC.");
        Label("But if a row has high raw max and GC samples, interpret the raw max carefully.");
        GUILayout.Space(6f);

        GroupLabel("Frame budget");
        Label("A 60 FPS target gives about 16.6 ms for the whole frame.");
        Label("A 30 FPS target gives about 33.3 ms.");
        Label("Patch time is only one part of the frame, so small values can still matter if they happen very often.");
        GUILayout.Space(6f);

        GroupLabel("Notes");
        Label("The profiler itself adds overhead. Use it for comparison and diagnosis, not as an exact production benchmark.");
        Label("Realtime windows continue to move and new measured calls are recorded while profiling is active.");
        Label("Pause profiling if you want to inspect the current data without changing it.");

        GUILayout.EndScrollView();
    }
}