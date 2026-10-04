import { AsyncLocalStorage } from "async_hooks";

/** The Revit plugin socket a tool call talks to. */
export interface RevitTarget {
  host: string;
  port: number;
}

const storage = new AsyncLocalStorage<RevitTarget>();

export function parsePort(raw: string | number | undefined, fallback: number): number {
  if (raw === undefined || raw === "") return fallback;
  const port = Number(raw);
  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    throw new Error(`Invalid port '${raw}'`);
  }
  return port;
}

/** `REVIT_HOST` / `REVIT_PORT`, defaulting to the local plugin on localhost:8080. */
export function defaultRevitTarget(env: NodeJS.ProcessEnv = process.env): RevitTarget {
  return {
    host: env.REVIT_HOST?.trim() || "localhost",
    port: parsePort(env.REVIT_PORT, 8080),
  };
}

/** Run `fn` so that every Revit connection opened inside it goes to `target`. */
export function runWithRevitTarget<T>(target: RevitTarget, fn: () => T): T {
  return storage.run(target, fn);
}

/** Target bound by the current HTTP request, else the environment default. */
export function currentRevitTarget(): RevitTarget {
  return storage.getStore() ?? defaultRevitTarget();
}
