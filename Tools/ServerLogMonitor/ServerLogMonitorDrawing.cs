#nullable disable

using BepInEx.Logging;
using System;
using UnityEngine;

namespace ValheimProfiler.Tools.ServerLogMonitor;

internal sealed partial class ServerLogMonitorTool
{
    private void DrawHeaderCell(
        ref float x,
        float y,
        float width,
        float height,
        string text,
        string tooltip)
    {
        GUI.Label(
            new Rect(x, y, width, height),
            new GUIContent(text ?? string.Empty, tooltip ?? string.Empty),
            _headerLabelStyle);
        x += width;
    }

    private static void DrawCell(
        ref float x,
        float y,
        float width,
        float height,
        string text,
        GUIStyle style)
    {
        GUI.Label(new Rect(x, y, width, height), text ?? string.Empty, style);
        x += width;
    }

    private Color GetLevelColor(LogLevel level)
    {
        if ((level & LogLevel.Fatal) != 0)
            return Color.Lerp(_theme.TextColor, new Color(1f, 0.18f, 0.18f, 1f), 0.78f);
        if ((level & LogLevel.Error) != 0)
            return Color.Lerp(_theme.TextColor, new Color(1f, 0.30f, 0.30f, 1f), 0.68f);
        if ((level & LogLevel.Warning) != 0)
            return Color.Lerp(_theme.TextColor, new Color(1f, 0.76f, 0.18f, 1f), 0.62f);
        if ((level & LogLevel.Message) != 0)
            return Color.white;
        if ((level & LogLevel.Debug) != 0)
            return Color.Lerp(_theme.TextColor, Color.gray, 0.48f);
        return _theme.TextColor;
    }

    private static bool Contains(string value, string search) =>
        !string.IsNullOrEmpty(value) &&
        !string.IsNullOrEmpty(search) &&
        value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
}
