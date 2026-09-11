#nullable disable

using UnityEngine;

namespace ValheimProfiler.UI;

internal sealed class TooltipManager
{
    private const float PointerOffsetX = 12f;
    private const float PointerOffsetY = 24f;
    private const float EdgePadding = 4f;
    private const float MaxAbsoluteWidth = 620f;
    private const float MaxRelativeWidth = 0.8f;

    private readonly ThemeManager _theme;
    private readonly GUIContent _content = new();

    private string _cachedSource;
    private GUIStyle _cachedStyle;
    private Vector2 _cachedBounds;
    private Vector2 _cachedSize;

    internal TooltipManager(ThemeManager theme)
    {
        _theme = theme;
    }

    internal void Draw(Rect area) => Draw(GUI.tooltip, Event.current?.mousePosition ?? Vector2.zero, area);

    internal void Draw(string rawTooltip, Vector2 pointerPosition, Rect area)
    {
        Event current = Event.current;
        if (current == null || current.type != EventType.Repaint || !Application.isFocused)
            return;
        if (string.IsNullOrEmpty(rawTooltip) || !area.Contains(pointerPosition))
            return;

        GUIStyle style = _theme.TooltipStyle;
        if (style == null)
            return;

        Rect bounds = area;
        bounds.xMin += EdgePadding;
        bounds.yMin += EdgePadding;
        bounds.xMax -= EdgePadding;
        bounds.yMax -= EdgePadding;
        if (bounds.width < 20f || bounds.height < 20f)
            return;

        if (_cachedSource != rawTooltip || !ReferenceEquals(_cachedStyle, style) || _cachedBounds != bounds.size)
        {
            _cachedSource = rawTooltip;
            _cachedStyle = style;
            _cachedBounds = bounds.size;
            _content.text = rawTooltip.Replace("\r\n", "\n").Replace('\r', '\n');

            style.CalcMinMaxWidth(_content, out _, out float preferredWidth);
            float maxWidth = Mathf.Min(MaxAbsoluteWidth, bounds.width * MaxRelativeWidth);
            float width = Mathf.Min(Mathf.Max(80f, preferredWidth + 2f), Mathf.Max(1f, maxWidth));
            float height = style.CalcHeight(_content, width);

            if (height > bounds.height)
            {
                width = Mathf.Min(MaxAbsoluteWidth, bounds.width);
                height = style.CalcHeight(_content, width);
            }

            _cachedSize = new Vector2(
                Mathf.Min(width, bounds.width),
                Mathf.Min(height, bounds.height));
        }

        float tooltipWidth = _cachedSize.x;
        float tooltipHeight = _cachedSize.y;
        float x = Mathf.Clamp(
            pointerPosition.x + PointerOffsetX,
            bounds.xMin,
            Mathf.Max(bounds.xMin, bounds.xMax - tooltipWidth));
        float y = pointerPosition.y + PointerOffsetY;
        if (y + tooltipHeight > bounds.yMax)
            y = pointerPosition.y - tooltipHeight - EdgePadding;
        y = Mathf.Clamp(
            y,
            bounds.yMin,
            Mathf.Max(bounds.yMin, bounds.yMax - tooltipHeight));

        bool oldEnabled = GUI.enabled;
        Color oldBackground = GUI.backgroundColor;
        Color oldColor = GUI.color;
        Color oldContentColor = GUI.contentColor;
        try
        {
            // Tooltips must remain readable for disabled/read-only controls.
            GUI.enabled = true;
            GUI.color = Color.white;
            GUI.contentColor = Color.white;
            GUI.Box(new Rect(x, y, tooltipWidth, tooltipHeight), _content, style);
        }
        finally
        {
            GUI.enabled = oldEnabled;
            GUI.backgroundColor = oldBackground;
            GUI.color = oldColor;
            GUI.contentColor = oldContentColor;
        }
    }
}
