#nullable disable

using UnityEngine;

namespace ValheimProfiler.UI;

internal static class ProfilerGui
{
    internal static bool ToggleLayout(
        ThemeManager theme,
        bool value,
        GUIContent content,
        float width,
        GUIStyle labelStyle = null,
        float verticalOffset = 1f)
    {
        GUIStyle textStyle = labelStyle ?? GUI.skin.label;
        float height = Mathf.Max(18f, textStyle.lineHeight + 4f);
        Rect rect = GUILayoutUtility.GetRect(
            width,
            height,
            GUILayout.Width(width),
            GUILayout.Height(height));

        return Toggle(theme, rect, value, content, textStyle, verticalOffset);
    }

    internal static bool Toggle(
        ThemeManager theme,
        Rect rect,
        bool value,
        GUIContent content,
        GUIStyle labelStyle = null,
        float verticalOffset = 1f)
    {
        GUIStyle textStyle = labelStyle ?? GUI.skin.label;
        string tooltip = content?.tooltip ?? string.Empty;
        GUIContent hitContent = new(string.Empty, tooltip);
        bool result = GUI.Toggle(rect, value, hitContent, GUIStyle.none);

        float size = theme?.CompactToggleSize ?? 10f;
        float centeredOffset = Mathf.Ceil(Mathf.Max(0f, (rect.height - size) * 0.5f));
        Rect checkRect = new(
            rect.x + 1f,
            rect.y + centeredOffset + verticalOffset,
            size,
            size);

        GUI.Box(
            checkRect,
            new GUIContent(string.Empty, tooltip),
            result ? theme.CompactToggleOnStyle : theme.CompactToggleOffStyle);

        string text = content?.text ?? string.Empty;
        if (!string.IsNullOrEmpty(text))
        {
            float textX = checkRect.xMax + 4f;
            GUI.Label(
                new Rect(textX, rect.y, Mathf.Max(0f, rect.xMax - textX), rect.height),
                new GUIContent(text, tooltip),
                textStyle);
        }

        return result;
    }
}
