#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace ValheimProfiler.Tools.GeneralMethodProfiler;

internal sealed partial class GeneralMethodProfilerTool
{
    private GeneralMethodEntry _treeSelectedMethod;
    private CallNode _treeSelectedNode;
    private TreeNodeView _treeRootView;
    private TreeNodeView _treeCurrentView;
    private List<TreeNodeView> _treeContexts = new List<TreeNodeView>();
    private List<TreeNodeView> _treeChildren = new List<TreeNodeView>();
    private Vector2 _treeScroll;
    private TreeSortColumn _treeSortColumn = TreeSortColumn.Inclusive;
    private bool _treeViewDirty = true;
    private float _nextTreeRefresh;
    private float TreeNameWidth = 440f;
    private float TreeNumberWidth = 102f;
    private float TreeOpenWidth = 72f;
    private const int TreeNumberColumns = 14;
    private float TreeContentWidth => TreeNameWidth + TreeNumberColumns * TreeNumberWidth + TreeOpenWidth;
    private GUISkin _treeLayoutSkin;

    private void ShowInCallTree(GeneralMethodEntry entry)
    {
        _treeSelectedMethod = entry;
        _treeSelectedNode = entry.Contexts.Count == 1 ? entry.Contexts[0] : null;
        _treeScroll = Vector2.zero;
        _treeViewDirty = true;
        _callTreeWindow.RequestedVisible = true;
        _app.ShowUi();
        _windows.BringToFront(_callTreeWindow);
    }

    private void SelectTreeNode(CallNode node)
    {
        _treeSelectedNode = node;
        _treeScroll = Vector2.zero;
        _treeViewDirty = true;
    }

    private void DrawCallTreeWindow(int id)
    {
        EnsureStyles();
        if (_treeLayoutSkin != GUI.skin)
        {
            _treeLayoutSkin = GUI.skin;
            TreeNumberWidth = CalculateHeaderAwareWidth(102f, "samples 60s", "gc samples", "9999999.999");
            TreeOpenWidth = CalculateHeaderAwareWidth(72f, "Navigate", "Open");
            TreeNameWidth = CalculateHeaderAwareWidth(440f, "Context / method");
        }
        RefreshTreeView();
        GUILayout.BeginHorizontal();
        HeaderLabel("Observed main-thread call contexts", GUILayout.ExpandWidth(true));
        if (GUILayout.Button("Close", GUILayout.Width(70f)))
            _callTreeWindow.RequestedVisible = false;
        GUILayout.EndHorizontal();
        DrawCaptureDiagnostics();

        if (_treeSelectedMethod == null)
        {
            Label("Open a method using Call tree in the profiler table.");
            return;
        }

        GUILayout.BeginHorizontal();
        bool enabled = GUI.enabled;
        GUI.enabled = enabled && _treeSelectedNode != null;
        if (GUILayout.Button(new GUIContent("Choose context", "Return to all recorded contexts of the method originally opened from the table."), GUILayout.Width(125f)))
            SelectTreeNode(null);
        GUI.enabled = enabled && _treeSelectedNode?.Parent != null;
        if (GUILayout.Button("Parent", GUILayout.Width(80f)))
            SelectTreeNode(_treeSelectedNode.Parent);
        if (GUILayout.Button("Root", GUILayout.Width(80f)))
            SelectTreeNode(_treeSelectedNode.Root);
        GUI.enabled = enabled && _treeSelectedNode != null;
        if (GUILayout.Button(new GUIContent("Contexts of current method", "Inspect every recorded calling context of the current method, including other roots and recursion levels."), GUILayout.Width(195f)))
        {
            _treeSelectedMethod = _treeSelectedNode.Entry;
            SelectTreeNode(null);
        }
        GUI.enabled = enabled;
        GUILayout.FlexibleSpace();
        Label(_statsFrozen ? "PAUSED - data frozen" : _profilingActive ? "LIVE" : "OFF");
        GUILayout.EndHorizontal();
        // Navigation happens on input events; draw one internally consistent cached node set.
        RefreshTreeView();

        List<TreeNodeView> rows;
        if (_treeSelectedNode == null)
        {
            GUILayout.Label(new GUIContent("Choose a context: " + _treeSelectedMethod.DisplayName, _treeSelectedMethod.Tooltip), _headerLabelStyle);
            GUILayout.Label("Each row is a distinct full runtime path. Session totals persist until reset; rolling columns cover 60 seconds.", _helpLabelStyle);
            rows = _treeContexts;
            if (rows.Count == 0)
            {
                GUILayout.Label("No call contexts were retained for this method. Trigger it after starting capture, or check the node/depth limits.", _helpLabelStyle);
                return;
            }
        }
        else
        {
            DrawNodeSummary("Root", _treeRootView);
            if (ReferenceEquals(_treeSelectedNode, _treeSelectedNode.Root))
                Label("Current is the root node shown above.");
            else
                DrawNodeSummary("Current", _treeCurrentView);
            HeaderLabel("Direct children - select Open to navigate down");
            rows = _treeChildren;
            if (rows.Count == 0)
            {
                GUILayout.Label("No recorded children. Unselected, unavailable or capped descendants are not represented; this does not prove the method calls nothing else.", _helpLabelStyle);
                return;
            }
        }

        DrawTreeHeader();
        _treeScroll = GUILayout.BeginScrollView(_treeScroll, false, true);
        float rowHeight = CurrentRowHeight;
        float contentHeight = Mathf.Max(rowHeight, rows.Count * rowHeight);
        Rect content = GUILayoutUtility.GetRect(TreeContentWidth, contentHeight,
            GUILayout.Width(TreeContentWidth), GUILayout.Height(contentHeight));
        int first = Mathf.Max(0, Mathf.FloorToInt(_treeScroll.y / rowHeight) - 3);
        int last = Mathf.Min(rows.Count, first + Mathf.CeilToInt(_callTreeWindow.Rect.height / rowHeight) + 6);
        for (int index = first; index < last; index++)
            DrawTreeRow(rows[index], new Rect(content.x, content.y + index * rowHeight, content.width, rowHeight));
        GUILayout.EndScrollView();
    }

    private void DrawCaptureDiagnostics()
    {
        string text = $"Nodes: {_treeNodeCount}/{_maxTreeNodes} | Paths omitted: {_treeLimitHits} | Depth omissions: {_depthLimitHits} | " +
                      $"Stack mismatches: {_stackMismatches} | Hook errors: {_hookFailures} | GC discarded: {_ignoredGcSamples}";
        GUILayout.Label(new GUIContent(text,
            "Path omissions: new paths not retained at the node cap; their method aggregates remain available.\n" +
            "Depth omissions: deeper invocations excluded from both aggregates and topology.\n" +
            "Stack mismatches or hook errors invalidate the active stack rather than inventing parent/child relations.\n" +
            "GC discarded is capture diagnostics only, not a measured sample count.\n" + text), _labelStyle);
    }

    private void DrawNodeSummary(string title, TreeNodeView view)
    {
        if (view == null)
            return;
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label(new GUIContent(title + ": " + view.Path, BuildNodePath(view.Node, full: true)), _headerLabelStyle);
        double average = view.Calls > 0 ? view.InclusiveMs / view.Calls : 0;
        double self = view.Calls > 0 ? view.SelfMs / view.Calls : 0;
        string session = $"Session - calls: {FormatSessionCount(view.Calls)} | inclusive total: {FormatSessionMs(view.InclusiveMs)} ms | " +
                         $"avg inclusive: {FormatMs(average)} ms | avg self: {FormatMs(self)} ms | max self: {FormatMs(view.MaxSelfMs)} ms";
        GUILayout.Label(new GUIContent(session, session + "\nSelf excludes measured nested intervals and the profiler bookkeeping around those measured children; unmeasured nested work remains included."), _labelStyle);
        RollingMaxSnapshot max = view.Snapshot.MaxSnapshot;
        string rolling = $"60 sec - samples: {FormatCount(max.WindowSampleCount)} | raw / 2nd / 3rd max: " +
                         $"{FormatRawMax(max)} / {FormatMs(max.SecondMaxMs)} / {FormatMs(max.ThirdMaxMs)} ms | " +
                         $"p95 / p99: {FormatMs(max.P95Ms)} / {FormatMs(max.P99Ms)} ms | GC: {FormatCount(max.GcSampleCount)}";
        GUILayout.Label(new GUIContent(rolling, rolling), _labelStyle);
        string tails = $"Above p95: {FormatCount(max.AboveP95Count)} (avg {FormatMs(max.AvgAboveP95Ms)} ms) | " +
                       $"above p99: {FormatCount(max.AboveP99Count)} (avg {FormatMs(max.AvgAboveP99Ms)} ms) | " +
                       $"Frame budget @{FrameBudgetTargetFps} FPS: {FormatMs(view.Snapshot.Avg1sMsPerFrame)} ms/frame, {FormatCount(view.Snapshot.Avg1sCallsPerFrame)} calls/frame, {FormatAverageCall(view.Snapshot.Avg1sAverageCallMs)} avg call";
        GUILayout.Label(new GUIContent(tails, tails), _labelStyle);
        GUILayout.EndVertical();
    }

    private void RefreshTreeView()
    {
        float now = DisplayRealtime;
        if (!_treeViewDirty && now < _nextTreeRefresh)
            return;
        _treeViewDirty = false;
        _nextTreeRefresh = now + 0.25f;
        _treeContexts = _treeSelectedMethod == null
            ? new List<TreeNodeView>()
            : SortTreeRows(_treeSelectedMethod.Contexts.Select(CreateTreeView));
        _treeCurrentView = _treeSelectedNode == null ? null : CreateTreeView(_treeSelectedNode);
        _treeRootView = _treeSelectedNode == null ? null : ReferenceEquals(_treeSelectedNode, _treeSelectedNode.Root)
            ? _treeCurrentView : CreateTreeView(_treeSelectedNode.Root);
        _treeChildren = _treeSelectedNode == null
            ? new List<TreeNodeView>()
            : SortTreeRows(_treeSelectedNode.Children.Values.Select(CreateTreeView));
    }

    private TreeNodeView CreateTreeView(CallNode node) => new TreeNodeView
    {
        Node = node,
        Snapshot = node.Stat?.GetFrameBudgetSnapshot(DisplayFrame, DisplayRealtime, FrameBudgetTargetFps) ?? default,
        Calls = node.Calls,
        InclusiveMs = node.InclusiveMs,
        SelfMs = node.SelfMs,
        MaxSelfMs = node.MaxSelfMs,
        LastSelfMs = node.LastSelfMs,
        Path = BuildNodePath(node, full: false)
    };

    private List<TreeNodeView> SortTreeRows(IEnumerable<TreeNodeView> rows)
    {
        IOrderedEnumerable<TreeNodeView> ordered = _treeSortColumn switch
        {
            TreeSortColumn.Self => rows.OrderByDescending(row => row.Calls > 0 ? row.SelfMs / row.Calls : 0),
            TreeSortColumn.Calls => rows.OrderByDescending(row => row.Calls),
            TreeSortColumn.RawMax => rows.OrderByDescending(row => row.Snapshot.MaxSnapshot.RawMaxMs),
            TreeSortColumn.Name => rows.OrderByDescending(row => row.Path, StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderByDescending(row => row.InclusiveMs)
        };
        return ordered.ThenBy(row => row.Node.Id).ToList();
    }

    private static string BuildNodePath(CallNode node, bool full)
    {
        var path = new List<string>();
        CallNode current = node;
        int limit = full ? int.MaxValue : 12;
        while (current != null && path.Count < limit)
        {
            path.Add(current.Entry.TypeEntry.Type.Name + "." + current.Entry.Method.Name);
            current = current.Parent;
        }
        if (current != null)
            path.Add("... " + (current.Depth + 1) + " ancestors ...");
        path.Reverse();
        return "#" + node.Id + " " + node.Root.Entry.TypeEntry.GroupName + " | " + string.Join(" > ", path) +
               (full ? "\n" + node.Entry.Tooltip : string.Empty);
    }

    private void DrawTreeHeader()
    {
        Rect rect = GUILayoutUtility.GetRect(1f, CurrentHeaderHeight, GUILayout.ExpandWidth(true), GUILayout.Height(CurrentHeaderHeight));
        GUI.BeginGroup(rect);
        try
        {
            float x = -_treeScroll.x;
            DrawTreeHeaderCell(ref x, TreeNameWidth, "Context / method", "Full runtime path; hover a row to inspect its complete signature.", TreeSortColumn.Name);
            DrawTreeHeaderCell(ref x, TreeNumberWidth, "calls (all)", "Accepted calls in this context since reset.", TreeSortColumn.Calls);
            DrawTreeHeaderCell(ref x, TreeNumberWidth, "total ms", "Inclusive total in this context since reset.", TreeSortColumn.Inclusive);
            DrawTreeHeaderCell(ref x, TreeNumberWidth, "avg ms", "Mean inclusive invocation time since reset.");
            DrawTreeHeaderCell(ref x, TreeNumberWidth, "avg self", "Mean self time since reset. Measured direct children are subtracted.", TreeSortColumn.Self);
            DrawTreeHeaderCell(ref x, TreeNumberWidth, "max self", "Largest self duration since reset.");
            DrawTreeHeaderCell(ref x, TreeNumberWidth, "last self", "Self duration of the last accepted call.");
            DrawTreeHeaderCell(ref x, TreeNumberWidth, "samples 60s", "Accepted samples in the rolling 60-second window.");
            DrawTreeHeaderCell(ref x, TreeNumberWidth, "raw max", "Slowest inclusive invocation in the rolling 60-second window.", TreeSortColumn.RawMax);
            DrawTreeHeaderCell(ref x, TreeNumberWidth, "2nd max", "Second-slowest inclusive invocation in the rolling window.");
            DrawTreeHeaderCell(ref x, TreeNumberWidth, "3rd max", "Third-slowest inclusive invocation in the rolling window.");
            DrawTreeHeaderCell(ref x, TreeNumberWidth, "p95", "Approximate p95 inclusive duration in the rolling window.");
            DrawTreeHeaderCell(ref x, TreeNumberWidth, "p99", "Approximate p99 inclusive duration in the rolling window.");
            DrawTreeHeaderCell(ref x, TreeNumberWidth, "gc samples", "GC-associated samples in the rolling window; zero when GC samples are discarded.");
            DrawTreeHeaderCell(ref x, TreeNumberWidth, "children", "Recorded direct child contexts, including zero-sample structural nodes.");
            DrawTreeHeaderCell(ref x, TreeOpenWidth, "Navigate", "Open this context.");
        }
        finally { GUI.EndGroup(); }
    }

    private void DrawTreeHeaderCell(ref float x, float width, string text, string tooltip, TreeSortColumn? sort = null)
    {
        Rect rect = new Rect(x, 0f, width, CurrentHeaderHeight);
        if (sort.HasValue)
        {
            if (GUI.Button(rect, new GUIContent(text, tooltip + "\nClick to sort descending."),
                _treeSortColumn == sort.Value ? _activeHeaderLabelStyle : _headerLabelStyle))
            {
                _treeSortColumn = sort.Value;
                _app.Config.GeneralMethodCallTreeSortColumn.Value = _treeSortColumn.ToString();
                _treeViewDirty = true;
            }
        }
        else
            GUI.Label(rect, new GUIContent(text, tooltip), _headerLabelStyle);
        x += width;
    }

    private static string FormatSessionMs(double value) => Math.Max(0, value).ToString("0.000", CultureInfo.InvariantCulture);
    private static string FormatSessionCount(long value) => Math.Max(0, value).ToString(CultureInfo.InvariantCulture);

    private void DrawTreeRow(TreeNodeView view, Rect rect)
    {
        float x = rect.x;
        string name = _treeSelectedNode == null ? view.Path : GetMethodDisplayName(view.Node.Entry);
        DrawCell(ref x, rect.y, TreeNameWidth, rect.height, name, _labelStyle, BuildNodePath(view.Node, full: true));
        DrawCell(ref x, rect.y, TreeNumberWidth, rect.height, FormatSessionCount(view.Calls), _labelStyle);
        DrawCell(ref x, rect.y, TreeNumberWidth, rect.height, FormatSessionMs(view.InclusiveMs), _labelStyle);
        DrawCell(ref x, rect.y, TreeNumberWidth, rect.height, FormatMs(view.Calls > 0 ? view.InclusiveMs / view.Calls : 0), _labelStyle);
        DrawCell(ref x, rect.y, TreeNumberWidth, rect.height, FormatMs(view.Calls > 0 ? view.SelfMs / view.Calls : 0), _labelStyle);
        DrawCell(ref x, rect.y, TreeNumberWidth, rect.height, FormatMs(view.MaxSelfMs), _labelStyle);
        DrawCell(ref x, rect.y, TreeNumberWidth, rect.height, FormatMs(view.LastSelfMs), _labelStyle);
        RollingMaxSnapshot max = view.Snapshot.MaxSnapshot;
        DrawCell(ref x, rect.y, TreeNumberWidth, rect.height, FormatCount(max.WindowSampleCount), _labelStyle);
        DrawCell(ref x, rect.y, TreeNumberWidth, rect.height, FormatRawMax(max), _labelStyle);
        DrawCell(ref x, rect.y, TreeNumberWidth, rect.height, FormatMs(max.SecondMaxMs), _labelStyle);
        DrawCell(ref x, rect.y, TreeNumberWidth, rect.height, FormatMs(max.ThirdMaxMs), _labelStyle);
        DrawCell(ref x, rect.y, TreeNumberWidth, rect.height, FormatMs(max.P95Ms), _labelStyle);
        DrawCell(ref x, rect.y, TreeNumberWidth, rect.height, FormatMs(max.P99Ms), _labelStyle);
        DrawCell(ref x, rect.y, TreeNumberWidth, rect.height, FormatCount(max.GcSampleCount), _labelStyle);
        DrawCell(ref x, rect.y, TreeNumberWidth, rect.height, FormatCount(view.Node.Children.Count), _labelStyle);
        if (GUI.Button(InsetButtonRect(new Rect(x, rect.y, TreeOpenWidth, rect.height)), "Open", _compactButtonStyle))
            SelectTreeNode(view.Node);
    }
}
