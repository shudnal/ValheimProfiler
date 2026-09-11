#nullable disable

using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ValheimProfiler.Valheim;

/// <summary>
/// While profiler IMGUI windows are visible, keep Valheim's native cursor owners from
/// recapturing/hiding the cursor. When the profiler closes, those methods run normally and
/// ValheimCursorController asks the active scene to recompute its current cursor state.
/// </summary>
[HarmonyPatch]
internal static class NativeCursorWindowOverridePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        MethodInfo gameCamera = AccessTools.DeclaredMethod(typeof(GameCamera), nameof(GameCamera.UpdateMouseCapture));
        MethodInfo menu = AccessTools.DeclaredMethod(typeof(Menu), nameof(Menu.UpdateCursor));
        MethodInfo fejd = AccessTools.DeclaredMethod(typeof(FejdStartup), nameof(FejdStartup.UpdateCursor));

        if (gameCamera != null)
            yield return gameCamera;
        if (menu != null)
            yield return menu;
        if (fejd != null)
            yield return fejd;
    }

    [HarmonyPriority(Priority.First)]
    private static bool Prefix()
    {
        ValheimProfilerApp app = ValheimProfilerPlugin.Instance?.App;
        if (app?.HasVisibleWindows != true || !Application.isFocused)
            return true;

        app.ApplyCursorOverride();
        return false;
    }
}
