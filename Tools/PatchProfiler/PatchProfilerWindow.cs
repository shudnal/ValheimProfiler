#nullable disable

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ValheimProfiler.Tools.PatchProfiler;

internal sealed partial class PatchProfilerTool
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

            MainTab newMainTab = (MainTab)GUILayout.Toolbar(
                (int)_mainTab,
                new[] { "Profiler", "Mods to profile", "Help" },
                GUILayout.Width(390f));

            if (newMainTab != _mainTab)
            {
                _mainTab = newMainTab;

                if (_mainTab == MainTab.ModsToProfile && !_listReady)
                    RefreshPatchList();

                MarkViewDirty();
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

                bool newGroupByMod = ProfilerGui.ToggleLayout(
                    _theme,
                    _groupByMod,
                    new GUIContent(
                        "Group by Mod",
                        "Group measured entries by their BepInEx mod.\nTranspiled target entries are grouped under Transpiled methods."),
                    135f,
                    _labelStyle);

                if (newGroupByMod != _groupByMod)
                {
                    _groupByMod = newGroupByMod;
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
                case MainTab.ModsToProfile:
                    DrawModsTab();
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

    private void ToggleTranspilerDetails(PatchContext ctx)
    {
        if (ctx == null)
            return;

        bool sameContext = ReferenceEquals(_selectedTranspilerDetailsContext, ctx);
        if (sameContext && _detailsWindow.RequestedVisible)
        {
            _detailsWindow.RequestedVisible = false;
            return;
        }

        if (!sameContext)
            _transpilerDetailsScroll = Vector2.zero;

        _selectedTranspilerDetailsContext = ctx;
        _selectedTranspilerDetailsTitle = ctx.TargetDisplay ?? GetMethodDisplay(ctx.InstrumentedMethod);
        _detailsWindow.RequestedVisible = true;
        _app.ShowUi();
        _windows.BringToFront(_detailsWindow);
    }

    private void DrawTranspilerDetailsWindow(int id)
    {
        EnsureStyles();

        Color oldContentColor = GUI.contentColor;
        GUI.contentColor = _theme.TextColor;

        try
        {
            GUILayout.BeginVertical();

            GUILayout.BeginHorizontal();
            HeaderLabel(_selectedTranspilerDetailsTitle, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("Close", GUILayout.Width(70f)))
                _detailsWindow.RequestedVisible = false;
            GUILayout.EndHorizontal();

            GUILayout.Space(4f);

            PatchContext ctx = _selectedTranspilerDetailsContext;
            if (ctx == null)
            {
                Label("No transpiler details are available for this row.");
                GUILayout.EndVertical();
                return;
            }

            int frame = DisplayFrame;
            float now = DisplayRealtime;
            var calls = new List<(PatchContext Context, PatchStatSnapshot Snapshot)>();

            lock (_lock)
            {
                foreach (PatchContext candidate in _context.Values)
                {
                    if (candidate == null || !candidate.IsTranspilerCallEntry ||
                        !string.Equals(
                            GetMethodIdentity(candidate.TranspiledTargetMethod),
                            GetMethodIdentity(ctx.InstrumentedMethod),
                            System.StringComparison.Ordinal))
                        continue;

                    if (_stats.TryGetValue(candidate.EntryId, out PatchStat stat))
                        calls.Add((candidate, stat.GetSnapshot(frame, now)));
                }
            }

            int sampledCalls = calls.Count(item =>
                item.Snapshot.Avg1sFrames > 0 || item.Snapshot.MaxSnapshot.WindowSampleCount > 0);
            Label($"Transpilers: {ctx.TranspilerDetails.Count} | Detected runtime calls: {calls.Count} | Sampled: {sampledCalls}");
            Label("Detection is static. A call with 0 samples has not been observed since profiling started; only sampled entries appear in the main table.");
            GUILayout.Space(3f);
            _transpilerDetailsScroll = GUILayout.BeginScrollView(_transpilerDetailsScroll);

            HeaderLabel("Transpilers");
            if (ctx.TranspilerDetails.Count == 0)
            {
                Label("No transpiler registrations were retained for this target.");
            }
            else
            {
                foreach (TranspilerDetail detail in ctx.TranspilerDetails)
                {
                    GUILayout.BeginVertical(GUI.skin.box);
                    HeaderLabel(detail.ModName ?? "Unknown");
                    Label("GUID: " + (detail.ModGuid ?? "(unknown guid)"));
                    Label("Owner: " + (detail.OwnerHarmonyId ?? "(no owner)"));
                    Label("Transpiler: " + (detail.TranspilerMethodDisplay ?? "(unknown transpiler)"));
                    GUILayout.EndVertical();
                    GUILayout.Space(2f);
                }
            }

            GUILayout.Space(5f);
            HeaderLabel("Detected calls injected by transpilers");
            Label("These rows are net new direct call/callvirt instructions found by comparing the final transpiled target IL with its original IL.");
            Label("Timing is injected around the exact detected direct call sites in this transpiled target and updates live on the main thread.");
            GUILayout.Space(2f);

            if (calls.Count == 0)
            {
                Label("No profileable net-new direct calls were detected in the final transpiled target IL.");
            }
            else
            {
                foreach (var call in calls
                    .OrderByDescending(item => item.Snapshot.MaxSnapshot.RawMaxMs)
                    .ThenBy(item => GetMethodDisplay(item.Context.InstrumentedMethod), System.StringComparer.OrdinalIgnoreCase))
                {
                    PatchContext callContext = call.Context;
                    PatchStatSnapshot snapshot = call.Snapshot;
                    MaxAnalyticsSnapshot max = snapshot.MaxSnapshot;

                    GUILayout.BeginVertical(GUI.skin.box);
                    HeaderLabel(GetMethodDisplay(callContext.InstrumentedMethod));
                    Label("Mod: " + (callContext.ModName ?? "Unknown") + " | GUID: " + (callContext.ModGuid ?? "(unknown guid)"));
                    Label("Transpiled target: " + GetMethodDisplay(callContext.TranspiledTargetMethod));
                    Label("Net added call sites: " + System.Math.Max(1, callContext.InjectedCallSiteCount));

                    if (callContext.TranspilerDetails.Count == 0)
                    {
                        Label("Source transpilers: attribution unavailable");
                    }
                    else
                    {
                        Label("Source transpilers:");
                        foreach (TranspilerDetail source in callContext.TranspilerDetails)
                        {
                            Label("  " +
                                  (source.ModName ?? "Unknown") + " | " +
                                  (source.TranspilerMethodDisplay ?? "(unknown transpiler)"));
                        }
                    }

                    if (snapshot.Avg1sFrames == 0 && max.WindowSampleCount == 0)
                        Label("Status: no runtime samples yet. The target or this branch has not executed since profiling started.");

                    Label($"Over 1 sec: {FormatMs(snapshot.Avg1sMsPerFrame)} ms/frame | {FormatCount(snapshot.Avg1sCallsPerFrame)} calls/frame");
                    Label($"Max over 60 sec: raw {FormatMs(max.RawMaxMs)} | 2nd {FormatMs(max.SecondMaxMs)} | 3rd {FormatMs(max.ThirdMaxMs)} | " +
                          $">p99 {max.AboveP99Count} avg {FormatMs(max.AvgAboveP99Ms)} | p99 {FormatMs(max.P99Ms)} | " +
                          $">p95 {max.AboveP95Count} avg {FormatMs(max.AvgAboveP95Ms)} | p95 {FormatMs(max.P95Ms)} | samples {max.WindowSampleCount}");
                    GUILayout.EndVertical();
                    GUILayout.Space(2f);
                }
            }

            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }
        finally
        {
            GUI.contentColor = oldContentColor;
        }
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

        _groupLabelStyle = new GUIStyle(GUI.skin.label)
        {
            wordWrap = false,
            clipping = TextClipping.Clip,
            alignment = TextAnchor.MiddleLeft,
            fontStyle = FontStyle.Bold
        };
        _groupLabelStyle.normal.textColor = _theme.HeaderTextColor;

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

    private void GroupLabel(string text, params GUILayoutOption[] options) =>
        GUILayout.Label(text ?? string.Empty, _groupLabelStyle, options);

    private void DrawToolbar()
    {
        GUILayout.BeginHorizontal();

        string listState = _listReady ? $"List: ready ({_stats.Count})" : "List: empty";
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
            lock (_lock)
            {
                foreach (PatchStat stat in _stats.Values)
                    stat.Reset();
            }

            ClearAnalyticsQueues();
            _statsFrozen = false;
            MarkViewDirty();
        }

        if (GUILayout.Button("Close", GUILayout.Width(70f)))
            IsWindowVisible = false;

        GUILayout.EndHorizontal();
    }

}