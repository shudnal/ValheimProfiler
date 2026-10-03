#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ValheimProfiler.Tools.GeneralMethodProfiler;

internal sealed partial class GeneralMethodProfilerTool
{
    private int _selectedMethodCount;
    private float _selectionContentWidth = 1000f;
    private bool _filtersDirty;
    private float _saveFiltersAt;

    private void DrawSelectionTab()
    {
        GUILayout.BeginHorizontal();
        bool enabled = GUI.enabled;
        bool mutationEnabled = enabled && !IsTransitioning;
        GUI.enabled = mutationEnabled && !_profilingActive;
        if (GUILayout.Button(new GUIContent(_listReady ? "Refresh list" : "Load list",
                "Inspect only loaded BepInEx plugin assemblies. Current selections are preserved by complete method signature."),
            GUILayout.Width(100f)))
            RefreshMethodList();
        GUI.enabled = mutationEnabled;
        if (GUILayout.Button(new GUIContent("Enable visible", "Select all available methods matching the current filters, including collapsed classes."), GUILayout.Width(110f)))
            SetFilteredSelection(true);
        if (GUILayout.Button(new GUIContent("Disable visible", "Deselect only methods matching the current filters."), GUILayout.Width(110f)))
            SetFilteredSelection(false);
        if (GUILayout.Button(new GUIContent("Disable all", "Clear all selections, including hidden methods. No methods are selected by default."), GUILayout.Width(95f)))
        {
            foreach (GeneralMethodEntry entry in _methodsByKey.Values)
                entry.Selected = false;
            SelectionChanged();
        }
        GUI.enabled = mutationEnabled && _selectionDirty;
        if (GUILayout.Button(new GUIContent("Apply selection", "Save the selection. Restart active profiling with fresh statistics and call contexts."),
            _selectionDirty ? _theme.AccentButtonStyle : GUI.skin.button, GUILayout.Width(130f)))
            ApplySelection();
        GUI.enabled = enabled;
        GUILayout.FlexibleSpace();
        Label("Selected: " + _selectedMethodCount, GUILayout.Width(115f));
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        bool selectedOnly = ProfilerGui.ToggleLayout(_theme, _selectedOnly,
            new GUIContent("Selected only", "View filter only; hidden selections are retained."), 115f, _labelStyle);
        bool unavailable = ProfilerGui.ToggleLayout(_theme, _showUnavailable,
            new GUIContent("Show unavailable", "Show excluded methods and their reasons. Unsafe methods cannot be enabled."), 145f, _labelStyle);
        if (selectedOnly != _selectedOnly || unavailable != _showUnavailable)
        {
            _selectedOnly = selectedOnly;
            _showUnavailable = unavailable;
            FiltersChanged();
        }
        if (GUILayout.Button("Expand all", GUILayout.Width(90f)))
            SetSelectionExpansion(true);
        if (GUILayout.Button("Collapse all", GUILayout.Width(90f)))
            SetSelectionExpansion(false);
        GUILayout.FlexibleSpace();
        if (_selectionDirty)
            GUILayout.Label("Selection has changed", _theme.AccentLabelStyle);
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        Label("Search:", GUILayout.Width(55f));
        string search = GUILayout.TextField(_search, GUILayout.ExpandWidth(true));
        if (search != _search)
        {
            _search = search;
            FiltersChanged();
        }
        if (GUILayout.Button("Clear", GUILayout.Width(65f)))
        {
            _search = string.Empty;
            FiltersChanged();
            GUI.FocusControl(null);
        }
        GUILayout.EndHorizontal();

        RebuildSelectionRowsIfNeeded();
        float height = CurrentRowHeight;
        _selectionScroll = GUILayout.BeginScrollView(_selectionScroll, false, true);
        float contentHeight = Mathf.Max(height, _selectionRows.Count * height);
        Rect content = GUILayoutUtility.GetRect(_selectionContentWidth, contentHeight,
            GUILayout.Width(_selectionContentWidth), GUILayout.Height(contentHeight));
        int first = Mathf.Max(0, Mathf.FloorToInt(_selectionScroll.y / height) - 3);
        int last = Mathf.Min(_selectionRows.Count, first + Mathf.CeilToInt(MainWindowHeight / height) + 6);
        for (int index = first; index < last; index++)
            DrawSelectionRow(_selectionRows[index], new Rect(content.x, content.y + index * height, content.width, height));
        GUILayout.EndScrollView();
    }

    private bool PassesSelectionFilters(GeneralMethodEntry entry) =>
        (!_selectedOnly || entry.Selected) &&
        (_showUnavailable || entry.UnsupportedReason == null || entry.Selected) && MatchesEntry(entry, _search);

    private void FiltersChanged()
    {
        _selectionScroll = Vector2.zero;
        _profilerScroll = Vector2.zero;
        _filtersDirty = true;
        _saveFiltersAt = Time.realtimeSinceStartup + 0.75f;
        MarkSelectionRowsDirty();
        MarkViewDirty();
    }

    private void PersistFilters()
    {
        if (!_filtersDirty)
            return;
        _filtersDirty = false;
        _app.Config.GeneralMethodSelectionSearch.Value = _search;
        _app.Config.GeneralMethodProfilerSearch.Value = _profilerSearch;
        _app.Config.GeneralMethodSelectedOnly.Value = _selectedOnly;
        _app.Config.GeneralMethodShowUnavailable.Value = _showUnavailable;
    }

    private void SelectionChanged()
    {
        _selectionDirty = true;
        _selectedMethodCount = _methodsByKey.Values.Count(entry => entry.Selected);
        MarkSelectionRowsDirty();
    }

    private void SetFilteredSelection(bool selected)
    {
        // Materialize before changing Selected because Selected only is a view filter.
        foreach (GeneralMethodEntry entry in _methodsByKey.Values.Where(PassesSelectionFilters).ToArray())
        {
            if (!selected || entry.UnsupportedReason == null)
                entry.Selected = selected;
        }
        if (selected)
            ExpandPathsToSelected();
        SelectionChanged();
    }

    private void ExpandPathsToSelected()
    {
        foreach (MethodTypeEntry type in _types)
        {
            if (!type.Methods.Any(entry => entry.Selected))
                continue;
            _selectionGroupExpanded["group:" + type.GroupId] = true;
            _selectionGroupExpanded["module:" + type.ModuleKey] = true;
            type.Expanded = true;
        }
        MarkSelectionRowsDirty();
    }

    private void SetSelectionExpansion(bool expanded)
    {
        foreach (MethodTypeEntry type in _types.Where(type => type.Methods.Any(PassesSelectionFilters)))
        {
            _selectionGroupExpanded["group:" + type.GroupId] = expanded;
            _selectionGroupExpanded["module:" + type.ModuleKey] = expanded;
            type.Expanded = expanded;
        }
        MarkSelectionRowsDirty();
    }

    private void RebuildSelectionRowsIfNeeded()
    {
        if (!_selectionRowsDirty)
            return;
        _selectionRowsDirty = false;
        _selectionRows.Clear();
        bool searching = !string.IsNullOrWhiteSpace(_search);
        var entries = _methodsByKey.Values.Where(PassesSelectionFilters).ToList();
        foreach (var group in entries.GroupBy(entry => entry.TypeEntry.GroupId)
            .OrderBy(group => group.First().TypeEntry.GroupName, StringComparer.OrdinalIgnoreCase))
        {
            MethodTypeEntry first = group.First().TypeEntry;
            List<GeneralMethodEntry> groupEntries = group.ToList();
            SelectionRow groupRow = CreateContainerRow(SelectionRowKind.Group, "group:" + group.Key,
                first.GroupName + " [" + first.AssemblyName + "]", groupEntries, 0, searching);
            _selectionRows.Add(groupRow);
            if (!groupRow.Expanded)
                continue;

            foreach (var module in groupEntries.GroupBy(entry => entry.TypeEntry.ModuleKey)
                .OrderBy(module => module.First().TypeEntry.ModuleName, StringComparer.OrdinalIgnoreCase))
            {
                List<GeneralMethodEntry> moduleEntries = module.ToList();
                SelectionRow moduleRow = CreateContainerRow(SelectionRowKind.Module, "module:" + module.Key,
                    "Module: " + moduleEntries[0].TypeEntry.ModuleName, moduleEntries, 1, searching);
                _selectionRows.Add(moduleRow);
                if (!moduleRow.Expanded)
                    continue;
                foreach (var type in moduleEntries.GroupBy(entry => entry.TypeEntry)
                    .OrderBy(type => type.Key.TypeName, StringComparer.OrdinalIgnoreCase))
                {
                    List<GeneralMethodEntry> typeEntries = type.ToList();
                    SelectionRow typeRow = CreateContainerRow(SelectionRowKind.Type, type.Key.TypeName,
                        type.Key.TypeName, typeEntries, 2, searching);
                    typeRow.TypeEntry = type.Key;
                    typeRow.Expanded = searching || type.Key.Expanded;
                    _selectionRows.Add(typeRow);
                    if (!typeRow.Expanded)
                        continue;
                    foreach (GeneralMethodEntry entry in typeEntries.OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase))
                    {
                        _selectionRows.Add(new SelectionRow
                        {
                            Kind = SelectionRowKind.Method,
                            MethodEntry = entry,
                            Text = entry.DisplayName + (entry.UnsupportedReason == null ? string.Empty : " [unavailable]"),
                            Tooltip = entry.Tooltip,
                            Indent = 3
                        });
                    }
                }
            }
        }
        _selectionContentWidth = Mathf.Max(MainWindowWidth - 35f, 800f);
        foreach (SelectionRow row in _selectionRows)
            _selectionContentWidth = Mathf.Max(_selectionContentWidth,
                Mathf.Min(3000f, _labelStyle.CalcSize(new GUIContent(row.Text)).x + row.Indent * 18f + 140f));
    }

    private SelectionRow CreateContainerRow(SelectionRowKind kind, string key, string text,
        List<GeneralMethodEntry> entries, int indent, bool searching)
    {
        int selected = entries.Count(entry => entry.Selected);
        return new SelectionRow
        {
            Kind = kind,
            Key = key,
            Entries = entries,
            SelectedCount = selected,
            AvailableCount = entries.Count(entry => entry.UnsupportedReason == null),
            AvailableSelectedCount = entries.Count(entry => entry.UnsupportedReason == null && entry.Selected),
            Text = text + "  (" + selected + "/" + entries.Count + ")",
            Tooltip = "Checkbox: select/deselect methods matching the current filters. Arrow or name: expand/collapse.\n" + text,
            Indent = indent,
            Expanded = searching || (_selectionGroupExpanded.TryGetValue(key, out bool expanded) && expanded)
        };
    }

    private void DrawSelectionRow(SelectionRow row, Rect rect)
    {
        float x = rect.x + row.Indent * 18f;
        bool oldEnabled = GUI.enabled;
        if (row.Kind == SelectionRowKind.Method)
        {
            GeneralMethodEntry entry = row.MethodEntry;
            GUI.enabled = oldEnabled && (entry.UnsupportedReason == null || entry.Selected);
            bool selected = ProfilerGui.Toggle(_theme, new Rect(x + 18f, rect.y, rect.xMax - x - 18f, rect.height),
                entry.Selected, new GUIContent(row.Text, row.Tooltip), _labelStyle);
            GUI.enabled = oldEnabled;
            if (selected != entry.Selected)
            {
                entry.Selected = selected;
                SelectionChanged();
            }
            // Keep the reason available even when the checkbox is disabled.
            if (entry.UnsupportedReason != null)
                GUI.Label(new Rect(x + 18f, rect.y, rect.xMax - x - 18f, rect.height),
                    new GUIContent(string.Empty, row.Tooltip), GUIStyle.none);
            return;
        }

        bool expand = GUI.Button(InsetGroupToggleRect(new Rect(x, rect.y, 18f, rect.height)),
            row.Expanded ? "▼" : "▶", _compactButtonStyle);
        x += 18f;
        bool allSelected = row.AvailableCount > 0
            ? row.AvailableSelectedCount == row.AvailableCount
            : row.SelectedCount > 0;
        GUI.enabled = oldEnabled && (row.AvailableCount > 0 || row.SelectedCount > 0);
        bool selectAll = ProfilerGui.Toggle(_theme, new Rect(x, rect.y, 20f, rect.height), allSelected,
            new GUIContent(string.Empty, row.Tooltip), _labelStyle);
        GUI.enabled = oldEnabled;
        if (selectAll != allSelected)
        {
            foreach (GeneralMethodEntry entry in row.Entries)
            {
                if (!selectAll || entry.UnsupportedReason == null)
                    entry.Selected = selectAll;
            }
            SelectionChanged();
        }
        x += 22f;
        GUIStyle rowTextStyle = row.Kind == SelectionRowKind.Group && row.SelectedCount > 0
            ? _headerLabelStyle
            : _labelStyle;
        expand |= GUI.Button(new Rect(x, rect.y, rect.xMax - x, rect.height),
            new GUIContent(row.Text, row.Tooltip), rowTextStyle);
        if (expand)
        {
            if (row.Kind == SelectionRowKind.Type)
                row.TypeEntry.Expanded = !row.Expanded;
            else
                _selectionGroupExpanded[row.Key] = !row.Expanded;
            MarkSelectionRowsDirty();
        }
    }
}
