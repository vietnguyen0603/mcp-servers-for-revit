// better-sqlite3 stand-in for the single-exe build: its native addon cannot be
// embedded by `bun build --compile`, and bun:sqlite exposes the same prepare /
// exec / transaction API. `strict` lets named params bind without their prefix,
// matching better-sqlite3.
import { Database as BunDatabase } from "bun:sqlite";

export default class Database extends BunDatabase {
  constructor(file: string) {
    super(file, { create: true, strict: true });
  }

  pragma(source: string) {
    return this.query(`PRAGMA ${source}`).all();
  }
}
