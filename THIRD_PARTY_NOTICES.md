# Third-party notices

## REVIT_MCP_study

https://github.com/shuotao/REVIT_MCP_study — MIT License.

The MCP bridge in `Mcp/` (WebSocket server on `HttpListener` with an exclusive connection
and Origin rejection, FIFO `ExternalEvent` command queue) and the message format
`{CommandName, Parameters, RequestId}` / `{Success, Data, Error, RequestId}` are adapted
from that project's `MCP/Core/SocketService.cs`, `MCP/Core/ExternalEventManager.cs`
and `MCP-Server/src/socket.ts`.

## npm packages (McpServer)

`@modelcontextprotocol/sdk`, `ws`, `zod` — MIT License; see each package in `McpServer/node_modules`.
