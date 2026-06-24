#nullable disable

using UnityEngine;

namespace ValheimProfiler.UI;

internal sealed class TooltipManager
{
    private const float PointerOffsetY = 25f;
    private const float MaxAbsoluteWidth = 620f;
    private const float MaxRelativeWidth = 0.8f;
    private const float ExtraHeight = 10f;

    private readonly ThemeManager _theme;

    internal TooltipManager(ThemeManager theme)
    {
        _theme = theme;
    }

    internal void Draw(Rect area) => Draw(GUI.tooltip, Event.current.mousePosition, area);

    internal void Draw(string rawTooltip, Vector2 pointerPosition, Rect area)
    {
        if (string.IsNullOrEmpty(rawTooltip))
            return;

        GUIStyle style = _theme.TooltipStyle;
        if (style == null)
            return;

        string tooltip = rawTooltip.Replace("\r\n", "\n").Replace('\r', '\n');
        GUIContent content = new(tooltip);

        float width = 0f;
        string[] lines = tooltip.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            style.CalcMinMaxWidth(new GUIContent(lines[i]), out _, out float lineWidth);
            width = Mathf.Max(width, lineWidth);
        }

        float maxWidth = Mathf.Max(1f, Mathf.Min(MaxAbsoluteWidth, area.width * MaxRelativeWidth));
        width = Mathf.Clamp(width + 2f, 1f, maxWidth);
        float height = Mathf.Min(
            style.CalcHeight(content, width) + ExtraHeight,
            Mathf.Max(1f, area.height));

        Vector2 mouse = pointerPosition;
        float x = mouse.x + width > area.xMax
            ? area.xMax - width
            : mouse.x;
        float y = mouse.y + PointerOffsetY + height > area.yMax
            ? mouse.y - height
            : mouse.y + PointerOffsetY;

        x = Mathf.Clamp(x, area.xMin, Mathf.Max(area.xMin, area.xMax - width));
        y = Mathf.Clamp(y, area.yMin, Mathf.Max(area.yMin, area.yMax - height));

        GUI.Box(new Rect(x, y, width, height), tooltip, style);
    }
}
