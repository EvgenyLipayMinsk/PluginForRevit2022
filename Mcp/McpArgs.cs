using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PluginForRevit2022.Mcp
{
    /// <summary>
    /// Параметры MCP-команды. JavaScriptSerializer отдаёт int/long/decimal/string/bool,
    /// массивы как object[] / ArrayList.
    /// </summary>
    internal sealed class McpArgs
    {
        private readonly Dictionary<string, object> _values;

        public McpArgs(Dictionary<string, object> values)
        {
            _values = values ?? new Dictionary<string, object>();
        }

        private object Raw(string key) => _values.TryGetValue(key, out object value) ? value : null;

        public string String(string key)
        {
            string value = Raw(key)?.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        public long? Long(string key)
        {
            object value = Raw(key);
            if (value == null || (value is string s && string.IsNullOrWhiteSpace(s)))
            {
                return null;
            }
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        public bool? Bool(string key)
        {
            object value = Raw(key);
            if (value == null)
            {
                return null;
            }
            return value is bool b ? b : bool.Parse(value.ToString());
        }

        public double? Double(string key)
        {
            object value = Raw(key);
            if (value == null || (value is string s && string.IsNullOrWhiteSpace(s)))
            {
                return null;
            }
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        public int? Int(string key)
        {
            long? value = Long(key);
            return value == null ? (int?)null : checked((int)value.Value);
        }

        public double[] DoubleArray(string key)
        {
            if (!(Raw(key) is IEnumerable items) || Raw(key) is string)
            {
                return null;
            }
            return items.Cast<object>().Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray();
        }

        /// <summary>Вложенный объект как McpArgs (или null).</summary>
        public McpArgs Object(string key) =>
            Raw(key) is Dictionary<string, object> dict ? new McpArgs(dict) : null;

        /// <summary>Массив объектов.</summary>
        public List<McpArgs> ObjectList(string key) =>
            Raw(key) is IEnumerable items && !(Raw(key) is string)
                ? items.Cast<object>().OfType<Dictionary<string, object>>().Select(d => new McpArgs(d)).ToList()
                : new List<McpArgs>();

        /// <summary>Объект «имя → значение» как словарь строк.</summary>
        public Dictionary<string, string> StringMap(string key) =>
            Raw(key) is Dictionary<string, object> dict
                ? dict.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => Convert.ToString(kv.Value, CultureInfo.InvariantCulture))
                : new Dictionary<string, string>();

        public List<string> StringList(string key)
        {
            object value = Raw(key);
            switch (value)
            {
                case null:
                    return new List<string>();
                case string single:
                    return string.IsNullOrWhiteSpace(single) ? new List<string>() : new List<string> { single };
                case IEnumerable items:
                    return items.Cast<object>().Where(x => x != null).Select(x => x.ToString()).ToList();
                default:
                    return new List<string> { value.ToString() };
            }
        }
    }
}
