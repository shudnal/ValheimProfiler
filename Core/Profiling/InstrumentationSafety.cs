#nullable disable

using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace ValheimProfiler.Core.Profiling;

/// <summary>
/// Main-thread preflight and reservations shared by the patch and general profilers.
/// Never obtains a native function pointer: that operation itself can crash Mono.
/// </summary>
internal sealed class InstrumentationSafety
{
    private readonly Dictionary<MethodBase, string> _methods = new Dictionary<MethodBase, string>();

    private static readonly Dictionary<MethodBase, string> FailedMethods = new Dictionary<MethodBase, string>();
    private static volatile string _restartReason;

    internal bool RestartRequired => _restartReason != null;
    internal string RestartMessage => "Restart the game before further Patch or General profiling. " +
        (_restartReason ?? "Instrumentation recovery could not be verified.");

    internal bool RequireRestart(string profilerOwner, MethodBase method, string reason)
    {
        if (_restartReason != null)
            return false;
        string name;
        try { name = method == null ? "(session)" : method.DeclaringType?.Assembly.GetName().Name + " :: " + method.DeclaringType?.FullName + ":" + method.Name; }
        catch { name = "(unknown method)"; }
        _restartReason = profilerOwner + " / " + name + ": " + reason;
        return true;
    }

    internal void Quarantine(MethodBase method, string reason)
    {
        if (method != null)
            FailedMethods[method] = "Previously failed instrumentation in this process; not retried. " + reason;
    }

    internal string GetPreviousFailure(MethodBase method) =>
        method != null && FailedMethods.TryGetValue(method, out string reason) ? reason : null;

    internal static bool IsInspectionFailure(string reason) => reason != null &&
        (reason.StartsWith("Safety inspection failed:", StringComparison.Ordinal) ||
         reason.StartsWith("Method metadata could not be inspected:", StringComparison.Ordinal) ||
         reason.StartsWith("IL preflight failed:", StringComparison.Ordinal) ||
         reason.StartsWith("Previously failed instrumentation", StringComparison.Ordinal));

    internal bool TryReserve(MethodBase method, string profilerOwner, InstrumentationCatalog catalog, out string reason)
    {
        reason = RestartRequired ? RestartMessage : GetPreviousFailure(method) ?? GetUnsupportedReason(method);
        if (reason != null)
            return false;

        if (catalog == null || !catalog.IsComplete)
        {
            reason = "Harmony ownership discovery is incomplete; instrumentation was not attempted.";
            return false;
        }

        if (_methods.TryGetValue(method, out string existingOwner))
        {
            reason = "Already reserved by " + existingOwner + ".";
            return false;
        }

        try
        {
            string activeProfiler = Harmony.GetPatchInfo(method)?.Owners.FirstOrDefault(owner =>
                !string.IsNullOrEmpty(owner) && owner != ValheimProfilerPlugin.CoreHarmonyId &&
                owner.StartsWith(ValheimProfilerPlugin.PluginGuid + ".", StringComparison.Ordinal));
            if (activeProfiler != null)
            {
                reason = "Already instrumented by " + activeProfiler + ".";
                return false;
            }

            // Identity is the actual MethodBase. Same-named methods in separate assemblies
            // are independent candidates; cache resets replace the old duplicate-name heuristic.
            _methods.Add(method, profilerOwner);

            reason = null;
            return true;
        }
        catch (Exception ex)
        {
            reason = "Safety inspection failed: " + ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    /// <summary>Release only after the corresponding Harmony patches were removed successfully.</summary>
    internal void Release(MethodBase method, string profilerOwner)
    {
        if (method == null || !_methods.TryGetValue(method, out string owner) || owner != profilerOwner)
            return;

        _methods.Remove(method);
    }

    internal static string GetUnsupportedReason(MethodBase method)
    {
        try
        {
            if (method == null)
                return "Missing method metadata.";
            if (method is DynamicMethod || method.DeclaringType == null || method.Module.Assembly.IsDynamic)
                return "Dynamic methods and generated dynamic assemblies are not instrumented.";

            Type type = method.DeclaringType;
            Assembly assembly = type.Assembly;
            if (assembly == typeof(ValheimProfilerPlugin).Assembly)
                return "The profiler's own assembly is excluded to prevent reentrancy.";
            if (IsInfrastructureAssembly(assembly) || IsInfrastructureNamespace(type.Namespace))
                return "Runtime, Harmony, MonoMod or BepInEx infrastructure is excluded.";
            if (HasUnsafeSignatureType(type))
                return "Byref-like or runtime-only declaring types are excluded.";
            // HSLColor-only instrumentation reproduced silent color corruption on the user's runtime.
            // The failing member/ABI path is not isolated, so exclude the whole value-type category,
            // including static helpers and operators. This is a conservative backend policy, not a
            // claim that every struct method is broken. Class methods returning structs remain eligible.
            if (type.IsValueType)
                return "Methods declared on value types are excluded by the conservative struct safety policy " +
                       "(including static methods and operators). No instrumentation was attempted.";
            if (method.IsAbstract || type.IsInterface)
                return "Abstract or interface methods do not have a concrete implementation.";
            if (method.IsGenericMethod || method.ContainsGenericParameters || type.IsGenericType || type.ContainsGenericParameters)
                return "Generic methods and generic declaring types are excluded because runtime bodies can be shared.";
            if (method.IsConstructor && method.IsStatic)
                return "Static constructors cannot be safely instrumented after type initialization.";
            if (method.Name == "Finalize" && method.GetParameters().Length == 0)
                return "Managed object finalizers are excluded.";
            if (typeof(MarshalByRefObject).IsAssignableFrom(type))
                return "Remoting/MarshalByRefObject methods are excluded.";
            if ((method.CallingConvention & CallingConventions.VarArgs) != 0)
                return "Variable-argument calling conventions are excluded.";

            MethodImplAttributes flags = method.GetMethodImplementationFlags();
            if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0 ||
                (flags & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.IL ||
                (flags & (MethodImplAttributes.InternalCall | MethodImplAttributes.Unmanaged | MethodImplAttributes.ForwardRef)) != 0)
                return "Native, extern, internal-call or runtime-provided implementations are excluded.";
            if (method is MethodInfo info && (info.ReturnType.IsByRef || HasUnsafeSignatureType(info.ReturnType)))
                return "By-reference returns, pointers and runtime-only return types are excluded.";
            if (method.GetParameters().Any(parameter => HasUnsafeSignatureType(parameter.ParameterType)))
                return "Pointer, byref-like or runtime-only parameter types are excluded.";

            MethodBody body = method.GetMethodBody();
            byte[] il = body?.GetILAsByteArray();
            if (il == null || il.Length == 0)
                return "No managed IL body is available.";
            if (body.LocalVariables.Any(local => HasUnsafeSignatureType(local.LocalType)))
                return "Pointer, byref-like or runtime-only local variables are excluded.";

            return MethodBodyPreflight.GetUnsupportedReason(il);
        }
        catch (Exception ex)
        {
            return "Method metadata could not be inspected: " + ex.GetType().Name + ": " + ex.Message;
        }
    }

    private static bool HasUnsafeSignatureType(Type type)
    {
        if (type == null)
            return true;
        if (type.IsPointer || type == typeof(TypedReference) || type == typeof(ArgIterator) || type == typeof(RuntimeArgumentHandle))
            return true;
        if (type.HasElementType)
            return HasUnsafeSignatureType(type.GetElementType());
        return type.GetCustomAttributesData().Any(attribute =>
            attribute.AttributeType.FullName == "System.Runtime.CompilerServices.IsByRefLikeAttribute");
    }

    internal static bool IsInfrastructureAssembly(Assembly assembly)
    {
        if (assembly == null || assembly == typeof(object).Assembly || assembly == typeof(Harmony).Assembly)
            return true;
        string name = assembly.GetName().Name ?? string.Empty;
        return name == "netstandard" || name == "System" || name.StartsWith("System.", StringComparison.Ordinal) ||
               name == "BepInEx" || name.StartsWith("BepInEx.", StringComparison.Ordinal) ||
               name == "0Harmony" || name.StartsWith("Harmony", StringComparison.Ordinal) ||
               name.StartsWith("MonoMod", StringComparison.Ordinal) || name.StartsWith("Mono.Cecil", StringComparison.Ordinal);
    }

    private static bool IsInfrastructureNamespace(string value) =>
        HasNamespacePrefix(value, "HarmonyLib") || HasNamespacePrefix(value, "MonoMod") ||
        HasNamespacePrefix(value, "Mono.Cecil") || HasNamespacePrefix(value, "BepInEx");

    private static bool HasNamespacePrefix(string value, string prefix) =>
        value == prefix || (value?.StartsWith(prefix + ".", StringComparison.Ordinal) ?? false);

    /// <summary>Assembly-independent signature, including overloads and return types.</summary>
    internal static string GetSignature(MethodBase method)
    {
        string result = method is MethodInfo info ? GetTypeName(info.ReturnType) : "void";
        return (method.IsStatic ? "static " : "instance ") + result + " " +
               GetTypeName(method.DeclaringType) + "::" + method.Name +
               (method.IsGenericMethod ? "`" + method.GetGenericArguments().Length : string.Empty) +
               "(" + string.Join(", ", method.GetParameters().Select(parameter => GetTypeName(parameter.ParameterType))) + ")";
    }

    internal static string GetTypeName(Type type)
    {
        if (type == null)
            return "?";
        if (type.IsByRef)
            return GetTypeName(type.GetElementType()) + "&";
        if (type.IsPointer)
            return GetTypeName(type.GetElementType()) + "*";
        if (type.IsArray)
            return GetTypeName(type.GetElementType()) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        if (type.IsGenericParameter)
            return (type.DeclaringMethod == null ? "!" : "!!") + type.GenericParameterPosition;
        if (type.IsGenericType)
            return type.GetGenericTypeDefinition().FullName + "<" +
                   string.Join(",", type.GetGenericArguments().Select(GetTypeName)) + ">";
        return type.FullName ?? type.Name;
    }

}

/// <summary>Snapshot of actual Harmony callback identities for General discovery and preflight.</summary>
internal sealed class InstrumentationCatalog
{
    private readonly HashSet<MethodBase> _callbacks = new HashSet<MethodBase>();
    internal bool IsComplete { get; private set; } = true;
    internal string Error { get; private set; }

    internal static InstrumentationCatalog Capture()
    {
        var result = new InstrumentationCatalog();
        try
        {
            foreach (MethodBase target in Harmony.GetAllPatchedMethods().ToArray())
            {
                Patches patches = Harmony.GetPatchInfo(target);
                if (patches == null)
                    continue;
                foreach (InstrumentationPatchSnapshot.Item item in InstrumentationPatchSnapshot.Read(patches))
                    result.Register(item.Patch);
            }
        }
        catch (Exception ex)
        {
            result.IsComplete = false;
            result.Error = ex.GetType().Name + ": " + ex.Message;
        }
        return result;
    }

    internal bool IsPatchCallback(MethodBase method) => method != null && _callbacks.Contains(method);

    private void Register(Patch patch)
    {
        MethodInfo method = patch?.PatchMethod;
        if (method == null || method.DeclaringType == null || method.DeclaringType.Assembly == typeof(ValheimProfilerPlugin).Assembly)
            return;

        // Keep actual identity even when an owner is missing. Never infer helper families by name.
        _callbacks.Add(method);
    }
}
