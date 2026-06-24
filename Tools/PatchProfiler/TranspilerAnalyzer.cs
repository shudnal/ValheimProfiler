#nullable disable

using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace ValheimProfiler.Tools.PatchProfiler;

internal sealed partial class PatchProfilerTool
{
    private sealed class InjectedRuntimeCall
    {
        public MethodBase CalledMethod;
        public int AddedCallSites;
        public readonly HashSet<int> FinalMethodOrdinals = new HashSet<int>();
        public readonly List<HarmonyLib.Patch> SourcePatches = new List<HarmonyLib.Patch>();
    }

    private sealed class DirectCallSite
    {
        public MethodBase Method;
        public string Identity;
        public int MethodOrdinal;
    }

    private List<InjectedRuntimeCall> FindInjectedRuntimeCalls(
        MethodBase target,
        IReadOnlyList<HarmonyLib.Patch> transpilers)
    {
        var result = new List<InjectedRuntimeCall>();

        if (target == null || transpilers == null || transpilers.Count == 0)
            return result;

        List<CodeInstruction> originalInstructions;
        List<CodeInstruction> finalInstructions;

        try
        {
            originalInstructions = PatchProcessor.GetOriginalInstructions(target);
            finalInstructions = PatchProcessor.GetCurrentInstructions(target);
        }
        catch (Exception ex)
        {
            _logger.LogDebug($"Could not compare transpiled IL for {GetMethodDisplay(target)}: {ex.GetType().Name}: {ex.Message}");
            return result;
        }

        List<DirectCallSite> originalCalls = GetDirectRuntimeCalls(originalInstructions);
        List<DirectCallSite> finalCalls = GetDirectRuntimeCalls(finalInstructions);
        bool[] addedFinalCalls = FindAddedFinalCalls(originalCalls, finalCalls);
        var byMethod = new Dictionary<string, InjectedRuntimeCall>(StringComparer.Ordinal);

        for (int i = 0; i < finalCalls.Count; i++)
        {
            if (!addedFinalCalls[i])
                continue;

            DirectCallSite finalCall = finalCalls[i];
            MethodBase calledMethod = finalCall.Method;
            if (!IsProfileableInjectedRuntimeCall(calledMethod, target))
                continue;

            if (!byMethod.TryGetValue(finalCall.Identity, out InjectedRuntimeCall injected))
            {
                injected = new InjectedRuntimeCall
                {
                    CalledMethod = calledMethod
                };
                byMethod[finalCall.Identity] = injected;
            }

            injected.AddedCallSites++;
            injected.FinalMethodOrdinals.Add(finalCall.MethodOrdinal);
        }

        foreach (InjectedRuntimeCall injected in byMethod.Values)
        {
            foreach (HarmonyLib.Patch source in ResolveLikelySourceTranspilers(injected.CalledMethod, target, transpilers))
                injected.SourcePatches.Add(source);

            result.Add(injected);
        }

        return result
            .OrderBy(call => GetMethodDisplay(call.CalledMethod), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<DirectCallSite> GetDirectRuntimeCalls(IEnumerable<CodeInstruction> instructions)
    {
        var result = new List<DirectCallSite>();
        var ordinalByMethod = new Dictionary<string, int>(StringComparer.Ordinal);

        if (instructions == null)
            return result;

        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction == null)
                continue;

            if (instruction.opcode != OpCodes.Call && instruction.opcode != OpCodes.Callvirt)
                continue;

            if (!(instruction.operand is MethodBase calledMethod))
                continue;

            string identity = GetMethodIdentity(calledMethod);
            ordinalByMethod.TryGetValue(identity, out int ordinal);
            ordinalByMethod[identity] = ordinal + 1;

            result.Add(new DirectCallSite
            {
                Method = calledMethod,
                Identity = identity,
                MethodOrdinal = ordinal
            });
        }

        return result;
    }

    private static bool[] FindAddedFinalCalls(
        IReadOnlyList<DirectCallSite> originalCalls,
        IReadOnlyList<DirectCallSite> finalCalls)
    {
        var added = new bool[finalCalls.Count];
        if (finalCalls.Count == 0)
            return added;

        if (originalCalls.Count == 0)
        {
            for (int i = 0; i < added.Length; i++)
                added[i] = true;

            return added;
        }

        // Comparing only the ordered direct-call stream keeps the diff small while still
        // distinguishing calls inserted before or between existing calls in the target.
        long matrixCells = (long)(originalCalls.Count + 1) * (finalCalls.Count + 1);
        if (matrixCells <= 2_000_000)
            return FindAddedFinalCallsWithLcs(originalCalls, finalCalls);

        // Very large methods fall back to count-based matching to avoid a large temporary matrix.
        var remainingOriginal = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < originalCalls.Count; i++)
        {
            string identity = originalCalls[i].Identity;
            remainingOriginal.TryGetValue(identity, out int count);
            remainingOriginal[identity] = count + 1;
        }

        for (int i = 0; i < finalCalls.Count; i++)
        {
            string identity = finalCalls[i].Identity;
            if (remainingOriginal.TryGetValue(identity, out int count) && count > 0)
            {
                remainingOriginal[identity] = count - 1;
                continue;
            }

            added[i] = true;
        }

        return added;
    }

    private static bool[] FindAddedFinalCallsWithLcs(
        IReadOnlyList<DirectCallSite> originalCalls,
        IReadOnlyList<DirectCallSite> finalCalls)
    {
        int originalCount = originalCalls.Count;
        int finalCount = finalCalls.Count;
        var lcs = new int[originalCount + 1, finalCount + 1];

        for (int i = originalCount - 1; i >= 0; i--)
        {
            string originalIdentity = originalCalls[i].Identity;
            for (int j = finalCount - 1; j >= 0; j--)
            {
                if (string.Equals(originalIdentity, finalCalls[j].Identity, StringComparison.Ordinal))
                    lcs[i, j] = lcs[i + 1, j + 1] + 1;
                else
                    lcs[i, j] = Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var matchedFinal = new bool[finalCount];
        int originalIndex = 0;
        int finalIndex = 0;

        while (originalIndex < originalCount && finalIndex < finalCount)
        {
            if (string.Equals(
                    originalCalls[originalIndex].Identity,
                    finalCalls[finalIndex].Identity,
                    StringComparison.Ordinal))
            {
                matchedFinal[finalIndex] = true;
                originalIndex++;
                finalIndex++;
                continue;
            }

            if (lcs[originalIndex + 1, finalIndex] >= lcs[originalIndex, finalIndex + 1])
                originalIndex++;
            else
                finalIndex++;
        }

        var added = new bool[finalCount];
        for (int i = 0; i < finalCount; i++)
            added[i] = !matchedFinal[i];

        return added;
    }

    private bool IsProfileableInjectedRuntimeCall(MethodBase calledMethod, MethodBase target)
    {
        if (calledMethod == null || target == null)
            return false;

        if (GetMethodIdentity(calledMethod) == GetMethodIdentity(target))
            return false;

        if (!(calledMethod is MethodInfo methodInfo))
            return false;

        if (calledMethod.DeclaringType == null)
            return false;

        if (calledMethod.DeclaringType.Assembly == typeof(ValheimProfilerPlugin).Assembly)
            return false;

        if (calledMethod.ContainsGenericParameters || calledMethod.IsAbstract)
            return false;

        string typeName = calledMethod.DeclaringType.FullName ?? string.Empty;
        if (typeName.StartsWith("HarmonyLib.", StringComparison.Ordinal) ||
            typeName.StartsWith("System.", StringComparison.Ordinal) ||
            typeName.StartsWith("Microsoft.", StringComparison.Ordinal) ||
            typeName.StartsWith("UnityEngine.", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            if (methodInfo.GetMethodBody() == null)
                return false;
        }
        catch
        {
            return false;
        }

        return true;
    }

    private List<HarmonyLib.Patch> ResolveLikelySourceTranspilers(
        MethodBase calledMethod,
        MethodBase target,
        IReadOnlyList<HarmonyLib.Patch> transpilers)
    {
        var available = transpilers
            .Where(patch => patch?.PatchMethod != null &&
                            patch.PatchMethod.DeclaringType?.Assembly != typeof(ValheimProfilerPlugin).Assembly)
            .ToList();

        if (available.Count <= 1)
            return available;

        Assembly calledAssembly = calledMethod?.DeclaringType?.Assembly;
        if (calledAssembly != null)
        {
            List<HarmonyLib.Patch> sameAssembly = available
                .Where(patch => patch.PatchMethod.DeclaringType?.Assembly == calledAssembly)
                .ToList();

            if (sameAssembly.Count > 0)
                return sameAssembly;
        }

        string calledGuid = GuessModGuid(calledMethod, null);
        List<HarmonyLib.Patch> sameMod = available
            .Where(patch => string.Equals(
                GuessModGuid(patch.PatchMethod, patch.owner),
                calledGuid,
                StringComparison.Ordinal))
            .ToList();

        if (sameMod.Count > 0)
            return sameMod;

        return available;
    }
}
