import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { db } from "../database/db.js";
import {
  getDetailIndexInfo,
  listIndexedDocuments,
  replaceDetailIndex,
  searchDetailIndex,
  type IndexedView,
} from "../database/detailLibraryIndex.js";
import { errorText, requestRevit, textResult } from "../utils/draftingLibrary.js";

const PAGE_SIZE = 200;

interface ViewIndexPage {
  documentTitle: string;
  documentPath?: string | null;
  total: number;
  returned: number;
  truncated: boolean;
  views: IndexedView[];
}

async function fetchPage(offset: number, limit: number): Promise<ViewIndexPage> {
  const outcome = await requestRevit<ViewIndexPage>("get_drafting_view_index", { offset, limit });
  if (!outcome.success || !outcome.body) {
    throw new Error(outcome.message || "get_drafting_view_index returned no data");
  }
  return outcome.body;
}

export function registerSearchDetailLibraryTool(server: McpServer) {
  server.tool(
    "search_detail_library",
    "Find typical details by content, e.g. 'HSS shear tab', 'hold down \"anchor bolt\"', 'S2-104'. Searches an index of every drafting view of the active document - view name and title on sheet, sheet/detail number ('3/S2-104') and sheet name, the text of all text notes and sub-detail title symbols, and the family : type names of detail items, generic annotations and detail groups - stored in the server's local SQLite database per document title (FTS5 candidates when available, else LIKE). " +
      "The index is built on first use and rebuilt when `refresh` is true or the drafting-view count changed (call with refresh:true after editing details). Terms match at word starts (prefix); \"quoted phrases\" stay together; results rank by terms matched, then field weight (name > sheet > families > text). Each hit returns the view id/name, sheet, detail number, matched fields, up to 3 matching note snippets and matching families. Pass documentTitle to search a stored index without Revit. Writes only the local index, never the model.",
    {
      query: z.string().trim().min(1).max(500).describe("Keywords; \"quoted phrases\" match together"),
      refresh: z.boolean().optional().default(false).describe("Rebuild the index from the active document first"),
      limit: z.number().int().min(1).max(200).optional().default(20),
      documentTitle: z
        .string()
        .min(1)
        .max(512)
        .optional()
        .describe("Search the stored index of this document title without contacting Revit (ignored with refresh)"),
    },
    async (args) => {
      try {
        let title = args.documentTitle;
        let indexedNow = false;

        if (args.refresh || !title) {
          const header = await fetchPage(0, 0);
          title = header.documentTitle;
          const existing = getDetailIndexInfo(db, title);
          if (args.refresh || !existing || existing.viewCount !== header.total) {
            const views: IndexedView[] = [];
            for (let offset = 0; offset < header.total; offset += PAGE_SIZE) {
              const page = await fetchPage(offset, PAGE_SIZE);
              views.push(...page.views);
              if (!page.truncated) break;
            }
            replaceDetailIndex(db, { title, path: header.documentPath }, views);
            indexedNow = true;
          }
        }

        const info = getDetailIndexInfo(db, title);
        if (!info) {
          const known = listIndexedDocuments(db).map((d) => d.documentTitle);
          return textResult(
            `search_detail_library failed: no index stored for '${title}'. Open it in Revit and call without documentTitle (or with refresh:true).` +
              (known.length ? ` Indexed documents: ${known.join(", ")}.` : ""),
            true
          );
        }

        const search = searchDetailIndex(db, title, args.query, args.limit);
        return textResult(
          JSON.stringify(
            {
              documentTitle: info.documentTitle,
              indexedAt: info.indexedAt,
              indexedNow,
              viewCount: info.viewCount,
              engine: search.engine,
              query: args.query,
              terms: search.terms,
              total: search.total,
              returned: search.results.length,
              results: search.results,
            },
            null,
            2
          )
        );
      } catch (error) {
        return errorText("search_detail_library", error);
      }
    }
  );
}
