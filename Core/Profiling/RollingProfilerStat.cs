#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ValheimProfiler.Core.Profiling;

internal sealed class RollingProfilerStat
{
    private const float AvgWindowSec = 1.0f;
    private const int MaxWindowSeconds = 60;
    private const double HistogramMinMs = 0.0005;
    private const double HistogramBase = 1.15;

    private readonly object _sync = new object();

    private int _frame = -1;
    private double _frameMs;
    private int _frameCalls;

    private readonly Queue<Bucket> _avgWindow = new Queue<Bucket>();
    private double _avgWindowMs;
    private int _avgWindowCalls;
    private int _avgWindowFrames;

    private readonly Queue<TimedSample> _pendingSamples = new Queue<TimedSample>();
    private readonly AnalyticsBucket[] _analyticsBuckets;
    private readonly SortedDictionary<int, HistogramEntry> _totalHistogram = new SortedDictionary<int, HistogramEntry>();

    private long _totalSampleCount;
    private int _windowSampleCount;
    private int _gcWindowSampleCount;
    private bool _analyticsDirty;
    private RollingMaxSnapshot _maxSnapshot = RollingMaxSnapshot.Empty;

    public RollingProfilerStat()
    {
        _analyticsBuckets = new AnalyticsBucket[MaxWindowSeconds];
        for (int i = 0; i < _analyticsBuckets.Length; i++)
            _analyticsBuckets[i] = new AnalyticsBucket();
    }

    public bool HasAnyAnalyticsData
    {
        get
        {
            lock (_sync)
                return _windowSampleCount > 0 || _pendingSamples.Count > 0;
        }
    }

    public bool Add(double ms, int frame, float now, bool gcSample)
    {
        lock (_sync)
        {
            SyncFrameInternal(frame, now);

            _frameMs += ms;
            _frameCalls++;

            _pendingSamples.Enqueue(new TimedSample
            {
                RealtimeSecond = Mathf.FloorToInt(now),
                Ms = ms,
                GcSample = gcSample
            });

            _totalSampleCount++;
            _analyticsDirty = true;
            return _pendingSamples.Count == 1;
        }
    }

    public void ProcessBackgroundAnalytics(float now)
    {
        lock (_sync)
        {
            int currentSecond = Mathf.FloorToInt(now);
            bool changed = false;

            while (_pendingSamples.Count > 0)
            {
                var sample = _pendingSamples.Dequeue();

                if (sample.RealtimeSecond < currentSecond - (MaxWindowSeconds - 1))
                    continue;

                AddSampleToAnalyticsWindow(sample);
                changed = true;
            }

            if (ExpireOldAnalyticsBuckets(currentSecond))
                changed = true;

            if (!changed && !_analyticsDirty)
                return;

            RebuildMaxSnapshot();
            _analyticsDirty = false;
        }
    }

    public RollingProfilerSnapshot GetSnapshot(int frame, float now)
    {
        lock (_sync)
        {
            SyncFrameInternal(frame, now);

            return new RollingProfilerSnapshot
            {
                Avg1sMsPerFrame = _avgWindowFrames > 0 ? _avgWindowMs / _avgWindowFrames : 0,
                Avg1sCallsPerFrame = _avgWindowFrames > 0 ? (double)_avgWindowCalls / _avgWindowFrames : 0,
                Avg1sFrames = _avgWindowFrames,
                MaxSnapshot = _maxSnapshot
            };
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _frame = -1;
            _frameMs = 0;
            _frameCalls = 0;

            _avgWindow.Clear();
            _avgWindowMs = 0;
            _avgWindowCalls = 0;
            _avgWindowFrames = 0;

            _pendingSamples.Clear();
            _totalHistogram.Clear();
            _totalSampleCount = 0;
            _windowSampleCount = 0;
            _gcWindowSampleCount = 0;
            _analyticsDirty = false;
            _maxSnapshot = RollingMaxSnapshot.Empty;

            for (int i = 0; i < _analyticsBuckets.Length; i++)
                _analyticsBuckets[i].Reset();
        }
    }

    private void SyncFrameInternal(int frame, float now)
    {
        if (_frame == -1)
            _frame = frame;

        if (frame != _frame)
        {
            CommitFrame(now);
            _frame = frame;
            _frameMs = 0;
            _frameCalls = 0;
        }

        while (_avgWindow.Count > 0 && (now - _avgWindow.Peek().Time) > AvgWindowSec)
        {
            var old = _avgWindow.Dequeue();
            _avgWindowMs -= old.Ms;
            _avgWindowCalls -= old.Calls;
            _avgWindowFrames -= 1;
        }

        if (_avgWindowFrames < 0)
            _avgWindowFrames = 0;
    }

    private void CommitFrame(float now)
    {
        var bucket = new Bucket
        {
            Time = now,
            Ms = _frameMs,
            Calls = _frameCalls
        };

        _avgWindow.Enqueue(bucket);
        _avgWindowMs += bucket.Ms;
        _avgWindowCalls += bucket.Calls;
        _avgWindowFrames += 1;
    }

    private void AddSampleToAnalyticsWindow(TimedSample sample)
    {
        int bucketIndex = Mod(sample.RealtimeSecond, MaxWindowSeconds);
        var bucket = _analyticsBuckets[bucketIndex];

        if (bucket.RealtimeSecond != sample.RealtimeSecond)
            ReplaceAnalyticsBucket(bucket, sample.RealtimeSecond);

        int bin = MsToHistogramBin(sample.Ms);

        bucket.SampleCount++;
        bucket.SumMs += sample.Ms;
        bucket.AddTop(sample.Ms);

        if (sample.GcSample)
            bucket.GcSampleCount++;

        if (bucket.Histogram.TryGetValue(bin, out var bucketEntry))
        {
            bucketEntry.Count++;
            bucketEntry.SumMs += sample.Ms;
            bucket.Histogram[bin] = bucketEntry;
        }
        else
        {
            bucket.Histogram[bin] = new HistogramEntry { Count = 1, SumMs = sample.Ms };
        }

        if (_totalHistogram.TryGetValue(bin, out var totalEntry))
        {
            totalEntry.Count++;
            totalEntry.SumMs += sample.Ms;
            _totalHistogram[bin] = totalEntry;
        }
        else
        {
            _totalHistogram[bin] = new HistogramEntry { Count = 1, SumMs = sample.Ms };
        }

        _windowSampleCount++;

        if (sample.GcSample)
            _gcWindowSampleCount++;
    }

    private void ReplaceAnalyticsBucket(AnalyticsBucket bucket, int newSecond)
    {
        if (bucket.RealtimeSecond != int.MinValue && bucket.SampleCount > 0)
            RemoveBucketFromTotals(bucket);

        bucket.Reset();
        bucket.RealtimeSecond = newSecond;
    }

    private void RemoveBucketFromTotals(AnalyticsBucket bucket)
    {
        _windowSampleCount -= bucket.SampleCount;
        if (_windowSampleCount < 0)
            _windowSampleCount = 0;

        _gcWindowSampleCount -= bucket.GcSampleCount;
        if (_gcWindowSampleCount < 0)
            _gcWindowSampleCount = 0;

        foreach (var kv in bucket.Histogram)
        {
            if (_totalHistogram.TryGetValue(kv.Key, out var totalEntry))
            {
                totalEntry.Count -= kv.Value.Count;
                totalEntry.SumMs -= kv.Value.SumMs;

                if (totalEntry.Count <= 0)
                    _totalHistogram.Remove(kv.Key);
                else
                    _totalHistogram[kv.Key] = totalEntry;
            }
        }
    }

    private bool ExpireOldAnalyticsBuckets(int currentSecond)
    {
        bool changed = false;

        for (int i = 0; i < _analyticsBuckets.Length; i++)
        {
            var bucket = _analyticsBuckets[i];
            if (bucket.RealtimeSecond == int.MinValue)
                continue;

            if (currentSecond - bucket.RealtimeSecond >= MaxWindowSeconds)
            {
                RemoveBucketFromTotals(bucket);
                bucket.Reset();
                changed = true;
            }
        }

        return changed;
    }

    private void RebuildMaxSnapshot()
    {
        if (_windowSampleCount <= 0 || _totalHistogram.Count == 0)
        {
            _maxSnapshot = new RollingMaxSnapshot
            {
                RawMaxMs = 0,
                SecondMaxMs = 0,
                ThirdMaxMs = 0,
                P95Ms = 0,
                P99Ms = 0,
                AboveP95Count = 0,
                AboveP99Count = 0,
                AvgAboveP95Ms = 0,
                AvgAboveP99Ms = 0,
                TotalSampleCount = _totalSampleCount,
                WindowSampleCount = 0,
                GcSampleCount = 0
            };
            return;
        }

        double top1 = 0;
        double top2 = 0;
        double top3 = 0;

        for (int i = 0; i < _analyticsBuckets.Length; i++)
        {
            var bucket = _analyticsBuckets[i];

            AddTop(ref top1, ref top2, ref top3, bucket.Top1Ms);
            AddTop(ref top1, ref top2, ref top3, bucket.Top2Ms);
            AddTop(ref top1, ref top2, ref top3, bucket.Top3Ms);
        }

        int p95Target = Math.Max(1, Mathf.CeilToInt(_windowSampleCount * 0.95f));
        int p99Target = Math.Max(1, Mathf.CeilToInt(_windowSampleCount * 0.99f));

        int running = 0;
        int p95Bin = 0;
        int p99Bin = 0;
        bool p95Found = false;
        bool p99Found = false;

        foreach (var kv in _totalHistogram)
        {
            running += kv.Value.Count;

            if (!p95Found && running >= p95Target)
            {
                p95Bin = kv.Key;
                p95Found = true;
            }

            if (!p99Found && running >= p99Target)
            {
                p99Bin = kv.Key;
                p99Found = true;
                break;
            }
        }

        int aboveP95 = 0;
        int aboveP99 = 0;
        double aboveP95Sum = 0;
        double aboveP99Sum = 0;

        foreach (var kv in _totalHistogram)
        {
            if (kv.Key > p95Bin)
            {
                aboveP95 += kv.Value.Count;
                aboveP95Sum += kv.Value.SumMs;
            }

            if (kv.Key > p99Bin)
            {
                aboveP99 += kv.Value.Count;
                aboveP99Sum += kv.Value.SumMs;
            }
        }

        _maxSnapshot = new RollingMaxSnapshot
        {
            RawMaxMs = top1,
            SecondMaxMs = top2,
            ThirdMaxMs = top3,
            P95Ms = HistogramBinToMs(p95Bin),
            P99Ms = HistogramBinToMs(p99Bin),
            AboveP95Count = aboveP95,
            AboveP99Count = aboveP99,
            AvgAboveP95Ms = aboveP95 > 0 ? aboveP95Sum / aboveP95 : 0,
            AvgAboveP99Ms = aboveP99 > 0 ? aboveP99Sum / aboveP99 : 0,
            TotalSampleCount = _totalSampleCount,
            WindowSampleCount = _windowSampleCount,
            GcSampleCount = _gcWindowSampleCount
        };
    }

    private static void AddTop(ref double top1, ref double top2, ref double top3, double value)
    {
        if (value <= 0)
            return;

        if (value > top1)
        {
            top3 = top2;
            top2 = top1;
            top1 = value;
            return;
        }

        if (value > top2)
        {
            top3 = top2;
            top2 = value;
            return;
        }

        if (value > top3)
            top3 = value;
    }

    private static int MsToHistogramBin(double ms)
    {
        if (ms <= HistogramMinMs)
            return 0;

        double value = Math.Log(ms / HistogramMinMs, HistogramBase);
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
            return 0;

        return (int)Math.Floor(value);
    }

    private static double HistogramBinToMs(int bin) =>
        bin <= 0 ? HistogramMinMs : HistogramMinMs * Math.Pow(HistogramBase, bin);

    private static int Mod(int x, int m)
    {
        int r = x % m;
        return r < 0 ? r + m : r;
    }

    private struct Bucket
    {
        public float Time;
        public double Ms;
        public int Calls;
    }

    private struct TimedSample
    {
        public int RealtimeSecond;
        public double Ms;
        public bool GcSample;
    }

    private struct HistogramEntry
    {
        public int Count;
        public double SumMs;
    }

    private sealed class AnalyticsBucket
    {
        public int RealtimeSecond = int.MinValue;
        public int SampleCount;
        public int GcSampleCount;
        public double SumMs;
        public double Top1Ms;
        public double Top2Ms;
        public double Top3Ms;
        public readonly Dictionary<int, HistogramEntry> Histogram = new Dictionary<int, HistogramEntry>();

        public void Reset()
        {
            RealtimeSecond = int.MinValue;
            SampleCount = 0;
            GcSampleCount = 0;
            SumMs = 0;
            Top1Ms = 0;
            Top2Ms = 0;
            Top3Ms = 0;
            Histogram.Clear();
        }

        public void AddTop(double value)
        {
            if (value <= 0)
                return;

            if (value > Top1Ms)
            {
                Top3Ms = Top2Ms;
                Top2Ms = Top1Ms;
                Top1Ms = value;
                return;
            }

            if (value > Top2Ms)
            {
                Top3Ms = Top2Ms;
                Top2Ms = value;
                return;
            }

            if (value > Top3Ms)
                Top3Ms = value;
        }
    }
}

internal struct RollingProfilerSnapshot
{
    public double Avg1sMsPerFrame;
    public double Avg1sCallsPerFrame;
    public int Avg1sFrames;
    public RollingMaxSnapshot MaxSnapshot;
}

internal struct RollingMaxSnapshot
{
    public double RawMaxMs;
    public double SecondMaxMs;
    public double ThirdMaxMs;
    public double P95Ms;
    public double P99Ms;
    public int AboveP95Count;
    public int AboveP99Count;
    public double AvgAboveP95Ms;
    public double AvgAboveP99Ms;
    public long TotalSampleCount;
    public int WindowSampleCount;
    public int GcSampleCount;

    public static readonly RollingMaxSnapshot Empty = new RollingMaxSnapshot
    {
        RawMaxMs = 0,
        SecondMaxMs = 0,
        ThirdMaxMs = 0,
        P95Ms = 0,
        P99Ms = 0,
        AboveP95Count = 0,
        AboveP99Count = 0,
        AvgAboveP95Ms = 0,
        AvgAboveP99Ms = 0,
        TotalSampleCount = 0,
        WindowSampleCount = 0,
        GcSampleCount = 0
    };

    public static RollingMaxSnapshot Aggregate(IEnumerable<RollingMaxSnapshot> snapshots)
    {
        double top1 = 0;
        double top2 = 0;
        double top3 = 0;
        int above95 = 0;
        int above99 = 0;
        double above95Sum = 0;
        double above99Sum = 0;
        long total = 0;
        int window = 0;
        int gcSamples = 0;
        double p95Max = 0;
        double p99Max = 0;

        foreach (var s in snapshots)
        {
            AddTop(ref top1, ref top2, ref top3, s.RawMaxMs);
            AddTop(ref top1, ref top2, ref top3, s.SecondMaxMs);
            AddTop(ref top1, ref top2, ref top3, s.ThirdMaxMs);

            if (s.P95Ms > p95Max)
                p95Max = s.P95Ms;

            if (s.P99Ms > p99Max)
                p99Max = s.P99Ms;

            above95 += s.AboveP95Count;
            above99 += s.AboveP99Count;
            above95Sum += s.AvgAboveP95Ms * s.AboveP95Count;
            above99Sum += s.AvgAboveP99Ms * s.AboveP99Count;
            total += s.TotalSampleCount;
            window += s.WindowSampleCount;
            gcSamples += s.GcSampleCount;
        }

        return new RollingMaxSnapshot
        {
            RawMaxMs = top1,
            SecondMaxMs = top2,
            ThirdMaxMs = top3,
            P95Ms = p95Max,
            P99Ms = p99Max,
            AboveP95Count = above95,
            AboveP99Count = above99,
            AvgAboveP95Ms = above95 > 0 ? above95Sum / above95 : 0,
            AvgAboveP99Ms = above99 > 0 ? above99Sum / above99 : 0,
            TotalSampleCount = total,
            WindowSampleCount = window,
            GcSampleCount = gcSamples
        };
    }

    private static void AddTop(ref double top1, ref double top2, ref double top3, double value)
    {
        if (value <= 0)
            return;

        if (value > top1)
        {
            top3 = top2;
            top2 = top1;
            top1 = value;
            return;
        }

        if (value > top2)
        {
            top3 = top2;
            top2 = value;
            return;
        }

        if (value > top3)
            top3 = value;
    }
}