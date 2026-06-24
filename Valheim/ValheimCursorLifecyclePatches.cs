#nullable disable

using HarmonyLib;
using UnityEngine;

namespace ValheimProfiler.Valheim;

// FejdStartup owns the native cursor defaults on the main-menu side of a
// world transition. If a profiler window stays open during connect/disconnect,
// the actual cursor must remain unlocked, but the state restored when the UI is
// later hidden has to follow the destination scene.
[HarmonyPatch(typeof(FejdStartup), "Start")]
internal static class FejdStartupStartCursorPatch
{
    private static void Postfix()
    {
        ValheimProfilerPlugin.Instance?.App?.OverrideCursorReleaseState(
            CursorLockMode.None,
            visible: true);
    }
}

[HarmonyPatch(typeof(FejdStartup), "OnDestroy")]
internal static class FejdStartupOnDestroyCursorPatch
{
    private static void Prefix()
    {
        ValheimProfilerPlugin.Instance?.App?.OverrideCursorReleaseState(
            CursorLockMode.Locked,
            visible: false);
    }
}
