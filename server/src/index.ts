#!/usr/bin/env node
import path from "path";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { registerTools } from "./tools/register.js";
import { startHttpServer } from "./http/httpServer.js";
import { UserRegistry } from "./http/users.js";
import { parsePort } from "./utils/revitTarget.js";

async function createServer(): Promise<McpServer> {
  const server = new McpServer({
    name: "mcp-server-for-revit",
    version: "1.0.0",
  });
  await registerTools(server);
  return server;
}

// Local mode: one client on the same PC as Revit, over stdio
async function startStdio() {
  const server = await createServer();
  const transport = new StdioServerTransport();
  await server.connect(transport);
  console.error("Revit MCP Server start success");
}

// Shared mode: many employees over HTTP, each routed to their own Revit PC.
// REVIT_MCP_AUTH=none (default) routes to the caller's own IP; token uses users.json
async function startHttp() {
  const auth = (process.env.REVIT_MCP_AUTH ?? "none").trim().toLowerCase();
  if (auth !== "none" && auth !== "token") {
    throw new Error(`Unknown REVIT_MCP_AUTH "${auth}", expected "none" or "token"`);
  }
  const host = process.env.MCP_HTTP_HOST?.trim() || "0.0.0.0";
  const running = await startHttpServer({
    port: parsePort(process.env.MCP_HTTP_PORT, 3000),
    host,
    users:
      auth === "token"
        ? new UserRegistry(path.resolve(process.env.REVIT_MCP_USERS_FILE?.trim() || "users.json"))
        : undefined,
    revitPort: parsePort(process.env.REVIT_PORT, 8080),
    releasesDir: process.env.REVIT_MCP_RELEASES_DIR?.trim() || undefined,
    createServer,
  });
  console.error(
    `Revit MCP Server listening on http://${host}:${running.port}/mcp (` +
      (auth === "token" ? "token auth" : "no auth, routing to each caller's own PC") +
      ")"
  );

  const shutdown = () => {
    running.close().finally(() => process.exit(0));
  };
  process.on("SIGINT", shutdown);
  process.on("SIGTERM", shutdown);
}

async function main() {
  const mode = (process.env.MCP_TRANSPORT ?? "stdio").trim().toLowerCase();
  if (mode === "http") {
    await startHttp();
  } else {
    if (mode !== "stdio") console.error(`Unknown MCP_TRANSPORT "${mode}", using "stdio"`);
    await startStdio();
  }
}

main().catch((error) => {
  console.error("Error starting Revit MCP Server:", error);
  process.exit(1);
});
