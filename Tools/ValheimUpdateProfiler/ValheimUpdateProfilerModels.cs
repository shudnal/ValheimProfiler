#nullable disable

using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimProfiler.Tools.ValheimUpdateProfiler;

internal enum ValheimUpdateKind
{
    FixedUpdate,
    Update,
    LateUpdate,
    AI,
    WearNTear
}

internal enum ValheimUpdateView
{
    OverOneSecond,
    MaxOverSixtySeconds
}

internal enum ValheimUpdateSortColumn
{
    MsOneSecond,
    CallsOneSecond,
    ItemsOneSecond,
    AverageBatchMs,
    AverageItems,
    AverageMsPerItem,
    LastMs,
    LastItems,
    RawMax,
    SecondMax,
    ThirdMax,
    AboveP99,
    AvgAboveP99,
    P99,
    AboveP95,
    AvgAboveP95,
    P95,
    MaxItems
}

internal readonly struct ValheimUpdateScopeKey : IEquatable<ValheimUpdateScopeKey>
{
    internal ValheimUpdateScopeKey(ValheimUpdateKind kind, string scope)
    {
        Kind = kind;
        Scope = scope ?? string.Empty;
    }

    internal ValheimUpdateKind Kind { get; }
    internal string Scope { get; }

    public bool Equals(ValheimUpdateScopeKey other) =>
        Kind == other.Kind && string.Equals(Scope, other.Scope, StringComparison.Ordinal);

    public override bool Equals(object obj) => obj is ValheimUpdateScopeKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            return ((int)Kind * 397) ^ StringComparer.Ordinal.GetHashCode(Scope ?? string.Empty);
        }
    }
}

internal sealed class ValheimUpdateScopeEntry
{
    internal ValheimUpdateScopeEntry(
        ValheimUpdateKind kind,
        string scope,
        string displayName,
        bool itemsApplicable,
        int fixedOrder = 100)
    {
        Kind = kind;
        Scope = scope ?? string.Empty;
        DisplayName = displayName ?? Scope;
        ItemsApplicable = itemsApplicable;
        FixedOrder = fixedOrder;
    }

    internal ValheimUpdateKind Kind { get; }
    internal string Scope { get; }
    internal string DisplayName { get; }
    internal bool ItemsApplicable { get; }
    internal int FixedOrder { get; }
    internal string RuntimeTypeName { get; set; } = string.Empty;
    internal RollingProfilerStat Timing { get; } = new RollingProfilerStat();
    internal RollingItemStat Items { get; } = new RollingItemStat();
    internal double LastMs { get; private set; }
    internal int LastItems { get; private set; }
    internal float LastSeenRealtime { get; private set; }

    internal void Add(double elapsedMs, int items, int frame, float now)
    {
        Timing.Add(elapsedMs, frame, now, gcSample: false);
        LastMs = elapsedMs;
        LastSeenRealtime = now;

        if (ItemsApplicable)
        {
            LastItems = Math.Max(0, items);
            Items.Add(LastItems, now);
        }
    }

    internal ValheimUpdateRowSnapshot Snapshot(int frame, float now)
    {
        Timing.ProcessBackgroundAnalytics(now);
        RollingProfilerSnapshot timing = Timing.GetSnapshot(frame, now);
        RollingItemSnapshot items = ItemsApplicable ? Items.GetSnapshot(now) : default;
        double callsOneSecond = timing.Avg1sCallsPerFrame * timing.Avg1sFrames;
        double msOneSecond = timing.Avg1sMsPerFrame * timing.Avg1sFrames;

        return new ValheimUpdateRowSnapshot
        {
            Entry = this,
            MsOneSecond = msOneSecond,
            CallsOneSecond = callsOneSecond,
            ItemsOneSecond = items.ItemsOneSecond,
            AverageBatchMs = callsOneSecond > 0 ? msOneSecond / callsOneSecond : 0,
            AverageItems = items.CallsOneSecond > 0 ? (double)items.ItemsOneSecond / items.CallsOneSecond : 0,
            AverageMsPerItem = items.ItemsOneSecond > 0 ? msOneSecond / items.ItemsOneSecond : 0,
            LastMs = LastMs,
            LastItems = LastItems,
            MaxItems = items.MaxItemsSixtySeconds,
            MaxSnapshot = timing.MaxSnapshot
        };
    }

    internal void Reset()
    {
        Timing.Reset();
        Items.Reset();
        LastMs = 0;
        LastItems = 0;
        LastSeenRealtime = 0;
    }
}

internal struct ValheimUpdateRowSnapshot
{
    internal ValheimUpdateScopeEntry Entry;
    internal double MsOneSecond;
    internal double CallsOneSecond;
    internal long ItemsOneSecond;
    internal double AverageBatchMs;
    internal double AverageItems;
    internal double AverageMsPerItem;
    internal double LastMs;
    internal int LastItems;
    internal int MaxItems;
    internal RollingMaxSnapshot MaxSnapshot;
}

internal sealed class RollingItemStat
{
    private const int MaxWindowSeconds = 60;
    private readonly object _sync = new object();
    private readonly Queue<ItemSample> _oneSecondSamples = new Queue<ItemSample>(256);
    private readonly ItemSecondBucket[] _maxBuckets = new ItemSecondBucket[MaxWindowSeconds];
    private long _itemsOneSecond;
    private int _callsOneSecond;

    internal RollingItemStat()
    {
        for (int i = 0; i < _maxBuckets.Length; i++)
            _maxBuckets[i].Second = int.MinValue;
    }

    internal void Add(int items, float now)
    {
        lock (_sync)
        {
            TrimOneSecond(now);
            _oneSecondSamples.Enqueue(new ItemSample(now, items));
            _itemsOneSecond += items;
            _callsOneSecond++;

            int second = Mathf.FloorToInt(now);
            int index = PositiveMod(second, MaxWindowSeconds);
            ref ItemSecondBucket bucket = ref _maxBuckets[index];
            if (bucket.Second != second)
            {
                bucket.Second = second;
                bucket.MaxItems = items;
            }
            else if (items > bucket.MaxItems)
            {
                bucket.MaxItems = items;
            }
        }
    }

    internal RollingItemSnapshot GetSnapshot(float now)
    {
        lock (_sync)
        {
            TrimOneSecond(now);
            int currentSecond = Mathf.FloorToInt(now);
            int max = 0;

            for (int i = 0; i < _maxBuckets.Length; i++)
            {
                ItemSecondBucket bucket = _maxBuckets[i];
                if (bucket.Second == int.MinValue || currentSecond - bucket.Second >= MaxWindowSeconds)
                    continue;
                if (bucket.MaxItems > max)
                    max = bucket.MaxItems;
            }

            return new RollingItemSnapshot
            {
                ItemsOneSecond = _itemsOneSecond,
                CallsOneSecond = _callsOneSecond,
                MaxItemsSixtySeconds = max
            };
        }
    }

    internal void Reset()
    {
        lock (_sync)
        {
            _oneSecondSamples.Clear();
            _itemsOneSecond = 0;
            _callsOneSecond = 0;
            for (int i = 0; i < _maxBuckets.Length; i++)
            {
                _maxBuckets[i].Second = int.MinValue;
                _maxBuckets[i].MaxItems = 0;
            }
        }
    }

    private void TrimOneSecond(float now)
    {
        while (_oneSecondSamples.Count > 0 && now - _oneSecondSamples.Peek().Time > 1f)
        {
            ItemSample sample = _oneSecondSamples.Dequeue();
            _itemsOneSecond -= sample.Items;
            _callsOneSecond--;
        }

        if (_itemsOneSecond < 0)
            _itemsOneSecond = 0;
        if (_callsOneSecond < 0)
            _callsOneSecond = 0;
    }

    private static int PositiveMod(int value, int divisor)
    {
        int result = value % divisor;
        return result < 0 ? result + divisor : result;
    }

    private readonly struct ItemSample
    {
        internal ItemSample(float time, int items)
        {
            Time = time;
            Items = items;
        }

        internal float Time { get; }
        internal int Items { get; }
    }

    private struct ItemSecondBucket
    {
        internal int Second;
        internal int MaxItems;
    }
}

internal struct RollingItemSnapshot
{
    internal long ItemsOneSecond;
    internal int CallsOneSecond;
    internal int MaxItemsSixtySeconds;
}
