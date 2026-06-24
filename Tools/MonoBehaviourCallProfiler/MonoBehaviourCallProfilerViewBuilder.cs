#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;

namespace ValheimProfiler.Tools.MonoBehaviourCallProfiler;

internal sealed partial class MonoBehaviourCallProfilerTool
{
    private TableSortColumn CurrentSortColumn => _sortColumn;

    private void SetSortColumn(TableSortColumn column)
    {
        _sortColumn = column;
        _app.Config.MonoBehaviourCallProfilerSortColumn.Value = column.ToString();
        MarkViewDirty();
    }

    private static TableSortColumn ParseSortColumn(string value, TableSortColumn fallback) =>
        Enum.TryParse(value, true, out TableSortColumn parsed) ? parsed : fallback;

    private void RefreshCachedViewIfNeeded()
    {
        bool configChanged = _lastLayoutGroupByMod != _groupByMod;
        float now = UnityEngine.Time.realtimeSinceStartup;

        if (!configChanged && !_viewDirty && now < _nextViewRefreshTime)
            return;

        List<FlatRowView> rows = BuildRows();

        if (_groupByMod)
        {
            _cachedGroupedRows = BuildGroupedRows(rows);
            _cachedFlatRows.Clear();
        }
        else
        {
            _cachedFlatRows = rows.Take(1000).ToList();
            _cachedGroupedRows.Clear();
        }

        _viewDirty = false;
        _nextViewRefreshTime = now + 0.2f;
    }

    private List<FlatRowView> BuildRows()
    {
        var rows = new List<FlatRowView>();

        lock (_lock)
        {
            foreach (BehaviourTypeEntry typeEntry in _types)
            {
                foreach (BehaviourMethodEntry entry in typeEntry.Methods)
                {
                    LifetimeProfilerSnapshot snapshot = entry.Stat.GetSnapshot();
                    if (snapshot.Calls <= 0)
                        continue;

                    rows.Add(new FlatRowView
                    {
                        Entry = entry,
                        Snapshot = snapshot
                    });
                }
            }
        }

        return SortRows(rows);
    }

    private List<GroupRowView> BuildGroupedRows(List<FlatRowView> rows)
    {
        List<GroupRowView> result = rows
            .GroupBy(row => row.Entry.TypeEntry.GroupId ?? "(unknown)")
            .Select(group =>
            {
                List<FlatRowView> groupRows = SortRows(group.ToList());
                BehaviourTypeEntry type = groupRows[0].Entry.TypeEntry;

                return new GroupRowView
                {
                    GroupId = group.Key,
                    GroupName = type.GroupName ?? type.AssemblyName ?? "Unknown",
                    Rows = groupRows,
                    Summary = BuildSummary(groupRows)
                };
            })
            .ToList();

        result = SortGroups(result);

        lock (_lock)
        {
            foreach (GroupRowView group in result)
            {
                if (!_groupExpanded.ContainsKey(group.GroupId))
                    _groupExpanded[group.GroupId] = false;
            }
        }

        return result;
    }

    private List<FlatRowView> SortRows(IEnumerable<FlatRowView> rows)
    {
        IOrderedEnumerable<FlatRowView> ordered = CurrentSortColumn switch
        {
            TableSortColumn.Source => rows.OrderByDescending(
                row => _groupByMod ? BuildEntryMethodName(row.Entry) : BuildUngroupedEntryName(row.Entry),
                StringComparer.OrdinalIgnoreCase),
            TableSortColumn.Calls => rows.OrderByDescending(row => row.Snapshot.Calls),
            TableSortColumn.Total => rows.OrderByDescending(row => row.Snapshot.TotalMs),
            TableSortColumn.Average => rows.OrderByDescending(row => row.Snapshot.AverageMs),
            TableSortColumn.Max => rows.OrderByDescending(row => row.Snapshot.MaxMs),
            TableSortColumn.Last => rows.OrderByDescending(row => row.Snapshot.LastMs),
            TableSortColumn.P95 => rows.OrderByDescending(row => row.Snapshot.P95Ms),
            TableSortColumn.P99 => rows.OrderByDescending(row => row.Snapshot.P99Ms),
            TableSortColumn.FirstAt => rows.OrderByDescending(row => row.Snapshot.FirstCallAtSeconds),
            TableSortColumn.LastAt => rows.OrderByDescending(row => row.Snapshot.LastCallAtSeconds),
            TableSortColumn.MonoBehaviour => rows.OrderByDescending(
                row => row.Entry.TypeEntry.TypeName,
                StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderByDescending(row => row.Snapshot.TotalMs)
        };

        return ordered
            .ThenByDescending(row => row.Snapshot.MaxMs)
            .ThenBy(row => row.Entry.TypeEntry.GroupName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Entry.TypeEntry.TypeName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<GroupRowView> SortGroups(IEnumerable<GroupRowView> groups)
    {
        IOrderedEnumerable<GroupRowView> ordered = CurrentSortColumn switch
        {
            TableSortColumn.Source => groups.OrderByDescending(group => group.GroupName, StringComparer.OrdinalIgnoreCase),
            TableSortColumn.Calls => groups.OrderByDescending(group => group.Summary.Calls),
            TableSortColumn.Total => groups.OrderByDescending(group => group.Summary.TotalMs),
            TableSortColumn.Average => groups.OrderByDescending(group => group.Summary.AverageMs),
            TableSortColumn.Max => groups.OrderByDescending(group => group.Summary.MaxMs),
            TableSortColumn.Last => groups.OrderByDescending(group => group.Summary.LastMs),
            TableSortColumn.P95 => groups.OrderByDescending(group => group.Summary.P95Ms),
            TableSortColumn.P99 => groups.OrderByDescending(group => group.Summary.P99Ms),
            TableSortColumn.FirstAt => groups.OrderByDescending(group => group.Summary.FirstCallAtSeconds),
            TableSortColumn.LastAt => groups.OrderByDescending(group => group.Summary.LastCallAtSeconds),
            TableSortColumn.MonoBehaviour => groups.OrderByDescending(GetGroupMaxTypeName, StringComparer.OrdinalIgnoreCase),
            _ => groups.OrderByDescending(group => group.Summary.TotalMs)
        };

        return ordered
            .ThenByDescending(group => group.Summary.MaxMs)
            .ThenBy(group => group.GroupName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetGroupMaxTypeName(GroupRowView group)
    {
        if (group?.Rows == null || group.Rows.Count == 0)
            return string.Empty;

        string value = string.Empty;
        for (int i = 0; i < group.Rows.Count; i++)
        {
            string candidate = group.Rows[i].Entry?.TypeEntry?.TypeName ?? string.Empty;
            if (string.Compare(candidate, value, StringComparison.OrdinalIgnoreCase) > 0)
                value = candidate;
        }

        return value;
    }

    private static GroupSummary BuildSummary(List<FlatRowView> rows)
    {
        long calls = rows.Sum(row => row.Snapshot.Calls);
        double total = rows.Sum(row => row.Snapshot.TotalMs);
        FlatRowView latest = rows
            .OrderByDescending(row => row.Snapshot.LastCallAtSeconds)
            .FirstOrDefault();

        bool hasFirst = false;
        float first = 0f;
        foreach (FlatRowView row in rows)
        {
            float candidate = row.Snapshot.FirstCallAtSeconds;
            if (!hasFirst || candidate < first)
            {
                first = candidate;
                hasFirst = true;
            }
        }

        return new GroupSummary
        {
            Calls = calls,
            TotalMs = total,
            AverageMs = calls > 0 ? total / calls : 0,
            MaxMs = rows.Max(row => row.Snapshot.MaxMs),
            LastMs = latest?.Snapshot.LastMs ?? 0,
            P95Ms = rows.Max(row => row.Snapshot.P95Ms),
            P99Ms = rows.Max(row => row.Snapshot.P99Ms),
            FirstCallAtSeconds = first,
            LastCallAtSeconds = latest?.Snapshot.LastCallAtSeconds ?? 0f
        };
    }

    private static string BuildEntryMethodName(BehaviourMethodEntry entry)
    {
        if (entry == null)
            return "Unknown";

        string typeName = entry.TypeEntry?.Type?.Name;
        if (string.IsNullOrWhiteSpace(typeName))
        {
            typeName = entry.TypeEntry?.TypeName ?? string.Empty;
            int separator = Math.Max(typeName.LastIndexOf('.'), typeName.LastIndexOf('+'));
            if (separator >= 0 && separator + 1 < typeName.Length)
                typeName = typeName.Substring(separator + 1);
        }

        string methodName = entry.DisplayName ?? "Unknown";
        return string.IsNullOrWhiteSpace(typeName) ? methodName : typeName + "." + methodName;
    }

    private static string BuildUngroupedEntryName(BehaviourMethodEntry entry)
    {
        if (entry?.TypeEntry == null)
            return BuildEntryMethodName(entry);

        string group = entry.TypeEntry.GroupName ?? entry.TypeEntry.AssemblyName ?? "Unknown";
        return group + " │ " + BuildEntryMethodName(entry);
    }

    private static string FormatMs(double value)
    {
        if (value <= 0)
            return "0.000";
        if (value > MaxDisplayedMs)
            return $"{MaxDisplayedMs:0.000}+";
        return $"{value:0.000}";
    }

    private static string FormatCount(long value)
    {
        if (value <= 0)
            return "0";
        if (value > MaxDisplayedCount)
            return MaxDisplayedCount + "+";
        return value.ToString();
    }

    private static string FormatTimeline(float seconds)
    {
        if (seconds < 0f)
            return "-";
        if (seconds < 60f)
            return $"{seconds:0.0}s";
        if (seconds < 3600f)
            return $"{seconds / 60f:0.0}m";
        return $"{seconds / 3600f:0.0}h";
    }
}
