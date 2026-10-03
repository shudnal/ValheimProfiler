#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;

namespace ValheimProfiler.Tools.GeneralMethodProfiler;

internal sealed partial class GeneralMethodProfilerTool
{
    private TableSortColumn CurrentSortColumn =>
        _profilerView == ProfilerView.Avg1s ? _avgSortColumn : _maxSortColumn;

    private void SetSortColumn(TableSortColumn column)
    {
        if (_profilerView == ProfilerView.Avg1s)
        {
            _avgSortColumn = column;
            _app.Config.GeneralMethodProfilerAvgSortColumn.Value = column.ToString();
        }
        else
        {
            _maxSortColumn = column;
            _app.Config.GeneralMethodProfilerMaxSortColumn.Value = column.ToString();
        }

        MarkViewDirty();
    }

    private static TableSortColumn ParseSortColumn(
        string value,
        ProfilerView view,
        TableSortColumn fallback)
    {
        // Restore the equivalent text column from pre-layout-update configuration values.
        if (string.Equals(value, "Source", StringComparison.OrdinalIgnoreCase))
            value = nameof(TableSortColumn.Mod);
        else if (string.Equals(value, "MethodType", StringComparison.OrdinalIgnoreCase))
            value = nameof(TableSortColumn.Class);

        if (!Enum.TryParse(value, true, out TableSortColumn parsed))
            return fallback;

        bool valid = view == ProfilerView.Avg1s
            ? parsed is TableSortColumn.Mod or
                TableSortColumn.AvgMsPerFrame or
                TableSortColumn.AvgCallsPerFrame or
                TableSortColumn.AvgCall or
                TableSortColumn.Class or
                TableSortColumn.Method
            : parsed is TableSortColumn.Mod or
                TableSortColumn.RawMax or
                TableSortColumn.SecondMax or
                TableSortColumn.ThirdMax or
                TableSortColumn.AboveP99 or
                TableSortColumn.AvgAboveP99 or
                TableSortColumn.P99 or
                TableSortColumn.AboveP95 or
                TableSortColumn.AvgAboveP95 or
                TableSortColumn.P95 or
                TableSortColumn.Samples or
                TableSortColumn.GcSamples or
                TableSortColumn.Class or
                TableSortColumn.Method;

        return valid ? parsed : fallback;
    }

    private List<FlatRowView> BuildRows(int frame, float now)
    {
        var rows = new List<FlatRowView>();

        foreach (GeneralMethodEntry entry in _measuredEntries)
        {
            if (entry.Stat == null || !MatchesEntry(entry, _profilerSearch))
                continue;

            RollingProfilerSnapshot snapshot = _profilerView == ProfilerView.Avg1s
                ? entry.Stat.GetFrameBudgetSnapshot(frame, now, FrameBudgetTargetFps)
                : entry.Stat.GetSnapshot(frame, now);
            bool include = _profilerView == ProfilerView.Avg1s
                ? snapshot.Avg1sWindowCalls > 0 && snapshot.Avg1sCallsPerFrame > 0
                : snapshot.MaxSnapshot.WindowSampleCount > 0;
            if (include)
                rows.Add(new FlatRowView { Entry = entry, Snapshot = snapshot });
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
                GroupSummary summary = BuildSummary(groupRows);
                MethodTypeEntry type = groupRows[0].Entry.TypeEntry;

                return new GroupRowView
                {
                    GroupId = group.Key,
                    GroupName = type.GroupName ?? type.AssemblyName ?? "Unknown",
                    Rows = groupRows,
                    Summary = summary,
                    AggregateScore = _profilerView == ProfilerView.Avg1s
                        ? summary.Avg1sMsPerFrame
                        : summary.Max.ThirdMaxMs
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
            TableSortColumn.Mod => rows.OrderBy(row => GetModName(row.Entry), StringComparer.OrdinalIgnoreCase),
            TableSortColumn.AvgMsPerFrame => rows.OrderByDescending(row => row.Snapshot.Avg1sMsPerFrame),
            TableSortColumn.AvgCallsPerFrame => rows.OrderByDescending(row => row.Snapshot.Avg1sCallsPerFrame),
            TableSortColumn.AvgCall => rows.OrderByDescending(row => row.Snapshot.Avg1sAverageCallMs),
            TableSortColumn.RawMax => rows.OrderByDescending(row => row.Snapshot.MaxSnapshot.RawMaxMs),
            TableSortColumn.SecondMax => rows.OrderByDescending(row => row.Snapshot.MaxSnapshot.SecondMaxMs),
            TableSortColumn.ThirdMax => rows.OrderByDescending(row => row.Snapshot.MaxSnapshot.ThirdMaxMs),
            TableSortColumn.AboveP99 => rows.OrderByDescending(row => row.Snapshot.MaxSnapshot.AboveP99Count),
            TableSortColumn.AvgAboveP99 => rows.OrderByDescending(row => row.Snapshot.MaxSnapshot.AvgAboveP99Ms),
            TableSortColumn.P99 => rows.OrderByDescending(row => row.Snapshot.MaxSnapshot.P99Ms),
            TableSortColumn.AboveP95 => rows.OrderByDescending(row => row.Snapshot.MaxSnapshot.AboveP95Count),
            TableSortColumn.AvgAboveP95 => rows.OrderByDescending(row => row.Snapshot.MaxSnapshot.AvgAboveP95Ms),
            TableSortColumn.P95 => rows.OrderByDescending(row => row.Snapshot.MaxSnapshot.P95Ms),
            TableSortColumn.Samples => rows.OrderByDescending(row => row.Snapshot.MaxSnapshot.WindowSampleCount),
            TableSortColumn.GcSamples => rows.OrderByDescending(row => row.Snapshot.MaxSnapshot.GcSampleCount),
            TableSortColumn.Class => rows.OrderBy(row => GetClassName(row.Entry), StringComparer.OrdinalIgnoreCase),
            TableSortColumn.Method => rows.OrderBy(row => GetMethodDisplayName(row.Entry), StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderByDescending(row => row.Snapshot.MaxSnapshot.ThirdMaxMs)
        };

        return ordered
            .ThenByDescending(row => row.Snapshot.MaxSnapshot.RawMaxMs)
            .ThenBy(row => row.Entry.TypeEntry.GroupName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Entry.TypeEntry.TypeName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<GroupRowView> SortGroups(IEnumerable<GroupRowView> groups)
    {
        IOrderedEnumerable<GroupRowView> ordered = CurrentSortColumn switch
        {
            TableSortColumn.Mod => groups.OrderBy(group => group.GroupName, StringComparer.OrdinalIgnoreCase),
            TableSortColumn.AvgMsPerFrame => groups.OrderByDescending(group => group.Summary.Avg1sMsPerFrame),
            TableSortColumn.AvgCallsPerFrame => groups.OrderByDescending(group => group.Summary.Avg1sCallsPerFrame),
            TableSortColumn.AvgCall => groups.OrderByDescending(group => group.Summary.Avg1sAverageCallMs),
            TableSortColumn.RawMax => groups.OrderByDescending(group => group.Summary.Max.RawMaxMs),
            TableSortColumn.SecondMax => groups.OrderByDescending(group => group.Summary.Max.SecondMaxMs),
            TableSortColumn.ThirdMax => groups.OrderByDescending(group => group.Summary.Max.ThirdMaxMs),
            TableSortColumn.AboveP99 => groups.OrderByDescending(group => group.Summary.Max.AboveP99Count),
            TableSortColumn.AvgAboveP99 => groups.OrderByDescending(group => group.Summary.Max.AvgAboveP99Ms),
            TableSortColumn.P99 => groups.OrderByDescending(group => group.Summary.Max.P99Ms),
            TableSortColumn.AboveP95 => groups.OrderByDescending(group => group.Summary.Max.AboveP95Count),
            TableSortColumn.AvgAboveP95 => groups.OrderByDescending(group => group.Summary.Max.AvgAboveP95Ms),
            TableSortColumn.P95 => groups.OrderByDescending(group => group.Summary.Max.P95Ms),
            TableSortColumn.Samples => groups.OrderByDescending(group => group.Summary.Max.WindowSampleCount),
            TableSortColumn.GcSamples => groups.OrderByDescending(group => group.Summary.Max.GcSampleCount),
            TableSortColumn.Class => groups.OrderBy(
                group => GetGroupMinText(group, row => GetClassName(row.Entry)), StringComparer.OrdinalIgnoreCase),
            TableSortColumn.Method => groups.OrderBy(
                group => GetGroupMinText(group, row => GetMethodDisplayName(row.Entry)), StringComparer.OrdinalIgnoreCase),
            _ => groups.OrderByDescending(group => group.AggregateScore)
        };

        return ordered
            .ThenByDescending(group => group.Summary.Max.RawMaxMs)
            .ThenBy(group => group.GroupName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetGroupMinText(GroupRowView group, Func<FlatRowView, string> selector)
    {
        if (group?.Rows == null || group.Rows.Count == 0)
            return string.Empty;

        string value = selector(group.Rows[0]) ?? string.Empty;
        for (int i = 1; i < group.Rows.Count; i++)
        {
            string candidate = selector(group.Rows[i]) ?? string.Empty;
            if (string.Compare(candidate, value, StringComparison.OrdinalIgnoreCase) < 0)
                value = candidate;
        }
        return value;
    }

    private static bool IsTextSortColumn(TableSortColumn column) =>
        column is TableSortColumn.Mod or TableSortColumn.Class or TableSortColumn.Method;

    private static GroupSummary BuildSummary(List<FlatRowView> rows)
    {
        return new GroupSummary
        {
            Avg1sMsPerFrame = rows.Sum(row => row.Snapshot.Avg1sMsPerFrame),
            Avg1sCallsPerFrame = rows.Sum(row => row.Snapshot.Avg1sCallsPerFrame),
            Avg1sAverageCallMs = rows.Sum(row => row.Snapshot.Avg1sCallsPerFrame) > 0
                ? rows.Sum(row => row.Snapshot.Avg1sMsPerFrame) / rows.Sum(row => row.Snapshot.Avg1sCallsPerFrame)
                : 0,
            Max = RollingMaxSnapshot.Aggregate(rows.Select(row => row.Snapshot.MaxSnapshot))
        };
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

    private static string FormatCount(double value)
    {
        if (value <= 0)
            return "0";
        if (value > MaxDisplayedCount)
            return MaxDisplayedCount + "+";
        return $"{value:0.##}";
    }

    private static string FormatAverageCall(double milliseconds)
    {
        if (milliseconds <= 0)
            return "0 ns";
        if (milliseconds >= 1.0)
            return $"{milliseconds:0.###} ms";
        if (milliseconds >= 0.001)
            return $"{milliseconds * 1000.0:0.##} us";
        return $"{milliseconds * 1000000.0:0} ns";
    }

    private static string FormatRawMax(RollingMaxSnapshot max)
    {
        string value = FormatMs(max.RawMaxMs);
        return ShouldMarkRawMax(max) ? value + "!" : value;
    }

    private static bool ShouldMarkRawMax(RollingMaxSnapshot max)
    {
        bool isolated =
            max.RawMaxMs >= IsolatedRawMaxMinMs &&
            max.WindowSampleCount >= 3 &&
            max.ThirdMaxMs > 0 &&
            max.RawMaxMs >= max.ThirdMaxMs * IsolatedRawMaxMultiplier;

        bool gcContaminated =
            max.RawMaxMs >= IsolatedRawMaxMinMs &&
            max.GcSampleCount > 0;

        return isolated || gcContaminated;
    }
}
