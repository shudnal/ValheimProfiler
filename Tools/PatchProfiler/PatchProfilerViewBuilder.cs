#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;

namespace ValheimProfiler.Tools.PatchProfiler;

internal sealed partial class PatchProfilerTool
{
    private TableSortColumn CurrentSortColumn =>
        _profilerView == ProfilerView.Avg1s ? _avgSortColumn : _maxSortColumn;

    private void SetSortColumn(TableSortColumn column)
    {
        if (_profilerView == ProfilerView.Avg1s)
        {
            _avgSortColumn = column;
            _app.Config.PatchProfilerAvgSortColumn.Value = column.ToString();
        }
        else
        {
            _maxSortColumn = column;
            _app.Config.PatchProfilerMaxSortColumn.Value = column.ToString();
        }

        MarkViewDirty();
    }

    private static TableSortColumn ParseSortColumn(
        string value,
        ProfilerView view,
        TableSortColumn fallback)
    {
        if (!Enum.TryParse(value, true, out TableSortColumn parsed))
            return fallback;

        bool valid = view == ProfilerView.Avg1s
            ? parsed is TableSortColumn.Mod or
                TableSortColumn.AvgMsPerFrame or
                TableSortColumn.AvgCallsPerFrame or
                TableSortColumn.PatchType or
                TableSortColumn.Target or
                TableSortColumn.PatchMethod
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
                TableSortColumn.PatchType or
                TableSortColumn.CombinedMethod;

        return valid ? parsed : fallback;
    }

    private List<FlatRowView> BuildRowsLocked(int frame, float now)
    {
        IEnumerable<FlatRowView> rows = _stats
            .Select(kv =>
            {
                _context.TryGetValue(kv.Key, out PatchContext ctx);
                if (ctx == null)
                    return null;

                return new FlatRowView
                {
                    EntryId = kv.Key,
                    InstrumentedMethod = ctx.InstrumentedMethod,
                    Snapshot = kv.Value.GetSnapshot(frame, now),
                    Context = ctx
                };
            })
            .Where(row => row != null);

        rows = _profilerView == ProfilerView.Avg1s
            ? rows.Where(row => row.Snapshot.Avg1sFrames > 0 && row.Snapshot.Avg1sMsPerFrame > 0)
            : rows.Where(row => row.Snapshot.MaxSnapshot.WindowSampleCount > 0 && row.Snapshot.MaxSnapshot.RawMaxMs > 0);

        return SortRows(rows);
    }

    private List<ModGroupView> BuildGroupedRowsFromRowsLocked(List<FlatRowView> rows)
    {
        List<ModGroupView> groups = rows
            .GroupBy(row => row.Context.ModGuid ?? "(unknown guid)")
            .Select(group =>
            {
                List<FlatRowView> list = SortRows(group.ToList());
                FlatRowView first = list[0];
                GroupSummary summary = BuildSummary(list);

                return new ModGroupView
                {
                    ModGuid = group.Key,
                    ModName = first.Context.ModName ?? "Unknown",
                    Rows = list,
                    AggregateScore = CalculateGroupScore(summary),
                    Summary = summary
                };
            })
            .ToList();

        groups = SortGroups(groups);

        foreach (ModGroupView group in groups)
        {
            if (!_modExpanded.ContainsKey(group.ModGuid))
                _modExpanded[group.ModGuid] = false;
        }

        return groups;
    }

    private List<FlatRowView> SortRows(IEnumerable<FlatRowView> rows)
    {
        IOrderedEnumerable<FlatRowView> ordered = CurrentSortColumn switch
        {
            TableSortColumn.Mod => rows.OrderBy(GetRowModName, StringComparer.OrdinalIgnoreCase),
            TableSortColumn.AvgMsPerFrame => rows.OrderByDescending(row => row.Snapshot.Avg1sMsPerFrame),
            TableSortColumn.AvgCallsPerFrame => rows.OrderByDescending(row => row.Snapshot.Avg1sCallsPerFrame),
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
            TableSortColumn.PatchType => rows.OrderBy(
                row => row.Context.PatchType ?? string.Empty,
                StringComparer.OrdinalIgnoreCase),
            TableSortColumn.Target => rows.OrderBy(
                row => row.Context.TargetDisplay ?? string.Empty,
                StringComparer.OrdinalIgnoreCase),
            TableSortColumn.PatchMethod => rows.OrderBy(
                row => GetMethodDisplay(row.InstrumentedMethod),
                StringComparer.OrdinalIgnoreCase),
            TableSortColumn.CombinedMethod => rows.OrderBy(
                row => BuildCombinedMethodSortText(row),
                StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderByDescending(row => row.Snapshot.MaxSnapshot.ThirdMaxMs)
        };

        return ordered
            .ThenByDescending(row => row.Snapshot.MaxSnapshot.RawMaxMs)
            .ThenBy(GetRowModName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Context.TargetDisplay ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => GetMethodDisplay(row.InstrumentedMethod), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<ModGroupView> SortGroups(IEnumerable<ModGroupView> groups)
    {
        IOrderedEnumerable<ModGroupView> ordered = CurrentSortColumn switch
        {
            TableSortColumn.Mod => groups.OrderBy(group => group.ModName, StringComparer.OrdinalIgnoreCase),
            TableSortColumn.AvgMsPerFrame => groups.OrderByDescending(group => group.Summary.Avg1sMsPerFrame),
            TableSortColumn.AvgCallsPerFrame => groups.OrderByDescending(group => group.Summary.Avg1sCallsPerFrame),
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
            TableSortColumn.PatchType => groups.OrderBy(
                group => GetGroupMinText(group, row => row.Context.PatchType),
                StringComparer.OrdinalIgnoreCase),
            TableSortColumn.Target => groups.OrderBy(
                group => GetGroupMinText(group, row => row.Context.TargetDisplay),
                StringComparer.OrdinalIgnoreCase),
            TableSortColumn.PatchMethod => groups.OrderBy(
                group => GetGroupMinText(group, row => GetMethodDisplay(row.InstrumentedMethod)),
                StringComparer.OrdinalIgnoreCase),
            TableSortColumn.CombinedMethod => groups.OrderBy(
                group => GetGroupMinText(group, BuildCombinedMethodSortText),
                StringComparer.OrdinalIgnoreCase),
            _ => groups.OrderByDescending(group => group.AggregateScore)
        };

        return ordered
            .ThenByDescending(group => group.Summary.Max.RawMaxMs)
            .ThenBy(group => group.ModName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string GetRowModName(FlatRowView row) =>
        row?.Context?.ModName ?? GuessModName(row?.InstrumentedMethod, row?.Context?.ModGuid) ?? "Unknown";

    private static string BuildCombinedMethodSortText(FlatRowView row) =>
        $"{row?.Context?.TargetDisplay ?? string.Empty} | {GetMethodDisplay(row?.InstrumentedMethod)}";

    private static string GetGroupMinText(ModGroupView group, Func<FlatRowView, string> selector)
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
        column is TableSortColumn.Mod or
            TableSortColumn.PatchType or
            TableSortColumn.Target or
            TableSortColumn.PatchMethod or
            TableSortColumn.CombinedMethod;

    private double CalculateGroupScore(GroupSummary summary)
    {
        if (_profilerView == ProfilerView.Avg1s)
            return summary.Avg1sMsPerFrame;

        return summary.Max.ThirdMaxMs;
    }

    private GroupSummary BuildSummary(List<FlatRowView> rows)
    {
        var summary = new GroupSummary();

        if (rows == null || rows.Count == 0)
            return summary;

        if (_profilerView == ProfilerView.Avg1s)
        {
            summary.Avg1sMsPerFrame = rows.Sum(row => row.Snapshot.Avg1sMsPerFrame);
            summary.Avg1sCallsPerFrame = rows.Sum(row => row.Snapshot.Avg1sCallsPerFrame);
            summary.Avg1sFrames = rows.Max(row => row.Snapshot.Avg1sFrames);
            return summary;
        }

        summary.Max = MaxAnalyticsSnapshot.Aggregate(rows.Select(row => row.Snapshot.MaxSnapshot));
        return summary;
    }
}
