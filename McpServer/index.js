#!/usr/bin/env node
// MCP server for the AerBIM Manager Revit add-in (PluginForRevit2022).
//
//   AI client (Claude Code / Claude Desktop) --stdio--> this server --WebSocket--> Revit add-in (localhost:8965)
//
// Tools are declared in tools.json; every call is forwarded to the add-in command of the same name
// (Mcp/McpCommandExecutor.cs). Adding a command = register it in C# + add an entry to tools.json.
//
// The message format follows REVIT_MCP_study (MIT, github.com/shuotao/REVIT_MCP_study):
//   request  {"CommandName", "Parameters", "RequestId"}
//   response {"Success", "Data", "Error", "RequestId"}
//
// stdout belongs to the MCP protocol: log only to stderr.

import { randomUUID } from "node:crypto";
import { readFileSync } from "node:fs";
import WebSocket from "ws";
import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { CallToolRequestSchema, ListToolsRequestSchema } from "@modelcontextprotocol/sdk/types.js";

const DEFAULT_PORT = 8965;
const CONNECT_TIMEOUT_MS = 5000;
const COMMAND_TIMEOUT_MS = 120000;

function getPort() {
  const raw = process.env.AERBIM_MCP_PORT;
  const port = raw ? Number.parseInt(raw, 10) : DEFAULT_PORT;
  if (!Number.isInteger(port) || port < 1024 || port > 65535) {
    console.error(`[aerbim] Invalid AERBIM_MCP_PORT="${raw}", using ${DEFAULT_PORT}`);
    return DEFAULT_PORT;
  }
  return port;
}

const PORT = getPort();
const TOOLS = JSON.parse(readFileSync(new URL("./tools.json", import.meta.url), "utf8")).tools;
const TOOLS_BY_NAME = new Map(TOOLS.map((tool) => [tool.name, tool]));

class RevitConnection {
  constructor(port) {
    this.port = port;
    this.socket = null;
    this.connecting = null;
    this.pending = new Map();
  }

  async ensureConnected() {
    if (this.socket && this.socket.readyState === WebSocket.OPEN) return;
    if (this.connecting) return this.connecting;

    this.connecting = new Promise((resolve, reject) => {
      const socket = new WebSocket(`ws://localhost:${this.port}/`);
      const timer = setTimeout(() => {
        socket.terminate();
        reject(new Error(this.notReachableMessage()));
      }, CONNECT_TIMEOUT_MS);

      socket.on("open", () => {
        clearTimeout(timer);
        this.socket = socket;
        console.error(`[aerbim] Connected to Revit add-in on port ${this.port}`);
        resolve();
      });

      socket.on("unexpected-response", (_req, res) => {
        clearTimeout(timer);
        socket.terminate();
        reject(new Error(res.statusCode === 409
          ? "Revit add-in already has another MCP connection (409). Press 'Сброс MCP' in Revit and try again."
          : `Revit add-in rejected the connection (HTTP ${res.statusCode}).`));
      });

      socket.on("error", (err) => {
        clearTimeout(timer);
        reject(err.code === "ECONNREFUSED" ? new Error(this.notReachableMessage()) : err);
      });

      socket.on("message", (data) => this.onMessage(data));

      socket.on("close", () => {
        if (this.socket === socket) this.socket = null;
        for (const [, entry] of this.pending) {
          clearTimeout(entry.timer);
          entry.reject(new Error("Connection to Revit closed before the command finished."));
        }
        this.pending.clear();
      });
    }).finally(() => {
      this.connecting = null;
    });

    return this.connecting;
  }

  notReachableMessage() {
    return `Revit add-in is not reachable on localhost:${this.port}. ` +
      "Open Revit, then press 'MCP' on the 'AerBIM Manager' tab so it shows 'вкл'.";
  }

  onMessage(data) {
    let message;
    try {
      message = JSON.parse(data.toString());
    } catch (err) {
      console.error("[aerbim] Bad JSON from Revit:", err);
      return;
    }
    const entry = this.pending.get(message.RequestId);
    if (!entry) return;
    this.pending.delete(message.RequestId);
    clearTimeout(entry.timer);
    if (message.Success) entry.resolve(message.Data);
    else entry.reject(new Error(message.Error || "Command failed in Revit."));
  }

  async send(commandName, parameters = {}, timeoutMs = COMMAND_TIMEOUT_MS) {
    await this.ensureConnected();
    const requestId = randomUUID();
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(requestId);
        reject(new Error(`Revit did not answer '${commandName}' within ${timeoutMs / 1000} s. ` +
          "Revit may be busy or showing a modal dialog."));
      }, timeoutMs);
      this.pending.set(requestId, { resolve, reject, timer });
      this.socket.send(JSON.stringify({ CommandName: commandName, Parameters: parameters, RequestId: requestId }));
    });
  }
}

/** Applies schema defaults and checks required / basic types, so the add-in always gets explicit values. */
function prepareArguments(tool, args) {
  const schema = tool.inputSchema || {};
  const props = schema.properties || {};
  const result = {};
  for (const [key, value] of Object.entries(args || {})) {
    if (value !== undefined && value !== null && key in props) result[key] = value;
  }
  for (const [key, prop] of Object.entries(props)) {
    if (!(key in result) && "default" in prop) result[key] = prop.default;
  }
  const errors = [];
  for (const key of schema.required || []) {
    if (!(key in result)) errors.push(`'${key}' is required`);
  }
  for (const [key, value] of Object.entries(result)) {
    const prop = props[key];
    const ok =
      prop.type === "array" ? Array.isArray(value) && (!prop.minItems || value.length >= prop.minItems)
      : prop.type === "integer" ? Number.isInteger(value)
      : prop.type === "boolean" ? typeof value === "boolean"
      : prop.type === "string" ? typeof value === "string" && (!prop.enum || prop.enum.includes(value))
      : true;
    if (!ok) errors.push(`'${key}' must be ${prop.type}${prop.enum ? ` (${prop.enum.join("|")})` : ""}`);
  }
  if (errors.length) throw new Error(`Invalid arguments for ${tool.name}: ${errors.join("; ")}`);
  return result;
}

const revit = new RevitConnection(PORT);
const server = new Server({ name: "aerbim-revit", version: "0.2.0" }, { capabilities: { tools: {} } });

server.setRequestHandler(ListToolsRequestSchema, async () => ({
  tools: TOOLS.map(({ name, title, description, inputSchema, annotations }) => ({
    name, title, description, inputSchema, annotations,
  })),
}));

server.setRequestHandler(CallToolRequestSchema, async (request) => {
  const tool = TOOLS_BY_NAME.get(request.params.name);
  if (!tool) {
    return { isError: true, content: [{ type: "text", text: `Unknown tool: ${request.params.name}` }] };
  }
  try {
    const args = prepareArguments(tool, request.params.arguments);
    // Долгие инструменты задают свой лимит; пакет по видам получает его на каждый вид.
    const timeoutMs = tool.timeoutSeconds
      ? tool.timeoutSeconds * 1000 * Math.max(1, Array.isArray(args.views) ? args.views.length : 1)
      : COMMAND_TIMEOUT_MS;
    const data = await revit.send(tool.command || tool.name, args, timeoutMs);
    return { content: [{ type: "text", text: JSON.stringify(data, null, 2) }] };
  } catch (err) {
    return { isError: true, content: [{ type: "text", text: String(err.message || err) }] };
  }
});

await server.connect(new StdioServerTransport());
console.error(`[aerbim] MCP server ready: ${TOOLS.length} tools, Revit add-in port ${PORT}`);
