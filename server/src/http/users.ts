import { createHash, timingSafeEqual } from "crypto";
import fs from "fs";
import { parsePort, type RevitTarget } from "../utils/revitTarget.js";

/** One employee: the bearer token they send and the PC running their Revit. */
export interface UserEntry {
  name: string;
  token: string;
  revitHost: string;
  revitPort?: number;
}

export interface ResolvedUser {
  name: string;
  target: RevitTarget;
}

function digest(token: string): Buffer {
  return createHash("sha256").update(token, "utf8").digest();
}

/** Parse and validate a users file: `{ "users": [ { name, token, revitHost, revitPort? } ] }`. */
export function parseUsers(text: string, source = "users file"): UserEntry[] {
  const json = JSON.parse(text.replace(/^\uFEFF/, ""));
  const list: unknown = Array.isArray(json) ? json : json?.users;
  if (!Array.isArray(list)) {
    throw new Error(`${source}: expected { "users": [...] }`);
  }

  const names = new Set<string>();
  const tokens = new Set<string>();
  return list.map((raw, i) => {
    const entry = raw as Partial<UserEntry>;
    const where = `${source}: users[${i}]`;
    if (typeof entry?.name !== "string" || !entry.name.trim()) throw new Error(`${where} needs a name`);
    if (typeof entry.token !== "string" || entry.token.length < 16) {
      throw new Error(`${where} (${entry.name}) needs a token of at least 16 characters`);
    }
    if (typeof entry.revitHost !== "string" || !entry.revitHost.trim()) {
      throw new Error(`${where} (${entry.name}) needs a revitHost`);
    }
    if (names.has(entry.name)) throw new Error(`${where}: duplicate name '${entry.name}'`);
    if (tokens.has(entry.token)) throw new Error(`${where} (${entry.name}): token is already used by another user`);
    names.add(entry.name);
    tokens.add(entry.token);
    return {
      name: entry.name.trim(),
      token: entry.token,
      revitHost: entry.revitHost.trim(),
      revitPort: parsePort(entry.revitPort, 8080),
    };
  });
}

/**
 * Users loaded from a JSON file, re-read whenever the file changes so an
 * admin can add or remove employees without restarting the server. A file
 * that fails to parse keeps the last good list.
 */
export class UserRegistry {
  private users: UserEntry[] = [];
  private mtimeMs = -1;

  constructor(private readonly file: string) {
    this.reload(true);
  }

  private reload(throwOnError: boolean): void {
    let mtimeMs: number;
    try {
      mtimeMs = fs.statSync(this.file).mtimeMs;
    } catch (error) {
      if (throwOnError) throw new Error(`Cannot read users file '${this.file}': ${String(error)}`);
      return;
    }
    if (mtimeMs === this.mtimeMs) return;

    try {
      this.users = parseUsers(fs.readFileSync(this.file, "utf8"), this.file);
      this.mtimeMs = mtimeMs;
      console.error(`Loaded ${this.users.length} user(s) from ${this.file}`);
    } catch (error) {
      if (throwOnError) throw error;
      console.error(`Ignoring invalid users file, keeping previous list: ${String(error)}`);
      this.mtimeMs = mtimeMs;
    }
  }

  /** The user owning `token`, or undefined. Compares digests in constant time. */
  resolve(token: string): ResolvedUser | undefined {
    this.reload(false);
    const wanted = digest(token);
    const user = this.users.find((u) => timingSafeEqual(digest(u.token), wanted));
    return user && { name: user.name, target: { host: user.revitHost, port: user.revitPort ?? 8080 } };
  }
}
