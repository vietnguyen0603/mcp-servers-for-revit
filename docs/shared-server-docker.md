# Shared MCP server on the office network (Docker)

One MCP server runs in Docker on a LAN machine. Every employee connects to it
from Claude Desktop or Claude Code, and the server sends their tool calls to
the Revit plugin on **their own PC**.

```
Employee A: Claude Desktop ─┐                          ┌─► PC-A: Revit plugin :8080
Employee B: Claude Code   ──┼─► [LAN server / Docker] ─┼─► PC-B: Revit plugin :8080
Employee C: Claude Desktop ─┘    revit-mcp :3000/mcp   └─► PC-C: Revit plugin :8080
```

The two halves are separate builds:

| Part | Build | Runs on |
|---|---|---|
| Revit plugin host (C#) | `dotnet build plugin` | every employee PC, installed once |
| Command set (C#, the Revit half of the tools) | `scripts/publish-commandset.ps1` | published to the server, downloaded by each plugin |
| MCP server (Node.js) | `docker compose build` | the LAN server |

They talk JSON over TCP port 8080. The plugin host is installed once per PC.
The command set (the C# half of the tools) is published to the server and the
plugin downloads it on its own, so new tools need no reinstall. See
[Shipping new tools](#shipping-new-tools).

## How a request finds the right Revit

The server has two modes, set with `REVIT_MCP_AUTH`.

- **`none` (default).** The server connects back to port 8080 on the PC the
  request came from. Claude runs on the same PC as Revit, so no setup is
  needed. A client can name another PC with an `X-Revit-Host` header.
- **`token`.** Each employee sends a bearer token, and `users.json` maps
  tokens to PCs. Use this mode if the server must not be open to everyone on
  the LAN. See [Token mode](#token-mode).

## 1. Server

Use a **Linux** Docker host. The compose file uses `network_mode: host`, so
the server sees each employee's real IP.

```bash
cd server
docker compose up -d --build
curl http://localhost:3000/health     # {"status":"ok","sessions":0}
docker compose logs -f                # "Session opened for 192.168.1.52 -> 192.168.1.52:8080"
```

Docker Desktop on Windows or Mac shows every caller as `172.17.0.1`. With
Docker Desktop, either remove `network_mode: host`, add `ports: ["3000:3000"]`
and have every client send `X-Revit-Host` (below), or run the server without
Docker:

```powershell
cd server; npm ci; npm run build
$env:MCP_TRANSPORT="http"; node build/index.js
```

Settings (environment variables):

| Variable | Default | Meaning |
|---|---|---|
| `MCP_TRANSPORT` | `stdio` (`http` in the image) | `http` = shared server mode |
| `MCP_HTTP_PORT` / `MCP_HTTP_HOST` | `3000` / `0.0.0.0` | Listen address |
| `REVIT_MCP_AUTH` | `none` | `none` or `token` |
| `REVIT_PORT` | `8080` | Plugin port on the employee PCs (`none` mode) |
| `REVIT_MCP_USERS_FILE` | `users.json` (`/config/users.json` in the image) | `token` mode only |
| `REVIT_MCP_RELEASES_DIR` | unset (`/releases` in the image) | Command set packages served to the plugins |
| `REVIT_MCP_DB_PATH` | `server/revit-data.db` (`/data/revit-data.db` in the image) | SQLite store, shared by all users |
| `REVIT_MCP_TOOL_MODE` / `REVIT_MCP_CATALOGS` | `dynamic` | Same as local mode, applied to every session |

Update the server: `git pull && docker compose up -d --build`. Clients reconnect on their own.

## 2. Each employee PC

1. Install the plugin as usual (README → Quick Start). This is the last manual
   install: from then on the command set updates itself from the server (see
   [Shipping new tools](#shipping-new-tools)). Turn that on by creating
   `update.json` next to `RevitMCPPlugin.dll`, once per installed Revit year:
   ```powershell
   Get-ChildItem "$env:APPDATA\Autodesk\Revit\Addins\*\revit_mcp_plugin" -Directory | ForEach-Object {
     Set-Content (Join-Path $_.FullName 'update.json') '{ "serverUrl": "http://mcp-server.company.local:3000" }'
   }
   ```
2. Allow the server through Windows Firewall to the plugin port. Run in an admin
   PowerShell, using the server's LAN IP:
   ```powershell
   New-NetFirewallRule -DisplayName "Revit MCP (from MCP server)" -Direction Inbound `
     -Protocol TCP -LocalPort 8080 -RemoteAddress 192.168.1.10 -Action Allow
   ```
3. Start Revit and enable the commands in the plugin **Settings** as usual.

## 3. Connect Claude

**Claude Code**

```bash
claude mcp add --scope user --transport http revit http://mcp-server.company.local:3000/mcp
```

**Claude Desktop**

Claude Desktop's custom connectors (Settings → Connectors) connect from
Anthropic's cloud and cannot reach a server on the office LAN. Use the
`mcp-remote` bridge in `claude_desktop_config.json` instead. It runs locally
and needs Node.js on the PC:

```json
{
  "mcpServers": {
    "revit": {
      "command": "cmd",
      "args": ["/c", "npx", "-y", "mcp-remote", "http://mcp-server.company.local:3000/mcp", "--allow-http"]
    }
  }
}
```

`--allow-http` is required for a plain `http://` URL.

**Naming the PC explicitly (`X-Revit-Host`).** Use this when the server can't see real IPs, or when Revit runs on a
different PC than Claude:

- Claude Code: add `--header "X-Revit-Host: PC-VIET.company.local"`
- Claude Desktop: add `"--header", "X-Revit-Host:PC-VIET.company.local"` to `args`

## Shipping new tools

A tool is a TypeScript tool on the server plus, usually, a C# command in the
command set. Both halves are delivered by the server, so employees never
reinstall anything:

1. Merge the tool, then on a Windows dev PC with the Revit SDKs:
   ```powershell
   ./scripts/publish-commandset.ps1 -Years 2024,2025,2026 -Out \\mcp-server\revit-mcp\server\releases
   ```
   This builds the command set for each Revit year and writes
   `releases/<year>/commandset-<year>-<version>.zip` plus `manifest.json`
   (version, file, SHA-256). Without `-Out` it writes to `server/releases/`.
2. On the server: `git pull && docker compose up -d --build` (only needed when
   the TypeScript side changed; the releases folder is read live).
3. Employees restart Revit and click **Revit MCP Switch**. The plugin compares
   its version with the manifest, downloads and verifies the new package into
   `Commands\.managed\<year>\<version>\`, and loads **every** command in it.
   The per-PC Settings selection is not used while `update.json` is present.

Details:

- **Restart required.** A new version applies from the next Revit session. A running Revit keeps
  the version it loaded, because .NET can't unload a loaded DLL. If an employee calls a tool their Revit
  doesn't have yet, the error tells them to restart Revit.
- **Server unreachable.** The plugin keeps using the last downloaded version. If none was ever
  downloaded, it falls back to the commands enabled in Settings, as before.
- **Corrupt or partial downloads are rejected.** A package whose SHA-256 doesn't match the manifest is
  discarded, and the previous version stays in use.
- **Old versions are cleaned up.** They're deleted at the next start. A version still held by another
  open Revit is removed later.
- **Plugin changes still need a reinstall.** The plugin host itself (`RevitMCPPlugin.dll`: socket,
  loader, updater) isn't auto-updated. It changes rarely.
- **Release files have no access control.** `/commandset/...` is served without authentication, even in token mode,
  because the plugin has no token.

## Token mode

Use this when the server must only serve known employees:

1. `cp users.example.json users.json` and give each employee a token and their PC:
   ```json
   { "users": [ { "name": "viet", "token": "<random>", "revitHost": "PC-VIET.company.local" } ] }
   ```
   Generate tokens with `node -e "console.log(require('crypto').randomBytes(24).toString('hex'))"`.
   The file is re-read when it changes. It holds secrets and is git-ignored.
2. In `docker-compose.yml`, set `REVIT_MCP_AUTH: token` and uncomment the `users.json` volume.
3. Clients add the token:
   - Claude Code: `--header "Authorization: Bearer <token>"`
   - Claude Desktop: `"--header", "Authorization:${AUTH_HEADER}"` in `args`, plus
     `"env": { "AUTH_HEADER": "Bearer <token>" }`. Windows mangles arguments
     containing spaces, hence the env var.

## Limits in shared mode

- **No access control in `none` mode.** Anyone on the LAN who can reach the server can use it, and with
  `X-Revit-Host` can target any PC whose firewall admits the server. Switch to
  token mode when that stops being acceptable.
- **File paths are read on the server, not your PC.** This affects `dataFile` in the bulk
  create tools (`create_beams`, `create_slabs`, `create_foundations`,
  `create_structural_columns`) and the image tools (`image_info`,
  `crop_image_grid`, `overlay_images`). Mount a network share into the
  container, for example `- /mnt/projects:/projects:ro`, and pass `/projects/...`
  paths, or pass the items inline.
- **Files Revit writes stay on the employee's PC.** Exports, the `folder` of `capture_view`,
  and similar outputs are written on the PC running Revit, as before.
- **Requests to the same PC are queued.** Different PCs run in parallel.
- **Idle sessions close after 12 hours.** The client then opens a new one automatically.
