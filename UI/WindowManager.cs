#nullable disable

using BepInEx;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimProfiler.UI;

internal sealed class WindowManager
{
    internal const float DefaultToolWindowWidthFraction = 0.75f;
    internal const float DefaultCompactWindowWidthFraction = 0.25f;

    private const float TitleBarHeight = 22f;
    private const float ResizeEdgeHitSize = 4f;
    private const float ResizeHandleHitSize = 12f;
    private const float ResizeHandleVisualSize = 8f;
    private const float WindowBorderWidth = 1f;
    private const float LayoutSaveDelay = 0.5f;

    private readonly ValheimProfilerConfig _config;
    private readonly GuiScaleController _scale;
    private readonly ThemeManager _theme;
    private readonly TooltipManager _tooltips;
    private readonly List<ProfilerWindow> _windows = new();

    private ProfilerWindow _resizingWindow;
    private Vector2 _resizeStartMouse;
    private Rect _resizeStartRect;
    private bool _resizeWidth;
    private bool _resizeHeight;
    private int _bringToFrontId;
    private bool _resetLayoutRequested;
    private string _overflowTooltip = string.Empty;
    private Vector2 _overflowTooltipPointer;

    internal WindowManager(
        ValheimProfilerConfig config,
        GuiScaleController scale,
        ThemeManager theme,
        TooltipManager tooltips)
    {
        _config = config;
        _scale = scale;
        _theme = theme;
        _tooltips = tooltips;
    }

    internal IReadOnlyList<ProfilerWindow> Windows => _windows;

    internal Vector2 GetDefaultToolWindowSize(float preferredHeight, Vector2 minimumSize) =>
        GetDefaultWindowSize(preferredHeight, minimumSize, DefaultToolWindowWidthFraction);

    internal Vector2 GetDefaultCompactWindowSize(float preferredHeight, Vector2 minimumSize) =>
        GetDefaultWindowSize(preferredHeight, minimumSize, DefaultCompactWindowWidthFraction);

    private Vector2 GetDefaultWindowSize(float preferredHeight, Vector2 minimumSize, float widthFraction)
    {
        float width = Mathf.Max(minimumSize.x, _scale.LogicalWidth * widthFraction);
        float height = Mathf.Clamp(preferredHeight, minimumSize.y, Mathf.Max(minimumSize.y, _scale.LogicalHeight * 0.9f));
        return new Vector2(width, height);
    }

    internal ProfilerWindow Register(ProfilerWindow window)
    {
        if (window == null)
            throw new ArgumentNullException(nameof(window));

        window.WindowFunction = id => DrawWindow(window, id);
        _windows.Add(window);
        return window;
    }

    internal bool HasRequestedVisibleWindows
    {
        get
        {
            for (int i = 0; i < _windows.Count; i++)
            {
                if (_windows[i].RequestedVisible)
                    return true;
            }

            return false;
        }
    }

    internal void BringToFront(ProfilerWindow window)
    {
        if (window != null)
            _bringToFrontId = window.WindowId;
    }

    internal void DrawAll()
    {
        _overflowTooltip = string.Empty;

        if (_resetLayoutRequested)
            ResetLayoutNow();

        for (int i = 0; i < _windows.Count; i++)
        {
            ProfilerWindow window = _windows[i];
            window.ApplyPendingLayoutChanges();

            if (!window.RequestedVisible)
                continue;

            ResolveInitialPosition(window);
            window.Rect = ClampRect(window, window.Rect);

            Rect before = window.Rect;
            window.Rect = GUI.Window(window.WindowId, window.Rect, window.WindowFunction, window.Title, GUI.skin.window);
            window.Rect = ClampRect(window, window.Rect);

            if (RectChanged(before, window.Rect))
                MarkLayoutDirty(window);
        }

        // Only explicit requests from launcher/tool actions alter z-order. Mouse clicks are
        // left to Unity's own IMGUI window manager so external windows such as VCM retain
        // their natural visual and input priority.
        if (_bringToFrontId != 0)
        {
            GUI.BringWindowToFront(_bringToFrontId);
            _bringToFrontId = 0;
        }

        if (!string.IsNullOrEmpty(_overflowTooltip))
        {
            _tooltips.Draw(
                _overflowTooltip,
                _overflowTooltipPointer,
                new Rect(0f, 0f, _scale.LogicalWidth, _scale.LogicalHeight));
        }
    }

    internal void RequestResetLayout() => _resetLayoutRequested = true;

    internal void UpdatePersistence()
    {
        if (_resizingWindow != null && !UnityInput.Current.GetKey(KeyCode.Mouse0))
            ClearResizeState();

        float now = Time.realtimeSinceStartup;
        bool saveConfig = false;

        for (int i = 0; i < _windows.Count; i++)
        {
            ProfilerWindow window = _windows[i];
            window.ApplyPendingLayoutChanges();

            if (!window.LayoutDirty || now < window.SaveAfterRealtime)
                continue;

            window.SaveLayout();
            saveConfig = true;
        }

        if (saveConfig)
            _config.ConfigFile.Save();
    }

    internal void SaveAll()
    {
        for (int i = 0; i < _windows.Count; i++)
            _windows[i].SaveLayout();

        _config.ConfigFile.Save();
    }

    internal void Shutdown()
    {
        for (int i = 0; i < _windows.Count; i++)
            _windows[i].Dispose();

        _windows.Clear();
        ClearResizeState();
    }

    private void ResetLayoutNow()
    {
        _resetLayoutRequested = false;
        ClearResizeState();

        for (int i = 0; i < _windows.Count; i++)
        {
            ProfilerWindow window = _windows[i];
            window.ResetLayoutToDefault();
            ResolveInitialPosition(window);
            window.Rect = ClampRect(window, window.Rect);
            window.SaveLayout();
        }

        _config.ConfigFile.Save();
    }

    private void DrawWindow(ProfilerWindow window, int id)
    {
        _theme.EnsureStyles();
        GUI.tooltip = string.Empty;

        // Resize input is handled before window contents so a scrollbar under the
        // lower-right handle cannot consume the initial mouse-down event.
        HandleResizeInput(window);

        try
        {
            window.DrawContents(id);
        }
        catch (Exception ex)
        {
            GUILayout.Label($"Window error: {ex.GetType().Name}: {ex.Message}");
        }

        DrawWindowBorder(window);
        DrawResizeHandle(window);

        // Tool windows keep the proven VCM behaviour and clip tooltips to their owner.
        // The compact launcher is different: its tooltips are captured here and drawn
        // after all GUI.Window calls so they can extend beyond the launcher's bounds.
        if (window.AllowTooltipOverflow)
        {
            string tooltip = GUI.tooltip;
            if (!string.IsNullOrEmpty(tooltip))
            {
                _overflowTooltip = tooltip;
                _overflowTooltipPointer = window.Rect.position + Event.current.mousePosition;
            }
        }
        else
        {
            _tooltips.Draw(new Rect(0f, 0f, window.Rect.width, window.Rect.height));
        }

        GUI.DragWindow(new Rect(0f, 0f, Mathf.Max(0f, window.Rect.width - 4f), TitleBarHeight));
    }

    private void HandleResizeInput(ProfilerWindow window)
    {
        if (!window.Resizable)
            return;

        Event current = Event.current;
        Vector2 localMouse = current.mousePosition;
        Rect handleRect = GetResizeHandleHitRect(window);

        bool overCorner = handleRect.Contains(localMouse);
        bool overRightEdge =
            !overCorner &&
            localMouse.x >= window.Rect.width - ResizeEdgeHitSize &&
            localMouse.x <= window.Rect.width &&
            localMouse.y >= TitleBarHeight &&
            localMouse.y < handleRect.yMin;
        bool overBottomEdge =
            !overCorner &&
            localMouse.y >= window.Rect.height - ResizeEdgeHitSize &&
            localMouse.y <= window.Rect.height &&
            localMouse.x >= 0f &&
            localMouse.x < handleRect.xMin;

        if (current.type == EventType.MouseDown && current.button == 0 && (overCorner || overRightEdge || overBottomEdge))
        {
            _resizingWindow = window;
            _resizeStartMouse = _scale.GetLogicalMousePosition();
            _resizeStartRect = window.Rect;
            _resizeWidth = overCorner || overRightEdge;
            _resizeHeight = overCorner || overBottomEdge;
            current.Use();
        }

        if (_resizingWindow != window)
            return;

        if (UnityInput.Current.GetKey(KeyCode.Mouse0))
        {
            Vector2 delta = _scale.GetLogicalMousePosition() - _resizeStartMouse;
            Rect rect = _resizeStartRect;

            if (_resizeWidth)
                rect.width = _resizeStartRect.width + delta.x;
            if (_resizeHeight)
                rect.height = _resizeStartRect.height + delta.y;

            window.Rect = ClampRect(window, rect);
            MarkLayoutDirty(window);

            if (current.type is EventType.MouseDrag or EventType.MouseDown)
                current.Use();
        }

        if (UnityInput.Current.GetKeyUp(KeyCode.Mouse0))
            ClearResizeState();
    }

    private void DrawWindowBorder(ProfilerWindow window)
    {
        Texture2D texture = _theme.BorderTexture;
        if (texture == null)
            return;

        float width = Mathf.Max(1f, window.Rect.width);
        float height = Mathf.Max(1f, window.Rect.height);

        GUI.DrawTexture(new Rect(0f, 0f, width, WindowBorderWidth), texture);
        GUI.DrawTexture(new Rect(0f, height - WindowBorderWidth, width, WindowBorderWidth), texture);
        GUI.DrawTexture(new Rect(0f, 0f, WindowBorderWidth, height), texture);
        GUI.DrawTexture(new Rect(width - WindowBorderWidth, 0f, WindowBorderWidth, height), texture);
    }

    private void DrawResizeHandle(ProfilerWindow window)
    {
        if (!window.Resizable)
            return;

        Rect hitRect = GetResizeHandleHitRect(window);

        // Keep a lone horizontal or vertical scrollbar from drawing underneath the
        // resize corner. When both scrollbars are present this only covers their
        // unused intersection.
        Texture2D windowTexture = _theme.WindowTexture;
        if (windowTexture != null)
            GUI.DrawTexture(hitRect, windowTexture);

        Rect visualRect = new Rect(
            hitRect.xMax - ResizeHandleVisualSize,
            hitRect.yMax - ResizeHandleVisualSize,
            ResizeHandleVisualSize,
            ResizeHandleVisualSize);

        var content = new GUIContent(string.Empty, "Drag to resize");
        GUI.Label(visualRect, content, GUIStyle.none);

        if (Event.current.type == EventType.Repaint)
        {
            bool hover = visualRect.Contains(Event.current.mousePosition);
            bool active = _resizingWindow == window;
            _theme.ResizeHandleStyle.Draw(visualRect, content, hover, active, false, false);
        }

        Texture2D line = _theme.BorderTexture;
        if (line == null)
            return;

        float right = visualRect.xMax - 1f;
        float bottom = visualRect.yMax - 1f;
        GUI.DrawTexture(new Rect(right - 3f, bottom, 3f, 1f), line);
        GUI.DrawTexture(new Rect(right - 1f, bottom - 2f, 1f, 2f), line);
    }

    private static Rect GetResizeHandleHitRect(ProfilerWindow window)
    {
        return new Rect(
            Mathf.Max(0f, window.Rect.width - ResizeHandleHitSize - 2f),
            Mathf.Max(0f, window.Rect.height - ResizeHandleHitSize - 2f),
            ResizeHandleHitSize,
            ResizeHandleHitSize);
    }

    private void ClearResizeState()
    {
        _resizingWindow = null;
        _resizeWidth = false;
        _resizeHeight = false;
    }

    private void ResolveInitialPosition(ProfilerWindow window)
    {
        if (!window.CenterWhenPositionIsNegative || window.Rect.x >= 0f)
            return;

        Rect rect = window.Rect;
        rect.x = Mathf.Max(0f, (_scale.LogicalWidth - rect.width) * 0.5f);
        rect.y = Mathf.Max(0f, rect.y);
        window.Rect = rect;
    }

    private Rect ClampRect(ProfilerWindow window, Rect rect)
    {
        float maxWidth = Mathf.Max(window.MinimumSize.x, _scale.LogicalWidth);
        float maxHeight = Mathf.Max(window.MinimumSize.y, _scale.LogicalHeight);

        rect.width = Mathf.Clamp(rect.width, window.MinimumSize.x, maxWidth);
        rect.height = Mathf.Clamp(rect.height, window.MinimumSize.y, maxHeight);

        rect.x = Mathf.Clamp(rect.x, 0f, Mathf.Max(0f, _scale.LogicalWidth - 40f));
        rect.y = Mathf.Clamp(rect.y, 0f, Mathf.Max(0f, _scale.LogicalHeight - TitleBarHeight));
        return rect;
    }

    private static bool RectChanged(Rect left, Rect right)
    {
        return Mathf.Abs(left.x - right.x) > 0.01f ||
               Mathf.Abs(left.y - right.y) > 0.01f ||
               Mathf.Abs(left.width - right.width) > 0.01f ||
               Mathf.Abs(left.height - right.height) > 0.01f;
    }

    private static void MarkLayoutDirty(ProfilerWindow window)
    {
        window.LayoutDirty = true;
        window.SaveAfterRealtime = Time.realtimeSinceStartup + LayoutSaveDelay;
    }
}
