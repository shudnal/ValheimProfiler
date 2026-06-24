#nullable disable

using BepInEx.Logging;
using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ValheimProfiler.Core.Logging;

internal static class LogMonitorText
{
    private static readonly Regex UnityTimestampRegex = new(
        @"^(?<timestamp>\d{2}/\d{2}/\d{4} \d{2}:\d{2}:\d{2}):\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static string NormalizeMessage(string source, string rawMessage, out DateTime parsedTimestamp)
    {
        parsedTimestamp = default;
        string value = rawMessage ?? string.Empty;

        if (!string.Equals(source?.Trim(), "Unity Log", StringComparison.OrdinalIgnoreCase))
            return value;

        Match match = UnityTimestampRegex.Match(value);
        if (!match.Success)
            return value;

        DateTime.TryParseExact(
            match.Groups["timestamp"].Value,
            "MM/dd/yyyy HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out parsedTimestamp);

        return value.Substring(match.Length);
    }

    internal static string BuildFingerprint(LogLevel level, string source, string message, string details)
    {
        return GetLevelText(level) + "\n" +
               (source ?? string.Empty).Trim() + "\n" +
               NormalizeFingerprintText(message) + "\n" +
               NormalizeFingerprintText(details);
    }

    internal static string NormalizeFingerprintText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return value.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
    }

    internal static string GetLevelText(LogLevel level)
    {
        if ((level & LogLevel.Fatal) != 0)
            return "Fatal";
        if ((level & LogLevel.Error) != 0)
            return "Error";
        if ((level & LogLevel.Warning) != 0)
            return "Warning";
        if ((level & LogLevel.Message) != 0)
            return "Message";
        if ((level & LogLevel.Info) != 0)
            return "Info";
        if ((level & LogLevel.Debug) != 0)
            return "Debug";
        return level.ToString();
    }

    internal static LogLevel ParseLevel(string value)
    {
        string level = (value ?? string.Empty).Trim();
        if (level.Equals("Fatal", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Fatal;
        if (level.Equals("Error", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Error;
        if (level.Equals("Warning", StringComparison.OrdinalIgnoreCase) || level.Equals("Warn", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Warning;
        if (level.Equals("Message", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Message;
        if (level.Equals("Info", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Info;
        if (level.Equals("Debug", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Debug;
        if (level.Equals("None", StringComparison.OrdinalIgnoreCase))
            return LogLevel.None;

        return Enum.TryParse(level, true, out LogLevel parsed) ? parsed : LogLevel.Message;
    }

    internal static bool IsChainloaderStartupComplete(string source, string message)
    {
        return string.Equals(source?.Trim(), "BepInEx", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(message?.Trim(), "Chainloader startup complete", StringComparison.Ordinal);
    }
}
