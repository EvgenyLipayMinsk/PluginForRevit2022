#!/usr/bin/env node
// MCP server for the AerBIM Manager Revit add-in (PluginForRevit2022).
//
//   AI client (Claude Code / Claude Desktop) --stdio--> this server --WebSocket--> Revit add-in (localhost:8965)
//
// The message format follows REVIT_MCP_study (MIT, github.com/shuotao/REVIT_MCP_study):
//   request  {"CommandName", "Parameters", "RequestId"}
//   response {"Success", "Data", "Error", "RequestId"}
//
// stdout belongs to the MCP protocol: log only to stderr.

import { randomUUID } from "node:crypto";
import WebSocket from "ws";
import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";

const DEFAULT_PORT = 8965;
const CONNECT_TIMEOUT_MS = 5000;
const COMMAND_TIMEOUT_MS = 60000;

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

  async send(commandName, parameters = {}) {
    await this.ensureConnected();
    const requestId = randomUUID();
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(requestId);
        reject(new Error(`Revit did not answer '${commandName}' within ${COMMAND_TIMEOUT_MS / 1000} s. ` +
          "Revit may be busy or showing a modal dialog."));
      }, COMMAND_TIMEOUT_MS);
      this.pending.set(requestId, { resolve, reject, timer });
      this.socket.send(JSON.stringify({ CommandName: commandName, Parameters: parameters, RequestId: requestId }));
    });
  }
}

const revit = new RevitConnection(PORT);

async function callRevit(commandName, parameters) {
  try {
    const data = await revit.send(commandName, parameters);
    return { content: [{ type: "text", text: JSON.stringify(data, null, 2) }] };
  } catch (err) {
    return { isError: true, content: [{ type: "text", text: String(err.message || err) }] };
  }
}

const server = new McpServer({ name: "aerbim-revit", version: "0.1.0" });

server.registerTool(
  "ping",
  {
    title: "Check Revit connection",
    description: "Checks that the AerBIM Manager add-in in Revit is reachable. Returns Revit version and the active document.",
    annotations: { readOnlyHint: true },
  },
  async () => callRevit("ping", {}),
);

server.registerTool(
  "list_project_parameters",
  {
    title: "List project parameters",
    description: "Lists project parameters of the active Revit project (Manage > Project Parameters): id, name, " +
      "kind (shared/project), binding (instance/type) and bound categories.",
    inputSchema: {
      nameContains: z.string().optional().describe("Return only parameters whose name contains this text (case-insensitive)."),
    },
    annotations: { readOnlyHint: true },
  },
  async ({ nameContains }) => callRevit("list_project_parameters", nameContains ? { nameContains } : {}),
);

server.registerTool(
  "remove_project_parameter",
  {
    title: "Remove a project parameter",
    description: "Removes a project parameter from the active Revit project by id or exact name. " +
      "All values of this parameter in all elements are lost, and dependent elements (filters, schedule fields) may be removed. " +
      "dryRun defaults to true: it only reports what would be deleted. " +
      "Show the preview to the user and get explicit confirmation before calling again with dryRun=false.",
    inputSchema: {
      id: z.number().int().optional().describe("Parameter id from list_project_parameters (preferred)."),
      name: z.string().optional().describe("Exact parameter name. If several parameters share it, use id."),
      dryRun: z.boolean().optional().default(true).describe("true = preview only (default); false = actually delete."),
    },
    annotations: { readOnlyHint: false, destructiveHint: true },
  },
  async ({ id, name, dryRun }) => {
    if (id === undefined && !name) {
      return { isError: true, content: [{ type: "text", text: "Provide id or name of the parameter." }] };
    }
    const parameters = { dryRun };
    if (id !== undefined) parameters.id = id;
    if (name) parameters.name = name;
    return callRevit("remove_project_parameter", parameters);
  },
);

await server.connect(new StdioServerTransport());
console.error(`[aerbim] MCP server ready (Revit add-in port ${PORT})`);
