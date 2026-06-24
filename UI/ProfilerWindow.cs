#nullable disable

using BepInEx.Configuration;
using System;
using UnityEngine;

namespace ValheimProfiler.UI;

internal sealed class ProfilerWindow : IDisposable
{
    private readonly ConfigEntry<Vector2> _positionConfig;
    private readonly ConfigEntry<Vector2> _sizeConfig;
    private readonly object _layoutSync = new();

    private bool _layoutChangePending;
    private Vector2 _pendingPosition;
    private Vector2 _pendingSize;

    internal ProfilerWindow(
        string key,
        string title,
        Rect defaultRect,
        Vector2 minimumSize,
        bool resizable,
        bool requestedVisible,
        Action<int> drawContents,
        ConfigEntry<Vector2> positionConfig = null,
        ConfigEntry<Vector2> sizeConfig = null,
        bool centerWhenPositionIsNegative = false,
        bool allowTooltipOverflow = false)
    {
        Key = key;
        Title = title;
        MinimumSize = minimumSize;
        Resizable = resizable;
        RequestedVisible = requestedVisible;
        DrawContents = drawContents ?? throw new ArgumentNullException(nameof(drawContents));
        _positionConfig = positionConfig;
        _sizeConfig = sizeConfig;
        CenterWhenPositionIsNegative = centerWhenPositionIsNegative;
        AllowTooltipOverflow = allowTooltipOverflow;

        Vector2 position = positionConfig?.Value ?? defaultRect.position;
        Vector2 configuredSize = sizeConfig?.Value ?? defaultRect.size;
        Vector2 size = ResolveConfiguredSize(configuredSize, defaultRect.size);

        DefaultRect = new Rect(defaultRect.position, defaultRect.size);
        Rect = new Rect(position, size);
        WindowId = StableHash(key);

        if (_sizeConfig != null && (_sizeConfig.Value.x <= 0f || _sizeConfig.Value.y <= 0f))
            _sizeConfig.Value = size;

        if (_positionConfig != null)
            _positionConfig.SettingChanged += OnLayoutConfigChanged;
        if (_sizeConfig != null)
            _sizeConfig.SettingChanged += OnLayoutConfigChanged;
    }

    internal string Key { get; }
    internal string Title { get; set; }
    internal int WindowId { get; }
    internal Rect DefaultRect { get; }
    internal Vector2 MinimumSize { get; }
    internal bool Resizable { get; }
    internal bool CenterWhenPositionIsNegative { get; }
    internal bool AllowTooltipOverflow { get; }
    internal Action<int> DrawContents { get; }
    internal GUI.WindowFunction WindowFunction { get; set; }
    internal bool RequestedVisible { get; set; }
    internal Rect Rect { get; set; }
    internal bool LayoutDirty { get; set; }
    internal float SaveAfterRealtime { get; set; }

    internal void ApplyPendingLayoutChanges()
    {
        Vector2 position;
        Vector2 size;

        lock (_layoutSync)
        {
            if (!_layoutChangePending)
                return;

            position = _pendingPosition;
            size = _pendingSize;
            _layoutChangePending = false;
        }

        Rect rect = Rect;
        if (_positionConfig != null)
            rect.position = position;
        if (_sizeConfig != null)
            rect.size = ResolveConfiguredSize(size, DefaultRect.size);

        Rect = rect;
        LayoutDirty = false;
    }

    internal void ResetLayoutToDefault()
    {
        Rect reset = DefaultRect;
        if (_sizeConfig == null)
            reset.size = Rect.size;

        Rect = reset;
        LayoutDirty = false;
    }

    internal void SaveLayout()
    {
        if (_positionConfig != null)
            _positionConfig.Value = Rect.position;
        if (_sizeConfig != null)
            _sizeConfig.Value = Rect.size;

        LayoutDirty = false;
    }

    public void Dispose()
    {
        if (_positionConfig != null)
            _positionConfig.SettingChanged -= OnLayoutConfigChanged;
        if (_sizeConfig != null)
            _sizeConfig.SettingChanged -= OnLayoutConfigChanged;
    }

    private void OnLayoutConfigChanged(object sender, EventArgs e)
    {
        lock (_layoutSync)
        {
            _pendingPosition = _positionConfig?.Value ?? Rect.position;
            _pendingSize = _sizeConfig?.Value ?? Rect.size;
            _layoutChangePending = true;
        }
    }

    private static Vector2 ResolveConfiguredSize(Vector2 configured, Vector2 fallback)
    {
        return new Vector2(
            configured.x > 0f ? configured.x : fallback.x,
            configured.y > 0f ? configured.y : fallback.y);
    }

    private static int StableHash(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in value ?? string.Empty)
            {
                hash ^= c;
                hash *= 16777619;
            }

            int result = (int)(hash & 0x7fffffff);
            return result == 0 ? 1 : result;
        }
    }
}
