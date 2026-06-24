#nullable disable

using System;

namespace ValheimProfiler.Core.Profiling;

internal sealed class LifetimeProfilerStat
{
    private const double HistogramMinMs = 0.0005;
    private const double HistogramBase = 1.15;
    private const int HistogramBinCount = 256;

    private readonly object _sync = new object();
    private readonly long[] _histogram = new long[HistogramBinCount];

    private long _calls;
    private double _totalMs;
    private double _maxMs;
    private double _lastMs;
    private float _firstCallAtSeconds;
    private float _lastCallAtSeconds;

    internal void Add(double elapsedMs, float elapsedSinceStart)
    {
        if (elapsedMs < 0)
            return;

        lock (_sync)
        {
            if (_calls == 0)
                _firstCallAtSeconds = (elapsedSinceStart < 0f ? 0f : elapsedSinceStart);

            _calls++;
            _totalMs += elapsedMs;
            _lastMs = elapsedMs;
            _lastCallAtSeconds = (elapsedSinceStart < 0f ? 0f : elapsedSinceStart);

            if (elapsedMs > _maxMs)
                _maxMs = elapsedMs;

            _histogram[MsToHistogramBin(elapsedMs)]++;
        }
    }

    internal LifetimeProfilerSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            if (_calls <= 0)
                return LifetimeProfilerSnapshot.Empty;

            return new LifetimeProfilerSnapshot
            {
                Calls = _calls,
                TotalMs = _totalMs,
                AverageMs = _totalMs / _calls,
                MaxMs = _maxMs,
                LastMs = _lastMs,
                P95Ms = GetPercentile(0.95),
                P99Ms = GetPercentile(0.99),
                FirstCallAtSeconds = _firstCallAtSeconds,
                LastCallAtSeconds = _lastCallAtSeconds
            };
        }
    }

    internal void Reset()
    {
        lock (_sync)
        {
            _calls = 0;
            _totalMs = 0;
            _maxMs = 0;
            _lastMs = 0;
            _firstCallAtSeconds = 0;
            _lastCallAtSeconds = 0;
            Array.Clear(_histogram, 0, _histogram.Length);
        }
    }

    private double GetPercentile(double percentile)
    {
        long target = Math.Max(1L, (long)Math.Ceiling(_calls * percentile));
        long cumulative = 0;

        for (int i = 0; i < _histogram.Length; i++)
        {
            cumulative += _histogram[i];
            if (cumulative >= target)
                return HistogramBinToMs(i);
        }

        return _maxMs;
    }

    private static int MsToHistogramBin(double ms)
    {
        if (ms <= HistogramMinMs)
            return 0;

        double raw = Math.Log(ms / HistogramMinMs, HistogramBase);
        if (double.IsNaN(raw) || double.IsInfinity(raw))
            return HistogramBinCount - 1;

        return Math.Max(0, Math.Min(HistogramBinCount - 1, (int)Math.Floor(raw)));
    }

    private static double HistogramBinToMs(int bin)
    {
        if (bin <= 0)
            return HistogramMinMs;

        return HistogramMinMs * Math.Pow(HistogramBase, bin + 1);
    }
}

internal struct LifetimeProfilerSnapshot
{
    internal static readonly LifetimeProfilerSnapshot Empty = new LifetimeProfilerSnapshot();

    internal long Calls;
    internal double TotalMs;
    internal double AverageMs;
    internal double MaxMs;
    internal double LastMs;
    internal double P95Ms;
    internal double P99Ms;
    internal float FirstCallAtSeconds;
    internal float LastCallAtSeconds;
}
