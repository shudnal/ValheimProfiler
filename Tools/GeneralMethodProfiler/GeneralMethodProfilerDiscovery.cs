#nullable disable

using BepInEx.Bootstrap;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace ValheimProfiler.Tools.GeneralMethodProfiler;

internal sealed partial class GeneralMethodProfilerTool
{
    private static readonly HashSet<string> UnityMessageNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "Awake", "Start", "Update", "FixedUpdate", "LateUpdate", "OnGUI", "OnEnable", "OnDisable", "OnDestroy",
        "OnApplicationFocus", "OnApplicationPause", "OnApplicationQuit", "OnBecameVisible", "OnBecameInvisible",
        "OnCollisionEnter", "OnCollisionExit", "OnCollisionStay", "OnCollisionEnter2D", "OnCollisionExit2D", "OnCollisionStay2D",
        "OnTriggerEnter", "OnTriggerExit", "OnTriggerStay", "OnTriggerEnter2D", "OnTriggerExit2D", "OnTriggerStay2D",
        "OnAnimatorIK", "OnAnimatorMove", "OnAudioFilterRead", "OnJointBreak", "OnJointBreak2D",
        "OnMouseDown", "OnMouseDrag", "OnMouseEnter", "OnMouseExit", "OnMouseOver", "OnMouseUp", "OnMouseUpAsButton",
        "OnParticleCollision", "OnParticleTrigger", "OnParticleSystemStopped", "OnParticleUpdateJobScheduled",
        "OnPreCull", "OnPreRender", "OnPostRender", "OnRenderObject", "OnWillRenderObject", "OnRenderImage",
        "OnDrawGizmos", "OnDrawGizmosSelected", "OnValidate", "Reset", "OnTransformChildrenChanged", "OnTransformParentChanged",
        "OnRectTransformDimensionsChange", "OnBeforeTransformParentChanged", "OnCanvasGroupChanged", "OnCanvasHierarchyChanged",
        "OnDidApplyAnimationProperties", "OnLevelWasLoaded", "OnConnectedToServer", "OnDisconnectedFromServer",
        "OnFailedToConnect", "OnFailedToConnectToMasterServer", "OnMasterServerEvent", "OnNetworkInstantiate",
        "OnPlayerConnected", "OnPlayerDisconnected", "OnSerializeNetworkView", "OnServerInitialized"
    };

    private static readonly HashSet<string> HarmonyCallbackNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "Prefix", "Postfix", "Transpiler", "Finalizer", "Prepare", "Cleanup", "TargetMethod", "TargetMethods"
    };

    private static readonly HashSet<string> HarmonyCallbackAttributes = new HashSet<string>(StringComparer.Ordinal)
    {
        "HarmonyLib.HarmonyPrefix", "HarmonyLib.HarmonyPostfix", "HarmonyLib.HarmonyTranspiler", "HarmonyLib.HarmonyFinalizer",
        "HarmonyLib.HarmonyReversePatch", "HarmonyLib.HarmonyPrepare", "HarmonyLib.HarmonyCleanup",
        "HarmonyLib.HarmonyTargetMethod", "HarmonyLib.HarmonyTargetMethods", "HarmonyLib.HarmonyILManipulator"
    };

    private void RefreshMethodList()
    {
        if (_profilingActive)
            return;
        _listReady = false;

        using var log = _instrumentationLog.BeginOperation("Discovery");
        try
        {
            if (!StopProfilingInternal(false))
                return;
            _status = "Scanning BepInEx plugin assemblies...";
            var previous = _methodsByKey.ToDictionary(pair => pair.Key, pair => pair.Value.Selected, StringComparer.Ordinal);
            _selectionPolicy.Reload();
            InstrumentationCatalog catalog = InstrumentationCatalog.Capture();
            if (!catalog.IsComplete)
                throw new InvalidOperationException("Harmony ownership discovery failed: " + catalog.Error);

            // Do not scan the AppDomain or traverse plugin dependency assemblies.
            var plugins = Chainloader.PluginInfos.Values
                .Where(plugin => plugin?.Instance != null)
                .GroupBy(plugin => plugin.Instance.GetType().Assembly)
                .OrderBy(group => group.Key.GetName().Name, StringComparer.Ordinal)
                .ToArray();
            var discovered = new List<MethodTypeEntry>();
            int partialAssemblies = 0;
            int partialTypes = 0;
            int partialMethods = 0;
            foreach (var group in plugins)
            {
                Assembly assembly = group.Key;
                if (assembly == typeof(ValheimProfilerPlugin).Assembly || assembly.IsDynamic ||
                    InstrumentationSafety.IsInfrastructureAssembly(assembly))
                    continue;

                string assemblyName = assembly.GetName().Name;
                string groupId = string.Join("+", group.Select(plugin => plugin.Metadata.GUID).OrderBy(guid => guid, StringComparer.Ordinal));
                string groupName = string.Join(", ", group.Select(plugin => plugin.Metadata.Name).Distinct().OrderBy(name => name, StringComparer.Ordinal));
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(type => type != null).ToArray();
                    partialAssemblies++;
                    log.MetadataFailure("Assembly: " + assemblyName, "Partially loaded assembly", ex);
                }

                catch (Exception ex)
                {
                    partialAssemblies++;
                    log.MetadataFailure("Assembly: " + assemblyName, "Could not inspect assembly", ex);
                    continue;
                }

                foreach (Type type in types.OrderBy(item => item.FullName, StringComparer.Ordinal))
                {
                    var typeEntry = new MethodTypeEntry
                    {
                        Type = type,
                        TypeName = type.FullName ?? type.Name,
                        GroupId = groupId + "|" + assemblyName,
                        GroupName = groupName,
                        AssemblyName = assemblyName,
                        ModuleName = type.Module.ScopeName,
                        ModuleKey = groupId + "|" + assemblyName + "|" + type.Module.ScopeName
                    };
                    MethodInfo[] methods;
                    try
                    {
                        methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                            BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    }
                    catch (Exception ex)
                    {
                        partialTypes++;
                        log.MetadataFailure(assemblyName + " :: " + typeEntry.TypeName, "Could not inspect class", ex);
                        continue;
                    }
                    foreach (MethodInfo method in methods)
                    {
                        try
                        {
                            string signature = InstrumentationSafety.GetSignature(method);
                            string key = typeEntry.ModuleKey + "|" + signature;
                            string unsupported = GetGeneralUnsupportedReason(method, catalog);
                            string display = method.Name + "(" + string.Join(", ", method.GetParameters().Select(parameter =>
                                InstrumentationSafety.GetTypeName(parameter.ParameterType))) + ")";
                            string tooltip = assemblyName + " / " + type.Module.ScopeName + "\n" + signature;
                            if (unsupported != null)
                                tooltip += "\nUnavailable: " + unsupported;
                            var entry = new GeneralMethodEntry
                            {
                                TypeEntry = typeEntry,
                                Method = method,
                                Key = key,
                                DisplayName = display,
                                TableDisplayName = type.Name + ":" + display,
                                Tooltip = tooltip,
                                SearchText = groupName + " " + groupId + " " + assemblyName + " " + type.Module.ScopeName + " " + signature,
                                UnsupportedReason = unsupported,
                                Selected = previous.TryGetValue(key, out bool selected) ? selected : _selectionPolicy.Resolve(key)
                            };
                            typeEntry.Methods.Add(entry);
                        }
                        catch (Exception ex)
                        {
                            partialMethods++;
                            _safetyReport.Failure(method, "Could not inspect method", ex);
                            log.MethodFailure(method, "Could not inspect method", ex);
                        }
                    }
                    typeEntry.Methods.Sort((left, right) => string.CompareOrdinal(left.DisplayName, right.DisplayName));
                    if (typeEntry.Methods.Count > 0)
                        discovered.Add(typeEntry);
                }
            }

            ResetAllStats();
            _measuredEntries.Clear();
            _types.Clear();
            _types.AddRange(discovered);
            _methodsByKey.Clear();
            foreach (MethodTypeEntry type in discovered)
            {
                foreach (GeneralMethodEntry method in type.Methods)
                    _methodsByKey.Add(method.Key, method);
            }
            _selectionGroupExpanded.Clear();
            ExpandPathsToSelected();
            _listReady = true;
            _selectedMethodCount = _methodsByKey.Values.Count(entry => entry.Selected);
            _status = $"List ready. Assemblies: {discovered.Select(type => type.GroupId).Distinct().Count()}. " +
                      $"Classes: {discovered.Count}. Methods: {_methodsByKey.Count}. " +
                      $"Available: {_methodsByKey.Values.Count(entry => entry.UnsupportedReason == null)}.";
            if (partialAssemblies > 0 || partialTypes > 0 || partialMethods > 0)
            {
                string failures = $"Metadata failures: {partialAssemblies} assemblies, {partialTypes} classes, {partialMethods} methods.";
                _status += " " + failures;
                log.Summary(failures, hasFailures: true);
            }
            if (_selectionPolicy.LastError != null)
                _status += " Selection file read failed: " + _selectionPolicy.LastError;
            MarkSelectionRowsDirty();
            MarkViewDirty();
        }
        catch (Exception ex)
        {
            _status = "Discovery failed: " + ex.GetType().Name + ": " + ex.Message;
            _safetyReport.Failure(null, "Discovery failed", ex);
            log.Failure("Discovery failed", ex);
        }
    }

    private static string GetGeneralUnsupportedReason(MethodInfo method, InstrumentationCatalog catalog)
    {
        string unsupported = InstrumentationSafety.GetUnsupportedReason(method);
        if (unsupported != null)
            return unsupported;
        if (catalog.IsPatchCallback(method))
            return "Registered Harmony callback; use Patch Profiler.";

        var attributes = method.GetCustomAttributesData();
        if (attributes.Any(attribute => HarmonyCallbackAttributes.Contains(attribute.AttributeType.FullName)))
            return "Harmony callback or patch infrastructure; use Patch Profiler.";
        if (HarmonyCallbackNames.Contains(method.Name) && method.DeclaringType.GetCustomAttributesData()
            .Any(attribute => attribute.AttributeType.FullName == "HarmonyLib.HarmonyPatch"))
            return "Conventional Harmony patch callback; use Patch Profiler.";
        if (typeof(MonoBehaviour).IsAssignableFrom(method.DeclaringType) && UnityMessageNames.Contains(method.Name))
            return "MonoBehaviour message; use the dedicated MonoBehaviour profilers.";
        if (attributes.Any(attribute => attribute.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute" ||
                                        attribute.AttributeType.FullName == "System.Runtime.CompilerServices.IteratorStateMachineAttribute"))
            return "Async/iterator entry methods do not represent one synchronous invocation.";
        Type declaringType = method.DeclaringType;
        Type[] interfaces = declaringType.GetInterfaces();
        bool asyncStateMachine = interfaces.Any(type => type.FullName == "System.Runtime.CompilerServices.IAsyncStateMachine");
        bool iteratorStateMachine = interfaces.Any(type => type.FullName == "System.Collections.IEnumerator") &&
            (declaringType.Name.IndexOf('<') >= 0 || declaringType.GetCustomAttributesData().Any(attribute =>
                attribute.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute"));
        // Embedded/merged helper libraries may lose CompilerGeneratedAttribute. Do not rely on it alone.
        if (asyncStateMachine || iteratorStateMachine)
            return "Compiler-generated async/iterator state machines are excluded.";
        return null;
    }

    private static bool MatchesEntry(GeneralMethodEntry entry, string search) =>
        string.IsNullOrWhiteSpace(search) || entry.SearchText.IndexOf(search.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
}
