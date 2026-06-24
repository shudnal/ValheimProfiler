#nullable disable

using BepInEx.Bootstrap;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;

namespace ValheimProfiler.Tools.PatchProfiler;

internal sealed partial class PatchProfilerTool
{
    private void StartAnalyticsThread()
    {
        if (_analyticsThreadRunning)
            return;

        if (_analyticsWakeEvent == null)
            _analyticsWakeEvent = new AutoResetEvent(false);

        _analyticsThreadRunning = true;
        _analyticsThread = new Thread(AnalyticsThreadLoop)
        {
            IsBackground = true,
            Name = "PatchProfilerAnalytics"
        };
        _analyticsThread.Start();
    }

    private void StopAnalyticsThread()
    {
        _analyticsThreadRunning = false;

        try
        {
            _analyticsWakeEvent?.Set();
        }
        catch
        {
        }

        if (_analyticsThread != null && _analyticsThread.IsAlive)
        {
            try
            {
                _analyticsThread.Join(1000);
            }
            catch
            {
            }
        }

        _analyticsThread = null;

        try
        {
            _analyticsWakeEvent?.Dispose();
        }
        catch
        {
        }

        _analyticsWakeEvent = null;
    }

    private void AnalyticsThreadLoop()
    {
        while (_analyticsThreadRunning)
        {
            try
            {
                _analyticsWakeEvent.WaitOne(200);

                lock (_analyticsProcessingLock)
                {
                    if (!_profilingActive)
                        continue;

                    ProcessQueuedAnalytics(CurrentRealtime, forceSweep: false);
                }
            }
            catch
            {
            }
        }
    }

    private void ProcessQueuedAnalytics(float now, bool forceSweep)
    {
        List<PatchStat> dirty;
        List<PatchStat> active;

        lock (_analyticsQueueLock)
        {
            dirty = new List<PatchStat>(_dirtyAnalyticsQueue.Count);
            while (_dirtyAnalyticsQueue.Count > 0)
            {
                var stat = _dirtyAnalyticsQueue.Dequeue();
                _queuedAnalyticsStats.Remove(stat);
                dirty.Add(stat);
            }

            active = _activeAnalyticsStats.ToList();
        }

        bool sweepActive = forceSweep ||
                           now - _lastAnalyticsSweepRealtime >= 1f ||
                           now < _lastAnalyticsSweepRealtime;
        if (sweepActive)
            _lastAnalyticsSweepRealtime = now;

        foreach (var stat in dirty)
        {
            stat.ProcessBackgroundAnalytics(now);

            if (stat.HasAnyAnalyticsData)
            {
                lock (_analyticsQueueLock)
                    _activeAnalyticsStats.Add(stat);
            }
        }

        if (!sweepActive)
            return;

        foreach (var stat in active)
            stat.ProcessBackgroundAnalytics(now);

        lock (_analyticsQueueLock)
            _activeAnalyticsStats.RemoveWhere(stat => !stat.HasAnyAnalyticsData);
    }

    private void QueueAnalyticsWork(PatchStat stat)
    {
        lock (_analyticsQueueLock)
        {
            if (_queuedAnalyticsStats.Add(stat))
                _dirtyAnalyticsQueue.Enqueue(stat);
        }

        try
        {
            _analyticsWakeEvent?.Set();
        }
        catch
        {
        }
    }

    private void ClearAnalyticsQueues()
    {
        lock (_analyticsProcessingLock)
        {
            lock (_analyticsQueueLock)
            {
                _dirtyAnalyticsQueue.Clear();
                _queuedAnalyticsStats.Clear();
                _activeAnalyticsStats.Clear();
            }
        }
    }

    private void FlushAnalyticsAt(float now)
    {
        lock (_analyticsProcessingLock)
            ProcessQueuedAnalytics(now, forceSweep: true);
    }

    private void MarkViewDirty()
    {
        _viewDirty = true;
        _layoutMetricsDirty = true;
        _nextViewRefreshTime = 0f;
    }

    private float GetRefreshInterval(ProfilerView view) => view == ProfilerView.Avg1s ? 0.1f : 1.0f;

    private void RefreshCachedViewIfNeeded()
    {
        bool configChanged = _lastRenderedProfilerView != _profilerView || _lastRenderedGroupByMod != _groupByMod;
        float now = DisplayRealtime;
        float interval = GetRefreshInterval(_profilerView);

        if (!configChanged && !_viewDirty && now < _nextViewRefreshTime)
            return;

        lock (_lock)
        {
            var allRows = BuildRowsLocked(DisplayFrame, DisplayRealtime);
            _cachedTotalSummary = BuildSummary(allRows);

            if (_groupByMod)
            {
                _cachedGroupedRows = BuildGroupedRowsFromRowsLocked(allRows);
                _cachedFlatRows.Clear();
            }
            else
            {
                _cachedFlatRows = allRows.Take(500).ToList();
                _cachedGroupedRows.Clear();
            }
        }

        int layoutRowsSignature = CalculateLayoutRowsSignature();
        if (_lastLayoutRowsSignature != layoutRowsSignature)
        {
            _lastLayoutRowsSignature = layoutRowsSignature;
            _layoutMetricsDirty = true;
        }

        _lastRenderedProfilerView = _profilerView;
        _lastRenderedGroupByMod = _groupByMod;
        _viewDirty = false;
        _nextViewRefreshTime = now + interval;
    }

    private int CalculateLayoutRowsSignature()
    {
        unchecked
        {
            int signature = 17;
            int count = 0;

            if (_groupByMod)
            {
                foreach (ModGroupView group in _cachedGroupedRows)
                {
                    foreach (FlatRowView row in group.Rows)
                    {
                        signature ^= row.EntryId * 397;
                        count++;
                    }
                }
            }
            else
            {
                foreach (FlatRowView row in _cachedFlatRows)
                {
                    signature ^= row.EntryId * 397;
                    count++;
                }
            }

            return signature * 31 + count;
        }
    }

}