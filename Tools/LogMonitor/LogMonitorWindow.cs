#nullable disable

using System.Threading;
using UnityEngine;

namespace ValheimProfiler.Tools.LogMonitor;

internal sealed partial class LogMonitorTool
{
    private void DrawWindow(int id)
    {
        EnsureStyles();

        Color oldContentColor = GUI.contentColor;
        GUI.contentColor = _theme.TextColor;

        try
        {
            GUILayout.BeginVertical();

            DrawToolbar();
            GUILayout.Space(2f);

            MainTab newTab = (MainTab)GUILayout.Toolbar(
                (int)_mainTab,
                new[] { "Stream", "Issues", "Help" },
                GUILayout.Width(330f));

            if (newTab != _mainTab)
            {
                _mainTab = newTab;
                if (_mainTab == MainTab.Stream && _followStream)
                    _scrollStreamToEnd = true;
            }

            GUILayout.Space(3f);

            switch (_mainTab)
            {
                case MainTab.Stream:
                    DrawStreamTab();
                    break;
                case MainTab.Issues:
                    DrawIssuesTab();
                    break;
                default:
                    DrawHelpTab();
                    break;
            }

            GUILayout.EndVertical();
        }
        finally
        {
            GUI.contentColor = oldContentColor;
        }
    }

    private void DrawToolbar()
    {
        GUILayout.BeginHorizontal();

        string captureState = _captureEnabled ? "ON" : "PAUSED";
        GUILayout.Label(
            $"Client | Capture: {captureState} | Captured: {_capturedEntries} | Buffered: {_entries.Count}/{_app.Config.LogMonitorMaxEntries.Value} | " +
            $"Issues: {_issues.Count} | Pending: {Mathf.Max(0, Volatile.Read(ref _pendingCount))} | Dropped: {DroppedEntries}",
            _labelStyle,
            GUILayout.ExpandWidth(true));

        if (GUILayout.Button(_captureEnabled ? "Pause capture" : "Resume capture", GUILayout.Width(110f)))
            ToggleCapture();

        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && Volatile.Read(ref _historyLoadInProgress) == 0;
        if (GUILayout.Button("Clear", GUILayout.Width(62f)))
            ClearCapturedData();
        GUI.enabled = oldEnabled;
        if (GUILayout.Button("Close", GUILayout.Width(62f)))
            IsWindowVisible = false;

        GUILayout.EndHorizontal();
    }

    private void DrawHelpTab()
    {
        _helpScroll = GUILayout.BeginScrollView(_helpScroll);

        HeaderLabel("Client Log Monitor");
        BodyLabel("Captures live BepInEx log events from the current client process. Unity log messages forwarded through BepInEx are included without installing a second Unity callback, and their redundant leading timestamp is removed from Message while the parsed time remains in the Time column.");
        BodyLabel("Capture continues while the Log Monitor window is hidden. Pause capture stops adding new entries; it does not freeze or alter normal BepInEx and Unity logging.");
        GUILayout.Space(6f);

        HeaderLabel("Stream");
        BodyLabel("Stream keeps a bounded in-memory history of all captured levels. Filter by level, logger source or any text contained in the message, details, scene or source name.");
        BodyLabel("Follow keeps the view at the newest matching entry. Click selects one row, Ctrl-click toggles individual rows and Shift-click selects a continuous range. Copy selected copies the chosen rows with full details and stack traces; Copy filtered copies every currently visible filtered row in chronological order.");
        GUILayout.Space(6f);

        HeaderLabel("Issues");
        BodyLabel("Issues groups identical Warning, Error and Fatal events by severity, source, message and details. Warning groups are hidden by default and can be enabled from the Issues toolbar.");
        BodyLabel("Grouping is intentionally conservative. Dynamic values are not stripped from messages, so unrelated failures are not silently merged into one issue.");
        GUILayout.Space(6f);

        HeaderLabel("Startup and older history");
        BodyLabel("After the BepInEx 'Chainloader startup complete' event is observed, the current LogOutput.log is read once and entries written before Valheim Profiler initialized are merged ahead of the live stream. A sequence of normalized fingerprints is used to avoid duplicating the overlap.");
        BodyLabel("Load older reads previous bounded pages from LogOutput.log on demand. Manually loaded history is stored beyond the automatic live-entry limit and can be removed with Unload history.");
        GUILayout.Space(6f);

        HeaderLabel("Memory and limits");
        BodyLabel("Live entries and issue groups use bounded collections controlled by the Log Monitor config section. Manually requested history is allowed beyond the live limit because it is an explicit user action. Issue counts always describe the entries currently loaded in the window.");
        BodyLabel("Very large individual messages are truncated before storage. A bounded pending queue prevents a log storm on background threads from growing memory without limit.");
        GUILayout.Space(6f);

        HeaderLabel("Server logs");
        BodyLabel("Server Log Monitor is a separate window so client and server events can be compared at the same time. It is available only when the connected dedicated server also runs a compatible Valheim Profiler backend and the current player is a server administrator who explicitly subscribes.");

        GUILayout.EndScrollView();
    }

    private void EnsureStyles()
    {
        if (_styleSkin == GUI.skin && _labelStyle != null)
            return;

        _styleSkin = GUI.skin;

        _labelStyle = new GUIStyle(GUI.skin.label)
        {
            wordWrap = false,
            clipping = TextClipping.Clip,
            alignment = TextAnchor.MiddleLeft
        };
        _labelStyle.normal.textColor = _theme.TextColor;

        _headerLabelStyle = new GUIStyle(_labelStyle)
        {
            fontStyle = FontStyle.Bold
        };
        _headerLabelStyle.normal.textColor = _theme.HeaderTextColor;

        _activeHeaderLabelStyle = new GUIStyle(_headerLabelStyle);
        Color activeSortColor = Color.Lerp(_theme.HeaderTextColor, _theme.AccentColor, 0.5f);
        SetStyleTextColor(_activeHeaderLabelStyle, activeSortColor);

        _detailsStyle = new GUIStyle(GUI.skin.label)
        {
            wordWrap = true,
            clipping = TextClipping.Clip,
            alignment = TextAnchor.UpperLeft
        };
        _detailsStyle.normal.textColor = _theme.TextColor;
    }

    private static void SetStyleTextColor(GUIStyle style, Color color)
    {
        style.normal.textColor = color;
        style.hover.textColor = color;
        style.active.textColor = color;
        style.focused.textColor = color;
        style.onNormal.textColor = color;
        style.onHover.textColor = color;
        style.onActive.textColor = color;
        style.onFocused.textColor = color;
    }

    private void Label(string text, params GUILayoutOption[] options) =>
        GUILayout.Label(text ?? string.Empty, _labelStyle, options);

    private void HeaderLabel(string text, params GUILayoutOption[] options) =>
        GUILayout.Label(text ?? string.Empty, _headerLabelStyle, options);

    private void BodyLabel(string text, params GUILayoutOption[] options) =>
        GUILayout.Label(text ?? string.Empty, _detailsStyle, options);
}
