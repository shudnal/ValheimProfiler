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

        GlobalHotkey = config.Bind("General", "Toggle UI", new KeyboardShortcut(KeyCode.F7),
            "Show or hide the Valheim Profiler user interface without stopping active profilers.");
        PatchProfilerHotkey = config.Bind("General", "Toggle Patch Profiler", new KeyboardShortcut(KeyCode.F8),
            "Show or hide the Patch Profiler window.");
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
            "Block gameplay mouse buttons, wheel and camera movement while profiler windows are visible, while leaving keyboard movement, inventory and other hotkeys available. Full input blocking takes precedence.");
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
            "Over 1 sec sort column",
            "AvgMsPerFrame",
            "Descending sort column restored when the Patch Profiler opens the Over 1 sec view.");
        PatchProfilerMaxSortColumn = config.Bind(
            "Patch Profiler",
            "Max over 60 sec sort column",
            "ThirdMax",
            "Descending sort column restored when the Patch Profiler opens the Max over 60 sec view.");
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

    internal ConfigEntry<KeyboardShortcut> GlobalHotkey { get; }
    internal ConfigEntry<KeyboardShortcut> PatchProfilerHotkey { get; }
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
    internal ConfigEntry<string> MonoBehaviourProfilerAvgSortColumn { get; }
    internal ConfigEntry<string> MonoBehaviourProfilerMaxSortColumn { get; }
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