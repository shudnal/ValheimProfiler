#nullable disable

using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ValheimProfiler.Server;

internal static class NetworkProfilerInstrumentation
{
    private sealed class RegistrationHolder
    {
        internal NetworkProfilerService.RpcRegistrationInfo Registration;
    }

    private struct HandlerState
    {
        internal bool Active;
        internal long StartTicks;
        internal int PayloadBytes;
        internal long Sender;
        internal NetworkProfilerService.RpcRegistrationInfo Registration;
    }

    private struct DirectInvokeState
    {
        internal bool Active;
        internal string Method;
        internal int SentDataBefore;
    }

    private struct RouteState
    {
        internal bool Active;
        internal int Hash;
        internal int LogicalBytes;
        internal long TargetPeer;
        internal ZDOID TargetZdo;
        internal int PhysicalSends;
        internal int PhysicalBytes;
        internal RouteContext Previous;
    }

    private sealed class RouteContext
    {
        internal int PhysicalSends;
        internal int PhysicalBytes;
    }

    private struct MutationState
    {
        internal bool Active;
        internal uint DataRevision;
        internal int KeyHash;
        internal string ValueType;
    }

    private struct OwnerState
    {
        internal bool Active;
        internal long Owner;
    }

    private struct SerializeState
    {
        internal bool Active;
        internal long StartTicks;
        internal int StartSize;
        internal int StartPosition;
    }

    private struct SendZdoState
    {
        internal bool Active;
        internal long StartTicks;
        internal long PeerId;
        internal int SentBefore;
        internal int Queue;
        internal SendContext Previous;
    }

    private sealed class SendContext
    {
        internal long PeerId;
    }

    private struct SyncListState
    {
        internal bool Active;
        internal long StartTicks;
        internal long PeerId;
    }

    private struct ReceiveState
    {
        internal bool Active;
        internal ReceiveContext Previous;
    }

    private struct IncomingRoutedState
    {
        internal bool Active;
        internal bool Previous;
    }

    private sealed class ReceiveContext
    {
        internal long PeerId;
    }

    private static readonly ConditionalWeakTable<object, RegistrationHolder> Registrations = new();
    private static readonly HashSet<MethodBase> PatchedHandlerInvokes = new();
    private static readonly object HandlerPatchLock = new();
    private static NetworkProfilerService _service;
    private static Harmony _harmony;

    [ThreadStatic] private static RouteContext _routeContext;
    [ThreadStatic] private static SendContext _sendContext;
    [ThreadStatic] private static ReceiveContext _receiveContext;
    [ThreadStatic] private static bool _insideIncomingRoutedRpc;

    internal static void Install(NetworkProfilerService service, Harmony harmony)
    {
        _service = service;
        _harmony = harmony;

        PatchRegisterMethods(typeof(ZRpc));
        PatchRegisterMethods(typeof(ZRoutedRpc));
        PatchRegisterMethods(typeof(ZNetView));

        Patch(typeof(ZRpc), nameof(ZRpc.Invoke), prefix: nameof(DirectInvokePrefix), finalizer: nameof(DirectInvokeFinalizer));
        Patch(typeof(ZRpc), nameof(ZRpc.HandlePackage), prefix: nameof(DirectHandlePrefix));
        Patch(typeof(ZRoutedRpc), nameof(ZRoutedRpc.RouteRPC), prefix: nameof(RoutePrefix), finalizer: nameof(RouteFinalizer));
        Patch(typeof(ZRoutedRpc), nameof(ZRoutedRpc.RPC_RoutedRPC), prefix: nameof(IncomingRoutedPrefix), finalizer: nameof(IncomingRoutedFinalizer));
        Patch(typeof(ZRoutedRpc), nameof(ZRoutedRpc.HandleRoutedRPC), prefix: nameof(HandleRoutedPrefix));

        PatchZdoMutationMethods();
        Patch(typeof(ZDO), nameof(ZDO.SetOwnerInternal), prefix: nameof(OwnerPrefix), postfix: nameof(OwnerPostfix));
        Patch(typeof(ZDOMan), nameof(ZDOMan.CreateNewZDO), new[] { typeof(ZDOID), typeof(UnityEngine.Vector3), typeof(int) }, postfix: nameof(CreateZdoPostfix));
        Patch(typeof(ZDOMan), nameof(ZDOMan.DestroyZDO), prefix: nameof(DestroyZdoPrefix));
        Patch(typeof(ZDOMan), nameof(ZDOMan.SendZDOs), prefix: nameof(SendZdosPrefix), finalizer: nameof(SendZdosFinalizer));
        Patch(typeof(ZDOMan), nameof(ZDOMan.CreateSyncList), prefix: nameof(CreateSyncListPrefix), finalizer: nameof(CreateSyncListFinalizer));
        Patch(typeof(ZDO), nameof(ZDO.Serialize), prefix: nameof(SerializePrefix), finalizer: nameof(SerializeFinalizer));
        Patch(typeof(ZDOMan), nameof(ZDOMan.RPC_ZDOData), prefix: nameof(ReceivePrefix), finalizer: nameof(ReceiveFinalizer));
        Patch(typeof(ZDO), nameof(ZDO.Deserialize), prefix: nameof(DeserializePrefix), finalizer: nameof(DeserializeFinalizer));
    }

    internal static void Uninstall(NetworkProfilerService service)
    {
        if (!ReferenceEquals(_service, service))
            return;
        _service = null;
        _harmony = null;
        lock (HandlerPatchLock)
            PatchedHandlerInvokes.Clear();
    }

    internal static void BindHandler(object handlerObject, NetworkProfilerService.RpcRegistrationInfo registration)
    {
        if (handlerObject == null || registration == null)
            return;

        try
        {
            Registrations.Remove(handlerObject);
            Registrations.Add(handlerObject, new RegistrationHolder { Registration = registration });

            MethodInfo invoke = AccessTools.Method(handlerObject.GetType(), "Invoke");
            if (invoke == null)
                return;

            lock (HandlerPatchLock)
            {
                if (!PatchedHandlerInvokes.Add(invoke))
                    return;
                _harmony?.Patch(
                    invoke,
                    prefix: HarmonyMethod(nameof(HandlerPrefix), Priority.First),
                    finalizer: HarmonyMethod(nameof(HandlerFinalizer), Priority.Last));
            }
        }
        catch
        {
        }
    }

    private static void PatchRegisterMethods(Type type)
    {
        foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Where(m => m.Name == "Register" && m.GetParameters().Length == 2))
        {
            try { _harmony.Patch(method, postfix: HarmonyMethod(nameof(RegisterPostfix), Priority.Last)); } catch { }
        }
    }

    private static void RegisterPostfix(object __instance, object[] __args)
    {
        try
        {
            if (_service == null || __args == null || __args.Length < 2 || __args[0] is not string name || __args[1] is not Delegate handler)
                return;

            int hash = name.GetStableHashCode();
            object wrapper = null;
            string layer;
            string prefab = string.Empty;

            if (__instance is ZRpc direct)
            {
                layer = "Direct";
                direct.m_functions.TryGetValue(hash, out ZRpc.RpcMethodBase value);
                wrapper = value;
            }
            else if (__instance is ZRoutedRpc routed)
            {
                layer = "Global";
                routed.m_functions.TryGetValue(hash, out RoutedMethodBase value);
                wrapper = value;
            }
            else if (__instance is ZNetView view)
            {
                layer = "Object";
                view.m_functions.TryGetValue(hash, out RoutedMethodBase value);
                wrapper = value;
                try { prefab = view.GetPrefabName() ?? string.Empty; } catch { }
            }
            else
            {
                return;
            }

            _service.RegisterRpc(wrapper, layer, name, handler, prefab);
        }
        catch
        {
        }
    }

    private static void HandlerPrefix(object __instance, object[] __args, ref HandlerState __state)
    {
        try
        {
            if (_service?.IsCollecting != true || __instance == null || !Registrations.TryGetValue(__instance, out RegistrationHolder holder))
                return;
            __state.Active = true;
            __state.StartTicks = Stopwatch.GetTimestamp();
            __state.Registration = holder.Registration;
            if (__args != null)
            {
                for (int i = 0; i < __args.Length; i++)
                {
                    if (__args[i] is long sender)
                        __state.Sender = sender;
                    else if (__args[i] is ZPackage package)
                        __state.PayloadBytes = Math.Max(0, package.Size());
                    else if (__args[i] is ZRpc rpc)
                        __state.Sender = ResolvePeerId(rpc);
                }
            }
        }
        catch
        {
        }
    }

    private static Exception HandlerFinalizer(Exception __exception, HandlerState __state)
    {
        try
        {
            if (__state.Active && _service != null && __state.Registration != null && __state.StartTicks > 0)
            {
                double elapsed = ElapsedMs(__state.StartTicks);
                _service.RecordRpcHandler(__state.Registration, __state.Sender, __state.PayloadBytes, elapsed);
                if (__exception != null)
                    _service.RecordRpcHandlerException(__state.Registration, __state.Sender, __exception);
            }
        }
        catch
        {
        }
        return __exception;
    }

    private static void DirectInvokePrefix(string method, ZRpc __instance, ref DirectInvokeState __state)
    {
        if (_service?.IsCollecting != true)
            return;
        __state.Active = true;
        __state.Method = method ?? string.Empty;
        __state.SentDataBefore = __instance?.m_sentData ?? 0;
    }

    private static Exception DirectInvokeFinalizer(Exception __exception, ZRpc __instance, DirectInvokeState __state)
    {
        try
        {
            if (!__state.Active)
                return __exception;
            int bytes = Math.Max(0, (__instance?.m_sentData ?? __state.SentDataBefore) - __state.SentDataBefore);
            if (_routeContext != null && string.Equals(__state.Method, "RoutedRPC", StringComparison.Ordinal))
            {
                if (bytes > 0)
                {
                    _routeContext.PhysicalSends++;
                    _routeContext.PhysicalBytes += bytes;
                }
            }
            else if (_service != null && bytes > 0 && !string.Equals(__state.Method, "RoutedRPC", StringComparison.Ordinal))
            {
                _service.RecordDirectOutgoing(__state.Method, bytes, __instance);
            }
        }
        catch
        {
        }
        return __exception;
    }

    private static void DirectHandlePrefix(ZRpc __instance, ZPackage package)
    {
        try
        {
            if (_service?.IsCollecting != true || package == null)
                return;
            int pos = package.GetPos();
            int hash = package.ReadInt();
            package.SetPos(pos);
            if (hash != 0 && (__instance?.m_functions == null || !__instance.m_functions.ContainsKey(hash)))
                _service.RecordRoutingError("Direct handler missing", hash, ResolvePeerId(__instance), ZDOID.None, "Received direct RPC hash is not registered on this peer.");
        }
        catch
        {
        }
    }

    private static void RoutePrefix(ZRoutedRpc.RoutedRPCData rpcData, ref RouteState __state)
    {
        if (_service?.IsCollecting != true || rpcData == null)
            return;
        __state.Active = true;
        __state.Hash = rpcData.m_methodHash;
        __state.LogicalBytes = rpcData.m_parameters?.Size() ?? 0;
        __state.TargetPeer = rpcData.m_targetPeerID;
        __state.TargetZdo = rpcData.m_targetZDO;
        __state.Previous = _routeContext;
        _routeContext = new RouteContext();
    }

    private static Exception RouteFinalizer(Exception __exception, RouteState __state)
    {
        try
        {
            if (!__state.Active)
                return __exception;
            RouteContext context = _routeContext;
            __state.PhysicalSends = context?.PhysicalSends ?? 0;
            __state.PhysicalBytes = context?.PhysicalBytes ?? 0;
            _service?.RecordRoutedOutgoing(__state.Hash, __state.LogicalBytes, __state.PhysicalSends, __state.PhysicalBytes, __state.TargetZdo);

            if (__state.TargetPeer != 0L && ZRoutedRpc.instance != null && __state.TargetPeer != ZRoutedRpc.instance.m_id && __state.PhysicalSends == 0)
            {
                ZNetPeer peer = ZRoutedRpc.instance.GetPeer(__state.TargetPeer);
                string reason = peer == null
                    ? "Target peer was not found."
                    : !peer.IsReady()
                        ? "Target peer exists but is not ready."
                        : "No physical send was observed. The socket may be disconnected or a networking mod may have suppressed the route.";
                CaptureExternalCaller(out string caller, out string callerMod);
                _service?.RecordRoutingError("Target peer unavailable", __state.Hash, __state.TargetPeer, __state.TargetZdo, reason, caller: caller, callerMod: callerMod);
            }
        }
        catch
        {
        }
        finally
        {
            if (__state.Active)
                _routeContext = __state.Previous;
        }
        return __exception;
    }

    private static void IncomingRoutedPrefix(ZPackage pkg, ref IncomingRoutedState __state)
    {
        if (_service?.IsCollecting != true)
            return;
        __state.Active = true;
        __state.Previous = _insideIncomingRoutedRpc;
        _insideIncomingRoutedRpc = true;
        try
        {
            if (_service == null || pkg == null)
                return;
            int pos = pkg.GetPos();
            pkg.ReadLong(); // message id
            long senderPeerId = pkg.ReadLong();
            long targetPeerId = pkg.ReadLong();
            ZDOID targetZdo = pkg.ReadZDOID();
            int methodHash = pkg.ReadInt();
            pkg.SetPos(pos);
            InspectRoutedTarget(senderPeerId, targetPeerId, targetZdo, methodHash);
        }
        catch
        {
        }
    }

    private static Exception IncomingRoutedFinalizer(Exception __exception, IncomingRoutedState __state)
    {
        if (__state.Active)
            _insideIncomingRoutedRpc = __state.Previous;
        return __exception;
    }

    private static void HandleRoutedPrefix(ZRoutedRpc.RoutedRPCData data)
    {
        if (_service?.IsCollecting != true || _insideIncomingRoutedRpc)
            return;
        try { InspectRoutedTarget(data, captureCallerOnError: true); } catch { }
    }

    private static void InspectRoutedTarget(ZRoutedRpc.RoutedRPCData data, bool captureCallerOnError = false)
    {
        if (data == null)
            return;
        InspectRoutedTarget(data.m_senderPeerID, data.m_targetPeerID, data.m_targetZDO, data.m_methodHash, captureCallerOnError);
    }

    private static void InspectRoutedTarget(long senderPeerId, long targetPeerId, ZDOID targetZdo, int methodHash, bool captureCallerOnError = false)
    {
        if (_service == null || ZRoutedRpc.instance == null)
            return;
        if (targetPeerId != 0L && targetPeerId != ZRoutedRpc.instance.m_id)
            return;

        if (targetZdo.IsNone())
        {
            if (!ZRoutedRpc.instance.m_functions.ContainsKey(methodHash))
                RecordRoutedTargetError("Global routed handler missing", methodHash, senderPeerId, ZDOID.None, "The routed RPC reached this peer but no global handler is registered.", string.Empty, captureCallerOnError);
            return;
        }

        ZDO zdo = ZDOMan.instance?.GetZDO(targetZdo);
        if (zdo == null)
        {
            RecordRoutedTargetError("Target ZDO missing", methodHash, senderPeerId, targetZdo, "The routed object RPC targets a ZDO that is not present on this peer.", string.Empty, captureCallerOnError);
            return;
        }

        ZNetView view = ZNetScene.instance?.FindInstance(zdo);
        if (view == null)
        {
            RecordRoutedTargetError("Target ZNetView missing", methodHash, senderPeerId, targetZdo, "The target ZDO exists but its ZNetView is not instantiated.", SafePrefab(view, zdo), captureCallerOnError);
            return;
        }

        if (!view.m_functions.ContainsKey(methodHash))
            RecordRoutedTargetError("Object handler missing", methodHash, senderPeerId, targetZdo, "The target ZNetView exists but does not have this RPC registered.", SafePrefab(view, zdo), captureCallerOnError);
    }

    private static void RecordRoutedTargetError(string kind, int methodHash, long senderPeerId, ZDOID targetZdo, string details, string prefab, bool captureCaller)
    {
        string caller = string.Empty;
        string callerMod = string.Empty;
        if (captureCaller)
            CaptureExternalCaller(out caller, out callerMod);
        _service?.RecordRoutingError(kind, methodHash, senderPeerId, targetZdo, details, prefab: prefab, caller: caller, callerMod: callerMod);
    }

    private static string SafePrefab(ZNetView view, ZDO zdo)
    {
        try { return view?.GetPrefabName() ?? zdo?.GetPrefab().ToString() ?? string.Empty; } catch { return string.Empty; }
    }

    private static void PatchZdoMutationMethods()
    {
        foreach (MethodInfo method in typeof(ZDO).GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            ParameterInfo[] parameters = method.GetParameters();
            if (method.Name == nameof(ZDO.Set) && parameters.Length > 0 && parameters[0].ParameterType == typeof(string))
            {
                try { _harmony.Patch(method, prefix: HarmonyMethod(nameof(RememberStringKeyPrefix), Priority.First)); } catch { }
                continue;
            }

            bool mutation = false;
            if ((method.Name == nameof(ZDO.Set) || method.Name == nameof(ZDO.Update)) && parameters.Length > 0 &&
                parameters[0].ParameterType == typeof(int) &&
                !(method.Name == nameof(ZDO.Set) && parameters.Length > 1 && parameters[1].ParameterType == typeof(bool)))
                mutation = true;
            else if (method.Name is nameof(ZDO.SetConnection) or nameof(ZDO.UpdateConnection) or nameof(ZDO.SetPosition) or nameof(ZDO.SetRotation) or
                     nameof(ZDO.SetType) or nameof(ZDO.SetDistant) or nameof(ZDO.SetPrefab) or nameof(ZDO.SetSector))
                mutation = true;

            if (!mutation)
                continue;
            try
            {
                _harmony.Patch(method,
                    prefix: HarmonyMethod(nameof(MutationPrefix), Priority.First),
                    postfix: HarmonyMethod(nameof(MutationPostfix), Priority.Last));
            }
            catch
            {
            }
        }
    }

    private static void RememberStringKeyPrefix(object[] __args)
    {
        try
        {
            if (_service?.IsCollecting != true)
                return;
            if (__args != null && __args.Length > 0 && __args[0] is string name)
            {
                _service?.RememberKeyName(name.GetStableHashCode(), name);
                if (__args.Length > 1 && __args[1] is ZDOID)
                {
                    KeyValuePair<int, int> pair = ZDO.GetHashZDOID(name);
                    _service?.RememberKeyName(pair.Key, name + "_u");
                    _service?.RememberKeyName(pair.Value, name + "_i");
                }
            }
        }
        catch
        {
        }
    }

    private static void MutationPrefix(ZDO __instance, MethodBase __originalMethod, object[] __args, ref MutationState __state)
    {
        if (_service?.IsCollecting != true || __instance == null)
            return;
        __state.Active = true;
        __state.DataRevision = __instance.DataRevision;
        __state.ValueType = __originalMethod?.Name ?? "Mutation";
        if (__args != null && __args.Length > 0)
        {
            if (__args[0] is int hash)
                __state.KeyHash = hash;
            else if (__args[0] is KeyValuePair<int, int> pair)
                __state.KeyHash = pair.Key;
            if (__args.Length > 1 && __args[1] != null)
                __state.ValueType = __args[1].GetType().Name;
        }
    }

    private static void MutationPostfix(ZDO __instance, MutationState __state)
    {
        try
        {
            if (__state.Active && __instance != null && __instance.DataRevision != __state.DataRevision)
                _service?.RecordZdoMutation(__instance, __state.KeyHash, __state.ValueType);
        }
        catch
        {
        }
    }

    private static void OwnerPrefix(ZDO __instance, ref OwnerState __state)
    {
        if (_service?.IsCollecting != true || __instance == null)
            return;
        __state.Active = true;
        __state.Owner = __instance.GetOwner();
    }

    private static void OwnerPostfix(ZDO __instance, OwnerState __state)
    {
        try
        {
            if (__state.Active && __instance != null && __instance.GetOwner() != __state.Owner)
                _service?.RecordZdoOwnershipChange(__instance);
        }
        catch
        {
        }
    }

    private static void CreateZdoPostfix(ZDO __result)
    {
        try { if (_service?.IsCollecting == true) _service.RecordZdoCreate(__result); } catch { }
    }

    private static void DestroyZdoPrefix(ZDO zdo)
    {
        try { if (_service?.IsCollecting == true) _service.RecordZdoDestroy(zdo); } catch { }
    }

    private static void SendZdosPrefix(ZDOMan.ZDOPeer peer, ref SendZdoState __state)
    {
        if (_service?.IsCollecting != true)
            return;
        __state.Active = true;
        __state.StartTicks = Stopwatch.GetTimestamp();
        __state.PeerId = peer?.m_peer?.m_uid ?? 0L;
        __state.SentBefore = ZDOMan.instance?.m_zdosSent ?? 0;
        try { __state.Queue = peer?.m_peer?.m_socket?.GetSendQueueSize() ?? -1; } catch { __state.Queue = -1; }
        __state.Previous = _sendContext;
        _sendContext = new SendContext { PeerId = __state.PeerId };
    }

    private static Exception SendZdosFinalizer(Exception __exception, SendZdoState __state)
    {
        try
        {
            if (!__state.Active)
                return __exception;
            int sent = Math.Max(0, (ZDOMan.instance?.m_zdosSent ?? __state.SentBefore) - __state.SentBefore);
            _service?.RecordSendZdos(__state.PeerId, ElapsedMs(__state.StartTicks), sent, __state.Queue);
        }
        catch
        {
        }
        finally
        {
            if (__state.Active)
                _sendContext = __state.Previous;
        }
        return __exception;
    }

    private static void CreateSyncListPrefix(ZDOMan.ZDOPeer peer, ref SyncListState __state)
    {
        if (_service?.IsCollecting != true)
            return;
        __state.Active = true;
        __state.StartTicks = Stopwatch.GetTimestamp();
        __state.PeerId = peer?.m_peer?.m_uid ?? 0L;
    }

    private static Exception CreateSyncListFinalizer(Exception __exception, ZDOMan __instance, List<ZDO> toSync, SyncListState __state)
    {
        try
        {
            if (!__state.Active)
                return __exception;
            int candidates = (__instance?.m_tempSectorObjects?.Count ?? 0) + (__instance?.m_tempToSyncDistant?.Count ?? 0);
            _service?.RecordCreateSyncList(__state.PeerId, ElapsedMs(__state.StartTicks), candidates, toSync?.Count ?? 0);
        }
        catch
        {
        }
        return __exception;
    }

    private static void SerializePrefix(ZPackage pkg, ref SerializeState __state)
    {
        if (_service?.IsCollecting != true || _sendContext == null)
            return;
        __state.Active = true;
        __state.StartTicks = Stopwatch.GetTimestamp();
        __state.StartSize = pkg?.Size() ?? 0;
    }

    private static Exception SerializeFinalizer(Exception __exception, ZDO __instance, ZPackage pkg, SerializeState __state)
    {
        try
        {
            if (__state.Active && _sendContext != null)
                _service?.RecordZdoSerialize(__instance, Math.Max(0, (pkg?.Size() ?? __state.StartSize) - __state.StartSize), ElapsedMs(__state.StartTicks), _sendContext.PeerId, receiving: false);
        }
        catch
        {
        }
        return __exception;
    }

    private static void ReceivePrefix(ZRpc rpc, ref ReceiveState __state)
    {
        if (_service?.IsCollecting != true)
            return;
        __state.Active = true;
        __state.Previous = _receiveContext;
        _receiveContext = new ReceiveContext { PeerId = ResolvePeerId(rpc) };
    }

    private static Exception ReceiveFinalizer(Exception __exception, ReceiveState __state)
    {
        if (__state.Active)
            _receiveContext = __state.Previous;
        return __exception;
    }

    private static void DeserializePrefix(ZPackage pkg, ref SerializeState __state)
    {
        if (_service?.IsCollecting != true || _receiveContext == null)
            return;
        __state.Active = true;
        __state.StartTicks = Stopwatch.GetTimestamp();
        __state.StartPosition = pkg?.GetPos() ?? 0;
    }

    private static Exception DeserializeFinalizer(Exception __exception, ZDO __instance, ZPackage pkg, SerializeState __state)
    {
        try
        {
            if (__state.Active && _receiveContext != null)
                _service?.RecordZdoSerialize(__instance, Math.Max(0, (pkg?.GetPos() ?? __state.StartPosition) - __state.StartPosition), ElapsedMs(__state.StartTicks), _receiveContext.PeerId, receiving: true);
        }
        catch
        {
        }
        return __exception;
    }

    private static void Patch(Type type, string name, Type[] parameters = null, string prefix = null, string postfix = null, string finalizer = null)
    {
        try
        {
            MethodInfo method = parameters == null ? AccessTools.Method(type, name) : AccessTools.Method(type, name, parameters);
            if (method == null)
            {
                _service?.ReportInstrumentationWarning(type.FullName + "." + name + " was not found; related metrics are unavailable.");
                return;
            }
            _harmony.Patch(method,
                prefix: prefix == null ? null : HarmonyMethod(prefix, Priority.First),
                postfix: postfix == null ? null : HarmonyMethod(postfix, Priority.Last),
                finalizer: finalizer == null ? null : HarmonyMethod(finalizer, Priority.Last));
        }
        catch (Exception ex)
        {
            _service?.ReportInstrumentationWarning(type.FullName + "." + name + " could not be patched: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static HarmonyMethod HarmonyMethod(string methodName, int priority)
    {
        var method = new HarmonyMethod(AccessTools.Method(typeof(NetworkProfilerInstrumentation), methodName));
        method.priority = priority;
        return method;
    }

    private static void CaptureExternalCaller(out string caller, out string callerMod)
    {
        caller = string.Empty;
        callerMod = string.Empty;
        try
        {
            StackFrame[] frames = new StackTrace(2, false).GetFrames();
            if (frames == null)
                return;
            for (int i = 0; i < frames.Length; i++)
            {
                MethodBase method = frames[i].GetMethod();
                Type type = method?.DeclaringType;
                if (method == null || type == null)
                    continue;
                Assembly assembly = type.Assembly;
                string assemblyName = assembly.GetName().Name ?? string.Empty;
                if (assembly == typeof(NetworkProfilerInstrumentation).Assembly ||
                    assemblyName == "0Harmony" ||
                    type == typeof(ZRpc) || type == typeof(ZRoutedRpc) || type == typeof(ZNetView) ||
                    type.Namespace?.StartsWith("HarmonyLib", StringComparison.Ordinal) == true ||
                    type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true)
                    continue;

                caller = type.FullName + "." + method.Name;
                callerMod = _service?.ResolveMod(assembly) ?? assemblyName;
                return;
            }
        }
        catch
        {
        }
    }

    private static long ResolvePeerId(ZRpc rpc)
    {
        try { return ZNet.instance?.GetPeer(rpc)?.m_uid ?? 0L; } catch { return 0L; }
    }

    private static double ElapsedMs(long startTicks)
    {
        if (startTicks <= 0)
            return 0d;
        return Math.Max(0d, (Stopwatch.GetTimestamp() - startTicks) * 1000d / Stopwatch.Frequency);
    }
}
