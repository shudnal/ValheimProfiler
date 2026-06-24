#nullable disable

using UnityEngine;

namespace ValheimProfiler.Tools.ServerLogMonitor;

internal sealed partial class ServerLogMonitorTool
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

            bool oldEnabled = GUI.enabled;
            if (_mainTab != MainTab.Help)
                GUI.enabled = oldEnabled && _subscribed;

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

            GUI.enabled = oldEnabled;
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

        string version = string.IsNullOrEmpty(_serverVersion) ? "unknown" : _serverVersion;
        string subscription = _subscribed ? "SUBSCRIBED" : _subscriptionRequestPending ? "CONNECTING" : "OFF";
        GUILayout.Label(
            $"Server v{version} | {subscription} | Entries: {_entries.Count} | Issues: {_issues.Count} | Last seq: {_lastLiveSequence} | Gaps: {_detectedGaps} | Server dropped: {_serverDroppedCount}",
            _labelStyle,
            GUILayout.ExpandWidth(true));

        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && IsAvailable && !_subscriptionRequestPending;
        if (GUILayout.Button(_subscribed ? "Unsubscribe" : "Subscribe", GUILayout.Width(94f)))
        {
            if (_subscribed)
                Unsubscribe();
            else
                Subscribe();
        }
        GUI.enabled = oldEnabled && _entries.Count > 0 && !_historyRequestPending;
        if (GUILayout.Button("Clear local", GUILayout.Width(82f)))
            ClearLocalView(resetSession: false);
        GUI.enabled = oldEnabled;
        if (GUILayout.Button("Close", GUILayout.Width(62f)))
            IsWindowVisible = false;

        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        Label(_availabilityStatus, GUILayout.ExpandWidth(true));
        if (!_subscribed && IsAvailable)
            Label("Press Subscribe to start a private admin stream.", GUILayout.Width(300f));
        GUILayout.EndHorizontal();
    }

    private void DrawHelpTab()
    {
        _helpScroll = GUILayout.BeginScrollView(_helpScroll);

        HeaderLabel("Server Log Monitor requirements");
        BodyLabel("The connected server must be a headless dedicated Valheim server running a compatible Valheim Profiler. The current player must be listed as a server administrator.");
        BodyLabel("Press Subscribe to create a private stream for the current authenticated connection. The server targets snapshots and live batches only to peers that explicitly subscribed. Unsubscribe or disconnect removes that peer from the subscriber list.");
        GUILayout.Space(6f);

        HeaderLabel("Current state");
        BodyLabel(_availabilityStatus);
        if (!string.IsNullOrEmpty(_serverVersion))
            BodyLabel("Detected server mod version: " + _serverVersion);
        GUILayout.Space(6f);

        HeaderLabel("Transport");
        BodyLabel("Subscribe requests a bounded recent snapshot and then receives live entries in small batches. Every live event carries a monotonic sequence number. Server timestamps are transferred as UTC and displayed in the client local time zone for easier comparison with Client Log Monitor. A detected gap requests a fresh recent snapshot instead of silently presenting an incomplete stream.");
        BodyLabel("The server only runs the log backend on a headless instance. It does not initialize profiler discovery, IMGUI, input, cursor or pause systems.");
        GUILayout.Space(6f);

        HeaderLabel("Stream selection");
        BodyLabel("Click selects one row, Ctrl-click toggles individual rows and Shift-click selects a continuous filtered range. Copy selected copies the chosen server log entries in chronological order with their full details and stack traces. Copy filtered remains available for the complete current filter result.");
        GUILayout.Space(6f);

        HeaderLabel("History");
        BodyLabel("After the first snapshot, one server LogOutput.log page is loaded automatically and merged before the live stream, so normal startup logs are visible even though Valheim Profiler initialized later. Load older requests additional bounded pages on demand. A server restart or log-file replacement invalidates the history cursor.");
        GUILayout.Space(6f);

        HeaderLabel("Privacy and limits");
        BodyLabel("Server logs can contain file paths, player names, network details and data written by third-party mods. Access is admin-only, requires an explicit subscription, is rate-limited and never transfers the whole file automatically.");
        BodyLabel("Clear local only removes the client-side view. It does not modify the server log file or the server's recent ring buffer.");

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
