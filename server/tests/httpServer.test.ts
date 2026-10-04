import { mkdirSync, mkdtempSync, utimesSync, writeFileSync } from "fs";
import net from "net";
import { tmpdir } from "os";
import path from "path";
import { afterEach, describe, expect, it } from "vitest";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { clientAddress, startHttpServer, type RunningHttpServer } from "../src/http/httpServer.js";
import { parseUsers, UserRegistry } from "../src/http/users.js";
import { withRevitConnection } from "../src/utils/ConnectionManager.js";

/** A stand-in for one employee's Revit plugin: answers every command with its own name. */
async function fakeRevit(name: string): Promise<{ port: number; commands: string[]; close: () => Promise<void> }> {
  const commands: string[] = [];
  const server = net.createServer((socket) => {
    socket.on("data", (data) => {
      const request = JSON.parse(data.toString());
      commands.push(request.method);
      socket.write(JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { revit: name } }));
    });
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const port = (server.address() as net.AddressInfo).port;
  return { port, commands, close: () => new Promise((resolve) => server.close(() => resolve())) };
}

async function createTestServer(): Promise<McpServer> {
  const server = new McpServer({ name: "test", version: "1.0.0" });
  server.tool("whoami", "Ask the connected Revit who it is", async () => {
    const result = await withRevitConnection((client) => client.sendCommand("whoami", {}));
    return { content: [{ type: "text" as const, text: JSON.stringify(result) }] };
  });
  return server;
}

function writeUsers(file: string, users: unknown[]): void {
  writeFileSync(file, JSON.stringify({ users }));
}

const TOKEN_A = "token-for-alice-0123456789";
const TOKEN_B = "token-for-bob-0123456789ab";

describe("HTTP server", () => {
  const cleanups: Array<() => Promise<void>> = [];
  afterEach(async () => {
    for (const cleanup of cleanups.splice(0).reverse()) await cleanup();
  });

  async function setup() {
    const revitA = await fakeRevit("A");
    const revitB = await fakeRevit("B");
    cleanups.push(revitA.close, revitB.close);
    const usersFile = path.join(mkdtempSync(path.join(tmpdir(), "users-")), "users.json");
    writeUsers(usersFile, [
      { name: "alice", token: TOKEN_A, revitHost: "127.0.0.1", revitPort: revitA.port },
      { name: "bob", token: TOKEN_B, revitHost: "127.0.0.1", revitPort: revitB.port },
    ]);
    const running: RunningHttpServer = await startHttpServer({
      port: 0,
      host: "127.0.0.1",
      users: new UserRegistry(usersFile),
      createServer: createTestServer,
    });
    cleanups.push(running.close);
    const url = new URL(`http://127.0.0.1:${running.port}/mcp`);
    return { revitA, revitB, running, url, usersFile };
  }

  async function connect(url: URL, token: string) {
    const transport = new StreamableHTTPClientTransport(url, {
      requestInit: { headers: { Authorization: `Bearer ${token}` } },
    });
    const client = new Client({ name: "test-client", version: "1.0.0" });
    await client.connect(transport);
    cleanups.push(() => client.close());
    return { client, transport };
  }

  async function whoami(client: Client): Promise<string> {
    const result = (await client.callTool({ name: "whoami", arguments: {} })) as {
      content: Array<{ text: string }>;
    };
    return JSON.parse(result.content[0].text).revit;
  }

  it("routes each user's tool calls to their own Revit", async () => {
    const { revitA, revitB, running, url } = await setup();
    const alice = await connect(url, TOKEN_A);
    const bob = await connect(url, TOKEN_B);

    const answers = await Promise.all([whoami(alice.client), whoami(bob.client), whoami(alice.client)]);

    expect(answers).toEqual(["A", "B", "A"]);
    expect(revitA.commands).toEqual(["whoami", "whoami"]);
    expect(revitB.commands).toEqual(["whoami"]);
    expect(running.sessionCount()).toBe(2);
  });

  it("rejects missing or unknown tokens", async () => {
    const { url } = await setup();
    const init = JSON.stringify({
      jsonrpc: "2.0",
      id: 1,
      method: "initialize",
      params: { protocolVersion: "2025-03-26", capabilities: {}, clientInfo: { name: "x", version: "1" } },
    });
    const headers = { "Content-Type": "application/json", Accept: "application/json, text/event-stream" };

    const missing = await fetch(url, { method: "POST", headers, body: init });
    expect(missing.status).toBe(401);
    expect(missing.headers.get("www-authenticate")).toMatch(/Bearer/);

    const wrong = await fetch(url, { method: "POST", headers: { ...headers, Authorization: "Bearer nope" }, body: init });
    expect(wrong.status).toBe(401);
  });

  it("does not let one user drive another user's session", async () => {
    const { url } = await setup();
    const alice = await connect(url, TOKEN_A);

    const response = await fetch(url, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Accept: "application/json, text/event-stream",
        Authorization: `Bearer ${TOKEN_B}`,
        "mcp-session-id": alice.transport.sessionId!,
      },
      body: JSON.stringify({ jsonrpc: "2.0", id: 2, method: "tools/list" }),
    });
    expect(response.status).toBe(403);
  });

  it("answers 404 for unknown sessions and serves /health without a token", async () => {
    const { url } = await setup();
    const unknown = await fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json", Authorization: `Bearer ${TOKEN_A}`, "mcp-session-id": "gone" },
      body: JSON.stringify({ jsonrpc: "2.0", id: 2, method: "tools/list" }),
    });
    expect(unknown.status).toBe(404);

    const health = await fetch(new URL("/health", url));
    expect(health.status).toBe(200);
    expect(await health.json()).toMatchObject({ status: "ok" });
  });

  it("picks up user changes without a restart", async () => {
    const { revitA, url, usersFile } = await setup();
    const carol = "token-for-carol-0123456789";
    writeUsers(usersFile, [{ name: "carol", token: carol, revitHost: "127.0.0.1", revitPort: revitA.port }]);
    const future = new Date(Date.now() + 5000);
    utimesSync(usersFile, future, future);

    const { client } = await connect(url, carol);
    expect(await whoami(client)).toBe("A");
    await expect(connect(url, TOKEN_B)).rejects.toThrow();
  });
});

describe("HTTP server without auth", () => {
  it("routes tool calls back to the Revit on the caller's own address", async () => {
    const revit = await fakeRevit("local");
    const running = await startHttpServer({
      port: 0,
      host: "127.0.0.1",
      revitPort: revit.port,
      createServer: createTestServer,
    });
    const transport = new StreamableHTTPClientTransport(new URL(`http://127.0.0.1:${running.port}/mcp`));
    const client = new Client({ name: "test-client", version: "1.0.0" });
    try {
      await client.connect(transport);
      const result = (await client.callTool({ name: "whoami", arguments: {} })) as { content: Array<{ text: string }> };
      expect(JSON.parse(result.content[0].text).revit).toBe("local");
      expect(revit.commands).toEqual(["whoami"]);
    } finally {
      await client.close();
      await running.close();
      await revit.close();
    }
  });

  it("routes to the PC named in X-Revit-Host", async () => {
    const revit = await fakeRevit("named");
    const running = await startHttpServer({
      port: 0,
      host: "127.0.0.1",
      revitPort: revit.port,
      createServer: createTestServer,
    });
    const transport = new StreamableHTTPClientTransport(new URL(`http://127.0.0.1:${running.port}/mcp`), {
      requestInit: { headers: { "X-Revit-Host": "localhost" } },
    });
    const client = new Client({ name: "test-client", version: "1.0.0" });
    try {
      await client.connect(transport);
      const result = (await client.callTool({ name: "whoami", arguments: {} })) as { content: Array<{ text: string }> };
      expect(JSON.parse(result.content[0].text).revit).toBe("named");
    } finally {
      await client.close();
      await running.close();
      await revit.close();
    }
  });

  it("strips the IPv4-mapped prefix from client addresses", () => {
    expect(clientAddress("::ffff:192.168.1.5")).toBe("192.168.1.5");
    expect(clientAddress("192.168.1.5")).toBe("192.168.1.5");
    expect(clientAddress("fe80::1")).toBe("fe80::1");
    expect(clientAddress(undefined)).toBeUndefined();
  });
});

describe("command set releases", () => {
  it("serves release files to the plugin updater and nothing outside the releases folder", async () => {
    const releases = mkdtempSync(path.join(tmpdir(), "releases-"));
    mkdirSync(path.join(releases, "2026"));
    writeFileSync(path.join(releases, "2026", "manifest.json"), JSON.stringify({ version: "v1" }));
    writeFileSync(path.join(releases, "secret.json"), "{}");
    const running = await startHttpServer({
      port: 0,
      host: "127.0.0.1",
      releasesDir: releases,
      createServer: createTestServer,
    });
    const base = `http://127.0.0.1:${running.port}`;
    try {
      const manifest = await fetch(`${base}/commandset/2026/manifest.json`);
      expect(manifest.status).toBe(200);
      expect(await manifest.json()).toEqual({ version: "v1" });

      expect((await fetch(`${base}/commandset/2026/missing.zip`)).status).toBe(404);
      expect((await fetch(`${base}/commandset/2026/..%2Fsecret.json`)).status).toBe(404);
      expect((await fetch(`${base}/commandset/abcd/manifest.json`)).status).toBe(404);
    } finally {
      await running.close();
    }
  });
});

describe("parseUsers", () => {
  const ok = { name: "alice", token: TOKEN_A, revitHost: "PC-ALICE" };

  it("defaults the Revit port to 8080", () => {
    expect(parseUsers(JSON.stringify({ users: [ok] }))).toEqual([{ ...ok, revitPort: 8080 }]);
  });

  it("rejects short tokens, missing hosts and duplicates", () => {
    expect(() => parseUsers(JSON.stringify({ users: [{ ...ok, token: "short" }] }))).toThrow(/16 characters/);
    expect(() => parseUsers(JSON.stringify({ users: [{ ...ok, revitHost: "" }] }))).toThrow(/revitHost/);
    expect(() => parseUsers(JSON.stringify({ users: [ok, { ...ok, name: "bob" }] }))).toThrow(/token is already used/);
    expect(() => parseUsers(JSON.stringify({ users: [ok, { ...ok, token: TOKEN_B }] }))).toThrow(/duplicate name/);
    expect(() => parseUsers(JSON.stringify({ users: [{ ...ok, revitPort: 70000 }] }))).toThrow(/port/);
  });
});
