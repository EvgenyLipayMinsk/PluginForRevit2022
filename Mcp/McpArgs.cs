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
