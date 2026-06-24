#nullable disable

using BepInEx.Logging;
using UnityEngine;

namespace ValheimProfiler.Core;

internal sealed class ValheimProfilerApp
{
    private readonly ValheimProfilerConfig _config;
    private readonly ManualLogSource _logger;
    private readonly GuiScaleController _scale;
    private readonly ThemeManager _theme;
    private readonly TooltipManager _tooltips;
    private readonly WindowManager _windows;
    private readonly ToolRegistry _tools;
    private readonly ValheimCursorController _cursor;
    private readonly ValheimPauseController _pause;

    private ProfilerWindow _launcherWindow;
    private PatchProfilerTool _patchProfiler;
    private MonoBehaviourProfilerTool _monoBehaviourProfiler;
    private MonoBehaviourCallProfilerTool _monoBehaviourCallProfiler;
    private ValheimUpdateProfilerTool _valheimUpdateProfiler;
    private LogMonitorTool _logMonitor;
    private ServerLogMonitorTool _serverLogMonitor;
    private NetworkProfilerTool _networkProfiler;
    private bool _uiVisible;

    internal ValheimProfilerApp(ValheimProfilerConfig config, ManualLogSource logger)
    {
        _config = config;
        _logger = logger;
        _scale = new GuiScaleController(config);
        _theme = new ThemeManager(config);
        _tooltips = new TooltipManager(_theme);
        _windows = new WindowManager(config, _scale, _theme, _tooltips);
        _tools = new ToolRegistry();
        _cursor = new ValheimCursorController();
        _pause = new ValheimPauseController(config);
    }

    internal bool UiVisible => _uiVisible;
    internal bool IsDrawingUi { get; private set; }
    internal bool HasVisibleWindows => _uiVisible && _windows.HasRequestedVisibleWindows;
    internal bool ShouldBlockGameInput => HasVisibleWindows && _config.BlockGameInput.Value;
    internal bool ShouldBlockMouseInput => HasVisibleWindows && (_config.BlockGameInput.Value || _config.BlockMouseInput.Value);
    internal ValheimProfilerConfig Config => _config;
    internal GuiScaleController Scale => _scale;
    internal ThemeManager Theme => _theme;
    internal TooltipManager Tooltips => _tooltips;
    internal WindowManager Windows => _windows;
    internal ManualLogSource Logger => _logger;
    internal ServerLogMonitorTool ServerLogMonitor => _serverLogMonitor;
    internal NetworkProfilerTool NetworkProfiler => _networkProfiler;

    internal void Initialize()
    {
        _launcherWindow = _windows.Register(new ProfilerWindow(
            "ValheimProfiler.Launcher",
            $"{ValheimProfilerPlugin.PluginName} {ValheimProfilerPlugin.PluginVersion}",
            new Rect(ValheimProfilerConfig.DefaultLauncherPosition, new Vector2(470f, 48f)),
            new Vector2(320f, 42f),
            resizable: false,
            requestedVisible: true,
            drawContents: DrawLauncher,
            positionConfig: _config.LauncherPosition,
            centerWhenPositionIsNegative: true,
            allowTooltipOverflow: true));

        // Start log capture before constructing the profilers so their initialization messages are visible.
        _logMonitor = new LogMonitorTool(this);

        _patchProfiler = _tools.Register(new PatchProfilerTool(this));
        _monoBehaviourProfiler = _tools.Register(new MonoBehaviourProfilerTool(this));
        _monoBehaviourCallProfiler = _tools.Register(new MonoBehaviourCallProfilerTool(this));
        _valheimUpdateProfiler = _tools.Register(new ValheimUpdateProfilerTool(this));
        _tools.Register(_logMonitor);
        _serverLogMonitor = _tools.Register(new ServerLogMonitorTool(this));
        _networkProfiler = _tools.Register(new NetworkProfilerTool(this));
    }

    internal void MarkGameWindowReady() => _scale.MarkGameWindowReady();

    internal void Update()
    {
        if (_config.GlobalHotkey.Value.IsDown())
            SetUiVisible(!_uiVisible);

        HandleToolHotkey(_config.PatchProfilerHotkey.Value.IsDown(), _patchProfiler);

        // More specific configured shortcuts are evaluated first because BepInEx KeyboardShortcut
        // accepts additional held modifiers for an unmodified shortcut.
        bool callPressed = _config.MonoBehaviourCallProfilerHotkey.Value.IsDown();
        HandleToolHotkey(callPressed, _monoBehaviourCallProfiler);
        if (!callPressed)
            HandleToolHotkey(_config.MonoBehaviourProfilerHotkey.Value.IsDown(), _monoBehaviourProfiler);

        HandleToolHotkey(_config.ValheimUpdateProfilerHotkey.Value.IsDown(), _valheimUpdateProfiler);

        bool serverLogPressed = _config.ServerLogMonitorHotkey.Value.IsDown();
        HandleToolHotkey(serverLogPressed, _serverLogMonitor);
        if (!serverLogPressed)
            HandleToolHotkey(_config.LogMonitorHotkey.Value.IsDown(), _logMonitor);

        HandleToolHotkey(_config.NetworkProfilerHotkey.Value.IsDown(), _networkProfiler);

        _tools.Update();
        _windows.UpdatePersistence();
        _cursor.Update(HasVisibleWindows);
        _pause.Update(HasVisibleWindows);
    }

    internal void LateUpdate() => _cursor.LateUpdate(HasVisibleWindows);

    internal void OnGUI()
    {
        if (!HasVisibleWindows)
            return;

        _theme.EnsureStyles();

        Matrix4x4 oldMatrix = GUI.matrix;
        GUISkin oldSkin = GUI.skin;
        Color oldContentColor = GUI.contentColor;

        IsDrawingUi = true;
        try
        {
            GUI.matrix = _scale.Matrix;
            GUI.skin = _theme.Skin;
            GUI.contentColor = _theme.TextColor;

            UpdateLauncherSize();
            _cursor.OnGUI(true);
            _windows.DrawAll();
        }
        finally
        {
            GUI.contentColor = oldContentColor;
            GUI.skin = oldSkin;
            GUI.matrix = oldMatrix;
            IsDrawingUi = false;
        }
    }

    internal void ShowUi() => SetUiVisible(true);

    internal void SetUiVisible(bool visible)
    {
        if (_uiVisible == visible)
            return;

        _uiVisible = visible;
        _cursor.Update(HasVisibleWindows);
        _pause.Update(HasVisibleWindows);
    }

    internal void ReleaseCursorAndPause()
    {
        _cursor.Release();
        _pause.Release();
    }

    internal void OverrideCursorReleaseState(CursorLockMode lockState, bool visible)
    {
        if (!HasVisibleWindows)
            return;

        _cursor.OverrideReleaseState(lockState, visible);
    }

    internal void Shutdown()
    {
        _tools.Shutdown();
        _patchProfiler = null;
        _monoBehaviourProfiler = null;
        _monoBehaviourCallProfiler = null;
        _valheimUpdateProfiler = null;
        _logMonitor = null;
        _serverLogMonitor = null;
        _networkProfiler = null;

        _windows.SaveAll();
        _windows.Shutdown();
        ReleaseCursorAndPause();
        _theme.Shutdown();
    }

    private void HandleToolHotkey(bool pressed, IProfilerTool tool)
    {
        if (!pressed || tool == null)
            return;
        if (tool is IProfilerToolAvailability availability &&
            !availability.IsAvailable &&
            !availability.CanOpenWhenUnavailable)
            return;

        bool wasVisible = _uiVisible;
        ShowUi();

        if (wasVisible)
            tool.ToggleWindow();
        else
            tool.ShowWindow();
    }

    private void DrawLauncher(int id)
    {
        GUILayout.BeginHorizontal();

        for (int i = 0; i < _tools.Tools.Count; i++)
        {
            IProfilerTool tool = _tools.Tools[i];
            IProfilerToolAvailability availability = tool as IProfilerToolAvailability;
            bool available = availability?.IsAvailable != false;
            bool canOpenWhenUnavailable = availability?.CanOpenWhenUnavailable == true;
            bool launcherEnabled = available || canOpenWhenUnavailable;
            string availabilityTooltip = launcherEnabled
                ? string.Empty
                : availability?.AvailabilityTooltip ?? string.Empty;
            string activeTooltip = tool.IsActive
                ? "Currently active and collecting data."
                : string.Empty;
            string tooltip = string.IsNullOrEmpty(availabilityTooltip)
                ? activeTooltip
                : string.IsNullOrEmpty(activeTooltip)
                    ? availabilityTooltip
                    : activeTooltip + "\n" + availabilityTooltip;
            string displayName = tool.IsActive ? "● " + tool.DisplayName : tool.DisplayName;
            bool oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled && launcherEnabled;

            GUIStyle launcherButtonStyle = tool.IsActive ? _theme.AccentButtonStyle : GUI.skin.button;
            bool newVisible = GUILayout.Toggle(
                tool.IsWindowVisible,
                new GUIContent(displayName, tooltip),
                launcherButtonStyle);
            Rect toolRect = GUILayoutUtility.GetLastRect();
            GUI.enabled = oldEnabled;

            if (launcherEnabled && newVisible != tool.IsWindowVisible)
            {
                // Tools such as Server Log Monitor remain visually available and open
                // their own Help/status view when their remote backend is unavailable.
                tool.ToggleWindow();
            }
            else if (!launcherEnabled && !string.IsNullOrEmpty(availabilityTooltip))
            {
                GUI.Label(toolRect, new GUIContent(string.Empty, availabilityTooltip), GUIStyle.none);
            }
        }

        GUILayout.FlexibleSpace();

        float preventInputWidth = GetLauncherToggleWidth("Prevent input");
        bool preventInput = ProfilerGui.ToggleLayout(
            _theme,
            _config.BlockGameInput.Value,
            new GUIContent(
                "Prevent input",
                "Block all Valheim gameplay input while profiler windows are visible. Disable this to keep playing while watching profiler results."),
            preventInputWidth,
            GUI.skin.label,
            1f);
        if (preventInput != _config.BlockGameInput.Value)
            _config.BlockGameInput.Value = preventInput;

        float preventMouseWidth = GetLauncherToggleWidth("Prevent mouse");
        bool preventMouse = ProfilerGui.ToggleLayout(
            _theme,
            _config.BlockMouseInput.Value,
            new GUIContent(
                "Prevent mouse",
                "Block gameplay mouse clicks, wheel and camera movement while keeping keyboard movement, inventory and other hotkeys active. Prevent input overrides this setting."),
            preventMouseWidth,
            GUI.skin.label,
            1f);
        if (preventMouse != _config.BlockMouseInput.Value)
            _config.BlockMouseInput.Value = preventMouse;

        if (GUILayout.Button("Reset layout"))
        {
            _windows.RequestResetLayout();
        }

        if (GUILayout.Button("Hide"))
        {
            SetUiVisible(false);
        }

        GUILayout.EndHorizontal();
    }

    private void UpdateLauncherSize()
    {
        if (_launcherWindow == null || GUI.skin == null)
            return;

        float width = 20f;

        for (int i = 0; i < _tools.Tools.Count; i++)
        {
            IProfilerTool tool = _tools.Tools[i];
            string displayName = tool.IsActive ? "● " + tool.DisplayName : tool.DisplayName;
            width += GUI.skin.button.CalcSize(new GUIContent(displayName)).x + 8f;
        }

        width += GetLauncherToggleWidth("Prevent input") + 8f;
        width += GetLauncherToggleWidth("Prevent mouse") + 8f;
        width += GUI.skin.button.CalcSize(new GUIContent("Reset layout")).x + 8f;
        width += GUI.skin.button.CalcSize(new GUIContent("Hide")).x + 8f;

        float contentHeight = Mathf.Max(18f, GUI.skin.button.lineHeight + 6f);
        Rect rect = _launcherWindow.Rect;
        rect.width = Mathf.Max(_launcherWindow.MinimumSize.x, width);
        rect.height = Mathf.Max(_launcherWindow.MinimumSize.y, contentHeight + 22f);
        _launcherWindow.Rect = rect;
    }

    private float GetLauncherToggleWidth(string text)
    {
        GUIStyle labelStyle = GUI.skin?.label;
        float textWidth = labelStyle?.CalcSize(new GUIContent(text ?? string.Empty)).x ?? 80f;
        return Mathf.Ceil(1f + _theme.CompactToggleSize + 4f + textWidth + 4f);
    }
}