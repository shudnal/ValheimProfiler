#nullable disable

using BepInEx.Configuration;
using UnityEngine;

namespace ValheimProfiler.Configuration;

internal sealed class ValheimProfilerConfig
{
    internal static readonly Vector2 DefaultLauncherPosition = new(-1f, 9f);
    internal static readonly Vector2 DefaultPatchWindowPosition = new(80f, 80f);
    internal static readonly Vector2 DefaultPatchWindowSize = new(-1f, 600f);
    internal static readonly Vector2 DefaultTranspilerWindowPosition = new(120f, 120f);
    internal static readonly Vector2 DefaultGeneralMethodWindowPosition = new(100f, 90f);
    internal static readonly Vector2 DefaultGeneralMethodWindowSize = new(-1f, 650f);
    internal static readonly Vector2 DefaultGeneralCallTreeWindowPosition = new(150f, 120f);
    internal static readonly Vector2 DefaultGeneralCallTreeWindowSize = new(-1f, 650f);
    internal static readonly Vector2 DefaultMonoBehaviourWindowPosition = new(110f, 90f);
    internal static readonly Vector2 DefaultMonoBehaviourCallWindowPosition = new(140f, 110f);
    internal static readonly Vector2 DefaultValheimUpdateWindowPosition = new(155f, 120f);
    internal static readonly Vector2 DefaultLogMonitorWindowPosition = new(170f, 130f);
    internal static readonly Vector2 DefaultServerLogMonitorWindowPosition = new(200f, 150f);
    internal static readonly Vector2 DefaultNetworkProfilerWindowPosition = new(230f, 170f);
    internal static readonly Vector2 DefaultTranspilerWindowSize = new(-1f, 520f);
    internal static readonly Vector2 DefaultMonoBehaviourWindowSize = new(-1f, 650f);
    internal static readonly Vector2 DefaultMonoBehaviourCallWindowSize = new(-1f, 660f);
    internal static readonly Vector2 DefaultValheimUpdateWindowSize = new(-1f, 650f);
    internal static readonly Vector2 DefaultLogMonitorWindowSize = new(-1f, 680f);
    internal static readonly Vector2 DefaultServerLogMonitorWindowSize = new(-1f, 680f);
    internal static readonly Vector2 DefaultNetworkProfilerWindowSize = new(-1f, 700f);

    internal ValheimProfilerConfig(ConfigFile config)
    {
        ConfigFile = config;

        LoggingEnabled = config.Bind("General", "Logging enabled", false,
            "Add compact method failures and grouped safety skips for Patch Profiler and General Method Profiler. Each operation displays at most 10 failure identities and 8 skip groups; installation and cleanup failures share one method line. Full method details and exceptions go to rotating per-operation .log files in the profiler config directory. Disabled output contains aggregate counts and brief session errors. Uncertain recovery always emits one mandatory restart warning. The setting is captured when an operation starts. Safety checks, Skipped methods and the separate Write crash trace option are independent of this setting.");

        FrameBudgetTargetFps = config.Bind(
            "General",
            "Frame budget target FPS",
            60,
            new ConfigDescription(
                "Target FPS used to normalize the Frame budget views in Patch Profiler and General Method Profiler. The rolling one-second measured time and call count are divided by this fixed target, not by the game's current FPS. Changing it does not reset captured statistics.",
                new AcceptableValueRange<int>(1, 1000)));

        GlobalHotkey = config.Bind("General", "Toggle UI", new KeyboardShortcut(KeyCode.F7),
            "Show or hide the Valheim Profiler user interface without stopping active profilers.");
        PatchProfilerHotkey = config.Bind("General", "Toggle Patch Profiler", new KeyboardShortcut(KeyCode.F8),
            "Show or hide the Patch Profiler window.");
        GeneralMethodProfilerHotkey = config.Bind("General", "Toggle General Method Profiler", new KeyboardShortcut(KeyCode.None),
            "Show or hide the General Method Profiler window. No default shortcut is assigned.");
        MonoBehaviourProfilerHotkey = config.Bind("General", "Toggle MonoBehaviour Frame Profiler", new KeyboardShortcut(KeyCode.None),
            "Show or hide the MonoBehaviour Frame Profiler window. No default shortcut is assigned.");
        MonoBehaviourCallProfilerHotkey = config.Bind("General", "Toggle MonoBehaviour Call Profiler", new KeyboardShortcut(KeyCode.None),
            "Show or hide the MonoBehaviour Call Profiler window. No default shortcut is assigned.");
        LogMonitorHotkey = config.Bind("General", "Toggle Client Log Monitor", new KeyboardShortcut(KeyCode.F9),
            "Show or hide the client Log Monitor window.");
        ServerLogMonitorHotkey = config.Bind("General", "Toggle Server Log Monitor", new KeyboardShortcut(KeyCode.F10),
            "Show or hide the remote dedicated-server Log Monitor window.");
        ValheimUpdateProfilerHotkey = config.Bind("General", "Toggle Valheim Update Profiler", new KeyboardShortcut(KeyCode.None),
            "Show or hide the Valheim Update Profiler window. No default shortcut is assigned.");
        NetworkProfilerHotkey = config.Bind("General", "Toggle Network Profiler", new KeyboardShortcut(KeyCode.F11),
            "Show or hide the dedicated-server Network Profiler window.");
        BlockGameInput = config.Bind("Valheim", "Block game input", true,
            "Block all Valheim gameplay input while at least one profiler window is visible. IMGUI input and profiler hotkeys remain available.");
        BlockMouseInput = config.Bind("Valheim", "Block mouse input", false,
            "Block Valheim mouse-driven gameplay and background UI interactions while profiler windows are visible, while leaving keyboard/controller input available. Full input blocking takes precedence.");
        PauseGame = config.Bind("Valheim", "Pause game", false,
            "Pause the game, when possible, while profiler windows are visible.");
        UseValheimGuiScale = config.Bind("Interface", "Use Valheim GUI scaling", true,
            "Multiply the profiler scale by Valheim's Accessibility - Scale GUI setting.");
        UiScale = config.Bind("Interface", "Scale", 1.0f,
            new ConfigDescription("Additional profiler UI scale.", new AcceptableValueRange<float>(0.6f, 2.0f)));
        FontSize = config.Bind("Interface", "Font size", 12,
            new ConfigDescription("Base IMGUI font size.", new AcceptableValueRange<int>(9, 28)));

        MonoBehaviourIncludeValheimProfilerCallbacks = config.Bind(
            "MonoBehaviour Frame Profiler",
            "Include Valheim Profiler callbacks",
            true,
            "Include Valheim Profiler plugin callbacks in MonoBehaviour discovery so the profiler can profile itself. Refresh the behaviour list after changing this setting.");

        PatchProfilerAvgSortColumn = config.Bind(
            "Patch Profiler",
            "Frame budget sort column",
            "AvgMsPerFrame",
            "Descending sort column restored when the Patch Profiler opens the Frame budget view.");
        PatchProfilerMaxSortColumn = config.Bind(
            "Patch Profiler",
            "Max over 60 sec sort column",
            "ThirdMax",
            "Descending sort column restored when the Patch Profiler opens the Max over 60 sec view.");
        PatchProfilerIgnoreGcAllocations = config.Bind(
            "Patch Profiler",
            "Ignore gc allocations",
            false,
            "Discard an entire measured sample when the Patch Profiler GC heuristic detects a garbage collection during that sample. Discarded samples do not affect calls, averages, maxima, percentiles, sample counts, or GC sample counts.");
        PatchProfilerExcludeMeasuredNestedCalls = config.Bind(
            "Patch Profiler",
            "Exclude measured nested calls",
            false,
            "Subtract nested calls that are also measured by Patch Profiler, plus Patch Profiler timing-bookkeeping performed around those nested measurements, from the parent sample. Unmeasured nested calls remain included. Changing this option resets statistics.");
        PatchProfilerGradualProfilingStart = config.Bind(
            "Patch Profiler",
            "Gradual profiling start",
            true,
            "Spread Patch Profiler Harmony installation and removal across frames using a 400 ms / 2560 operation budget. Disable to process the complete transition in one Unity frame.");
        PatchProfilerCrashTrace = config.Bind(
            "Patch Profiler diagnostics",
            "Write crash trace",
            false,
            "Disabled by default. Write and flush each discovery/patch installation step to a separate file to identify native crashes. Enable only for an investigation and disable afterward; cache resets and struct safety remain active independently.");
        PatchProfilerInstrumentationLimit = config.Bind(
            "Patch Profiler diagnostics",
            "Limit instrumented methods",
            0,
            new ConfigDescription(
                "0 = unlimited. Methods are always sorted deterministically. Specify 1..N to stop installing additional methods after N candidates, including safety skips. This intentionally produces partial profiling data.",
                new AcceptableValueRange<int>(0, 100000)));
        GeneralMethodProfilerAvgSortColumn = config.Bind("General Method Profiler", "Frame budget sort column", "AvgMsPerFrame",
            "Restored sort column for Frame budget. Text columns (Mod, Class, Method) sort ascending; numeric columns sort descending.");
        GeneralMethodProfilerMaxSortColumn = config.Bind("General Method Profiler", "Max over 60 sec sort column", "ThirdMax",
            "Restored sort column. Text columns (Mod, Class, Method) sort ascending; numeric columns sort descending.");
        GeneralMethodCallTreeSortColumn = config.Bind("General Method Profiler", "Call tree sort column", "Inclusive",
            "Descending context sort: Inclusive, Self, Calls, RawMax or Name.");
        GeneralMethodProfilerIgnoreGcAllocations = config.Bind("General Method Profiler", "Ignore gc allocations", false,
            "Discard GC-associated samples before any method or context statistics are updated. Uses the existing GC-collection heuristic, not allocation-byte tracking.");
        GeneralMethodProfilerExcludeMeasuredNestedCalls = config.Bind("General Method Profiler", "Exclude measured nested calls", false,
            "Subtract nested calls that are also measured by General Method Profiler, plus profiler timing-bookkeeping performed around those nested measurements, from method and context timing statistics. Unmeasured nested calls remain included. Changing this option resets statistics.");
        GeneralMethodProfilerGradualProfilingStart = config.Bind("General Method Profiler", "Gradual profiling start", true,
            "Spread General Method Profiler Harmony installation and removal across frames using a 400 ms / 2560 operation budget. Disable to process the complete transition in one Unity frame.");
        GeneralMethodProfilerGroupByMod = config.Bind("General Method Profiler", "Group by mod", true,
            "Group measured methods by their owning BepInEx plugin assembly.");
        GeneralMethodProfilerView = config.Bind("General Method Profiler", "Profiler view", "MaxOver60Sec",
            "Restored timing view: FrameBudget or MaxOver60Sec. Legacy Avg1s is treated as FrameBudget.");
        GeneralMethodSelectionSearch = config.Bind("General Method Profiler", "Selection search", string.Empty,
            "Restored mod/module/class/method selection filter. Does not change saved selection.");
        GeneralMethodProfilerSearch = config.Bind("General Method Profiler", "Profiler search", string.Empty,
            "Restored method table filter. Does not change instrumentation.");
        GeneralMethodSelectedOnly = config.Bind("General Method Profiler", "Selected only", false,
            "Show only selected methods in the selection view.");
        GeneralMethodShowUnavailable = config.Bind("General Method Profiler", "Show unavailable", false,
            "Show excluded methods with their safety/discovery reasons.");
        GeneralMethodMaxTreeNodes = config.Bind("General Method Profiler", "Maximum call tree nodes", 4096,
            new ConfigDescription("Maximum retained full-path call contexts. New paths beyond this cap keep method aggregate measurements but no tree nodes. Applied on next Start.",
                new AcceptableValueRange<int>(256, 32768)));
        GeneralMethodMaxCallDepth = config.Bind("General Method Profiler", "Maximum call depth", 128,
            new ConfigDescription("Maximum simultaneous instrumented invocations. Deeper calls are omitted from both aggregates and the call tree and counted in diagnostics. Applied on next Start.",
                new AcceptableValueRange<int>(16, 1024)));

        MonoBehaviourProfilerAvgSortColumn = config.Bind(
            "MonoBehaviour Frame Profiler",
            "Over 1 sec sort column",
            "AvgMsPerFrame",
            "Descending sort column restored when the MonoBehaviour Frame Profiler opens the Over 1 sec view.");
        MonoBehaviourProfilerMaxSortColumn = config.Bind(
            "MonoBehaviour Frame Profiler",
            "Max over 60 sec sort column",
            "ThirdMax",
            "Descending sort column restored when the MonoBehaviour Frame Profiler opens the Max over 60 sec view.");
        MonoBehaviourProfilerIgnoreGcAllocations = config.Bind(
            "MonoBehaviour Frame Profiler",
            "Ignore gc allocations",
            false,
            "Discard an entire measured sample when the MonoBehaviour Frame Profiler GC heuristic detects a garbage collection during that sample. Discarded samples do not affect calls, averages, maxima, percentiles, sample counts, or GC sample counts.");

        MonoBehaviourCallIncludeValheimProfilerCallbacks = config.Bind(
            "MonoBehaviour Call Profiler",
            "Include Valheim Profiler callbacks",
            true,
            "Include Valheim Profiler plugin lifecycle and declared methods in MonoBehaviour Call Profiler discovery so the profiler can profile itself. Refresh the call list after changing this setting.");
        MonoBehaviourCallProfilerSortColumn = config.Bind(
            "MonoBehaviour Call Profiler",
            "Sort column",
            "Total",
            "Descending sort column restored when the MonoBehaviour Call Profiler opens.");

        ValheimUpdateProfilerAvgSortColumn = config.Bind(
            "Valheim Update Profiler",
            "Over 1 sec sort column",
            "MsOneSecond",
            "Descending sort column restored when the Valheim Update Profiler opens the Over 1 sec view.");
        ValheimUpdateProfilerMaxSortColumn = config.Bind(
            "Valheim Update Profiler",
            "Max over 60 sec sort column",
            "RawMax",
            "Descending sort column restored when the Valheim Update Profiler opens the Max over 60 sec view.");

        LogMonitorMaxEntries = config.Bind(
            "Log Monitor",
            "Maximum captured entries",
            5000,
            new ConfigDescription(
                "Maximum number of raw client log entries kept in memory. Older entries are discarded in batches.",
                new AcceptableValueRange<int>(500, 50000)));
        LogMonitorMaxIssueGroups = config.Bind(
            "Log Monitor",
            "Maximum issue groups",
            1000,
            new ConfigDescription(
                "Maximum number of distinct Warning, Error and Fatal groups kept since the last Clear.",
                new AcceptableValueRange<int>(100, 10000)));
        LogMonitorIssueSortColumn = config.Bind(
            "Log Monitor",
            "Issue sort column",
            "Count",
            "Descending sort column restored when the Log Monitor opens the Issues view.");
        LogMonitorHistoryPageEntries = config.Bind(
            "Log Monitor",
            "History page entries",
            1000,
            new ConfigDescription(
                "Maximum number of parsed LogOutput.log entries loaded by one Load older request.",
                new AcceptableValueRange<int>(100, 10000)));
        LogMonitorCopyMetadata = config.Bind(
            "Log Monitor",
            "Copy metadata",
            false,
            "Include timestamp, thread, scene and history metadata when copying client log rows. Disabled output stays close to BepInEx LogOutput.log format.");

        ServerLogRecentEntries = config.Bind(
            "Server Log Monitor",
            "Recent entries",
            5000,
            new ConfigDescription(
                "Maximum live server log entries retained for remote subscribers.",
                new AcceptableValueRange<int>(500, 50000)));
        ServerLogInitialEntries = config.Bind(
            "Server Log Monitor",
            "Initial snapshot entries",
            1000,
            new ConfigDescription(
                "Maximum recent entries sent when an admin subscribes or a live gap requires resynchronization.",
                new AcceptableValueRange<int>(100, 10000)));
        ServerLogHistoryPageEntries = config.Bind(
            "Server Log Monitor",
            "History page entries",
            1000,
            new ConfigDescription(
                "Maximum historical entries returned by one remote Load older request.",
                new AcceptableValueRange<int>(100, 10000)));
        ServerLogClientMaxEntries = config.Bind(
            "Server Log Monitor",
            "Client live entry limit",
            5000,
            new ConfigDescription(
                "Maximum live server entries retained by the client window. Manually loaded history is stored beyond this limit.",
                new AcceptableValueRange<int>(500, 50000)));
        ServerLogMaxIssueGroups = config.Bind(
            "Server Log Monitor",
            "Maximum issue groups",
            1000,
            new ConfigDescription(
                "Maximum number of distinct server Warning, Error and Fatal groups retained in the client window.",
                new AcceptableValueRange<int>(100, 10000)));
        ServerLogIssueSortColumn = config.Bind(
            "Server Log Monitor",
            "Issue sort column",
            "Count",
            "Descending sort column restored when the Server Log Monitor opens the Issues view.");
        ServerLogCopyMetadata = config.Bind(
            "Server Log Monitor",
            "Copy metadata",
            false,
            "Include timestamp, thread, scene, sequence and history metadata when copying server log rows. Disabled output stays close to BepInEx LogOutput.log format.");

        NetworkRpcSortColumn = config.Bind("Network Profiler", "RPC sort column", "HandlerMs",
            "Descending sort column restored for the RPC table.");
        NetworkZdoPrefabSortColumn = config.Bind("Network Profiler", "ZDO prefab sort column", "SentBytes",
            "Descending sort column restored for the ZDO By prefab table.");
        NetworkZdoInstanceSortColumn = config.Bind("Network Profiler", "ZDO instance sort column", "SentBytes",
            "Descending sort column restored for the Top ZDOs table.");
        NetworkZdoKeySortColumn = config.Bind("Network Profiler", "ZDO key sort column", "Mutations",
            "Descending sort column restored for the ZDO Keys table.");
        NetworkPeerSortColumn = config.Bind("Network Profiler", "Peer sort column", "SendQueue",
            "Descending sort column restored for the Peers / Transport table.");
        NetworkRoutingErrorSortColumn = config.Bind("Network Profiler", "Routing error sort column", "Count",
            "Descending sort column restored for the Routing errors table.");

        WindowBackground = config.Bind("Style", "Window background", new Color(0.16f, 0.16f, 0.16f, 1f),
            "Background color of profiler windows.");
        WindowBorder = config.Bind("Style", "Window border", new Color(0.48f, 0.48f, 0.48f, 1f),
            "One-pixel border color used to separate overlapping profiler windows.");
        EntryBackground = config.Bind("Style", "Entry background", new Color(0.12f, 0.12f, 0.12f, 0.95f),
            "Background color used by boxes and grouped sections.");
        TextColor = config.Bind("Style", "Text color", new Color(0.92f, 0.92f, 0.92f, 1f),
            "Normal text color.");
        HeaderTextColor = config.Bind("Style", "Header text color", Color.white,
            "Header and emphasized text color.");
        ButtonBackground = config.Bind("Style", "Button background", new Color(0.24f, 0.24f, 0.24f, 1f),
            "Normal button background color.");
        ButtonTextColor = config.Bind("Style", "Button text color", Color.white,
            "Button text color.");
        AccentColor = config.Bind("Style", "Accent color", new Color(0.38f, 0.55f, 0.72f, 1f),
            "Accent used for active controls and important actions.");

        LauncherPosition = config.Bind("Layout", "Launcher position", DefaultLauncherPosition,
            "Logical position of the launcher. A negative X centers it on first use.");
        PatchWindowPosition = config.Bind("Layout", "Patch Profiler position", DefaultPatchWindowPosition,
            "Logical position of the Patch Profiler window.");
        PatchWindowSize = config.Bind("Layout", "Patch Profiler size", DefaultPatchWindowSize,
            "Logical size of the Patch Profiler window. A non-positive width uses 75% of the logical screen width.");
        TranspilerWindowPosition = config.Bind("Layout", "Transpiler details position", DefaultTranspilerWindowPosition,
            "Logical position of the transpiler details window.");
        TranspilerWindowSize = config.Bind("Layout", "Transpiler details size", DefaultTranspilerWindowSize,
            "Logical size of the transpiler details window. A non-positive width uses 25% of the logical screen width.");
        GeneralMethodWindowPosition = config.Bind("Layout", "General Method Profiler position", DefaultGeneralMethodWindowPosition,
            "Logical position of the General Method Profiler window.");
        GeneralMethodWindowSize = config.Bind("Layout", "General Method Profiler size", DefaultGeneralMethodWindowSize,
            "Logical window size. A non-positive width uses 75% of the logical screen width.");
        GeneralCallTreeWindowPosition = config.Bind("Layout", "General call tree position", DefaultGeneralCallTreeWindowPosition,
            "Logical position of the General Method Profiler call tree window.");
        GeneralCallTreeWindowSize = config.Bind("Layout", "General call tree size", DefaultGeneralCallTreeWindowSize,
            "Logical call tree size. A non-positive width uses 75% of the logical screen width.");
        MonoBehaviourWindowPosition = config.Bind("Layout", "MonoBehaviour Frame Profiler position", DefaultMonoBehaviourWindowPosition,
            "Logical position of the MonoBehaviour Frame Profiler window.");
        MonoBehaviourWindowSize = config.Bind("Layout", "MonoBehaviour Frame Profiler size", DefaultMonoBehaviourWindowSize,
            "Logical size of the MonoBehaviour Frame Profiler window. A non-positive width uses 75% of the logical screen width.");
        MonoBehaviourCallWindowPosition = config.Bind("Layout", "MonoBehaviour Call Profiler position", DefaultMonoBehaviourCallWindowPosition,
            "Logical position of the MonoBehaviour Call Profiler window.");
        MonoBehaviourCallWindowSize = config.Bind("Layout", "MonoBehaviour Call Profiler size", DefaultMonoBehaviourCallWindowSize,
            "Logical size of the MonoBehaviour Call Profiler window. A non-positive width uses 75% of the logical screen width.");
        ValheimUpdateWindowPosition = config.Bind("Layout", "Valheim Update Profiler position", DefaultValheimUpdateWindowPosition,
            "Logical position of the Valheim Update Profiler window.");
        ValheimUpdateWindowSize = config.Bind("Layout", "Valheim Update Profiler size", DefaultValheimUpdateWindowSize,
            "Logical size of the Valheim Update Profiler window. A non-positive width uses 75% of the logical screen width.");
        LogMonitorWindowPosition = config.Bind("Layout", "Log Monitor position", DefaultLogMonitorWindowPosition,
            "Logical position of the client Log Monitor window.");
        LogMonitorWindowSize = config.Bind("Layout", "Log Monitor size", DefaultLogMonitorWindowSize,
            "Logical size of the client Log Monitor window. A non-positive width uses 75% of the logical screen width.");
        ServerLogMonitorWindowPosition = config.Bind("Layout", "Server Log Monitor position", DefaultServerLogMonitorWindowPosition,
            "Logical position of the remote dedicated-server Log Monitor window.");
        ServerLogMonitorWindowSize = config.Bind("Layout", "Server Log Monitor size", DefaultServerLogMonitorWindowSize,
            "Logical size of the remote dedicated-server Log Monitor window. A non-positive width uses 75% of the logical screen width.");
        NetworkProfilerWindowPosition = config.Bind("Layout", "Network Profiler position", DefaultNetworkProfilerWindowPosition,
            "Logical position of the dedicated-server Network Profiler window.");
        NetworkProfilerWindowSize = config.Bind("Layout", "Network Profiler size", DefaultNetworkProfilerWindowSize,
            "Logical size of the dedicated-server Network Profiler window. A non-positive width uses 75% of the logical screen width.");
    }

    internal ConfigFile ConfigFile { get; }

    internal ConfigEntry<bool> LoggingEnabled { get; }
    internal ConfigEntry<int> FrameBudgetTargetFps { get; }
    internal ConfigEntry<KeyboardShortcut> GlobalHotkey { get; }
    internal ConfigEntry<KeyboardShortcut> PatchProfilerHotkey { get; }
    internal ConfigEntry<KeyboardShortcut> GeneralMethodProfilerHotkey { get; }
    internal ConfigEntry<KeyboardShortcut> MonoBehaviourProfilerHotkey { get; }
    internal ConfigEntry<KeyboardShortcut> MonoBehaviourCallProfilerHotkey { get; }
    internal ConfigEntry<KeyboardShortcut> LogMonitorHotkey { get; }
    internal ConfigEntry<KeyboardShortcut> ServerLogMonitorHotkey { get; }
    internal ConfigEntry<KeyboardShortcut> ValheimUpdateProfilerHotkey { get; }
    internal ConfigEntry<KeyboardShortcut> NetworkProfilerHotkey { get; }
    internal ConfigEntry<bool> BlockGameInput { get; }
    internal ConfigEntry<bool> BlockMouseInput { get; }
    internal ConfigEntry<bool> PauseGame { get; }
    internal ConfigEntry<bool> UseValheimGuiScale { get; }
    internal ConfigEntry<float> UiScale { get; }
    internal ConfigEntry<int> FontSize { get; }
    internal ConfigEntry<bool> MonoBehaviourIncludeValheimProfilerCallbacks { get; }
    internal ConfigEntry<string> PatchProfilerAvgSortColumn { get; }
    internal ConfigEntry<string> PatchProfilerMaxSortColumn { get; }
    internal ConfigEntry<bool> PatchProfilerIgnoreGcAllocations { get; }
    internal ConfigEntry<bool> PatchProfilerExcludeMeasuredNestedCalls { get; }
    internal ConfigEntry<bool> PatchProfilerGradualProfilingStart { get; }
    internal ConfigEntry<bool> PatchProfilerCrashTrace { get; }
    internal ConfigEntry<int> PatchProfilerInstrumentationLimit { get; }
    internal ConfigEntry<string> GeneralMethodProfilerAvgSortColumn { get; }
    internal ConfigEntry<string> GeneralMethodProfilerMaxSortColumn { get; }
    internal ConfigEntry<string> GeneralMethodCallTreeSortColumn { get; }
    internal ConfigEntry<bool> GeneralMethodProfilerIgnoreGcAllocations { get; }
    internal ConfigEntry<bool> GeneralMethodProfilerExcludeMeasuredNestedCalls { get; }
    internal ConfigEntry<bool> GeneralMethodProfilerGradualProfilingStart { get; }
    internal ConfigEntry<bool> GeneralMethodProfilerGroupByMod { get; }
    internal ConfigEntry<string> GeneralMethodProfilerView { get; }
    internal ConfigEntry<string> GeneralMethodSelectionSearch { get; }
    internal ConfigEntry<string> GeneralMethodProfilerSearch { get; }
    internal ConfigEntry<bool> GeneralMethodSelectedOnly { get; }
    internal ConfigEntry<bool> GeneralMethodShowUnavailable { get; }
    internal ConfigEntry<int> GeneralMethodMaxTreeNodes { get; }
    internal ConfigEntry<int> GeneralMethodMaxCallDepth { get; }
    internal ConfigEntry<string> MonoBehaviourProfilerAvgSortColumn { get; }
    internal ConfigEntry<string> MonoBehaviourProfilerMaxSortColumn { get; }
    internal ConfigEntry<bool> MonoBehaviourProfilerIgnoreGcAllocations { get; }
    internal ConfigEntry<bool> MonoBehaviourCallIncludeValheimProfilerCallbacks { get; }
    internal ConfigEntry<string> MonoBehaviourCallProfilerSortColumn { get; }
    internal ConfigEntry<string> ValheimUpdateProfilerAvgSortColumn { get; }
    internal ConfigEntry<string> ValheimUpdateProfilerMaxSortColumn { get; }
    internal ConfigEntry<int> LogMonitorMaxEntries { get; }
    internal ConfigEntry<int> LogMonitorMaxIssueGroups { get; }
    internal ConfigEntry<string> LogMonitorIssueSortColumn { get; }
    internal ConfigEntry<int> LogMonitorHistoryPageEntries { get; }
    internal ConfigEntry<bool> LogMonitorCopyMetadata { get; }
    internal ConfigEntry<int> ServerLogRecentEntries { get; }
    internal ConfigEntry<int> ServerLogInitialEntries { get; }
    internal ConfigEntry<int> ServerLogHistoryPageEntries { get; }
    internal ConfigEntry<int> ServerLogClientMaxEntries { get; }
    internal ConfigEntry<int> ServerLogMaxIssueGroups { get; }
    internal ConfigEntry<string> ServerLogIssueSortColumn { get; }
    internal ConfigEntry<bool> ServerLogCopyMetadata { get; }
    internal ConfigEntry<string> NetworkRpcSortColumn { get; }
    internal ConfigEntry<string> NetworkZdoPrefabSortColumn { get; }
    internal ConfigEntry<string> NetworkZdoInstanceSortColumn { get; }
    internal ConfigEntry<string> NetworkZdoKeySortColumn { get; }
    internal ConfigEntry<string> NetworkPeerSortColumn { get; }
    internal ConfigEntry<string> NetworkRoutingErrorSortColumn { get; }

    internal ConfigEntry<Color> WindowBackground { get; }
    internal ConfigEntry<Color> WindowBorder { get; }
    internal ConfigEntry<Color> EntryBackground { get; }
    internal ConfigEntry<Color> TextColor { get; }
    internal ConfigEntry<Color> HeaderTextColor { get; }
    internal ConfigEntry<Color> ButtonBackground { get; }
    internal ConfigEntry<Color> ButtonTextColor { get; }
    internal ConfigEntry<Color> AccentColor { get; }

    internal ConfigEntry<Vector2> LauncherPosition { get; }
    internal ConfigEntry<Vector2> PatchWindowPosition { get; }
    internal ConfigEntry<Vector2> PatchWindowSize { get; }
    internal ConfigEntry<Vector2> TranspilerWindowPosition { get; }
    internal ConfigEntry<Vector2> TranspilerWindowSize { get; }
    internal ConfigEntry<Vector2> GeneralMethodWindowPosition { get; }
    internal ConfigEntry<Vector2> GeneralMethodWindowSize { get; }
    internal ConfigEntry<Vector2> GeneralCallTreeWindowPosition { get; }
    internal ConfigEntry<Vector2> GeneralCallTreeWindowSize { get; }
    internal ConfigEntry<Vector2> MonoBehaviourWindowPosition { get; }
    internal ConfigEntry<Vector2> MonoBehaviourWindowSize { get; }
    internal ConfigEntry<Vector2> MonoBehaviourCallWindowPosition { get; }
    internal ConfigEntry<Vector2> MonoBehaviourCallWindowSize { get; }
    internal ConfigEntry<Vector2> ValheimUpdateWindowPosition { get; }
    internal ConfigEntry<Vector2> ValheimUpdateWindowSize { get; }
    internal ConfigEntry<Vector2> LogMonitorWindowPosition { get; }
    internal ConfigEntry<Vector2> LogMonitorWindowSize { get; }
    internal ConfigEntry<Vector2> ServerLogMonitorWindowPosition { get; }
    internal ConfigEntry<Vector2> ServerLogMonitorWindowSize { get; }
    internal ConfigEntry<Vector2> NetworkProfilerWindowPosition { get; }
    internal ConfigEntry<Vector2> NetworkProfilerWindowSize { get; }
}