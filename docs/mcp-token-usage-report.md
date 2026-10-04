# Token usage report: structural modelling with the Revit MCP (P181, 2026-10-04)

**Scope.** This covers everything from "let read the project: P181 … PDF" (02:13 UTC) to this report (06:32 UTC), in
Claude Code session `48373f6f…`:
- the main conversation;
- the 4 sub-agents that built the batch-2 tools.

The drafting work done earlier in the same session is excluded.

**Method.**
- **Source:** `C:\Temp\p181\tokens.py` reads the session transcripts and writes the raw numbers to `C:\Temp\p181\tokens.json`.
- **Input token types:**
  - fresh input;
  - cache write (new context);
  - cache read (context re-read on every model call).
- **Weighted** uses the usual pricing ratios, in input-token equivalents: fresh 1×, cache write 1.25×, cache read 0.1×,
  output 5×. It is a cost proxy, not an invoice.
- **Tool sizes** are characters ÷ 4. Images are counted at about 1.6k tokens each.

## Totals

| | Model calls | Input processed | of which cache read | Cache write | Output | Weighted |
|---|---|---|---|---|---|---|
| Main conversation | 295 | 166.3M | 164.8M (99%) | 1.50M | 272k | **19.7M** |
| 4 sub-agents (tool building) | 124 | 9.9M | 9.6M | 0.36M | 10k | **1.5M** |
| **Total** | 419 | 176.2M | | | 282k | **21.2M** |

Weighted cost by token type:

| Token type | Weighted | Share |
|---|---|---|
| Cache reads (re-reading the context) | 16.5M | 78% |
| Cache writes | 1.9M | 9% |
| Output | 1.4M | 6% |
| Sub-agents | 1.5M | 7% |

## By phase (main conversation)

| Phase | Calls | Input | Cache write | Output | Weighted | Share |
|---|---|---|---|---|---|---|
| A PDF read + first P181 model (scripts) | 99 | 68.0M | 253k | 111.3k | 7.64M | 39% |
| B Build create_family_type / load_family, fix B1–B3 | 29 | 24.4M | 61k | 32.5k | 2.68M | 14% |
| C Re-test first build | 14 | 12.2M | 14k | 9.3k | 1.28M | 7% |
| D Batch 2 build (4 sub-agents) + merges | 36 | 32.9M | 51k | 26.9k | 3.48M | 18% |
| E Re-test batch 2 (includes the context compaction) | 22 | 17.3M | 980k | 11.4k | 2.91M | 15% |
| F Pile-cap drop investigation + guard | 14 | 0.8M | 23k | 14.1k | 0.18M | 1% |
| G Learn from the origin model, skill | 20 | 1.8M | 41k | 23.8k | 0.35M | 2% |
| H Deploy + repair 685 piles | 22 | 2.6M | 23k | 11.1k | 0.34M | 2% |
| I copy_families / load_family + re-test + this report | 39 | 6.3M | 53k | 31.6k | 0.85M | 4% |

Sub-agents in phase D:

| Sub-agent | Calls | Weighted |
|---|---|---|
| Datums | 30 | 0.32M |
| Framing | 46 | 0.56M |
| Slabs and foundations | 46 | 0.55M |
| First framing attempt (respawned) | 2 | 0.02M |

## Average context per model call

| Period | Context per call |
|---|---|
| A–E (before compaction) | 687k → 928k tokens |
| F–I (after compaction) | 57k → 175k tokens |

**The main cost driver is conversation length, not the MCP tools.**
- Modelling started in a session that still held the whole drafting work, about 690k tokens of context.
- Every model call re-reads that context, so phase A's 99 calls processed 68M tokens. The model itself (scripts, PDF
  extraction, tool calls) produced only about 111k tokens of output.
- After compaction, a call costs about 1/10 as much. Phase F (14 calls) did a full bug investigation for 0.18M weighted.
  Fourteen calls at phase A's context size would have cost about 1.1M.

## What the tools themselves cost (tool inputs + results, whole period)

| Activity | Calls | Tokens (in+out of tool) |
|---|---|---|
| Code development / build / git (Bash, Edit, Write, Grep) | 123 | ~77k |
| Revit MCP tools, direct | 43 | ~44k |
| Reading files and images (PDF pages, captures) | 13 | ~29k |
| Revit C# scripts (`send_code_to_revit`) | 46 | ~26k |
| PDF reading (Python vector extraction) | 48 | ~26k |
| Tool discovery (`enable_catalog`, `search_tools`, `call_tool`) | 6 | ~9k |
| Sub-agent orchestration | 8 | ~7k |
| Revit MCP tools via the pass-through client | 20 | ~6k |
| **Total** | 307 | **~224k** |

Tool traffic is about 0.1% of the input processed, but it is what makes the context grow. Every result stays in the
context and is re-read by every later call until compaction.

Notable items:

| Item | Cost | Lesson |
|---|---|---|
| `create_point_based_element` with pasted payloads | 6 calls, 14k tokens of input | Motivated `dataFile` (S9). Now 738 beams from a file cost about 50 tokens of input |
| `get_available_family_types` | 1 call, 4k tokens | Unfiltered lists are expensive |
| `create_structural_columns` | 1 call, 2.4k tokens of results | Per-item results; summary mode is S9b |
| `send_code_to_revit` | 43 calls, 17k tokens of input code | The scripts stand in for missing tools. Each one replaced by a tool saves its code plus the debugging round-trips |
| `capture_view` / image reads | 16 images, about 26k tokens | Useful for checking, but the most expensive single results |

## Recommendations

1. **Start a fresh session (or `/clear`) per job.** Modelling inside the drafting session cost about 5× what the same
   work would cost from a clean start.
   - Rough estimate: phase A–E from a clean start would have been about 4M weighted instead of 18M.
   - Hand over through the memory and docs files, which is exactly what they are for.
2. **Bulk tools should return summaries by default (S9b).** Return counts, failures and an id range, with per-item
   detail only on request.
3. **Keep using `dataFile` for any batch over about 20 items.** File input costs nearly nothing in tokens.
4. **Close the remaining tool gaps (S13, S18–S22).** Each `send_code_to_revit` fallback costs code tokens plus
   debugging round-trips, and the results stay in context.
5. **Prefer counts and grouped queries over full lists, and capture images only when the check needs eyes.**
6. **Session plumbing:** after `/mcp` reconnect the Revit tools are sometimes not re-exposed, which forces the
   pass-through client. Fixing that makes direct tool calls usable again.

## Re-test results in this phase (copy_families / load_family)

| Test | Result |
|---|---|
| `copy_families listOnly` from the reference `.rvt` (workshared, opened in background) | ✅ 44 s, 10 families (5 in-place flagged) + floor/foundation system types |
| Copy 3 families + 3 system type patterns, `saveToFolder` | ✅ 43 s; 2 families loaded with all types, existing `M_Footing-Rectangular` kept; 5 system types copied (deck floor, AC_S-2xx, wall foundation 1000x300); 3 .rfa saved |
| Re-run (idempotence) | ✅ family and types reported `alreadyInProject`, nothing duplicated |
| Unknown family name | ⚠️ failed the whole call after a 40 s open. Fixed to fail per item (commit e9c832d); deployed at the next Revit restart |
| `load_family` folder + pattern (`M_Pile Cap-*`) | ✅ 12 families: 11 loaded, 1 already present |
| `load_family` selected types from a catalog family (`M_W-Wide Flange-Column`: W310X97, W360X134) | ✅ only those 2 types loaded |
