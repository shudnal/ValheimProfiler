#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ValheimProfiler.Tools.MonoBehaviourCallProfiler;

internal sealed partial class MonoBehaviourCallProfilerTool
{
    private void DrawSelectionTab()
    {
        if (!_listReady)
        {
            GUILayout.BeginHorizontal();
            Label("MonoBehaviour call list is not loaded.", GUILayout.ExpandWidth(true));

            GUI.enabled = !_profilingActive;
            if (GUILayout.Button("Load list", GUILayout.Width(110f)))
                RefreshBehaviourList();
            GUI.enabled = true;

            GUILayout.EndHorizontal();
            return;
        }

        if (!_selectionExpansionInitialized)
        {
            ExpandPathsToSelected(collapseUnselected: true);
            _selectionExpansionInitialized = true;
        }

        DrawSelectionBulkActions();
        GUILayout.Space(3f);
        DrawSelectionFilterRow();
        GUILayout.Space(2f);
        DrawSelectionSearchRow();
        GUILayout.Space(2f);

        RebuildSelectionRowsIfNeeded();

        float rowHeight = CurrentRowHeight;
        float contentHeight = Mathf.Max(1f, _selectionRows.Count * rowHeight);

        _selectionScroll = GUILayout.BeginScrollView(_selectionScroll);

        Rect contentRect = GUILayoutUtility.GetRect(
            Mathf.Max(1f, MainWindowWidth - 30f),
            contentHeight,
            GUILayout.ExpandWidth(true),
            GUILayout.Height(contentHeight));

        DrawSelectionRows(contentRect, rowHeight);
        GUILayout.EndScrollView();
    }

    private void DrawSelectionBulkActions()
    {
        GUILayout.BeginHorizontal();

        GUI.enabled = !_profilingActive;
        if (GUILayout.Button(new GUIContent(
                "Refresh list",
                "Rescan loaded assemblies and rebuild lifecycle and declared method discovery.\nUnsaved selection changes are discarded."),
            GUILayout.Width(100f)))
        {
            RefreshBehaviourList();
        }
        GUI.enabled = true;

        if (GUILayout.Button(new GUIContent(
                "Enable all",
                "Enable every method visible through the current Source, Show declared methods, Present in active scene and Search filters.\nMethods hidden by the current filters are not changed."),
            GUILayout.Width(88f)))
        {
            EnableFilteredSelection();
        }

        if (GUILayout.Button(new GUIContent(
                "Enable lifecycle",
                "Enable default synchronous lifecycle callbacks added by mods: Awake, Start, OnEnable, OnDisable and OnDestroy.\nExisting manually enabled methods remain selected."),
            GUILayout.Width(120f)))
        {
            lock (_lock)
            {
                foreach (BehaviourMethodEntry entry in _types.SelectMany(type => type.Methods))
                {
                    if (entry.DefaultSelected)
                        entry.Selected = true;
                }
            }

            _selectionDirty = true;
            ExpandPathsToSelected(collapseUnselected: false);
            MarkSelectionRowsDirty();
        }

        if (GUILayout.Button(new GUIContent(
                "Disable all",
                "Clear every selected call and collapse the selection tree."),
            GUILayout.Width(88f)))
        {
            lock (_lock)
            {
                foreach (BehaviourMethodEntry entry in _types.SelectMany(type => type.Methods))
                    entry.Selected = false;
            }

            _selectionDirty = true;
            CollapseSelectionTree();
            MarkSelectionRowsDirty();
        }

        if (GUILayout.Button(new GUIContent(
                "Reset to defaults",
                "Restore the default selection: synchronous mod lifecycle callbacks enabled; declared, Valheim and Other methods disabled."),
            GUILayout.Width(120f)))
        {
            lock (_lock)
            {
                foreach (BehaviourMethodEntry entry in _types.SelectMany(type => type.Methods))
                    entry.Selected = entry.DefaultSelected;
            }

            _selectionDirty = true;
            ExpandPathsToSelected(collapseUnselected: true);
            MarkSelectionRowsDirty();
        }

        GUILayout.Space(8f);

        int selected = GetSelectedMethodCount();
        GUILayout.Box($"Selected: {selected}", GUILayout.Width(105f));

        GUILayout.Space(16f);

        GUI.enabled = _selectionDirty;
        GUIStyle buttonStyle = _selectionDirty ? _theme.AccentButtonStyle : GUI.skin.button;
        if (GUILayout.Button(new GUIContent(
                "Apply selection",
                "Apply the current call selection.\nIf profiling is active, instrumentation is restarted and lifetime statistics are reset."),
            buttonStyle,
            GUILayout.Width(132f)))
        {
            ApplySelection();
        }
        GUI.enabled = true;

        if (_selectionDirty)
        {
            GUILayout.Space(6f);
            GUILayout.Label("Selection has changed", _theme.AccentLabelStyle, GUILayout.ExpandWidth(true));
        }
        else
        {
            GUILayout.FlexibleSpace();
        }

        GUILayout.EndHorizontal();
    }

    private void DrawSelectionFilterRow()
    {
        GUILayout.BeginHorizontal();

        GUILayout.Label(
            new GUIContent(
                "Source:",
                "Choose one source category to display. These tabs only filter the view and do not change saved selection."),
            _labelStyle,
            GUILayout.Width(52f));

        BehaviourSource newSource = (BehaviourSource)GUILayout.Toolbar(
            (int)_selectionSource,
            new[]
            {
                new GUIContent("Mods", "Show MonoBehaviour calls discovered in BepInEx plugin assemblies."),
                new GUIContent("Valheim", "Show MonoBehaviour calls declared by Valheim game assemblies."),
                new GUIContent("Other", "Show calls from managed assemblies that are neither Valheim nor identifiable BepInEx plugins.")
            },
            GUILayout.Width(245f));

        if (newSource != _selectionSource)
        {
            _selectionSource = newSource;
            _selectionScroll = Vector2.zero;
            ExpandPathsToSelected(collapseUnselected: false);
            MarkSelectionRowsDirty();
        }

        GUILayout.Space(8f);

        bool showDeclared = ProfilerGui.ToggleLayout(
            _theme,
            _showDeclaredMethods,
            new GUIContent(
                "Show declared methods",
                "Show managed instance methods declared directly by each MonoBehaviour type in addition to lifecycle callbacks.\nSelected declared methods remain visible when this filter is disabled.\nFrame callbacks Update, FixedUpdate, LateUpdate and OnGUI are intentionally excluded."),
            185f,
            _labelStyle,
            0f);

        if (showDeclared != _showDeclaredMethods)
        {
            _showDeclaredMethods = showDeclared;
            _selectionScroll = Vector2.zero;
            MarkSelectionRowsDirty();
        }

        GUILayout.Space(8f);

        bool present = ProfilerGui.ToggleLayout(
            _theme,
            _presentInActiveScene,
            new GUIContent(
                "Present in active scene",
                "Show only MonoBehaviour types that currently have at least one loaded instance in the active Unity scene.\nInactive GameObjects and disabled components are still considered present.\nThis is a view filter only and does not remove saved selections."),
            190f,
            _labelStyle,
            0f);

        if (present != _presentInActiveScene)
        {
            _presentInActiveScene = present;
            if (_presentInActiveScene)
                RefreshActiveScenePresence();

            _selectionScroll = Vector2.zero;
            MarkSelectionRowsDirty();
        }

        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
    }

    private void DrawSelectionSearchRow()
    {
        GUILayout.BeginHorizontal();

        if (GUILayout.Button("Expand all", GUILayout.Width(78f)))
            SetVisibleSelectionExpansion(true);

        if (GUILayout.Button("Collapse all", GUILayout.Width(78f)))
            SetVisibleSelectionExpansion(false);

        GUILayout.Space(8f);
        GUILayout.Label("Search:", _labelStyle, GUILayout.Width(48f));

        string search = GUILayout.TextField(
            _search ?? string.Empty,
            GUILayout.Width(245f),
            GUILayout.Height(Mathf.Max(18f, CurrentRowHeight - 2f)));

        if (!string.Equals(search, _search, StringComparison.Ordinal))
        {
            _search = search ?? string.Empty;
            _selectionScroll = Vector2.zero;
            MarkSelectionRowsDirty();
        }

        if (GUILayout.Button("Clear", GUILayout.Width(55f)))
        {
            _search = string.Empty;
            _selectionScroll = Vector2.zero;
            GUI.FocusControl(null);
            MarkSelectionRowsDirty();
        }

        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
    }

    private void EnableFilteredSelection()
    {
        string search = (_search ?? string.Empty).Trim();
        bool hasSearch = search.Length > 0;
        bool changed = false;

        lock (_lock)
        {
            foreach (BehaviourTypeEntry type in _types)
            {
                if (!TypePassesSourceFilter(type) ||
                    !IsPresentInActiveScene(type) ||
                    !TypeMatchesSearch(type, search, hasSearch))
                    continue;

                bool typeIdentityMatches = hasSearch &&
                    (ContainsIgnoreCase(type.GroupName, search) ||
                     ContainsIgnoreCase(type.AssemblyName, search) ||
                     ContainsIgnoreCase(type.TypeName, search));

                foreach (BehaviourMethodEntry method in GetVisibleMethods(
                    type,
                    search,
                    hasSearch && !typeIdentityMatches))
                {
                    if (method.Selected)
                        continue;

                    method.Selected = true;
                    changed = true;
                }
            }
        }

        if (!changed)
            return;

        _selectionDirty = true;
        ExpandPathsToSelected(collapseUnselected: false);
        MarkSelectionRowsDirty();
    }

    private int GetSelectedMethodCount()
    {
        lock (_lock)
            return _types.Sum(type => type.Methods.Count(method => method.Selected));
    }

    private void ExpandPathsToSelected(bool collapseUnselected)
    {
        lock (_lock)
        {
            if (collapseUnselected)
                _selectionGroupExpanded.Clear();

            foreach (BehaviourTypeEntry type in _types)
            {
                bool hasSelected = type.Methods.Any(method => method.Selected);
                if (collapseUnselected || hasSelected)
                    type.Expanded = hasSelected;

                if (hasSelected)
                    _selectionGroupExpanded[type.GroupId] = true;
                else if (collapseUnselected && !_selectionGroupExpanded.ContainsKey(type.GroupId))
                    _selectionGroupExpanded[type.GroupId] = false;
            }
        }
    }

    private void CollapseSelectionTree()
    {
        lock (_lock)
        {
            foreach (BehaviourTypeEntry type in _types)
                type.Expanded = false;

            foreach (string groupId in _types.Select(type => type.GroupId).Distinct().ToList())
                _selectionGroupExpanded[groupId] = false;
        }
    }

    private void SetVisibleSelectionExpansion(bool expanded)
    {
        string search = (_search ?? string.Empty).Trim();
        bool hasSearch = search.Length > 0;

        lock (_lock)
        {
            foreach (BehaviourTypeEntry type in _types)
            {
                if (!TypePassesSourceFilter(type) || !IsPresentInActiveScene(type) || !TypeMatchesSearch(type, search, hasSearch))
                    continue;

                type.Expanded = expanded;
                _selectionGroupExpanded[type.GroupId] = expanded;
            }
        }

        MarkSelectionRowsDirty();
    }

    private void RebuildSelectionRowsIfNeeded()
    {
        if (!_selectionRowsDirty && string.Equals(_lastSelectionSearch, _search, StringComparison.Ordinal))
            return;

        _selectionRowsDirty = false;
        _lastSelectionSearch = _search ?? string.Empty;
        _selectionRows.Clear();

        string search = (_search ?? string.Empty).Trim();
        bool hasSearch = search.Length > 0;

        List<BehaviourTypeEntry> visibleTypes;
        lock (_lock)
        {
            visibleTypes = _types
                .Where(TypePassesSourceFilter)
                .Where(IsPresentInActiveScene)
                .Where(type => TypeMatchesSearch(type, search, hasSearch))
                .ToList();
        }

        foreach (IGrouping<string, BehaviourTypeEntry> group in visibleTypes
            .GroupBy(type => type.GroupId)
            .Where(group => group.Any())
            .OrderBy(group => group.First().GroupName, StringComparer.OrdinalIgnoreCase))
        {
            BehaviourTypeEntry first = group.First();

            if (!_selectionGroupExpanded.TryGetValue(group.Key, out bool groupExpanded))
            {
                groupExpanded = group.Any(type => type.Methods.Any(method => method.Selected));
                _selectionGroupExpanded[group.Key] = groupExpanded;
            }

            _selectionRows.Add(new SelectionRow
            {
                Kind = SelectionRowKind.Group,
                GroupId = group.Key,
                Text = first.GroupName ?? first.AssemblyName ?? "Unknown",
                Indent = 0
            });

            if (!groupExpanded)
                continue;

            foreach (BehaviourTypeEntry type in group.OrderBy(item => item.TypeName, StringComparer.OrdinalIgnoreCase))
            {
                bool typeIdentityMatches = hasSearch &&
                    (ContainsIgnoreCase(type.GroupName, search) ||
                     ContainsIgnoreCase(type.AssemblyName, search) ||
                     ContainsIgnoreCase(type.TypeName, search));

                List<BehaviourMethodEntry> visibleMethods = GetVisibleMethods(
                    type,
                    search,
                    hasSearch && !typeIdentityMatches);

                if (visibleMethods.Count == 0)
                    continue;

                _selectionRows.Add(new SelectionRow
                {
                    Kind = SelectionRowKind.Type,
                    GroupId = group.Key,
                    Text = type.TypeName,
                    TypeEntry = type,
                    Indent = 1
                });

                if (!type.Expanded && !hasSearch)
                    continue;

                foreach (BehaviourMethodEntry method in visibleMethods)
                {
                    _selectionRows.Add(new SelectionRow
                    {
                        Kind = SelectionRowKind.Method,
                        GroupId = group.Key,
                        Text = method.DisplayName,
                        Tooltip = method.Tooltip,
                        TypeEntry = type,
                        MethodEntry = method,
                        Indent = 2
                    });
                }
            }
        }
    }

    private bool TypePassesSourceFilter(BehaviourTypeEntry type) =>
        type != null && type.Source == _selectionSource;

    private bool TypeMatchesSearch(BehaviourTypeEntry type, string search, bool hasSearch)
    {
        if (!hasSearch)
            return GetVisibleMethods(type, search, false).Count > 0;

        if (ContainsIgnoreCase(type.GroupName, search) ||
            ContainsIgnoreCase(type.AssemblyName, search) ||
            ContainsIgnoreCase(type.TypeName, search))
            return GetVisibleMethods(type, search, false).Count > 0;

        return GetVisibleMethods(type, search, true).Count > 0;
    }

    private List<BehaviourMethodEntry> GetVisibleMethods(
        BehaviourTypeEntry type,
        string search,
        bool filterBySearch)
    {
        IEnumerable<BehaviourMethodEntry> methods = type.Methods;

        methods = methods.Where(method =>
            method.Kind == ProfileMethodKind.Lifecycle ||
            _showDeclaredMethods ||
            method.Selected);

        if (filterBySearch)
        {
            methods = methods.Where(method =>
                ContainsIgnoreCase(method.DisplayName, search) ||
                ContainsIgnoreCase(method.Tooltip, search) ||
                ContainsIgnoreCase(method.Kind.ToString(), search));
        }

        return methods
            .OrderBy(method => method.Kind)
            .ThenBy(method => method.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(method => method.Key, StringComparer.Ordinal)
            .ToList();
    }

    private void DrawSelectionRows(Rect contentRect, float rowHeight)
    {
        float visibleTop = _selectionScroll.y - VirtualizationOverscan;
        float visibleBottom = _selectionScroll.y + Mathf.Max(100f, MainWindowHeight - 255f) + VirtualizationOverscan;

        for (int i = 0; i < _selectionRows.Count; i++)
        {
            float y = i * rowHeight;
            if (!RowVisible(y, rowHeight, visibleTop, visibleBottom))
                continue;

            SelectionRow row = _selectionRows[i];
            float rowY = contentRect.y + y;
            float x = contentRect.x + row.Indent * 17f;

            switch (row.Kind)
            {
                case SelectionRowKind.Group:
                {
                    bool expanded = _selectionGroupExpanded.TryGetValue(row.GroupId, out bool value) && value;
                    Rect buttonRect = InsetButtonRect(new Rect(x, rowY, GroupToggleWidth, rowHeight));

                    bool toggleRequested = GUI.Button(buttonRect, expanded ? "▼" : "▶", _compactButtonStyle);

                    x += GroupToggleWidth;
                    toggleRequested |= GUI.Button(
                        new Rect(x, rowY, contentRect.width - x + contentRect.x, rowHeight),
                        row.Text,
                        _groupLabelStyle);

                    if (toggleRequested)
                    {
                        _selectionGroupExpanded[row.GroupId] = !expanded;
                        MarkSelectionRowsDirty();
                    }
                    break;
                }

                case SelectionRowKind.Type:
                {
                    BehaviourTypeEntry type = row.TypeEntry;
                    bool expanded = type != null && type.Expanded;
                    Rect buttonRect = InsetButtonRect(new Rect(x, rowY, GroupToggleWidth, rowHeight));

                    bool toggleRequested = GUI.Button(buttonRect, expanded ? "▼" : "▶", _compactButtonStyle);
                    x += GroupToggleWidth;

                    List<BehaviourMethodEntry> visibleMethods = type == null
                        ? new List<BehaviourMethodEntry>()
                        : GetVisibleMethods(type, string.Empty, false);
                    int selected = visibleMethods.Count(method => method.Selected);
                    string text = $"{row.Text}  ({selected}/{visibleMethods.Count})";

                    toggleRequested |= GUI.Button(
                        new Rect(x, rowY, contentRect.width - x + contentRect.x, rowHeight),
                        text,
                        _headerLabelStyle);

                    if (toggleRequested)
                    {
                        if (type != null)
                            type.Expanded = !expanded;

                        MarkSelectionRowsDirty();
                    }
                    break;
                }

                case SelectionRowKind.Method:
                {
                    BehaviourMethodEntry method = row.MethodEntry;
                    if (method == null)
                        break;

                    Rect toggleRect = new Rect(x, rowY, contentRect.width - (x - contentRect.x), rowHeight);
                    string display = method.IsAsyncOrIterator ? method.DisplayName + "  !" : method.DisplayName;
                    bool selected = ProfilerGui.Toggle(
                        _theme,
                        toggleRect,
                        method.Selected,
                        new GUIContent(display, row.Tooltip ?? string.Empty),
                        _labelStyle,
                        0f);

                    if (selected != method.Selected)
                    {
                        method.Selected = selected;
                        _selectionDirty = true;
                        MarkSelectionRowsDirty();
                    }
                    break;
                }
            }
        }
    }

    private static bool ContainsIgnoreCase(string value, string search) =>
        !string.IsNullOrEmpty(value) &&
        value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
}
