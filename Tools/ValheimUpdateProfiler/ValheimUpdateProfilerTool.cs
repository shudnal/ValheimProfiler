#nullable disable

using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace ValheimProfiler.Tools.ValheimUpdateProfiler;

internal sealed partial class ValheimUpdateProfilerTool : IProfilerTool
{
    internal const string ToolId = "ValheimUpdateProfiler";
    internal const string HarmonyId = ValheimProfilerPlugin.PluginGuid + ".ValheimUpdateProfiler";
    internal const string DisplayTitle = "Valheim Update Profiler";

    private const string TotalScope = "$total";
    private const string MeasuredScope = "$measured";
    private const string RemainderScope = "$remainder";
    private const string WearFullPassScope = "WearNTear.Full pass";
    private const string WearBatchScope = "WearNTear.Wear batch";
    private const string WearTotalScope = "WearNTear.Total";

    private static readonly HashSet<string> KnownVanillaScopes = new HashSet<string>(StringComparer.Ordinal)
    {
        "MonoUpdaters.FixedUpdate.ZSyncTransform",
        "MonoUpdaters.FixedUpdate.ZSyncAnimation",
        "MonoUpdaters.FixedUpdate.Floating",
        "MonoUpdaters.FixedUpdate.Ship",
        "MonoUpdaters.FixedUpdate.Fish",
        "MonoUpdaters.FixedUpdate.CharacterAnimEvent",
        "MonoUpdaters.FixedUpdate.BaseAI",
        "MonoUpdaters.FixedUpdate.Character",
        "MonoUpdaters.FixedUpdate.Aoe",
        "MonoUpdaters.FixedUpdate.EffectArea",
        "MonoUpdaters.FixedUpdate.RandomFlyingBird",
        "MonoUpdaters.FixedUpdate.MeleeWeaponTrail",
        "MonoUpdaters.Update.Smoke",
        "MonoUpdaters.Update.ZSFX",
        "MonoUpdaters.Update.VisEquipment",
        "MonoUpdaters.Update.FootStep",
        "MonoUpdaters.Update.InstanceRenderer",
        "MonoUpdaters.Update.WaterTrigger",
        "MonoUpdaters.Update.LightFlicker",
        "MonoUpdaters.Update.SmokeSpawner",
        "MonoUpdaters.Update.CraftingStation",
        "MonoUpdaters.LateUpdate.ZSyncTransform",
        "MonoUpdaters.LateUpdate.CharacterAnimEvent",
        "MonoUpdaters.LateUpdate.Heightmap",
        "MonoUpdaters.LateUpdate.ShipEffects",
        "MonoUpdaters.LateUpdate.Tail",
        "MonoUpdaters.LateUpdate.LineAttach"
    };

    private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
    private static int _mainThreadId;
    private static ValheimUpdateProfilerTool _instance;

    private readonly ValheimProfilerApp _app;
    private readonly ManualLogSource _logger;
    private readonly WindowManager _windows;
    private readonly ThemeManager _theme;
    private readonly Harmony _harmony;
    private readonly ProfilerWindow _window;
    private readonly object _sync = new object();
    private readonly Dictionary<ValheimUpdateScopeKey, ValheimUpdateScopeEntry> _entries = new Dictionary<ValheimUpdateScopeKey, ValheimUpdateScopeEntry>();
    private readonly Dictionary<ValheimUpdateKind, bool> _groupExpanded = new Dictionary<ValheimUpdateKind, bool>();

    private bool _profilingActive;
    private bool _statsFrozen;
    private float _frozenRealtime;
    private int _frozenFrame;
    private float _nextAnalyticsUpdate;
    private string _status = "Ready. Start profiling to instrument centralized Valheim update loops.";

    private ValheimUpdateView _view;
    private ValheimUpdateSortColumn _avgSortColumn;
    private ValheimUpdateSortColumn _maxSortColumn;
    private string _search = string.Empty;
    private Vector2 _scroll;
    private Vector2 _helpScroll;

    private GUISkin _styleSkin;
    private GUIStyle _labelStyle;
    private GUIStyle _headerStyle;
    private GUIStyle _activeHeaderStyle;
    private GUIStyle _groupStyle;
    private GUIStyle _compactButtonStyle;

    private int _wearCurrentInstances;
    private int _wearCurrentUpdatesPerFrame;
    private int _wearCurrentIndex;
    private bool _wearCycleActive;
    private float _wearCycleStartRealtime;
    private double _wearLastCycleDurationMs;
    private double _wearMaxCycleDurationMs;
    private double _wearLastCycleLagMs;
    private double _wearMaxCycleLagMs;

    internal ValheimUpdateProfilerTool(ValheimProfilerApp app)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _logger = app.Logger;
        _windows = app.Windows;
        _theme = app.Theme;
        _harmony = new Harmony(HarmonyId);
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        _instance = this;

        CreateFixedEntries();

        ValheimProfilerConfig config = app.Config;
        _avgSortColumn = ParseSortColumn(config.ValheimUpdateProfilerAvgSortColumn.Value, ValheimUpdateSortColumn.MsOneSecond);
        _maxSortColumn = ParseSortColumn(config.ValheimUpdateProfilerMaxSortColumn.Value, ValheimUpdateSortColumn.RawMax);

        foreach (ValheimUpdateKind kind in Enum.GetValues(typeof(ValheimUpdateKind)))
            _groupExpanded[kind] = true;

        var minimumSize = new Vector2(820f, 440f);
        Vector2 defaultSize = _windows.GetDefaultToolWindowSize(650f, minimumSize);
        _window = _windows.Register(new ProfilerWindow(
            "ValheimProfiler.ValheimUpdateProfiler",
            DisplayTitle,
            new Rect(ValheimProfilerConfig.DefaultValheimUpdateWindowPosition, defaultSize),
            minimumSize,
            resizable: true,
            requestedVisible: false,
            drawContents: DrawWindow,
            positionConfig: config.ValheimUpdateWindowPosition,
            sizeConfig: config.ValheimUpdateWindowSize));
    }

    string IProfilerTool.Id => ToolId;
    string IProfilerTool.DisplayName => "Valheim Updates";
    bool IProfilerTool.IsWindowVisible => IsWindowVisible;
    bool IProfilerTool.IsActive => _profilingActive;
    void IProfilerTool.ShowWindow() => ShowWindow();
    void IProfilerTool.ToggleWindow() => ToggleWindow();
    void IProfilerTool.Update() => Update();
    void IProfilerTool.Shutdown() => Shutdown();

    internal bool IsWindowVisible
    {
        get => _window.RequestedVisible;
        set => _window.RequestedVisible = value;
    }

    private int DisplayFrame => _statsFrozen ? _frozenFrame : Time.frameCount;
    private float DisplayRealtime => _statsFrozen ? _frozenRealtime : Time.realtimeSinceStartup;

    internal void ShowWindow()
    {
        IsWindowVisible = true;
        _app.ShowUi();
        _windows.BringToFront(_window);
    }

    internal void ToggleWindow()
    {
        IsWindowVisible = !IsWindowVisible;
        if (IsWindowVisible)
            ShowWindow();
    }

    internal void Update()
    {
        if (!_profilingActive)
            return;

        float now = Time.realtimeSinceStartup;
        if (now < _nextAnalyticsUpdate)
            return;

        ProcessAllStats(now);
        _nextAnalyticsUpdate = now + 0.25f;
    }

    internal void Shutdown()
    {
        if (_instance == this)
            _instance = null;

        _profilingActive = false;
        try
        {
            _harmony.UnpatchSelf();
        }
        catch
        {
        }

        IsWindowVisible = false;
    }

    private void CreateFixedEntries()
    {
        CreateEntry(ValheimUpdateKind.FixedUpdate, TotalScope, "Total phase", false, 0);
        CreateEntry(ValheimUpdateKind.FixedUpdate, MeasuredScope, "Measured scopes", false, 1);
        CreateEntry(ValheimUpdateKind.FixedUpdate, RemainderScope, "Unattributed remainder", false, 2);
        CreateEntry(ValheimUpdateKind.Update, TotalScope, "Total phase", false, 0);
        CreateEntry(ValheimUpdateKind.Update, MeasuredScope, "Measured scopes", false, 1);
        CreateEntry(ValheimUpdateKind.Update, RemainderScope, "Unattributed remainder", false, 2);
        CreateEntry(ValheimUpdateKind.LateUpdate, TotalScope, "Total phase", false, 0);
        CreateEntry(ValheimUpdateKind.LateUpdate, MeasuredScope, "Measured scopes", false, 1);
        CreateEntry(ValheimUpdateKind.LateUpdate, RemainderScope, "Unattributed remainder", false, 2);

        CreateEntry(ValheimUpdateKind.WearNTear, WearFullPassScope, "Full pass", true, 0);
        CreateEntry(ValheimUpdateKind.WearNTear, WearBatchScope, "Wear batch", true, 1);
        CreateEntry(ValheimUpdateKind.WearNTear, WearTotalScope, "Total", true, 2);

        foreach (ValheimUpdateScopeEntry entry in _entries.Values)
        {
            entry.RuntimeTypeName = entry.Kind == ValheimUpdateKind.WearNTear
                ? typeof(WearNTearUpdater).FullName
                : typeof(MonoUpdaters).FullName;
        }
    }

    private ValheimUpdateScopeEntry CreateEntry(
        ValheimUpdateKind kind,
        string scope,
        string displayName,
        bool itemsApplicable,
        int fixedOrder = 100)
    {
        var key = new ValheimUpdateScopeKey(kind, scope);
        var entry = new ValheimUpdateScopeEntry(kind, scope, displayName, itemsApplicable, fixedOrder);
        _entries[key] = entry;
        return entry;
    }

    private ValheimUpdateScopeEntry GetOrCreateScope(ValheimUpdateKind kind, string scope)
    {
        scope ??= string.Empty;
        var key = new ValheimUpdateScopeKey(kind, scope);
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out ValheimUpdateScopeEntry existing))
                return existing;

            bool isVanilla = IsKnownVanillaScope(kind, scope);
            ValheimUpdateScopeEntry created = CreateEntry(
                kind,
                scope,
                BuildLocalScopeDisplayName(kind, scope, isVanilla),
                true);
            return created;
        }
    }

    private void RecordScope(
        ValheimUpdateKind kind,
        string scope,
        int items,
        object firstItem,
        double elapsedMs,
        int frame,
        float now)
    {
        if (!_profilingActive)
            return;

        ValheimUpdateScopeEntry entry = GetOrCreateScope(kind, scope);
        ResolveEntrySource(entry, firstItem);
        entry.Add(elapsedMs, items, frame, now);
    }

    private void RecordTopLevel(
        ValheimUpdateKind kind,
        double totalMs,
        double measuredMs,
        double remainderMs,
        int frame,
        float now)
    {
        GetOrCreateScope(kind, TotalScope).Add(totalMs, 0, frame, now);
        GetOrCreateScope(kind, MeasuredScope).Add(measuredMs, 0, frame, now);
        GetOrCreateScope(kind, RemainderScope).Add(remainderMs, 0, frame, now);
    }

    private void RecordWear(
        WearTimingState state,
        WearNTearUpdater updater,
        double elapsedMs,
        int frame,
        float now)
    {
        string scope = state.FullPass ? WearFullPassScope : WearBatchScope;
        int items = state.FullPass ? state.InstanceCount : state.PlannedItems;
        ValheimUpdateScopeEntry phaseEntry = GetOrCreateScope(ValheimUpdateKind.WearNTear, scope);
        ResolveEntrySource(phaseEntry, state.FirstItem);
        phaseEntry.Add(elapsedMs, items, frame, now);

        ValheimUpdateScopeEntry totalEntry = GetOrCreateScope(ValheimUpdateKind.WearNTear, WearTotalScope);
        ResolveEntrySource(totalEntry, state.FirstItem);
        totalEntry.Add(elapsedMs, items, frame, now);

        lock (_sync)
        {
            _wearCurrentInstances = state.InstanceCount;
            _wearCurrentUpdatesPerFrame = updater?.m_updatesPerFrame ?? state.UpdatesPerFrame;
            _wearCurrentIndex = updater?.m_index ?? state.StartIndex;

            if (state.FullPass)
            {
                _wearCycleActive = false;
                return;
            }

            if (!_wearCycleActive || state.StartIndex == 0)
            {
                _wearCycleActive = true;
                _wearCycleStartRealtime = now - (float)(elapsedMs * 0.001);
            }

            if (updater != null && updater.m_index == 0)
            {
                double durationMs = Math.Max(0, (now - _wearCycleStartRealtime) * 1000.0);
                double lagMs = (state.TimeArgument - state.SleepUntilNext) * 1000.0;
                _wearLastCycleDurationMs = durationMs;
                _wearLastCycleLagMs = lagMs;
                if (durationMs > _wearMaxCycleDurationMs)
                    _wearMaxCycleDurationMs = durationMs;
                if (lagMs > _wearMaxCycleLagMs)
                    _wearMaxCycleLagMs = lagMs;
                _wearCycleActive = false;
            }
        }
    }

    private void ResetAllStats()
    {
        lock (_sync)
        {
            foreach (ValheimUpdateScopeEntry entry in _entries.Values)
                entry.Reset();

            _wearCurrentInstances = 0;
            _wearCurrentUpdatesPerFrame = 0;
            _wearCurrentIndex = 0;
            _wearCycleActive = false;
            _wearCycleStartRealtime = 0;
            _wearLastCycleDurationMs = 0;
            _wearMaxCycleDurationMs = 0;
            _wearLastCycleLagMs = 0;
            _wearMaxCycleLagMs = 0;
        }

        _statsFrozen = false;
    }

    private void ProcessAllStats(float now)
    {
        List<ValheimUpdateScopeEntry> entries;
        lock (_sync)
            entries = _entries.Values.ToList();

        for (int i = 0; i < entries.Count; i++)
            entries[i].Timing.ProcessBackgroundAnalytics(now);
    }

    private List<ValheimUpdateRowSnapshot> BuildRows(ValheimUpdateKind kind)
    {
        int frame = DisplayFrame;
        float now = DisplayRealtime;
        string search = _search?.Trim() ?? string.Empty;
        List<ValheimUpdateScopeEntry> entries;

        lock (_sync)
            entries = _entries.Values.Where(entry => entry.Kind == kind).ToList();

        var fixedRows = new List<ValheimUpdateRowSnapshot>();
        var dynamicRows = new List<ValheimUpdateRowSnapshot>();

        for (int i = 0; i < entries.Count; i++)
        {
            ValheimUpdateScopeEntry entry = entries[i];
            if (!MatchesSearch(entry, search))
                continue;

            ValheimUpdateRowSnapshot row = entry.Snapshot(frame, now);
            if (entry.FixedOrder < 100)
                fixedRows.Add(row);
            else
                dynamicRows.Add(row);
        }

        fixedRows.Sort((left, right) => left.Entry.FixedOrder.CompareTo(right.Entry.FixedOrder));
        dynamicRows.Sort(CompareRowsDescending);
        fixedRows.AddRange(dynamicRows);
        return fixedRows;
    }

    private int CompareRowsDescending(ValheimUpdateRowSnapshot left, ValheimUpdateRowSnapshot right)
    {
        ValheimUpdateSortColumn column = _view == ValheimUpdateView.OverOneSecond ? _avgSortColumn : _maxSortColumn;
        double leftValue = GetSortValue(left, column);
        double rightValue = GetSortValue(right, column);
        int value = rightValue.CompareTo(leftValue);
        return value != 0
            ? value
            : string.Compare(left.Entry.DisplayName, right.Entry.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    private static double GetSortValue(ValheimUpdateRowSnapshot row, ValheimUpdateSortColumn column)
    {
        return column switch
        {
            ValheimUpdateSortColumn.MsOneSecond => row.MsOneSecond,
            ValheimUpdateSortColumn.CallsOneSecond => row.CallsOneSecond,
            ValheimUpdateSortColumn.ItemsOneSecond => row.ItemsOneSecond,
            ValheimUpdateSortColumn.AverageBatchMs => row.AverageBatchMs,
            ValheimUpdateSortColumn.AverageItems => row.AverageItems,
            ValheimUpdateSortColumn.AverageMsPerItem => row.AverageMsPerItem,
            ValheimUpdateSortColumn.LastMs => row.LastMs,
            ValheimUpdateSortColumn.LastItems => row.LastItems,
            ValheimUpdateSortColumn.RawMax => row.MaxSnapshot.RawMaxMs,
            ValheimUpdateSortColumn.SecondMax => row.MaxSnapshot.SecondMaxMs,
            ValheimUpdateSortColumn.ThirdMax => row.MaxSnapshot.ThirdMaxMs,
            ValheimUpdateSortColumn.AboveP99 => row.MaxSnapshot.AboveP99Count,
            ValheimUpdateSortColumn.AvgAboveP99 => row.MaxSnapshot.AvgAboveP99Ms,
            ValheimUpdateSortColumn.P99 => row.MaxSnapshot.P99Ms,
            ValheimUpdateSortColumn.AboveP95 => row.MaxSnapshot.AboveP95Count,
            ValheimUpdateSortColumn.AvgAboveP95 => row.MaxSnapshot.AvgAboveP95Ms,
            ValheimUpdateSortColumn.P95 => row.MaxSnapshot.P95Ms,
            ValheimUpdateSortColumn.MaxItems => row.MaxItems,
            _ => 0
        };
    }

    private static bool MatchesSearch(ValheimUpdateScopeEntry entry, string search)
    {
        if (string.IsNullOrEmpty(search))
            return true;

        return Contains(entry.DisplayName, search) ||
               Contains(entry.Scope, search) ||
               Contains(entry.RuntimeTypeName, search);
    }

    private static bool Contains(string value, string search) =>
        !string.IsNullOrEmpty(value) && value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;

    private void ResolveEntrySource(ValheimUpdateScopeEntry entry, object firstItem)
    {
        if (entry == null || firstItem == null || !string.IsNullOrEmpty(entry.RuntimeTypeName))
            return;

        try
        {
            Type type = firstItem.GetType();
            entry.RuntimeTypeName = type.FullName ?? type.Name;
        }
        catch
        {
        }
    }

    private static bool IsKnownVanillaScope(ValheimUpdateKind kind, string scope)
    {
        if (kind == ValheimUpdateKind.WearNTear)
            return true;

        return !string.IsNullOrEmpty(scope) && KnownVanillaScopes.Contains(scope);
    }

    private static string BuildLocalScopeDisplayName(ValheimUpdateKind kind, string scope, bool isVanilla)
    {
        string value = scope ?? string.Empty;
        string[] prefixes = kind switch
        {
            ValheimUpdateKind.FixedUpdate => new[] { "MonoUpdaters.FixedUpdate." },
            ValheimUpdateKind.Update => new[] { "MonoUpdaters.Update." },
            ValheimUpdateKind.LateUpdate => new[] { "MonoUpdaters.LateUpdate." },
            ValheimUpdateKind.AI => new[] { "MonoUpdaters.UpdateAI.", "MonoUpdaters.FixedUpdate." },
            ValheimUpdateKind.WearNTear => new[] { "WearNTear." },
            _ => Array.Empty<string>()
        };

        for (int i = 0; i < prefixes.Length; i++)
        {
            if (value.StartsWith(prefixes[i], StringComparison.Ordinal))
            {
                value = value.Substring(prefixes[i].Length);
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(value))
            value = scope ?? "Unknown";

        return isVanilla ? value : "Mod | " + value;
    }

    private static ValheimUpdateSortColumn ParseSortColumn(string value, ValheimUpdateSortColumn fallback) =>
        Enum.TryParse(value, ignoreCase: true, out ValheimUpdateSortColumn parsed) ? parsed : fallback;

    private void SetSortColumn(ValheimUpdateSortColumn column)
    {
        if (_view == ValheimUpdateView.OverOneSecond)
        {
            _avgSortColumn = column;
            _app.Config.ValheimUpdateProfilerAvgSortColumn.Value = column.ToString();
        }
        else
        {
            _maxSortColumn = column;
            _app.Config.ValheimUpdateProfilerMaxSortColumn.Value = column.ToString();
        }
    }

    private bool IsSortColumn(ValheimUpdateSortColumn column) =>
        (_view == ValheimUpdateView.OverOneSecond ? _avgSortColumn : _maxSortColumn) == column;
}
