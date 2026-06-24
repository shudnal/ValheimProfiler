#nullable disable

using BepInEx;
using BepInEx.Bootstrap;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ValheimProfiler.Tools.MonoBehaviourProfiler;

internal sealed partial class MonoBehaviourProfilerTool
{
    private static readonly string[] DefaultCallbackNames =
    {
        "Update",
        "FixedUpdate",
        "LateUpdate",
        "OnGUI"
    };

    private void RefreshBehaviourList()
    {
        if (_profilingActive)
            return;

        try
        {
            _status = "Scanning loaded MonoBehaviours...";
            _selectionPolicy.Reload();

            var directPluginNames = new Dictionary<Assembly, string>();
            foreach (var pair in Chainloader.PluginInfos)
            {
                var info = pair.Value;
                if (info?.Instance == null)
                    continue;

                Assembly assembly = info.Instance.GetType().Assembly;
                string name = info.Metadata?.Name;
                if (string.IsNullOrWhiteSpace(name))
                    name = info.Metadata?.GUID;
                if (string.IsNullOrWhiteSpace(name))
                    name = assembly.GetName().Name;

                directPluginNames[assembly] = name ?? "Unknown mod";
            }

            var gameAssemblies = new HashSet<Assembly>
            {
                typeof(Player).Assembly,
                typeof(ZInput).Assembly,
                typeof(GuiScaler).Assembly
            };

            string pluginRoot = NormalizeDirectoryPath(Paths.PluginPath);
            var foundTypes = new List<BehaviourTypeEntry>();

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!ShouldInspectAssembly(assembly, gameAssemblies, _app.Config.MonoBehaviourIncludeValheimProfilerCallbacks.Value))
                    continue;

                ClassifyAssembly(
                    assembly,
                    gameAssemblies,
                    directPluginNames,
                    pluginRoot,
                    out BehaviourSource source,
                    out string groupName,
                    out string groupId);

                foreach (Type type in GetLoadableTypes(assembly))
                {
                    if (!IsProfileableMonoBehaviourType(type))
                        continue;

                    var typeEntry = new BehaviourTypeEntry
                    {
                        Type = type,
                        GroupId = groupId,
                        GroupName = groupName,
                        AssemblyName = assembly.GetName().Name ?? "Unknown assembly",
                        TypeName = type.FullName ?? type.Name,
                        Source = source,
                        Expanded = false
                    };

                    var addedMethods = new HashSet<MethodInfo>();

                    for (int i = 0; i < DefaultCallbackNames.Length; i++)
                    {
                        string callbackName = DefaultCallbackNames[i];
                        MethodInfo callback = FindEffectiveCallback(type, callbackName);
                        if (callback == null || !addedMethods.Add(callback))
                            continue;

                        bool defaultSelected =
                            source == BehaviourSource.Mod &&
                            IsModAssembly(callback.DeclaringType?.Assembly, gameAssemblies, directPluginNames, pluginRoot);

                        AddMethodEntry(typeEntry, callback, ParseCallbackKind(callbackName), defaultSelected);
                    }

                    if (typeEntry.Methods.Count > 0)
                        foundTypes.Add(typeEntry);
                }
            }

            foundTypes.Sort((left, right) =>
            {
                int group = string.Compare(left.GroupName, right.GroupName, StringComparison.OrdinalIgnoreCase);
                return group != 0 ? group : string.Compare(left.TypeName, right.TypeName, StringComparison.OrdinalIgnoreCase);
            });

            lock (_lock)
            {
                _types.Clear();
                _types.AddRange(foundTypes);

                _methodsByKey.Clear();
                foreach (BehaviourTypeEntry typeEntry in _types)
                {
                    foreach (BehaviourMethodEntry methodEntry in typeEntry.Methods)
                        _methodsByKey[methodEntry.Key] = methodEntry;
                }

                _runtimeEntries = new Dictionary<MethodBase, RuntimeBehaviourEntry[]>();
                _instrumentedMethods.Clear();
                _groupExpanded.Clear();
                _selectionGroupExpanded.Clear();
                _typesPresentInActiveScene.Clear();
            }

            _listReady = true;
            _selectionDirty = false;
            _selectionExpansionInitialized = false;

            if (_presentInActiveScene)
                RefreshActiveScenePresence();

            ExpandPathsToSelected(collapseUnselected: true);
            _selectionExpansionInitialized = true;
            MarkSelectionRowsDirty();
            MarkViewDirty();

            int methodCount = foundTypes.Sum(x => x.Methods.Count);
            int selectedCount = foundTypes.Sum(x => x.Methods.Count(x => x.Selected));
            _status = $"List ready. Types: {foundTypes.Count}. Frame callbacks: {methodCount}. Selected: {selectedCount}.";
        }
        catch (Exception ex)
        {
            _status = $"Scan error: {ex.GetType().Name}: {ex.Message}";
            _logger.LogError(ex);
        }
    }

    private void AddMethodEntry(
        BehaviourTypeEntry typeEntry,
        MethodInfo method,
        ProfileMethodKind kind,
        bool defaultSelected)
    {
        string key = BuildSelectionKey(typeEntry.Type, method);
        bool selected = _selectionPolicy.Resolve(key, defaultSelected);

        typeEntry.Methods.Add(new BehaviourMethodEntry
        {
            TypeEntry = typeEntry,
            Method = method,
            Key = key,
            DisplayName = method?.Name ?? "(unknown method)",
            Kind = kind,
            DefaultSelected = defaultSelected,
            Selected = selected
        });
    }

    private void RefreshActiveScenePresence()
    {
        int activeSceneHandle = SceneManager.GetActiveScene().handle;
        List<BehaviourTypeEntry> snapshot;

        lock (_lock)
            snapshot = _types.ToList();

        var present = new HashSet<Type>();

        foreach (BehaviourTypeEntry typeEntry in snapshot)
        {
            if (typeEntry?.Type == null)
                continue;

            try
            {
                UnityEngine.Object[] objects = Resources.FindObjectsOfTypeAll(typeEntry.Type);
                for (int i = 0; i < objects.Length; i++)
                {
                    Component component = objects[i] as Component;
                    if (component == null || component.gameObject == null)
                        continue;

                    Scene scene = component.gameObject.scene;
                    if (scene.IsValid() && scene.handle == activeSceneHandle)
                    {
                        present.Add(typeEntry.Type);
                        break;
                    }
                }
            }
            catch
            {
            }
        }

        lock (_lock)
        {
            _typesPresentInActiveScene.Clear();
            foreach (Type type in present)
                _typesPresentInActiveScene.Add(type);
        }

        _activeSceneHandle = activeSceneHandle;
    }

    private bool IsPresentInActiveScene(BehaviourTypeEntry typeEntry)
    {
        if (!_presentInActiveScene)
            return true;

        if (typeEntry?.Type == null)
            return false;

        lock (_lock)
            return _typesPresentInActiveScene.Contains(typeEntry.Type);
    }

    private static bool ShouldInspectAssembly(
        Assembly assembly,
        HashSet<Assembly> gameAssemblies,
        bool includeProfilerMonoBehaviours)
    {
        if (assembly == null || assembly.IsDynamic)
            return false;

        if (assembly == typeof(ValheimProfilerPlugin).Assembly && !includeProfilerMonoBehaviours)
            return false;

        string name;
        try
        {
            name = assembly.GetName().Name ?? string.Empty;
        }
        catch
        {
            return false;
        }

        if (gameAssemblies != null && gameAssemblies.Contains(assembly))
            return true;

        if (name.StartsWith("UnityEngine", StringComparison.Ordinal) ||
            name.StartsWith("System", StringComparison.Ordinal) ||
            name.StartsWith("Microsoft", StringComparison.Ordinal) ||
            name.StartsWith("Mono.", StringComparison.Ordinal) ||
            name.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("BepInEx", StringComparison.Ordinal) ||
            name.StartsWith("0Harmony", StringComparison.Ordinal) ||
            name.StartsWith("Harmony", StringComparison.Ordinal))
            return false;

        return true;
    }

    private static void ClassifyAssembly(
        Assembly assembly,
        HashSet<Assembly> gameAssemblies,
        Dictionary<Assembly, string> directPluginNames,
        string pluginRoot,
        out BehaviourSource source,
        out string groupName,
        out string groupId)
    {
        string assemblyName = assembly.GetName().Name ?? "Unknown assembly";

        if (gameAssemblies != null && gameAssemblies.Contains(assembly))
        {
            source = BehaviourSource.Valheim;
            groupName = "Valheim";
            groupId = "valheim|" + assemblyName;
            return;
        }

        if (directPluginNames.TryGetValue(assembly, out string pluginName))
        {
            source = BehaviourSource.Mod;
            groupName = pluginName;
            groupId = "mod|" + assemblyName;
            return;
        }

        string location = GetAssemblyLocation(assembly);
        if (!string.IsNullOrEmpty(pluginRoot) &&
            !string.IsNullOrEmpty(location) &&
            location.StartsWith(pluginRoot, StringComparison.OrdinalIgnoreCase))
        {
            source = BehaviourSource.Mod;
            groupName = assemblyName;
            groupId = "mod|" + assemblyName;
            return;
        }

        source = BehaviourSource.Other;
        groupName = assemblyName;
        groupId = "other|" + assemblyName;
    }

    private static bool IsProfileableMonoBehaviourType(Type type)
    {
        if (type == null ||
            type.IsAbstract ||
            type.IsInterface ||
            type.ContainsGenericParameters)
            return false;

        try
        {
            return type != typeof(MonoBehaviour) && typeof(MonoBehaviour).IsAssignableFrom(type);
        }
        catch
        {
            return false;
        }
    }

    private static MethodInfo FindEffectiveCallback(Type type, string methodName)
    {
        for (Type current = type; current != null && current != typeof(MonoBehaviour); current = current.BaseType)
        {
            MethodInfo method;
            try
            {
                method = current.GetMethod(
                    methodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    null,
                    Type.EmptyTypes,
                    null);
            }
            catch
            {
                method = null;
            }

            if (method == null || method.ReturnType != typeof(void) || !HasManagedBody(method))
                continue;

            return method;
        }

        return null;
    }

    private static bool IsModAssembly(
        Assembly assembly,
        HashSet<Assembly> gameAssemblies,
        Dictionary<Assembly, string> directPluginNames,
        string pluginRoot)
    {
        if (assembly == null || (gameAssemblies != null && gameAssemblies.Contains(assembly)))
            return false;

        if (directPluginNames.ContainsKey(assembly))
            return true;

        string location = GetAssemblyLocation(assembly);
        return !string.IsNullOrEmpty(pluginRoot) &&
               !string.IsNullOrEmpty(location) &&
               location.StartsWith(pluginRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasManagedBody(MethodBase method)
    {
        try
        {
            return method != null && method.GetMethodBody() != null;
        }
        catch
        {
            return false;
        }
    }

    private static ProfileMethodKind ParseCallbackKind(string methodName)
    {
        switch (methodName)
        {
            case "FixedUpdate":
                return ProfileMethodKind.FixedUpdate;
            case "LateUpdate":
                return ProfileMethodKind.LateUpdate;
            case "OnGUI":
                return ProfileMethodKind.OnGUI;
            default:
                return ProfileMethodKind.Update;
        }
    }

    private static Type[] GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types?.Where(type => type != null).ToArray() ?? Array.Empty<Type>();
        }
        catch
        {
            return Array.Empty<Type>();
        }
    }

    private static string BuildSelectionKey(Type type, MethodInfo method)
    {
        string assembly = type.Assembly.GetName().Name ?? "Unknown";
        string typeName = type.FullName ?? type.Name;
        string parameters;

        try
        {
            parameters = string.Join(",", method.GetParameters()
                .Select(parameter => parameter.ParameterType.FullName ?? parameter.ParameterType.Name)
                .ToArray());
        }
        catch
        {
            parameters = "?";
        }

        return $"{assembly}|{typeName}|{method.Name}({parameters})";
    }

    private static string GetAssemblyLocation(Assembly assembly)
    {
        try
        {
            return NormalizeFilePath(assembly.Location);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeDirectoryPath(string path)
    {
        string normalized = NormalizeFilePath(path);
        if (string.IsNullOrEmpty(normalized))
            return string.Empty;

        return normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
               Path.DirectorySeparatorChar;
    }

    private static string NormalizeFilePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }
}