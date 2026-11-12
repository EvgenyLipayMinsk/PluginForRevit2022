using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace ARBM.CableJournalExporter.Services
{
    /// <summary>
    /// Simple JSON?lines execution logger used by profiling patch.
    /// Writes small JSON objects (one per line) into a timestamped file in %TEMP%.
    /// Thread?safe and lightweight.
    /// 
    /// NOTE:
    /// - Fields are intentionally simple to avoid heavy serialization dependencies.
    /// - Logs should not contain PII. ElementId is logged as string only when helpful.
    /// </summary>
    internal static class ExecutionLogger
    {
        private static readonly object _sync = new object();
        private static string _logFilePath;
        private static bool _initialized;

        /// <summary>
        /// Initialize logger and create file path in %TEMP%.
        /// Called lazily on first Log call.
        /// </summary>
        private static void EnsureInitialized()
        {
            if (_initialized) return;

            lock (_sync)
            {
                if (_initialized) return;

                string temp = Path.GetTempPath();
                string fileName = $"ARBM_ExportAps_log_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jsonl";
                _logFilePath = Path.Combine(temp, fileName);

                try
                {
                    // create file with header line (optional)
                    File.AppendAllText(_logFilePath, $"{{\"logStarted\":\"{DateTime.UtcNow:O}\"}}{Environment.NewLine}", Encoding.UTF8);
                }
                catch
                {
                    // best?effort: swallow IO exceptions to avoid affecting main flow
                }

                _initialized = true;
            }
        }

        /// <summary>
        /// Escape string for simple JSON encoding (very small subset).
        /// Replaces backslash and double quote and control characters.
        /// </summary>
        private static string JsonEscape(string s)
        {
            if (s == null) return "null";

            var sb = new StringBuilder();
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (char.IsControl(c))
                            sb.AppendFormat("\\u{0:X4}", (int)c);
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// Writes a single JSON line with provided properties.
        /// counts/details are optional dictionaries/strings.
        /// This method is safe to call from multiple threads.
        /// </summary>
        public static void Log(
            string stage,
            string action,
            long? durationMs = null,
            IDictionary<string, object> counts = null,
            string details = null)
        {
            try
            {
                EnsureInitialized();

                var sb = new StringBuilder();
                sb.Append('{');
                sb.Append($"\"timestamp\":\"{DateTime.UtcNow:O}\",");
                sb.Append($"\"stage\":{JsonEscape(stage ?? string.Empty)},");
                sb.Append($"\"action\":{JsonEscape(action ?? string.Empty)}");

                if (durationMs.HasValue)
                    sb.Append($",\"durationMs\":{durationMs.Value}");

                if (counts != null && counts.Count > 0)
                {
                    sb.Append(",\"counts\":{");
                    bool first = true;
                    foreach (var kv in counts)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        string key = kv.Key ?? string.Empty;
                        object val = kv.Value;
                        if (val == null)
                            sb.Append($"{JsonEscape(key)}:null");
                        else if (val is string)
                            sb.Append($"{JsonEscape(key)}:{JsonEscape(val as string)}");
                        else if (val is bool)
                            sb.Append($"{JsonEscape(key)}:{val.ToString().ToLowerInvariant()}");
                        else
                            sb.Append($"{JsonEscape(key)}:{val}");
                    }
                    sb.Append('}');
                }

                if (!string.IsNullOrEmpty(details))
                {
                    sb.Append($",\"details\":{JsonEscape(details)}");
                }

                sb.Append('}');
                sb.Append(Environment.NewLine);

                lock (_sync)
                {
                    File.AppendAllText(_logFilePath, sb.ToString(), Encoding.UTF8);
                }
            }
            catch
            {
                // Swallow all exceptions: logging must not break main flow.
            }
        }

        /// <summary>
        /// Returns current log file path (best effort).
        /// </summary>
        public static string GetLogPath()
        {
            try
            {
                EnsureInitialized();
                return _logFilePath;
            }
            catch
            {
                return null;
            }
        }
    }
}