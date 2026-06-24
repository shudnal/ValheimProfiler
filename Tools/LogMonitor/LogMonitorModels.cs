#nullable disable

using BepInEx.Logging;
using System;

namespace ValheimProfiler.Tools.LogMonitor;

internal sealed partial class LogMonitorTool
{
    private enum MainTab
    {
        Stream = 0,
        Issues = 1,
        Help = 2
    }

    private enum IssueSortColumn
    {
        Count = 0,
        FirstSeen = 1,
        LastSeen = 2,
        Level = 3,
        Source = 4,
        Message = 5
    }

    private sealed class PendingLogEvent
    {
        internal long Sequence;
        internal DateTime Timestamp;
        internal LogLevel Level;
        internal string Source;
        internal string RawMessage;
        internal string Message;
        internal string Details;
        internal int ThreadId;
    }

    private sealed class LogEntry
    {
        internal long Sequence;
        internal DateTime Timestamp;
        internal LogLevel Level;
        internal string Source;
        internal string RawMessage;
        internal string Message;
        internal string Details;
        internal string Scene;
        internal int ThreadId;
        internal bool IsHistorical;
        internal long FileOffset;

        internal string TimeText => Timestamp == default ? "--:--:--.---" : Timestamp.ToString("HH:mm:ss.fff");
        internal string LevelText => LogMonitorText.GetLevelText(Level);
        internal string Fingerprint => LogMonitorText.BuildFingerprint(Level, Source, Message, Details);

        internal string GetClipboardText(bool includeMetadata)
        {
            string levelSource = $"[{LevelText.PadRight(7)}: {Source}]";
            string clipboardMessage = string.IsNullOrEmpty(RawMessage) ? Message ?? string.Empty : RawMessage;

            if (!includeMetadata)
            {
                string compact = levelSource + " " + clipboardMessage;
                return string.IsNullOrEmpty(Details)
                    ? compact
                    : compact + Environment.NewLine + Details;
            }

            string time = Timestamp == default ? "unknown time" : Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string header = time + " " + levelSource + $" [thread {ThreadId}]";
            if (!string.IsNullOrEmpty(Scene))
                header += $" [scene {Scene}]";
            if (IsHistorical)
                header += $" [history offset {FileOffset}]";

            if (string.IsNullOrEmpty(Details))
                return header + Environment.NewLine + clipboardMessage;

            return header + Environment.NewLine + clipboardMessage + Environment.NewLine + Details;
        }
    }

    private sealed class IssueGroup
    {
        internal string Key;
        internal LogLevel Level;
        internal string Source;
        internal string Message;
        internal string Details;
        internal string Scene;
        internal int LastThreadId;
        internal int Count;
        internal DateTime FirstSeen;
        internal DateTime LastSeen;
        internal long FirstSequence;
        internal long LastSequence;

        internal string LevelText => LogMonitorText.GetLevelText(Level);

        internal string GetClipboardText()
        {
            string first = FirstSeen == default ? "unknown" : FirstSeen.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string last = LastSeen == default ? "unknown" : LastSeen.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string header = $"[{LevelText}:{Source}] Count: {Count} | First: {first} | Last: {last}";

            if (!string.IsNullOrEmpty(Scene))
                header += $" | Last scene: {Scene}";

            header += $" | Last thread: {LastThreadId}";

            if (string.IsNullOrEmpty(Details))
                return header + Environment.NewLine + Message;

            return header + Environment.NewLine + Message + Environment.NewLine + Details;
        }
    }
}
