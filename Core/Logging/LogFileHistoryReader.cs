#nullable disable

using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ValheimProfiler.Core.Logging;

internal sealed class ParsedLogFileEntry
{
    internal long FileOffset;
    internal DateTime Timestamp;
    internal LogLevel Level;
    internal string Source;
    internal string RawMessage;
    internal string Message;
    internal string Details;
    internal string Scene;
    internal int ThreadId;

    internal string Fingerprint => LogMonitorText.BuildFingerprint(Level, Source, Message, Details);
}

internal sealed class LogFilePage
{
    internal readonly List<ParsedLogFileEntry> Entries = new();
    internal long NextCursor;
    internal bool HasMore;
    internal long FileLength;
    internal long FileCreationUtcTicks;
    internal string Error;
}

internal static class LogFileHistoryReader
{
    private static readonly Regex ExtendedHeaderRegex = new(
        @"^(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(?:\.\d{1,7})?)\s+\[(?<level>[^:\]]+):\s*(?<source>[^\]]+)\](?:\s+\[thread\s+(?<thread>\d+)\])?(?:\s+\[scene\s+(?<scene>[^\]]*)\])?\s*(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex StandardHeaderRegex = new(
        @"^\[(?<level>[^:\]]+):\s*(?<source>[^\]]+)\]\s*(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed class LineInfo
    {
        internal long Offset;
        internal string Text;
    }

    internal static LogFilePage ReadOlder(string path, long beforeOffset, int maxEntries, int maxBytes)
    {
        var page = new LogFilePage();

        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                page.Error = "LogOutput.log was not found.";
                return page;
            }

            var info = new FileInfo(path);
            page.FileLength = info.Length;
            page.FileCreationUtcTicks = info.CreationTimeUtc.Ticks;

            if (beforeOffset > info.Length)
            {
                page.Error = "LogOutput.log changed while history was being paged.";
                return page;
            }

            long end = beforeOffset > 0L ? beforeOffset : info.Length;
            if (end <= 0L)
            {
                page.NextCursor = 0L;
                page.HasMore = false;
                return page;
            }

            int byteLimit = Math.Max(64 * 1024, maxBytes);
            long start = Math.Max(0L, end - byteLimit);
            int length = checked((int)(end - start));
            byte[] buffer = new byte[length];

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                stream.Position = start;
                int read = 0;
                while (read < length)
                {
                    int chunk = stream.Read(buffer, read, length - read);
                    if (chunk <= 0)
                        break;
                    read += chunk;
                }

                if (read != buffer.Length)
                    Array.Resize(ref buffer, read);
            }

            List<LineInfo> lines = DecodeLines(buffer, start, start > 0L);
            List<ParsedLogFileEntry> parsed = ParseLines(lines, info.LastWriteTime);
            if (parsed.Count == 0)
            {
                page.NextCursor = start;
                page.HasMore = start > 0L;
                return page;
            }

            int take = Math.Max(1, maxEntries);
            int first = Math.Max(0, parsed.Count - take);
            for (int i = first; i < parsed.Count; i++)
                page.Entries.Add(parsed[i]);

            page.NextCursor = page.Entries.Count > 0 ? page.Entries[0].FileOffset : start;
            page.HasMore = page.NextCursor > 0L;
            return page;
        }
        catch (Exception ex)
        {
            page.Error = ex.GetType().Name + ": " + ex.Message;
            return page;
        }
    }

    private static List<LineInfo> DecodeLines(byte[] data, long absoluteStart, bool discardFirstPartialLine)
    {
        var result = new List<LineInfo>();
        if (data == null || data.Length == 0)
            return result;

        int start = 0;
        if (discardFirstPartialLine)
        {
            while (start < data.Length && data[start] != (byte)'\n')
                start++;
            if (start < data.Length)
                start++;
        }

        int lineStart = start;
        for (int i = start; i <= data.Length; i++)
        {
            if (i < data.Length && data[i] != (byte)'\n')
                continue;

            int lineEnd = i;
            if (lineEnd > lineStart && data[lineEnd - 1] == (byte)'\r')
                lineEnd--;

            string text = Encoding.UTF8.GetString(data, lineStart, Math.Max(0, lineEnd - lineStart));
            result.Add(new LineInfo
            {
                Offset = absoluteStart + lineStart,
                Text = text
            });

            lineStart = i + 1;
        }

        return result;
    }

    private static List<ParsedLogFileEntry> ParseLines(List<LineInfo> lines, DateTime fallbackTimestamp)
    {
        var entries = new List<ParsedLogFileEntry>();
        ParsedLogFileEntry current = null;
        var continuation = new StringBuilder();

        void FinishCurrent()
        {
            if (current == null)
                return;

            string extra = continuation.ToString();
            if (string.IsNullOrEmpty(current.RawMessage) && !string.IsNullOrEmpty(extra))
            {
                int breakIndex = extra.IndexOf('\n');
                if (breakIndex >= 0)
                {
                    current.RawMessage = extra.Substring(0, breakIndex);
                    current.Details = extra.Substring(breakIndex + 1);
                }
                else
                {
                    current.RawMessage = extra;
                    current.Details = string.Empty;
                }
            }
            else
            {
                current.Details = extra;
            }

            current.RawMessage ??= string.Empty;
            current.Details ??= string.Empty;
            current.Message = LogMonitorText.NormalizeMessage(current.Source, current.RawMessage, out DateTime unityTimestamp);
            if (current.Timestamp == default && unityTimestamp != default)
                current.Timestamp = unityTimestamp;
            if (current.Timestamp == default)
                current.Timestamp = fallbackTimestamp;

            entries.Add(current);
            current = null;
            continuation.Clear();
        }

        for (int i = 0; i < lines.Count; i++)
        {
            LineInfo line = lines[i];
            if (TryParseHeader(line, out ParsedLogFileEntry parsed))
            {
                FinishCurrent();
                current = parsed;
                continue;
            }

            if (current == null)
                continue;

            if (continuation.Length > 0)
                continuation.Append('\n');
            continuation.Append(line.Text);
        }

        FinishCurrent();
        return entries;
    }

    private static bool TryParseHeader(LineInfo line, out ParsedLogFileEntry entry)
    {
        entry = null;
        string text = line.Text ?? string.Empty;
        if (line.Offset == 0L && text.Length > 0 && text[0] == '\uFEFF')
            text = text.TrimStart('\uFEFF');

        Match match = ExtendedHeaderRegex.Match(text);
        bool extended = match.Success;
        if (!extended)
            match = StandardHeaderRegex.Match(text);
        if (!match.Success)
            return false;

        DateTime timestamp = default;
        if (extended)
        {
            DateTime.TryParse(
                match.Groups["timestamp"].Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out timestamp);
        }

        int threadId = 0;
        if (extended)
            int.TryParse(match.Groups["thread"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out threadId);

        entry = new ParsedLogFileEntry
        {
            FileOffset = line.Offset,
            Timestamp = timestamp,
            Level = LogMonitorText.ParseLevel(match.Groups["level"].Value),
            Source = match.Groups["source"].Value.Trim(),
            RawMessage = match.Groups["message"].Value,
            Scene = extended ? match.Groups["scene"].Value : string.Empty,
            ThreadId = threadId
        };
        return true;
    }
}
