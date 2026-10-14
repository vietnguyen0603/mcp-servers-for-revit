import { withRevitConnection } from "./ConnectionManager.js";

/** Shared contract and helpers for get_drafting_standards / audit_detail_library / search_detail_library. */

export const STANDARDS_SECTIONS = [
  "scales",
  "lineStyles",
  "textTypes",
  "dimensionTypes",
  "filledRegionTypes",
  "detailComponents",
  "annotationSymbols",
  "roles",
  "detailGroups",
  "viewportTypes",
  "titleBlocks",
  "viewNames",
] as const;

export const AUDIT_CHECKS = [
  "references",
  "placement",
  "naming",
  "lineStyles",
  "textTypes",
  "dimensionTypes",
  "filledRegionTypes",
  "cadImports",
  "textOverlaps",
  "dimensionOverrides",
  "drawingList",
] as const;

/** The library commands walk every drafting view; the C# side allows 180 s. */
export const LIBRARY_COMMAND_TIMEOUT_MS = 190000;

export interface RevitCommandOutcome<T> {
  success: boolean;
  message: string;
  body: T | undefined;
  raw: unknown;
}

/** Sends a command and unwraps the `{ Success, Message, Response }` envelope (either casing). */
export async function requestRevit<T>(
  command: string,
  params: unknown,
  timeoutMs = LIBRARY_COMMAND_TIMEOUT_MS
): Promise<RevitCommandOutcome<T>> {
  const raw = (await withRevitConnection((client) => client.sendCommand(command, params, timeoutMs))) as Record<
    string,
    any
  > | null;
  const success = raw?.Success ?? raw?.success;
  return {
    success: success !== false && raw != null,
    message: String(raw?.Message ?? raw?.message ?? ""),
    body: (raw?.Response ?? raw?.response) as T | undefined,
    raw,
  };
}

export function textResult(text: string, isError = false) {
  return { content: [{ type: "text" as const, text }], ...(isError ? { isError: true } : {}) };
}

export function errorText(command: string, error: unknown) {
  return textResult(`${command} failed: ${error instanceof Error ? error.message : String(error)}`, true);
}

// ---------------------------------------------------------------------------
// get_drafting_standards markdown rendering

type Row = Record<string, any>;

function cell(value: unknown): string {
  if (value === null || value === undefined || value === "") return "";
  return String(value).replace(/\|/g, "\\|").replace(/\r?\n/g, " ");
}

function table(headers: string[], rows: unknown[][]): string {
  const lines = [`| ${headers.join(" | ")} |`, `|${headers.map(() => "---").join("|")}|`];
  for (const row of rows) lines.push(`| ${row.map(cell).join(" | ")} |`);
  return lines.join("\n");
}

function inches(row: Row): string {
  if (typeof row.sizeIn !== "number") return "";
  return `${row.sizeIn}" (${row.sizeMm} mm)`;
}

function more(section: Row | undefined): string {
  return section?.truncated ? `\n(${section.items.length} of ${section.total} shown)` : "";
}

/**
 * Renders a get_drafting_standards response in the layout of the
 * revit-detail-standards snapshot (`reference/snapshot-<model>.md`).
 */
export function renderStandardsMarkdown(result: Row): string {
  const s: Row = result.sections ?? {};
  const out: string[] = [];
  out.push(`# Drafting standards snapshot: ${result.documentTitle ?? "?"}`);
  out.push(
    `${result.draftingViewsOnly === false ? "Views" : "Drafting views"}: ${result.views ?? 0}, generated ${result.generatedAt ?? ""}`
  );

  if (s.scales) {
    out.push("", "## Scales", s.scales.map((r: Row) => `${r.label} (${r.views})`).join(", "));
  }
  if (s.lineStyles) {
    out.push(
      "",
      "## Line styles (by use)",
      table(
        ["style", "uses", "weight", "color", "pattern"],
        s.lineStyles.items.map((r: Row) => [r.name, r.uses, r.weight, r.color, r.pattern])
      ) + more(s.lineStyles)
    );
  }
  if (s.textTypes) {
    out.push(
      "",
      "## Text note types (by use)",
      table(
        ["type", "uses", "size", "leader arrowhead", "font", "bold"],
        s.textTypes.items.map((r: Row) => [r.name, r.uses, inches(r), r.leaderArrowhead, r.font, r.bold ? "yes" : ""])
      ) + more(s.textTypes),
      `Text with leaders: ${s.textTypes.withLeaders} of ${s.textTypes.textNotes}`
    );
  }
  if (s.dimensionTypes) {
    out.push(
      "",
      "## Dimension types (by use)",
      table(
        ["type", "uses", "style"],
        s.dimensionTypes.items.map((r: Row) => [r.name, r.uses, r.style])
      ) + more(s.dimensionTypes),
      `Common value overrides: ${(s.dimensionTypes.commonOverrides ?? [])
        .map((o: Row) => "`" + o.text + "`")
        .join(", ")}`
    );
  }
  if (s.filledRegionTypes) {
    out.push(
      "",
      "## Filled region types (by use)",
      table(
        ["type", "uses", "masking", "CAD-derived"],
        s.filledRegionTypes.items.map((r: Row) => [r.name, r.uses, r.isMasking ? "yes" : "", r.cadDerived ? "yes" : ""])
      ) + more(s.filledRegionTypes)
    );
  }
  if (s.roles) {
    const roleRows: unknown[][] = [];
    for (const [role, rows] of Object.entries(s.roles as Record<string, Row[]>)) {
      for (const r of rows) roleRows.push([role, `${r.family} : ${r.type}`, r.uses, r.category, r.placement]);
    }
    out.push(
      "",
      "## Families by role (top 5 each)",
      table(["role", "family : type", "uses", "category", "placement"], roleRows)
    );
  }
  if (s.detailComponents) {
    out.push(
      "",
      "## Detail component families (by use)",
      table(
        ["family : type", "uses", "placement", "role"],
        s.detailComponents.items.map((r: Row) => [`${r.family} : ${r.type}`, r.uses, r.placement, r.role])
      ) + more(s.detailComponents)
    );
  }
  if (s.annotationSymbols) {
    out.push("", "## Generic annotation families (by use) with editable parameters");
    for (const r of s.annotationSymbols.items as Row[]) {
      out.push(
        `- **${r.family} : ${r.type}** (${r.uses}, leaders ${r.hasLeaders ? "yes" : "no"}, role ${r.role}): ${(r.parameters ?? []).join(", ")}`
      );
    }
    const tail = more(s.annotationSymbols);
    if (tail) out.push(tail.trim());
  }
  if (s.detailGroups) {
    out.push(
      "",
      "## Detail groups (by use)",
      (s.detailGroups.items as Row[]).map((r) => `${r.name} (${r.uses})`).join(", ") + more(s.detailGroups)
    );
  }
  if (s.viewportTypes || s.titleBlocks || s.viewNames) {
    out.push("", "## Sheets");
    if (s.viewportTypes)
      out.push(`Viewport types: ${(s.viewportTypes.items as Row[]).map((r) => `${r.name} (${r.uses})`).join(", ")}`);
    if (s.titleBlocks)
      out.push(
        `Title blocks: ${(s.titleBlocks.items as Row[]).map((r) => `${r.family} : ${r.type} (${r.uses})`).join(", ")}`
      );
    if (s.viewNames) {
      const n = s.viewNames;
      out.push(`View names matching \`${n.pattern}\`: ${n.matching} of ${n.total}`);
      out.push(`Name prefixes: ${(n.prefixes as Row[]).map((p) => `${p.prefix} (${p.views})`).join(", ")}`);
      out.push(`View name samples: ${(n.samples as string[]).join(" | ")}`);
    }
  }
  return out.join("\n") + "\n";
}
