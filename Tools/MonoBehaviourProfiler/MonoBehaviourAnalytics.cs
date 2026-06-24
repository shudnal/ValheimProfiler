#nullable disable

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace ValheimProfiler.Tools.MonoBehaviourProfiler;

internal sealed partial class MonoBehaviourProfilerTool
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
            Name = "MonoBehaviourProfilerAnalytics"
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
        List<RollingProfilerStat> dirty;
        List<RollingProfilerStat> active;

        lock (_analyticsQueueLock)
        {
            dirty = new List<RollingProfilerStat>(_dirtyAnalyticsQueue.Count);
            while (_dirtyAnalyticsQueue.Count > 0)
            {
                RollingProfilerStat stat = _dirtyAnalyticsQueue.Dequeue();
                _queuedAnalyticsStats.Remove(stat);
                dirty.Add(stat);
            }

            active = _activeAnalyticsStats.ToList();
        }

        bool sweepActive =
            forceSweep ||
            now - _lastAnalyticsSweepRealtime >= 1f ||
            now < _lastAnalyticsSweepRealtime;

        if (sweepActive)
            _lastAnalyticsSweepRealtime = now;

        foreach (RollingProfilerStat stat in dirty)
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

        foreach (RollingProfilerStat stat in active)
            stat.ProcessBackgroundAnalytics(now);

        lock (_analyticsQueueLock)
            _activeAnalyticsStats.RemoveWhere(stat => !stat.HasAnyAnalyticsData);
    }

    private void QueueAnalyticsWork(RollingProfilerStat stat)
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

    private float GetRefreshInterval(ProfilerView view) =>
        view == ProfilerView.Avg1s ? 0.1f : 1.0f;

    private void RefreshCachedViewIfNeeded()
    {
        bool configChanged =
            _lastRenderedProfilerView != _profilerView ||
            _lastRenderedGroupByMod != _groupByMod;

        float now = DisplayRealtime;
        float interval = GetRefreshInterval(_profilerView);

        if (!configChanged && !_viewDirty && now < _nextViewRefreshTime)
            return;

        List<FlatRowView> rows = BuildRows(DisplayFrame, DisplayRealtime);

        if (_groupByMod)
        {
            _cachedGroupedRows = BuildGroupedRows(rows);
            _cachedFlatRows.Clear();
        }
        else
        {
            _cachedFlatRows = rows.Take(750).ToList();
            _cachedGroupedRows.Clear();
        }

        _lastRenderedProfilerView = _profilerView;
        _lastRenderedGroupByMod = _groupByMod;
        _viewDirty = false;
        _nextViewRefreshTime = now + interval;
    }
}