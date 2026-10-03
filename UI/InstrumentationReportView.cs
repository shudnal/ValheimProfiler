#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace ValheimProfiler.UI;

/// <summary>Searchable diagnostics: recovery failures first, ordinary exclusions next.</summary>
internal sealed class InstrumentationReportView
{
    internal readonly InstrumentationReport Report = new InstrumentationReport();
    private List<InstrumentationReport.Entry> _filtered = new List<InstrumentationReport.Entry>();
    private Vector2 _scroll;
    private string _search = string.Empty;
    private bool _failuresOnly;
    private bool _dirty = true;
    private int _revision = -1;
    private float _contentWidth = 1200f;
    private GUISkin _skin;
    private GUIStyle _rowStyle;
    private GUIStyle _warningStyle;
    private GUIStyle _warningTabStyle;
    private GUIStyle _bannerStyle;
    private static readonly Color WarningColor = new Color(1f, 0.84f, 0.25f, 1f);

    internal int Count => Report.Entries.Count;
    internal bool HasFailures => Report.HasFailures;

    // Existing callers start a new capture here. Failures deliberately survive this operation.
    internal void Clear()
    {
        Report.BeginCapture();
        _scroll = Vector2.zero;
        _dirty = true;
    }

    internal void Add(MethodBase method, string reason) => Report.Skip(method, reason);
    internal void Failure(MethodBase method, string stage, Exception exception, bool restartRequired = false) =>
        Report.Failure(method, stage, exception, restartRequired);

    internal int DrawTabs(int selected, string selectionCaption, float width, bool recoveryRequired)
    {
        EnsureStyles();
        string[] captions = { "Profiler", selectionCaption, "Help", "Skipped methods" };
        GUILayout.BeginHorizontal(GUILayout.Width(width));
        for (int index = 0; index < captions.Length; index++)
        {
            bool warning = index == 3 && (HasFailures || recoveryRequired);
            string tooltip = index == 3
                ? "Installation and removal failures are shown first. Expected exclusions are not errors. Failures remain visible after Reset stats."
                : captions[index];
            Color old = GUI.contentColor;
            if (warning)
                GUI.contentColor = Color.white;
            bool pressed;
            try
            {
                pressed = GUILayout.Toggle(selected == index, new GUIContent(captions[index], tooltip),
                    warning ? _warningTabStyle : GUI.skin.button, GUILayout.Width(width / 4f - 2f));
            }
            finally { GUI.contentColor = old; }
            if (pressed && selected != index)
                selected = index;
        }
        GUILayout.EndHorizontal();
        return selected;
    }

    internal void DrawRecoveryBanner(InstrumentationSafety safety)
    {
        if (!safety.RestartRequired)
            return;
        EnsureStyles();
        Color old = GUI.contentColor;
        GUI.contentColor = Color.white;
        try
        {
            GUILayout.Label(new GUIContent("RECOVERY UNCERTAIN - " + safety.RestartMessage,
                "Measurement is disabled. Resetting statistics or reopening the window cannot repair an uncertain detour or undo game-state changes."),
                _bannerStyle, GUILayout.ExpandWidth(true));
        }
        finally { GUI.contentColor = old; }
    }

    internal void Draw(float windowHeight)
    {
        EnsureStyles();
        GUILayout.Label("Failed methods: " + Report.FailureCount + " | Other exclusions: " + Report.OtherSkipCount);
        GUILayout.Label("Failures are retained for this process. Expected exclusions did not disable the original methods.");
        GUILayout.BeginHorizontal();
        bool failuresOnly = GUILayout.Toggle(_failuresOnly, "Failures only", GUILayout.Width(115f));
        if (failuresOnly != _failuresOnly)
        {
            _failuresOnly = failuresOnly;
            _dirty = true;
            _scroll = Vector2.zero;
        }
        GUILayout.Label("Search:", GUILayout.Width(55f));
        string search = GUILayout.TextField(_search, GUILayout.MinWidth(180f), GUILayout.ExpandWidth(true));
        if (!string.Equals(search, _search, StringComparison.Ordinal))
        {
            _search = search;
            _dirty = true;
            _scroll = Vector2.zero;
        }
        if (GUILayout.Button("Clear filter", GUILayout.Width(90f)))
        {
            _search = string.Empty;
            _dirty = true;
            _scroll = Vector2.zero;
            GUI.FocusControl(null);
        }
        GUILayout.EndHorizontal();

        if (_dirty || _revision != Report.Revision)
        {
            _dirty = false;
            _revision = Report.Revision;
            _filtered = Report.Entries.Where(entry => (!_failuresOnly || entry.HasFailure) &&
                (entry.Assembly + " " + entry.Method + " " + entry.Details).IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(entry => entry.SortPriority)
                .ThenBy(entry => entry.Assembly, StringComparer.Ordinal)
                .ThenBy(entry => entry.Method, StringComparer.Ordinal).ToList();
            _contentWidth = Math.Max(1200f, _filtered.Count == 0 ? 0f : _filtered.Max(entry =>
                Math.Max((entry.HasFailure ? _warningStyle : _rowStyle).CalcSize(new GUIContent(Identity(entry))).x,
                    _rowStyle.CalcSize(new GUIContent(entry.Details)).x) + 20f));
        }

        float lineHeight = Mathf.Max(20f, _rowStyle.lineHeight + 4f);
        float rowHeight = lineHeight * 2f + 6f;
        _scroll = GUILayout.BeginScrollView(_scroll, false, true);
        float contentHeight = Mathf.Max(rowHeight, _filtered.Count * rowHeight);
        Rect content = GUILayoutUtility.GetRect(_contentWidth, contentHeight,
            GUILayout.Width(_contentWidth), GUILayout.Height(contentHeight));
        int first = Mathf.Max(0, Mathf.FloorToInt(_scroll.y / rowHeight) - 2);
        int last = Mathf.Min(_filtered.Count, first + Mathf.CeilToInt(windowHeight / rowHeight) + 5);
        for (int index = first; index < last; index++)
        {
            InstrumentationReport.Entry entry = _filtered[index];
            string identity = Identity(entry);
            string tooltip = identity + "\n" + string.Join("\n", entry.Messages);
            Rect row = new Rect(content.x, content.y + index * rowHeight, content.width, lineHeight);
            Color old = GUI.contentColor;
            if (entry.HasFailure)
                GUI.contentColor = Color.white;
            try { GUI.Label(row, new GUIContent(identity, tooltip), entry.HasFailure ? _warningStyle : _rowStyle); }
            finally { GUI.contentColor = old; }
            row.y += lineHeight;
            GUI.Label(row, new GUIContent(entry.Details, tooltip), _rowStyle);
        }
        if (_filtered.Count == 0)
            GUI.Label(new Rect(content.x, content.y, content.width, lineHeight), "No diagnostics match the current filter.", _rowStyle);
        GUILayout.EndScrollView();
    }

    private static string Identity(InstrumentationReport.Entry entry) =>
        (entry.RequiresRestart ? "[RESTART REQUIRED] " : entry.HasFailure ? "[FAILURE] " : "[SKIPPED] ") +
        entry.Assembly + " | " + entry.Method;

    private void EnsureStyles()
    {
        if (_skin == GUI.skin && _rowStyle != null)
            return;
        _skin = GUI.skin;
        _rowStyle = new GUIStyle(GUI.skin.label) { wordWrap = false, richText = false };
        _warningStyle = new GUIStyle(_rowStyle) { fontStyle = FontStyle.Bold };
        _bannerStyle = new GUIStyle(_warningStyle) { wordWrap = true };
        _warningTabStyle = new GUIStyle(GUI.skin.button);
        foreach (GUIStyle style in new[] { _warningStyle, _warningTabStyle, _bannerStyle })
        {
            style.normal.textColor = WarningColor;
            style.hover.textColor = WarningColor;
            style.active.textColor = WarningColor;
            style.focused.textColor = WarningColor;
            style.onNormal.textColor = WarningColor;
            style.onHover.textColor = WarningColor;
            style.onActive.textColor = WarningColor;
            style.onFocused.textColor = WarningColor;
        }
        _dirty = true;
    }
}
