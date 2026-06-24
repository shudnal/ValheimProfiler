#nullable disable

using HarmonyLib;

namespace ValheimProfiler.Server;

internal static class ServerLogRpcBridge
{
    private static ZRoutedRpc _registeredRpc;

    internal static void RegisterForCurrentNetwork()
    {
        ZRoutedRpc rpc = ZRoutedRpc.instance;
        if (rpc == null || ReferenceEquals(rpc, _registeredRpc))
            return;

        _registeredRpc = rpc;
        ValheimProfilerPlugin plugin = ValheimProfilerPlugin.Instance;

        if (plugin?.ServerLogService != null && rpc.m_server)
            rpc.Register<ZPackage>(ServerLogProtocol.RequestRpc, plugin.ServerLogService.HandleRequest);

        if (plugin?.App?.ServerLogMonitor != null && !rpc.m_server)
            rpc.Register<ZPackage>(ServerLogProtocol.ResponseRpc, plugin.App.ServerLogMonitor.HandleResponse);
    }

    internal static void NetworkDestroyed()
    {
        _registeredRpc = null;
        ServerLogTransport.Clear();
        ValheimProfilerPlugin.Instance?.ServerLogService?.OnNetworkDestroyed();
        ValheimProfilerPlugin.Instance?.App?.ServerLogMonitor?.OnNetworkDestroyed();
    }
}

[HarmonyPatch(typeof(ZNet), "Awake")]
internal static class ZNetAwakeServerLogPatch
{
    [HarmonyPostfix]
    internal static void Postfix()
    {
        ServerLogRpcBridge.RegisterForCurrentNetwork();
        NetworkProfilerRpcBridge.RegisterForCurrentNetwork();
    }
}

[HarmonyPatch(typeof(ZNet), "OnDestroy")]
internal static class ZNetOnDestroyServerLogPatch
{
    [HarmonyPrefix]
    internal static void Prefix()
    {
        ServerLogRpcBridge.NetworkDestroyed();
        NetworkProfilerRpcBridge.NetworkDestroyed();
    }
}
