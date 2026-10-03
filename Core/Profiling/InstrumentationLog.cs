#nullable disable

using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ValheimProfiler.Core.Profiling;

/// <summary>Bounded ordinary logging and separate detailed reports for instrumentation operations.</summary>
internal sealed class InstrumentationLog
{
    private readonly ManualLogSource _logger;
    private readonly ValheimProfilerConfig _config;
    private readonly string _name;
    private readonly string _filePrefix;

    internal InstrumentationLog(ManualLogSource logger, ValheimProfilerConfig config, string name)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _filePrefix = FileToken(name);
    }

    internal Operation BeginOperation(string operation) => new Operation(this, operation, _config.LoggingEnabled.Value);

    internal void Failure(string operation, Exception exception, bool warning = false)
    {
        using var log = BeginOperation("Session");
        log.Failure(operation, exception, warning);
    }

    internal void RecoveryRequired(string message) => Write(LogLevel.Warning, message);

    private void Write(LogLevel level, string message)
    {
        try { _logger.Log(level, _name + " | " + message); }
        catch
        {
            // A failing console or file listener must not turn a diagnostic into an installation failure.
            // Do not recursively log failures of the logging infrastructure itself.
        }
    }

    private static string FileToken(string value) => new string(value.Where(char.IsLetterOrDigit).ToArray());

    private static string SingleLine(string value, int limit = 360)
    {
        if (string.IsNullOrEmpty(value))
            return "(no details)";
        var result = new StringBuilder(Math.Min(value.Length, limit));
        bool whitespace = false;
        foreach (char character in value)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                whitespace = result.Length > 0;
                continue;
            }
            if (result.Length + (whitespace ? 1 : 0) >= limit)
            {
                result.Append("...");
                break;
            }
            if (whitespace)
                result.Append(' ');
            result.Append(character);
            whitespace = false;
        }
        return result.ToString();
    }

    private static string MethodIdentity(MethodBase method)
    {
        try
        {
            return method == null ? "(session)" :
                (method.DeclaringType?.Assembly.GetName().Name ?? "(unknown assembly)") + " :: " +
                InstrumentationSafety.GetSignature(method);
        }
        catch { return "(unavailable method identity)"; }
    }

    internal static string ExceptionSummary(Exception exception)
    {
        if (exception == null)
            return string.Empty;
        try
        {
            Exception cause = exception.GetBaseException();
            string message = cause.Message;
            // Keep the failing IL instruction without repeating the generated wrapper's full signature.
            if (cause is InvalidProgramException && message != null &&
                message.StartsWith("Invalid IL code in ", StringComparison.Ordinal))
            {
                int instruction = message.IndexOf("IL_", StringComparison.Ordinal);
                if (instruction >= 0)
                    message = "Invalid IL code; " + message.Substring(instruction);
            }
            return SingleLine(cause.GetType().Name + ": " + message);
        }
        catch { return exception.GetType().Name; }
    }

    private static string ExceptionText(Exception exception)
    {
        try { return exception?.ToString() ?? string.Empty; }
        catch { return exception?.GetType().Name ?? string.Empty; }
    }

    /// <summary>
    /// Owns one discovery/installation report. Nested operations never share counters or a writer.
    /// The logging setting is captured at entry, before any log-only signature or exception formatting.
    /// </summary>
    internal sealed class Operation : IDisposable
    {
        private const int MaxFailureEntries = 10;
        private const int MaxFailureParts = 4;
        private const int MaxSkipGroups = 8;
        private readonly InstrumentationLog _owner;
        private readonly string _operation;
        private readonly bool _detailed;
        private readonly long _initialCacheClearCount = MonoModResolverCache.ClearCount;
        private readonly Dictionary<string, FailureEntry> _failures;
        private readonly Dictionary<string, int> _skipGroups;
        private string _summary;
        private bool _hasFailures;
        private int _omittedFailureEvents;
        private int _omittedSkips;
        private StreamWriter _writer;
        private string _filePath;
        private string _fileError;
        private bool _disposed;

        internal Operation(InstrumentationLog owner, string operation, bool detailed)
        {
            _owner = owner;
            _operation = operation;
            _detailed = detailed;
            if (detailed)
            {
                _failures = new Dictionary<string, FailureEntry>(StringComparer.Ordinal);
                _skipGroups = new Dictionary<string, int>(StringComparer.Ordinal);
            }
        }

        internal void Summary(string summary, bool hasFailures = false)
        {
            long resets = MonoModResolverCache.ClearCount - _initialCacheClearCount;
            _summary = resets > 0 ? summary + " Resolver cache resets in operation (including nested work): " + resets + "." : summary;
            _hasFailures |= hasFailures;
        }

        internal void Skipped(MethodBase method, string reason)
        {
            _hasFailures |= InstrumentationSafety.IsInspectionFailure(reason);
            if (!_detailed || _disposed)
                return;
            string identity = MethodIdentity(method);
            WriteDetail("SKIPPED", identity, reason);

            // A failed inspection is not an expected exclusion, even though it contributes to safety skips.
            if (InstrumentationSafety.IsInspectionFailure(reason))
            {
                AddFailure(identity, reason, null);
                return;
            }

            string assembly;
            try { assembly = method?.DeclaringType?.Assembly.GetName().Name ?? "(unknown assembly)"; }
            catch { assembly = "(unknown assembly)"; }
            string key = assembly + " | " + (reason ?? "Unspecified safety exclusion.");
            if (_skipGroups.TryGetValue(key, out int count))
                _skipGroups[key] = count + 1;
            else if (_skipGroups.Count < MaxSkipGroups)
                _skipGroups.Add(key, 1);
            else
                _omittedSkips++;
        }

        internal void MethodFailure(MethodBase method, string operation, Exception exception)
        {
            _hasFailures = true;
            if (!_detailed || _disposed)
                return;
            string identity = MethodIdentity(method);
            WriteDetail("FAILURE", identity, operation, exception);
            AddFailure(identity, operation, exception);
        }

        internal void MetadataFailure(string identity, string operation, Exception exception)
        {
            _hasFailures = true;
            if (!_detailed || _disposed)
                return;
            WriteDetail("METADATA FAILURE", identity, operation, exception);
            AddFailure(identity, operation, exception);
        }

        internal void Failure(string operation, Exception exception, bool warning = false)
        {
            // Session-level failures are never hidden behind the per-method display budget.
            _hasFailures = true;
            string detail = _detailed ? ": " + ExceptionSummary(exception) :
                " (" + (exception?.GetType().Name ?? "Exception") + "). Enable General / Logging enabled for details.";
            _owner.Write(warning ? LogLevel.Warning : LogLevel.Error, SingleLine(operation) + detail);
            if (_detailed && !_disposed)
                WriteDetail("SESSION FAILURE", "(session)", operation, exception);
        }

        private void AddFailure(string identity, string operation, Exception exception)
        {
            _hasFailures = true;
            if (!_failures.TryGetValue(identity, out FailureEntry entry))
            {
                if (_failures.Count >= MaxFailureEntries)
                {
                    _omittedFailureEvents++;
                    return;
                }
                entry = new FailureEntry();
                _failures.Add(identity, entry);
            }
            string cause = ExceptionSummary(exception);
            string action = SingleLine(operation);
            if (entry.Parts.Any(part => part.Action == action && part.Cause == cause))
                return;
            if (entry.Parts.Count < MaxFailureParts)
                entry.Parts.Add(new FailurePart(action, cause));
            else
                _omittedFailureEvents++;
        }

        private void WriteDetail(string kind, string identity, string reason, Exception exception = null)
        {
            if (_fileError != null)
                return;
            try
            {
                if (_writer == null)
                {
                    _filePath = ProfilerPaths.GetConfigFilePath(_owner._filePrefix + "-" + FileToken(_operation) + ".log");
                    string previousPath = Path.ChangeExtension(_filePath, ".previous.log");
                    if (File.Exists(_filePath))
                    {
                        if (File.Exists(previousPath))
                            File.Delete(previousPath);
                        File.Move(_filePath, previousPath);
                    }
                    _writer = new StreamWriter(new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.Read),
                        new UTF8Encoding(false));
                    _writer.WriteLine(_owner._name + " | " + _operation + " | " +
                        DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
                    _writer.WriteLine("Version: " + ValheimProfilerPlugin.PluginVersion);
                    _writer.WriteLine("Detailed operation report. Buffered output; not a native-crash trace.");
                    _writer.WriteLine();
                }
                _writer.WriteLine(kind + " | " + identity);
                _writer.WriteLine(reason);
                if (exception != null)
                    _writer.WriteLine(ExceptionText(exception));
                _writer.WriteLine();
            }
            catch (Exception ex)
            {
                DisableWriter(ex);
            }
        }

        private void DisableWriter(Exception exception)
        {
            _fileError = ExceptionSummary(exception);
            try { _writer?.Dispose(); }
            catch { /* Preserve the first I/O failure without recursively logging it. */ }
            _writer = null;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_writer != null)
            {
                try
                {
                    if (_summary != null)
                        _writer.WriteLine("SUMMARY | " + _summary);
                    _writer.Dispose();
                    _writer = null;
                }
                catch (Exception ex) { DisableWriter(ex); }
            }

            if (_summary != null)
                _owner.Write(_hasFailures ? LogLevel.Warning : LogLevel.Info, SingleLine(_summary, 1200));
            if (!_detailed)
                return;

            // Print actual failures before routine skips, using one line per method/metadata identity.
            foreach (var failure in _failures.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                string detail = string.Join(" | ", failure.Value.Parts.GroupBy(part => part.Cause, StringComparer.Ordinal)
                    .Select(group => string.Join("; ", group.Select(part => part.Action)) +
                        (group.Key.Length == 0 ? string.Empty : ": " + group.Key)));
                _owner.Write(LogLevel.Warning, SingleLine(failure.Key, 600) + " | " + detail);
            }
            if (_omittedFailureEvents > 0)
                _owner.Write(LogLevel.Warning, "Additional failure events not expanded: " + _omittedFailureEvents +
                    ". See the detailed report and Skipped methods where available.");
            foreach (var group in _skipGroups.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal))
                _owner.Write(LogLevel.Info, "Safety skips: " + group.Value + " | " + SingleLine(group.Key, 600));
            if (_omittedSkips > 0)
                _owner.Write(LogLevel.Info, "Additional safety skips not expanded: " + _omittedSkips + ". See Skipped methods.");

            if (_fileError != null)
                _owner.Write(LogLevel.Warning, "Detailed report unavailable or incomplete" +
                    (_filePath == null ? string.Empty : " at " + _filePath) + ": " + _fileError);
            else if (_filePath != null)
                _owner.Write(LogLevel.Info, "Detailed report: " + _filePath);
        }

        private sealed class FailureEntry
        {
            internal readonly List<FailurePart> Parts = new List<FailurePart>();
        }

        private readonly struct FailurePart
        {
            internal readonly string Action;
            internal readonly string Cause;

            internal FailurePart(string action, string cause)
            {
                Action = action;
                Cause = cause;
            }
        }
    }
}
