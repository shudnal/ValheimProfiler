#nullable disable

using BepInEx.Bootstrap;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;

namespace ValheimProfiler.Tools.PatchProfiler;

internal sealed partial class PatchProfilerTool
{
    private void BuildAssemblyPluginMap()
    {
        var nameMap = new Dictionary<Assembly, string>();
        var guidMap = new Dictionary<Assembly, string>();
        var guidToName = new Dictionary<string, string>();

        foreach (var kv in Chainloader.PluginInfos)
        {
            var pi = kv.Value;
            if (pi?.Instance == null)
                continue;

            var asm = pi.Instance.GetType().Assembly;

            var guid = pi.Metadata?.GUID;
            if (string.IsNullOrWhiteSpace(guid))
                guid = kv.Key ?? "Unknown";

            var name = pi.Metadata?.Name;
            if (string.IsNullOrWhiteSpace(name))
                name = guid;

            nameMap[asm] = name;
            guidMap[asm] = guid;

            if (!guidToName.ContainsKey(guid))
                guidToName[guid] = name;
        }

        lock (_lock)
        {
            _assemblyToPluginName = nameMap;
            _assemblyToPluginGuid = guidMap;
            _pluginGuidToName = guidToName;
        }
    }

    private string GuessModName(MethodBase method, string knownGuid = null)
    {
        try
        {
            lock (_lock)
            {
                if (!string.IsNullOrWhiteSpace(knownGuid) && _pluginGuidToName.TryGetValue(knownGuid, out var byGuidName))
                    return byGuidName;
            }

            if (method == null)
                return "Unknown";

            var asm = method.DeclaringType?.Assembly;
            if (asm == null)
                return "Unknown";

            lock (_lock)
            {
                if (_assemblyToPluginName.TryGetValue(asm, out var name))
                    return name;
            }

            return asm.GetName().Name ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    private string GuessModGuid(MethodBase method, string ownerId)
    {
        try
        {
            lock (_lock)
            {
                if (!string.IsNullOrWhiteSpace(ownerId) && _pluginGuidToName.ContainsKey(ownerId))
                    return ownerId;
            }

            if (method == null)
                return !string.IsNullOrWhiteSpace(ownerId) ? ownerId : "(unknown guid)";

            var asm = method.DeclaringType?.Assembly;
            if (asm == null)
                return !string.IsNullOrWhiteSpace(ownerId) ? ownerId : "(unknown guid)";

            lock (_lock)
            {
                if (_assemblyToPluginGuid.TryGetValue(asm, out var guid))
                    return guid;
            }

            return asm.GetName().Name ?? (!string.IsNullOrWhiteSpace(ownerId) ? ownerId : "(unknown guid)");
        }
        catch
        {
            return !string.IsNullOrWhiteSpace(ownerId) ? ownerId : "(unknown guid)";
        }
    }

    private static string GetMethodDisplay(MethodBase m)
    {
        if (m == null)
            return "(null)";

        try
        {
            var dt = m.DeclaringType;
            string typeName = dt != null ? dt.FullName : "(no type)";
            return $"{typeName}::{m.Name}";
        }
        catch
        {
            return "(unprintable)";
        }
    }

    private static string GetMethodIdentity(MethodBase method)
    {
        if (method == null)
            return "(null)";

        try
        {
            return method.Module.ModuleVersionId.ToString("N") + ":" + method.MetadataToken;
        }
        catch
        {
            return GetMethodDisplay(method) + "#" + method.GetHashCode();
        }
    }

    private static string Truncate(string value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || maxChars <= 0 || value.Length <= maxChars)
            return value ?? string.Empty;

        if (maxChars == 1)
            return "…";

        return value.Substring(0, maxChars - 1) + "…";
    }

    private static string FormatMs(double value)
    {
        if (value <= 0)
            return "0.000";

        if (value > MaxDisplayedMs)
            return $"{MaxDisplayedMs:0.000}+";

        return $"{value:0.000}";
    }

    private static string FormatRawMax(MaxAnalyticsSnapshot max)
    {
        string value = FormatMs(max.RawMaxMs);
        return ShouldMarkRawMax(max) ? value + "!" : value;
    }

    private static string FormatCount(int value) => FormatCount((long)value);

    private static string FormatCount(long value)
    {
        if (value <= 0)
            return "0";

        if (value > MaxDisplayedCount)
            return MaxDisplayedCount + "+";

        return value.ToString();
    }

    private static string FormatCount(double value)
    {
        if (value <= 0)
            return "0";

        if (value > MaxDisplayedCount)
            return MaxDisplayedCount + "+";

        return $"{value:0.##}";
    }

    private static bool ShouldMarkRawMax(MaxAnalyticsSnapshot max) =>
        IsIsolatedRawMax(max) || IsGcContaminatedRawMax(max);

    private static bool IsIsolatedRawMax(MaxAnalyticsSnapshot max)
    {
        if (max.RawMaxMs < IsolatedRawMaxMinMs)
            return false;

        if (max.WindowSampleCount < 3)
            return false;

        if (max.ThirdMaxMs <= 0)
            return false;

        return max.RawMaxMs >= max.ThirdMaxMs * IsolatedRawMaxMultiplier;
    }

    private static bool IsGcContaminatedRawMax(MaxAnalyticsSnapshot max)
    {
        if (max.RawMaxMs < IsolatedRawMaxMinMs)
            return false;

        return max.GcSampleCount > 0;
    }

}