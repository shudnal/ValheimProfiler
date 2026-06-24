#nullable disable

namespace ValheimProfiler.Server;

internal static class NetworkProfilerRpcBridge
{
    private static ZRoutedRpc _registeredRpc;

    internal static void RegisterForCurrentNetwork()
    {
        ZRoutedRpc rpc = ZRoutedRpc.instance;
        if (rpc == null || ReferenceEquals(rpc, _registeredRpc))
            return;

        _registeredRpc = rpc;
        ValheimProfilerPlugin plugin = ValheimProfilerPlugin.Instance;

        if (plugin?.NetworkProfilerService != null && rpc.m_server)
            rpc.Register<ZPackage>(NetworkProfilerProtocol.RequestRpc, plugin.NetworkProfilerService.HandleRequest);

        if (plugin?.App?.NetworkProfiler != null && !rpc.m_server)
            rpc.Register<ZPackage>(NetworkProfilerProtocol.ResponseRpc, plugin.App.NetworkProfiler.HandleResponse);
    }

    internal static void NetworkDestroyed()
    {
        _registeredRpc = null;
        NetworkProfilerTransport.Clear();
        ValheimProfilerPlugin.Instance?.NetworkProfilerService?.OnNetworkDestroyed();
        ValheimProfilerPlugin.Instance?.App?.NetworkProfiler?.OnNetworkDestroyed();
    }
}
