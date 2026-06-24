#nullable disable

using System;
using UnityEngine;

namespace ValheimProfiler.UI;

internal sealed class ThemeManager
{
    private readonly ValheimProfilerConfig _config;
    private GUISkin _sourceSkin;
    private GUISkin _runtimeSkin;
    private bool _dirty = true;

    private Texture2D _windowTexture;
    private Texture2D _borderTexture;
    private Texture2D _entryTexture;
    private Texture2D _buttonTexture;
    private Texture2D _buttonHoverTexture;
    private Texture2D _buttonActiveTexture;
    private Texture2D _accentTexture;
    private Texture2D _accentHoverTexture;
    private Texture2D _scrollbarTrackTexture;
    private Texture2D _tooltipTexture;
    private Texture2D _toggleOffTexture;
    private Texture2D _toggleOnTexture;
    private Texture2D _toggleOffHoverTexture;
    private Texture2D _toggleOnHoverTexture;

    private GUIStyle _accentButtonStyle;
    private GUIStyle _accentLabelStyle;
    private GUIStyle _tooltipStyle;
    private GUIStyle _resizeHandleStyle;
    private GUIStyle _compactToggleOffStyle;
    private GUIStyle _compactToggleOnStyle;

    internal ThemeManager(ValheimProfilerConfig config)
    {
        _config = config;

        _config.FontSize.SettingChanged += OnThemeChanged;
        _config.WindowBackground.SettingChanged += OnThemeChanged;
        _config.WindowBorder.SettingChanged += OnThemeChanged;
        _config.EntryBackground.SettingChanged += OnThemeChanged;
        _config.TextColor.SettingChanged += OnThemeChanged;
        _config.HeaderTextColor.SettingChanged += OnThemeChanged;
        _config.ButtonBackground.SettingChanged += OnThemeChanged;
        _config.ButtonTextColor.SettingChanged += OnThemeChanged;
        _config.AccentColor.SettingChanged += OnThemeChanged;
    }

    internal Color TextColor => _config.TextColor.Value;
    internal Color HeaderTextColor => _config.HeaderTextColor.Value;
    internal Color AccentColor => _config.AccentColor.Value;

    internal GUISkin Skin
    {
        get
        {
            EnsureStyles();
            return _runtimeSkin ?? GUI.skin;
        }
    }


    internal Texture2D WindowTexture
    {
        get
        {
            EnsureStyles();
            return _windowTexture;
        }
    }

    internal Texture2D BorderTexture
    {
        get
        {
            EnsureStyles();
            return _borderTexture;
        }
    }

    internal GUIStyle AccentButtonStyle
    {
        get
        {
            EnsureStyles();
            return _accentButtonStyle ?? GUI.skin.button;
        }
    }

    internal GUIStyle AccentLabelStyle
    {
        get
        {
            EnsureStyles();
            return _accentLabelStyle ?? GUI.skin.label;
        }
    }

    internal GUIStyle TooltipStyle
    {
        get
        {
            EnsureStyles();
            return _tooltipStyle ?? GUI.skin.box;
        }
    }

    internal GUIStyle ResizeHandleStyle
    {
        get
        {
            EnsureStyles();
            return _resizeHandleStyle ?? GUI.skin.box;
        }
    }

    internal GUIStyle CompactToggleOffStyle
    {
        get
        {
            EnsureStyles();
            return _compactToggleOffStyle ?? GUI.skin.box;
        }
    }

    internal GUIStyle CompactToggleOnStyle
    {
        get
        {
            EnsureStyles();
            return _compactToggleOnStyle ?? GUI.skin.box;
        }
    }

    internal float CompactToggleSize
    {
        get
        {
            EnsureStyles();
            return Mathf.Clamp(_config.FontSize.Value - 2f, 9f, 16f);
        }
    }

    internal void EnsureStyles()
    {
        GUISkin current = GUI.skin;
        if (!_dirty && _runtimeSkin != null && (_sourceSkin == current || _runtimeSkin == current))
            return;

        Rebuild(current == _runtimeSkin ? _sourceSkin : current);
    }

    internal void Shutdown()
    {
        _config.FontSize.SettingChanged -= OnThemeChanged;
        _config.WindowBackground.SettingChanged -= OnThemeChanged;
        _config.WindowBorder.SettingChanged -= OnThemeChanged;
        _config.EntryBackground.SettingChanged -= OnThemeChanged;
        _config.TextColor.SettingChanged -= OnThemeChanged;
        _config.HeaderTextColor.SettingChanged -= OnThemeChanged;
        _config.ButtonBackground.SettingChanged -= OnThemeChanged;
        _config.ButtonTextColor.SettingChanged -= OnThemeChanged;
        _config.AccentColor.SettingChanged -= OnThemeChanged;

        DestroyResources();
    }

    private void OnThemeChanged(object sender, EventArgs e) => _dirty = true;

    private void Rebuild(GUISkin baseSkin)
    {
        DestroyResources();

        _sourceSkin = baseSkin;
        if (baseSkin == null)
            return;

        Color window = _config.WindowBackground.Value;
        Color entry = _config.EntryBackground.Value;
        Color button = _config.ButtonBackground.Value;
        Color accent = _config.AccentColor.Value;

        _windowTexture = CreateTexture(window);
        _borderTexture = CreateTexture(_config.WindowBorder.Value);
        _entryTexture = CreateTexture(entry);
        _buttonTexture = CreateTexture(button);
        _buttonHoverTexture = CreateTexture(Lighten(button, 0.12f));
        _buttonActiveTexture = CreateTexture(Darken(accent, 0.1f));
        _accentTexture = CreateTexture(accent);
        _accentHoverTexture = CreateTexture(Lighten(accent, 0.12f));
        _scrollbarTrackTexture = CreateTexture(Darken(entry, 0.08f));
        _tooltipTexture = CreateTexture(new Color(entry.r, entry.g, entry.b, 1f));
        Color toggleOff = CreateNeutralToggleColor(entry, button);
        Color toggleBorder = _config.WindowBorder.Value;
        _toggleOffTexture = CreateBorderedTexture(toggleOff, toggleBorder);
        _toggleOnTexture = CreateBorderedTexture(accent, toggleBorder);
        _toggleOffHoverTexture = CreateBorderedTexture(Lighten(toggleOff, 0.12f), Lighten(toggleBorder, 0.2f));
        _toggleOnHoverTexture = CreateBorderedTexture(Lighten(accent, 0.12f), Lighten(toggleBorder, 0.2f));

        _runtimeSkin = UnityEngine.Object.Instantiate(baseSkin);
        _runtimeSkin.name = "ValheimProfilerSkin";
        _runtimeSkin.hideFlags = HideFlags.HideAndDontSave;

        int fontSize = Mathf.Clamp(_config.FontSize.Value, 9, 28);
        Color text = _config.TextColor.Value;
        Color header = _config.HeaderTextColor.Value;
        Color buttonText = _config.ButtonTextColor.Value;

        ConfigureTextStyle(_runtimeSkin.label, text, fontSize);
        ConfigureTextStyle(_runtimeSkin.box, text, fontSize);
        ConfigureTextStyle(_runtimeSkin.window, header, fontSize);
        ConfigureTextStyle(_runtimeSkin.button, buttonText, fontSize);
        ConfigureTextStyle(_runtimeSkin.toggle, text, fontSize);
        ConfigureTextStyle(_runtimeSkin.textField, text, fontSize);
        ConfigureTextStyle(_runtimeSkin.textArea, text, fontSize);

        SetAllBackgrounds(_runtimeSkin.window, _windowTexture);
        _runtimeSkin.window.padding = new RectOffset(4, 4, 21, 4);
        _runtimeSkin.window.margin = new RectOffset(0, 0, 0, 0);
        _runtimeSkin.window.border = new RectOffset(0, 0, 0, 0);

        SetAllBackgrounds(_runtimeSkin.box, _entryTexture);
        _runtimeSkin.box.padding = new RectOffset(4, 4, 3, 3);
        _runtimeSkin.box.margin = new RectOffset(1, 1, 1, 1);
        _runtimeSkin.box.border = new RectOffset(0, 0, 0, 0);

        SetAllBackgrounds(_runtimeSkin.textField, _entryTexture);
        _runtimeSkin.textField.padding = new RectOffset(4, 4, 1, 1);
        _runtimeSkin.textField.margin = new RectOffset(1, 1, 1, 1);
        _runtimeSkin.textField.border = new RectOffset(0, 0, 0, 0);

        _runtimeSkin.button.padding = new RectOffset(5, 5, 2, 2);
        _runtimeSkin.button.margin = new RectOffset(2, 2, 1, 1);
        _runtimeSkin.button.border = new RectOffset(0, 0, 0, 0);
        _runtimeSkin.toggle.margin = new RectOffset(1, 1, 1, 1);
        _runtimeSkin.label.margin = new RectOffset(1, 1, 0, 0);

        SetButtonBackgrounds(_runtimeSkin.button);
        ConfigureSquareScrollbars(_runtimeSkin);

        _accentButtonStyle = new GUIStyle(_runtimeSkin.button)
        {
            name = "ValheimProfilerAccentButton"
        };
        SetAllBackgrounds(_accentButtonStyle, _accentTexture);
        _accentButtonStyle.hover.background = _accentHoverTexture;
        _accentButtonStyle.onHover.background = _accentHoverTexture;
        _accentButtonStyle.active.background = _buttonActiveTexture;
        _accentButtonStyle.onActive.background = _buttonActiveTexture;

        _accentLabelStyle = new GUIStyle(_runtimeSkin.label)
        {
            name = "ValheimProfilerAccentLabel",
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };
        ConfigureTextStyle(_accentLabelStyle, Lighten(accent, 0.35f), fontSize);

        _tooltipStyle = new GUIStyle(_runtimeSkin.box)
        {
            name = "ValheimProfilerTooltip",
            wordWrap = true,
            richText = false,
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(10, 10, 5, 5),
            margin = new RectOffset(0, 0, 0, 0),
            border = new RectOffset(0, 0, 0, 0)
        };
        ConfigureTextStyle(_tooltipStyle, text, fontSize);
        SetAllBackgrounds(_tooltipStyle, _tooltipTexture);

        _resizeHandleStyle = new GUIStyle(_runtimeSkin.box)
        {
            name = "ValheimProfilerResizeHandle",
            padding = new RectOffset(0, 0, 0, 0),
            margin = new RectOffset(0, 0, 0, 0),
            border = new RectOffset(0, 0, 0, 0)
        };
        SetAllBackgrounds(_resizeHandleStyle, _accentTexture);
        _resizeHandleStyle.hover.background = _accentHoverTexture;
        _resizeHandleStyle.active.background = _buttonActiveTexture;

        _compactToggleOffStyle = new GUIStyle(_runtimeSkin.box)
        {
            name = "ValheimProfilerCompactToggleOff",
            padding = new RectOffset(0, 0, 0, 0),
            margin = new RectOffset(0, 0, 0, 0),
            border = new RectOffset(1, 1, 1, 1)
        };
        SetAllBackgrounds(_compactToggleOffStyle, _toggleOffTexture);
        _compactToggleOffStyle.hover.background = _toggleOffHoverTexture;
        _compactToggleOffStyle.active.background = _toggleOffHoverTexture;

        _compactToggleOnStyle = new GUIStyle(_compactToggleOffStyle)
        {
            name = "ValheimProfilerCompactToggleOn",
            border = new RectOffset(1, 1, 1, 1)
        };
        SetAllBackgrounds(_compactToggleOnStyle, _toggleOnTexture);
        _compactToggleOnStyle.hover.background = _toggleOnHoverTexture;
        _compactToggleOnStyle.active.background = _toggleOnHoverTexture;

        _dirty = false;
    }

    private void ConfigureSquareScrollbars(GUISkin skin)
    {
        const float size = 13f;

        if (skin.scrollView != null)
        {
            RectOffset padding = skin.scrollView.padding;
            skin.scrollView.padding = new RectOffset(
                padding?.left ?? 0,
                Mathf.Max(1, padding?.right ?? 0),
                padding?.top ?? 0,
                padding?.bottom ?? 0);
        }

        ConfigureScrollbarTrack(skin.horizontalScrollbar, true, size);
        ConfigureScrollbarTrack(skin.verticalScrollbar, false, size);
        ConfigureScrollbarThumb(skin.horizontalScrollbarThumb, true, size);
        ConfigureScrollbarThumb(skin.verticalScrollbarThumb, false, size);
        HideScrollbarButton(skin.horizontalScrollbarLeftButton);
        HideScrollbarButton(skin.horizontalScrollbarRightButton);
        HideScrollbarButton(skin.verticalScrollbarUpButton);
        HideScrollbarButton(skin.verticalScrollbarDownButton);
    }

    private void ConfigureScrollbarTrack(GUIStyle style, bool horizontal, float size)
    {
        if (style == null)
            return;

        SetAllBackgrounds(style, _scrollbarTrackTexture);
        style.border = new RectOffset(0, 0, 0, 0);
        style.margin = horizontal
            ? new RectOffset(0, 0, 0, 0)
            : new RectOffset(0, 1, 0, 0);
        style.padding = new RectOffset(0, 0, 0, 0);
        if (horizontal)
            style.fixedHeight = size;
        else
            style.fixedWidth = size;
    }

    private void ConfigureScrollbarThumb(GUIStyle style, bool horizontal, float size)
    {
        if (style == null)
            return;

        SetAllBackgrounds(style, _accentTexture);
        style.hover.background = _accentHoverTexture;
        style.active.background = _buttonActiveTexture;
        style.border = new RectOffset(0, 0, 0, 0);
        style.margin = new RectOffset(1, 1, 1, 1);
        style.padding = new RectOffset(0, 0, 0, 0);
        if (horizontal)
            style.fixedHeight = Mathf.Max(1f, size - 2f);
        else
            style.fixedWidth = Mathf.Max(1f, size - 2f);
    }

    private static void HideScrollbarButton(GUIStyle style)
    {
        if (style == null)
            return;

        SetAllBackgrounds(style, null);
        style.border = new RectOffset(0, 0, 0, 0);
        style.margin = new RectOffset(0, 0, 0, 0);
        style.padding = new RectOffset(0, 0, 0, 0);
        style.fixedWidth = 0f;
        style.fixedHeight = 0f;
        style.stretchWidth = false;
        style.stretchHeight = false;
    }

    private void SetButtonBackgrounds(GUIStyle style)
    {
        if (style == null)
            return;

        style.normal.background = _buttonTexture;
        style.onNormal.background = _accentTexture;
        style.hover.background = _buttonHoverTexture;
        style.onHover.background = _buttonHoverTexture;
        style.active.background = _buttonActiveTexture;
        style.onActive.background = _buttonActiveTexture;
        style.focused.background = _buttonTexture;
        style.onFocused.background = _accentTexture;
    }

    private static void SetAllBackgrounds(GUIStyle style, Texture2D texture)
    {
        if (style == null)
            return;

        style.normal.background = texture;
        style.hover.background = texture;
        style.active.background = texture;
        style.focused.background = texture;
        style.onNormal.background = texture;
        style.onHover.background = texture;
        style.onActive.background = texture;
        style.onFocused.background = texture;
    }

    private static void ConfigureTextStyle(GUIStyle style, Color color, int fontSize)
    {
        if (style == null)
            return;

        style.fontSize = fontSize;
        SetTextColor(style.normal, color);
        SetTextColor(style.hover, color);
        SetTextColor(style.active, color);
        SetTextColor(style.focused, color);
        SetTextColor(style.onNormal, color);
        SetTextColor(style.onHover, color);
        SetTextColor(style.onActive, color);
        SetTextColor(style.onFocused, color);
    }

    private static void SetTextColor(GUIStyleState state, Color color)
    {
        if (state != null)
            state.textColor = color;
    }

    private static Texture2D CreateTexture(Color color)
    {
        var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            name = "ValheimProfilerColor"
        };
        texture.SetPixel(0, 0, color);
        texture.Apply(false, true);
        return texture;
    }

    private static Texture2D CreateBorderedTexture(Color fill, Color border)
    {
        var texture = new Texture2D(3, 3, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            name = "ValheimProfilerBorderedColor"
        };

        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 3; x++)
                texture.SetPixel(x, y, x == 0 || y == 0 || x == 2 || y == 2 ? border : fill);
        }

        texture.Apply(false, true);
        return texture;
    }

    private static Color Lighten(Color color, float amount) =>
        new Color(Mathf.Lerp(color.r, 1f, amount), Mathf.Lerp(color.g, 1f, amount), Mathf.Lerp(color.b, 1f, amount), color.a);

    private static Color Darken(Color color, float amount) =>
        new Color(color.r * (1f - amount), color.g * (1f - amount), color.b * (1f - amount), color.a);

    private static Color CreateNeutralToggleColor(Color entry, Color button)
    {
        Color mixed = Color.Lerp(entry, button, 0.55f);
        float gray = mixed.r * 0.299f + mixed.g * 0.587f + mixed.b * 0.114f;
        gray = Mathf.Lerp(gray, 1f, 0.08f);
        return new Color(gray, gray, gray, 1f);
    }

    private void DestroyResources()
    {
        Destroy(_runtimeSkin);
        Destroy(_windowTexture);
        Destroy(_borderTexture);
        Destroy(_entryTexture);
        Destroy(_buttonTexture);
        Destroy(_buttonHoverTexture);
        Destroy(_buttonActiveTexture);
        Destroy(_accentTexture);
        Destroy(_accentHoverTexture);
        Destroy(_scrollbarTrackTexture);
        Destroy(_tooltipTexture);
        Destroy(_toggleOffTexture);
        Destroy(_toggleOnTexture);
        Destroy(_toggleOffHoverTexture);
        Destroy(_toggleOnHoverTexture);

        _runtimeSkin = null;
        _windowTexture = null;
        _borderTexture = null;
        _entryTexture = null;
        _buttonTexture = null;
        _buttonHoverTexture = null;
        _buttonActiveTexture = null;
        _accentTexture = null;
        _accentHoverTexture = null;
        _scrollbarTrackTexture = null;
        _tooltipTexture = null;
        _toggleOffTexture = null;
        _toggleOnTexture = null;
        _toggleOffHoverTexture = null;
        _toggleOnHoverTexture = null;
        _accentButtonStyle = null;
        _accentLabelStyle = null;
        _tooltipStyle = null;
        _resizeHandleStyle = null;
        _compactToggleOffStyle = null;
        _compactToggleOnStyle = null;
    }

    private static void Destroy(UnityEngine.Object value)
    {
        if (value != null)
            UnityEngine.Object.Destroy(value);
    }
}