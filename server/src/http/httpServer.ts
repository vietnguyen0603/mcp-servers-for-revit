import { randomUUID } from "crypto";
import fs from "fs";
import http from "http";
import path from "path";
import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StreamableHTTPServerTransport } from "@modelcontextprotocol/sdk/server/streamableHttp.js";
import { isInitializeRequest } from "@modelcontextprotocol/sdk/types.js";
import { runWithRevitTarget } from "../utils/revitTarget.js";
import type { ResolvedUser, UserRegistry } from "./users.js";

export interface HttpServerOptions {
  port: number;
  host: string;
  /**
   * Token mode: each bearer token maps to an employee's Revit PC. Omit for
   * IP mode: tool calls go to the Revit plugin on the PC the request came from.
   */
  users?: UserRegistry;
  /** Revit plugin port used in IP mode. Default 8080. */
  revitPort?: number;
  /**
   * Folder with command set releases (`<year>/manifest.json` + package),
   * served at /commandset/<year>/<file> so the Revit plugins can update
   * themselves. Omit to disable.
   */
  releasesDir?: string;
  /** Builds a fresh MCP server (with its own tool catalog) for each session. */
  createServer: () => Promise<McpServer>;
  /** Close sessions idle for this long. Default 12 h. */
  sessionIdleMs?: number;
  /** Largest accepted request body. Default 50 MB. */
  maxBodyBytes?: number;
}

interface Session {
  transport: StreamableHTTPServerTransport;
  server: McpServer;
  user: string;
  lastSeen: number;
}

export interface RunningHttpServer {
  server: http.Server;
  port: number;
  sessionCount: () => number;
  close: () => Promise<void>;
}

class HttpError extends Error {
  constructor(readonly status: number, message: string, readonly headers: Record<string, string> = {}) {
    super(message);
  }
}

function sendError(res: http.ServerResponse, error: HttpError): void {
  if (res.headersSent) return;
  res.writeHead(error.status, { "Content-Type": "application/json", ...error.headers });
  res.end(JSON.stringify({ jsonrpc: "2.0", error: { code: -32000, message: error.message }, id: null }));
}

function authenticate(req: http.IncomingMessage, users: UserRegistry): ResolvedUser {
  const match = /^Bearer\s+(.+)$/i.exec(req.headers.authorization ?? "");
  const user = match ? users.resolve(match[1].trim()) : undefined;
  if (!user) {
    throw new HttpError(401, "Missing or invalid bearer token", { "WWW-Authenticate": 'Bearer realm="revit-mcp"' });
  }
  return user;
}

/** Client address without the IPv4-mapped IPv6 prefix ("::ffff:192.168.1.5" -> "192.168.1.5"). */
export function clientAddress(remoteAddress: string | undefined): string | undefined {
  if (!remoteAddress) return undefined;
  return remoteAddress.startsWith("::ffff:") ? remoteAddress.slice(7) : remoteAddress;
}

/**
 * IP mode: the caller is routed back to its own address, or to the PC named
 * in an `X-Revit-Host` header (needed where Docker hides the client address,
 * e.g. Docker Desktop on Windows, or when Revit runs on another PC).
 */
function identifyByAddress(req: http.IncomingMessage, revitPort: number): ResolvedUser {
  const header = req.headers["x-revit-host"];
  const named = typeof header === "string" ? header.trim() : "";
  const host = named || clientAddress(req.socket.remoteAddress);
  if (!host) throw new HttpError(400, "Cannot determine the client address");
  return { name: host, target: { host, port: revitPort } };
}

async function readJsonBody(req: http.IncomingMessage, maxBytes: number): Promise<unknown> {
  const chunks: Buffer[] = [];
  let size = 0;
  for await (const chunk of req) {
    size += chunk.length;
    if (size > maxBytes) throw new HttpError(413, `Request body exceeds ${maxBytes} bytes`);
    chunks.push(chunk);
  }
  if (size === 0) return undefined;
  try {
    return JSON.parse(Buffer.concat(chunks).toString("utf8"));
  } catch {
    throw new HttpError(400, "Request body is not valid JSON");
  }
}

const RELEASE_PATH = /^\/commandset\/(\d{4})\/([A-Za-z0-9][A-Za-z0-9._-]*)$/;

/** Serve a command set release file to the Revit plugin updater. */
function serveRelease(res: http.ServerResponse, releasesDir: string, year: string, file: string): void {
  const fullPath = path.join(releasesDir, year, file);
  fs.stat(fullPath, (error, stat) => {
    if (error || !stat.isFile()) {
      sendError(res, new HttpError(404, "Not found"));
      return;
    }
    res.writeHead(200, {
      "Content-Type": file.endsWith(".json") ? "application/json" : "application/octet-stream",
      "Content-Length": stat.size,
      "Cache-Control": "no-cache",
    });
    fs.createReadStream(fullPath).pipe(res);
  });
}

/**
 * MCP over Streamable HTTP for a shared office server. Each request is tied
 * to an employee - by bearer token, or by the address it came from - which
 * picks the PC whose Revit plugin the session's tool calls go to. A session
 * can only be used by the employee who opened it.
 */
export async function startHttpServer(options: HttpServerOptions): Promise<RunningHttpServer> {
  const sessions = new Map<string, Session>();
  const idleMs = options.sessionIdleMs ?? 12 * 60 * 60 * 1000;
  const maxBodyBytes = options.maxBodyBytes ?? 50 * 1024 * 1024;

  async function openSession(user: ResolvedUser): Promise<StreamableHTTPServerTransport> {
    const server = await options.createServer();
    const transport: StreamableHTTPServerTransport = new StreamableHTTPServerTransport({
      sessionIdGenerator: () => randomUUID(),
      onsessioninitialized: (id) => {
        sessions.set(id, { transport, server, user: user.name, lastSeen: Date.now() });
        console.error(`Session opened for ${user.name} -> ${user.target.host}:${user.target.port}`);
      },
    });
    transport.onclose = () => {
      const id = transport.sessionId;
      if (id && sessions.delete(id)) console.error(`Session closed for ${user.name}`);
    };
    await server.connect(transport);
    return transport;
  }

  async function handleMcp(req: http.IncomingMessage, res: http.ServerResponse): Promise<void> {
    const user = options.users
      ? authenticate(req, options.users)
      : identifyByAddress(req, options.revitPort ?? 8080);
    const body = req.method === "POST" ? await readJsonBody(req, maxBodyBytes) : undefined;
    const sessionId = req.headers["mcp-session-id"];

    let transport: StreamableHTTPServerTransport;
    if (typeof sessionId === "string") {
      const session = sessions.get(sessionId);
      // 404 tells the client to start a new session (e.g. after a server restart)
      if (!session) throw new HttpError(404, "Session not found");
      if (session.user !== user.name) throw new HttpError(403, "Session belongs to another user");
      session.lastSeen = Date.now();
      transport = session.transport;
    } else if (req.method === "POST" && isInitializeRequest(body)) {
      transport = await openSession(user);
    } else {
      throw new HttpError(400, "Missing mcp-session-id header");
    }

    await runWithRevitTarget(user.target, () => transport.handleRequest(req, res, body));
  }

  const server = http.createServer((req, res) => {
    const url = new URL(req.url ?? "/", "http://localhost");
    if (url.pathname === "/health") {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ status: "ok", sessions: sessions.size }));
      return;
    }
    const release = req.method === "GET" && options.releasesDir ? RELEASE_PATH.exec(url.pathname) : null;
    if (release && !release[2].includes("..")) {
      serveRelease(res, options.releasesDir!, release[1], release[2]);
      return;
    }
    if (url.pathname !== "/mcp") {
      sendError(res, new HttpError(404, "Not found"));
      return;
    }
    handleMcp(req, res).catch((error) => {
      if (error instanceof HttpError) {
        sendError(res, error);
      } else {
        console.error("MCP request failed:", error);
        sendError(res, new HttpError(500, "Internal server error"));
      }
    });
  });

  const sweep = setInterval(() => {
    const cutoff = Date.now() - idleMs;
    for (const [id, session] of sessions) {
      if (session.lastSeen < cutoff) {
        sessions.delete(id);
        void session.transport.close();
        console.error(`Session expired for ${session.user}`);
      }
    }
  }, Math.min(idleMs, 60_000));
  sweep.unref();

  await new Promise<void>((resolve, reject) => {
    server.once("error", reject);
    server.listen(options.port, options.host, () => {
      server.off("error", reject);
      resolve();
    });
  });
  const address = server.address();
  const port = typeof address === "object" && address ? address.port : options.port;

  return {
    server,
    port,
    sessionCount: () => sessions.size,
    close: async () => {
      clearInterval(sweep);
      await Promise.all([...sessions.values()].map((s) => s.transport.close()));
      sessions.clear();
      server.closeAllConnections();
      await new Promise<void>((resolve) => server.close(() => resolve()));
    },
  };
}
