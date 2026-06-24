#nullable disable

using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ValheimProfiler.Valheim;

[HarmonyPatch]
internal static class GuiPointScalingPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(GUIUtility), nameof(GUIUtility.ScreenToGUIPoint));
        yield return AccessTools.Method(typeof(GUIUtility), nameof(GUIUtility.GUIToScreenPoint));
    }

    [HarmonyPriority(Priority.First)]
    private static void Prefix(ref Matrix4x4 __state)
    {
        if (ValheimProfilerPlugin.Instance?.App?.IsDrawingUi != true)
            return;

        __state = GUI.matrix;
        GUI.matrix = Matrix4x4.identity;
    }

    [HarmonyPriority(Priority.First)]
    private static void Postfix(Matrix4x4 __state)
    {
        if (ValheimProfilerPlugin.Instance?.App?.IsDrawingUi == true)
            GUI.matrix = __state;
    }
}