#nullable disable

using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using ValheimProfiler.Server;

namespace ValheimProfiler;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class ValheimProfilerPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "shudnal.ValheimProfiler";
    public const string PluginName = "Valheim Profiler";
    public const string PluginVersion = "0.8.6";

    internal const string CoreHarmonyId = PluginGuid + ".Core";

    private Harmony _coreHarmony;
    private bool _headlessServerBackend;

    internal static ValheimProfilerPlugin Instance { get; private set; }
    internal ValheimProfilerApp App { get; private set; }
    internal ServerLogService ServerLogService { get; private set; }
    internal NetworkProfilerService NetworkProfilerService { get; private set; }

    private void Awake()
    {
        Instance = this;

        var config = new ValheimProfilerConfig(Config);
        _coreHarmony = new Harmony(CoreHarmonyId);
        _headlessServerBackend = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

        if (_headlessServerBackend)
        {
            // A headless dedicated server only runs bounded remote diagnostics backends and their RPC bridges.
            // No profiler discovery, IMGUI, input, cursor or pause components are created.
            ServerLogService = new ServerLogService(config);
            NetworkProfilerService = new NetworkProfilerService();
            PatchHeadlessNetworkBridges();
            ServerLogRpcBridge.RegisterForCurrentNetwork();
            NetworkProfilerRpcBridge.RegisterForCurrentNetwork();
            return;
        }

        App = new ValheimProfilerApp(config, Logger);
        _coreHarmony.PatchAll(typeof(ValheimProfilerPlugin).Assembly);
        App.Initialize();
        ServerLogRpcBridge.RegisterForCurrentNetwork();
        NetworkProfilerRpcBridge.RegisterForCurrentNetwork();
    }

    private void PatchHeadlessNetworkBridges()
    {
        _coreHarmony.Patch(
            AccessTools.Method(typeof(ZNet), "Awake"),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(ZNetAwakeServerLogPatch), nameof(ZNetAwakeServerLogPatch.Postfix))));
        _coreHarmony.Patch(
            AccessTools.Method(typeof(ZNet), "OnDestroy"),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(ZNetOnDestroyServerLogPatch), nameof(ZNetOnDestroyServerLogPatch.Prefix))));
    }

    private void Start() => App?.MarkGameWindowReady();

    private void Update()
    {
        if (_headlessServerBackend)
        {
            ServerLogService?.Update();
            NetworkProfilerService?.Update();
        }
        else
            App?.Update();
    }

    private void LateUpdate() => App?.LateUpdate();

    private void OnGUI() => App?.OnGUI();

    private void OnDisable()
    {
        App?.SetUiVisible(false);
        App?.ReleaseCursorAndPause();
    }

    private void OnDestroy()
    {
        try
        {
            App?.Shutdown();
            ServerLogService?.Shutdown();
            NetworkProfilerService?.Shutdown();
        }
        finally
        {
            _coreHarmony?.UnpatchSelf();
            _coreHarmony = null;
            App = null;
            ServerLogService = null;
            NetworkProfilerService = null;
            ServerLogTransport.Clear();
            NetworkProfilerTransport.Clear();

            if (Instance == this)
                Instance = null;
        }
    }
}
