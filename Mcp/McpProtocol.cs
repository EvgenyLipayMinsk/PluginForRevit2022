using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace PluginForRevit2022.Mcp
{
    /// <summary>
    /// Формат сообщений совместим с REVIT_MCP_study (MIT, github.com/shuotao/REVIT_MCP_study):
    /// запрос  {"CommandName": "...", "Parameters": {...}, "RequestId": "..."}
    /// ответ   {"Success": true|false, "Data": ..., "Error": "...", "RequestId": "..."}
    /// </summary>
    internal sealed class McpRequest
    {
        public string CommandName { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public string RequestId { get; set; }
    }

    internal sealed class McpResponse
    {
        public bool Success { get; set; }
        public object Data { get; set; }
        public string Error { get; set; }
        public string RequestId { get; set; }

        public static McpResponse Ok(string requestId, object data) =>
            new McpResponse { Success = true, Data = data, RequestId = requestId };

        public static McpResponse Fail(string requestId, string error) =>
            new McpResponse { Success = false, Error = error, RequestId = requestId };
    }

    internal static class McpJson
    {
        private static JavaScriptSerializer CreateSerializer() =>
            new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };

        public static McpRequest ParseRequest(string json)
        {
            var raw = CreateSerializer().DeserializeObject(json) as Dictionary<string, object>;
            if (raw == null)
            {
                return null;
            }

            var request = new McpRequest
            {
                CommandName = raw.TryGetValue("CommandName", out object name) ? name as string : null,
                RequestId = raw.TryGetValue("RequestId", out object id) ? id?.ToString() : null,
            };
            if (raw.TryGetValue("Parameters", out object parameters) && parameters is Dictionary<string, object> dict)
            {
                request.Parameters = dict;
            }
            return request;
        }

        public static string Serialize(object value) => CreateSerializer().Serialize(value);
    }
}
