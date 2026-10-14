import type Database from "better-sqlite3";

/**
 * Drafting-view search index (G18), one set of rows per document title.
 * Candidates come from an FTS5 table when the SQLite build has it, else from
 * LIKE filters; both are ranked by the same field-weighted term scoring so
 * results do not depend on the engine.
 */

type Db = Database.Database;

export interface IndexedFamily {
  name: string;
  category?: string;
  count?: number;
}

/** One row of get_drafting_view_index. */
export interface IndexedView {
  id: number;
  name: string;
  viewType?: string | null;
  scale?: number | null;
  titleOnSheet?: string | null;
  sheetNumber?: string | null;
  sheetName?: string | null;
  detailNumber?: string | null;
  text?: string[];
  families?: IndexedFamily[];
}

export interface DetailIndexInfo {
  documentTitle: string;
  documentPath: string | null;
  indexedAt: string;
  viewCount: number;
  engine: "fts5" | "like";
}

export interface DetailSearchHit {
  viewId: number;
  viewName: string;
  titleOnSheet: string | null;
  sheetNumber: string | null;
  sheetName: string | null;
  detailNumber: string | null;
  scale: number | null;
  score: number;
  matchedTerms: string[];
  matchedIn: string[];
  snippets: string[];
  families: string[];
}

export interface DetailSearchResult {
  engine: "fts5" | "like";
  terms: string[];
  total: number;
  results: DetailSearchHit[];
}

interface ViewRow {
  rid: number;
  view_id: number;
  name: string;
  scale: number | null;
  title_on_sheet: string | null;
  sheet_number: string | null;
  sheet_name: string | null;
  detail_number: string | null;
  text: string;
  families: string;
}

const FIELD_WEIGHTS = { name: 6, sheet: 4, families: 2, text: 1 } as const;
type Field = keyof typeof FIELD_WEIGHTS;
const MAX_CANDIDATES = 5000;

const ftsSupport = new WeakMap<Db, boolean>();

/** Creates the tables (idempotent) and reports whether FTS5 is available. */
export function ensureDetailIndexSchema(db: Db): boolean {
  const known = ftsSupport.get(db);
  if (known !== undefined) return known;

  db.exec(`
    CREATE TABLE IF NOT EXISTS detail_index_docs (
      doc_title TEXT PRIMARY KEY,
      doc_path TEXT,
      indexed_at INTEGER NOT NULL,
      view_count INTEGER NOT NULL
    );
    CREATE TABLE IF NOT EXISTS detail_index_views (
      rid INTEGER PRIMARY KEY AUTOINCREMENT,
      doc_title TEXT NOT NULL,
      view_id INTEGER NOT NULL,
      name TEXT NOT NULL,
      view_type TEXT,
      scale INTEGER,
      title_on_sheet TEXT,
      sheet_number TEXT,
      sheet_name TEXT,
      detail_number TEXT,
      text TEXT NOT NULL DEFAULT '',
      families TEXT NOT NULL DEFAULT '',
      UNIQUE(doc_title, view_id)
    );
    CREATE INDEX IF NOT EXISTS idx_detail_index_views_doc ON detail_index_views(doc_title);
  `);

  let fts = false;
  try {
    db.exec(
      "CREATE VIRTUAL TABLE IF NOT EXISTS detail_index_fts USING fts5(name, sheet, text, families, tokenize = 'unicode61')"
    );
    fts = true;
  } catch {
    fts = false;
  }
  ftsSupport.set(db, fts);
  return fts;
}

/** Test hook: pretend FTS5 is missing so the LIKE path is exercised. */
export function disableFtsForTesting(db: Db) {
  ensureDetailIndexSchema(db);
  ftsSupport.set(db, false);
}

function sheetField(v: { sheetNumber?: string | null; detailNumber?: string | null; sheetName?: string | null }) {
  const parts: string[] = [];
  if (v.sheetNumber) parts.push(v.sheetNumber);
  if (v.detailNumber && v.sheetNumber) parts.push(`${v.detailNumber}/${v.sheetNumber}`);
  if (v.sheetName) parts.push(v.sheetName);
  return parts.join("\n");
}

/** Replaces the whole index of a document in one transaction. */
export function replaceDetailIndex(
  db: Db,
  doc: { title: string; path?: string | null },
  views: IndexedView[]
): DetailIndexInfo {
  const fts = ensureDetailIndexSchema(db);
  const now = Date.now();
  const insertView = db.prepare(`
    INSERT INTO detail_index_views
      (doc_title, view_id, name, view_type, scale, title_on_sheet, sheet_number, sheet_name, detail_number, text, families)
    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
  `);
  const insertFts = fts
    ? db.prepare("INSERT INTO detail_index_fts (rowid, name, sheet, text, families) VALUES (?, ?, ?, ?, ?)")
    : null;

  db.transaction(() => {
    if (fts) {
      db.prepare(
        "DELETE FROM detail_index_fts WHERE rowid IN (SELECT rid FROM detail_index_views WHERE doc_title = ?)"
      ).run(doc.title);
    }
    db.prepare("DELETE FROM detail_index_views WHERE doc_title = ?").run(doc.title);
    const seen = new Set<number>();
    for (const v of views) {
      if (seen.has(v.id)) continue;
      seen.add(v.id);
      const text = (v.text ?? []).join("\n");
      const families = (v.families ?? []).map((f) => f.name).join("\n");
      const name = [v.name, v.titleOnSheet].filter(Boolean).join("\n");
      const info = insertView.run(
        doc.title,
        v.id,
        v.name,
        v.viewType ?? null,
        v.scale ?? null,
        v.titleOnSheet ?? null,
        v.sheetNumber ?? null,
        v.sheetName ?? null,
        v.detailNumber ?? null,
        text,
        families
      );
      insertFts?.run(Number(info.lastInsertRowid), name, sheetField(v), text, families);
    }
    db.prepare(`
      INSERT INTO detail_index_docs (doc_title, doc_path, indexed_at, view_count) VALUES (?, ?, ?, ?)
      ON CONFLICT(doc_title) DO UPDATE SET doc_path = excluded.doc_path, indexed_at = excluded.indexed_at,
        view_count = excluded.view_count
    `).run(doc.title, doc.path ?? null, now, seen.size);
  })();

  return getDetailIndexInfo(db, doc.title)!;
}

export function getDetailIndexInfo(db: Db, title: string): DetailIndexInfo | null {
  const fts = ensureDetailIndexSchema(db);
  const row = db
    .prepare("SELECT doc_title, doc_path, indexed_at, view_count FROM detail_index_docs WHERE doc_title = ?")
    .get(title) as { doc_title: string; doc_path: string | null; indexed_at: number; view_count: number } | undefined;
  if (!row) return null;
  return {
    documentTitle: row.doc_title,
    documentPath: row.doc_path,
    indexedAt: new Date(row.indexed_at).toISOString(),
    viewCount: row.view_count,
    engine: fts ? "fts5" : "like",
  };
}

export function listIndexedDocuments(db: Db): DetailIndexInfo[] {
  ensureDetailIndexSchema(db);
  const titles = db.prepare("SELECT doc_title FROM detail_index_docs ORDER BY doc_title").all() as {
    doc_title: string;
  }[];
  return titles.map((t) => getDetailIndexInfo(db, t.doc_title)!);
}

/** Lower-case, punctuation to spaces, collapsed: the form both sides are matched in. */
export function normalizeText(value: string): string {
  return value
    .toLowerCase()
    .replace(/[^\p{L}\p{N}]+/gu, " ")
    .trim();
}

/** Splits a query into terms; "quoted phrases" stay one term. At most 12 terms. */
export function tokenizeQuery(query: string): string[] {
  const terms: string[] = [];
  const pattern = /"([^"]+)"|(\S+)/g;
  let match: RegExpExecArray | null;
  while ((match = pattern.exec(query)) !== null) {
    const term = normalizeText(match[1] ?? match[2] ?? "");
    if (term && !terms.includes(term)) terms.push(term);
  }
  return terms.slice(0, 12);
}

function escapeRegExp(value: string) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/** Matches a normalized term at a word start (prefix semantics, like FTS5 "tok"*). */
function termMatcher(term: string): RegExp {
  return new RegExp(`(?:^| )${escapeRegExp(term)}`, "u");
}

function ftsQuery(terms: string[]): string {
  return terms
    .map((term) => {
      const tokens = term.split(" ").filter(Boolean);
      const expr = tokens.map((t) => `"${t.replace(/"/g, '""')}"*`).join(" ");
      return tokens.length > 1 ? `(${expr})` : expr;
    })
    .join(" OR ");
}

function candidates(db: Db, title: string, terms: string[], fts: boolean): ViewRow[] {
  const columns =
    "v.rid, v.view_id, v.name, v.scale, v.title_on_sheet, v.sheet_number, v.sheet_name, v.detail_number, v.text, v.families";
  if (fts) {
    return db
      .prepare(
        `SELECT ${columns} FROM detail_index_fts f JOIN detail_index_views v ON v.rid = f.rowid
         WHERE detail_index_fts MATCH ? AND v.doc_title = ? LIMIT ${MAX_CANDIDATES}`
      )
      .all(ftsQuery(terms), title) as ViewRow[];
  }

  const haystack =
    "lower(v.name || ' ' || coalesce(v.title_on_sheet, '') || ' ' || coalesce(v.sheet_number, '') || ' ' || " +
    "coalesce(v.sheet_name, '') || ' ' || v.text || ' ' || v.families)";
  const clauses: string[] = [];
  const params: string[] = [];
  for (const term of terms) {
    const tokens = term.split(" ").filter(Boolean);
    clauses.push(`(${tokens.map(() => `${haystack} LIKE ? ESCAPE '\\'`).join(" AND ")})`);
    for (const t of tokens) params.push(`%${t.replace(/[\\%_]/g, (c) => `\\${c}`)}%`);
  }
  return db
    .prepare(`SELECT ${columns} FROM detail_index_views v WHERE v.doc_title = ? AND (${clauses.join(" OR ")}) LIMIT ${MAX_CANDIDATES}`)
    .all(title, ...params) as ViewRow[];
}

function truncate(value: string, max: number) {
  const flat = value.replace(/\s+/g, " ").trim();
  return flat.length <= max ? flat : `${flat.slice(0, max - 1)}…`;
}

/** Ranked search of a document's index. */
export function searchDetailIndex(db: Db, title: string, query: string, limit: number): DetailSearchResult {
  const fts = ensureDetailIndexSchema(db);
  const terms = tokenizeQuery(query);
  const engine = fts ? "fts5" : "like";
  if (terms.length === 0) return { engine, terms, total: 0, results: [] };

  const matchers = terms.map(termMatcher);
  const hits: DetailSearchHit[] = [];
  for (const row of candidates(db, title, terms, fts)) {
    const textLines = row.text ? row.text.split("\n") : [];
    const familyNames = row.families ? row.families.split("\n") : [];
    const fields: Record<Field, string> = {
      name: normalizeText(`${row.name} ${row.title_on_sheet ?? ""}`),
      sheet: normalizeText(
        `${row.sheet_number ?? ""} ${row.detail_number ?? ""} ${row.detail_number ?? ""} ${row.sheet_number ?? ""} ${row.sheet_name ?? ""}`
      ),
      families: normalizeText(row.families),
      text: normalizeText(row.text),
    };

    let score = 0;
    const matchedTerms: string[] = [];
    const matchedIn = new Set<Field>();
    terms.forEach((term, i) => {
      let best = 0;
      for (const field of Object.keys(FIELD_WEIGHTS) as Field[]) {
        if (matchers[i].test(fields[field])) {
          matchedIn.add(field);
          best = Math.max(best, FIELD_WEIGHTS[field]);
        }
      }
      if (best > 0) {
        matchedTerms.push(term);
        // Longer terms are more specific; repeated notes add a little.
        const repeats = textLines.filter((line) => matchers[i].test(normalizeText(line))).length;
        score += best * (1 + Math.min(term.length, 12) / 12) + Math.min(repeats, 3) * 0.25;
      }
    });
    if (matchedTerms.length === 0) continue;
    if (matchedTerms.length === terms.length) score += 5;

    const lineMatches = (line: string) => {
      const n = normalizeText(line);
      return matchers.some((m) => m.test(n));
    };
    hits.push({
      viewId: row.view_id,
      viewName: row.name,
      titleOnSheet: row.title_on_sheet,
      sheetNumber: row.sheet_number,
      sheetName: row.sheet_name,
      detailNumber: row.detail_number,
      scale: row.scale,
      score: Math.round(score * 100) / 100,
      matchedTerms,
      matchedIn: [...matchedIn],
      snippets: textLines.filter(lineMatches).slice(0, 3).map((line) => truncate(line, 160)),
      families: familyNames.filter(lineMatches).slice(0, 5),
    });
  }

  hits.sort(
    (a, b) =>
      b.matchedTerms.length - a.matchedTerms.length ||
      b.score - a.score ||
      a.viewName.localeCompare(b.viewName) ||
      a.viewId - b.viewId
  );
  return { engine, terms, total: hits.length, results: hits.slice(0, limit) };
}
