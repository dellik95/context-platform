# Context Platform — Token-Efficiency & Search Roadmap

Tracks features aimed at one goal: let Claude Code (and any other agent) get
oriented in a codebase and recall past decisions using as few tokens as
possible, via persistent memory + code-intelligence search instead of
re-reading files from scratch.

Status legend: ✅ done · 🚧 in progress · 📋 planned (not started)

## ✅ Done

- Hybrid search (lexical FTS5 + vector cosine + graph proximity + recency + memory signal), token-budgeted context packs (`ContextPackBuilder`).
- Symbol-level incremental indexing (`SymbolDiffer`), tree-sitter based AST parsing.
- Persistent typed memory (Decision/Discovery/BugFix/Feature/...) with reinforcement/supersession, extracted from session summaries via LLM.
- Session lifecycle: start/prompt/tool-use/end hooks, `SessionSummary` (Completed/Discovered/Decisions/OpenIssues/NextSteps/ChangedFiles).
- Periodic mid-session summarization (`SessionSummaryScheduler`) — a session no longer needs to formally "end" to produce a memory. Batches on event count (not just elapsed time) to avoid noise memories.
- MCP tools exposing all of the above to Claude Code directly (`context_search`, `code_search`, `symbol_search`, `memory_search`, `graph_impact`, etc.) + an installable `context-memory` skill teaching Claude to prefer them over re-deriving context.
- Dashboard (Angular): Projects, Overview, Search, Memories, Sessions, Graph (impact/blast-radius), Settings (provider config, reindex schedule, Claude integration installer, folder-browser project creation), System (live health/job status).
- Live SignalR updates: reindex progress, project stats, session-summary/memory-created activity — panels refresh without polling or reload.
- Cross-platform hook installer (fixed a real bug: was double-registering both `.ps1` and `.sh` commands when a repo was set up from more than one OS, causing every hook to fire twice).

## 🚧 In progress

- **Session titles** — `SessionSummary` gets an LLM-generated short descriptive title (like a commit subject line), shown in the Sessions list instead of a bare timestamp.
- **Structured memory (Facts + Narrative)** — inspired by comparing against `codemem`'s memory model: today a `Memory` is one flat `Body` string. Splitting a short, scannable `Facts` list (atomic bullet points) out from the full prose narrative lets a search result show the cheap/compact form first and the full form on demand — the actual token-saving mechanism.
- **Per-file semantic summary ("file wiki")** — indexing currently embeds symbol-level code chunks only. A short LLM-generated natural-language description of what each *file* is for, embedded as its own searchable chunk (`Chunk.SymbolId = null`, schema already supports this), should meaningfully improve recall for natural-language queries ("where is auth handled") that don't literally match code tokens well.

## 📋 Planned (not started)

- **Graph tab visualization** — currently text-only impact lists (callers/implementers/tests). Wanted: an actual node/edge graph visualization; clicking a node shows its connections plus an info panel. Deferred — lower priority than the token/search-efficiency items above.
- **Codemem-style hybrid pack re-ranking refinements** — kind-based ranking bonus (decision/feature/bugfix prioritized) and query-intent detection (todo/recall-style queries route to a specialized retrieval path) in `ContextPackBuilder`/`HybridScorer`. Noted as a possible enhancement, not yet scheduled.
- **Embedding cache cross-provider invalidation** — `Embedding` is keyed by `ContentHash` alone, not `(ContentHash, Provider)`; switching embedding providers doesn't invalidate stale cross-provider cache entries. Known architecture gap, not yet fixed.
- **Docker container → host Ollama networking** — unresolved on this machine's Rancher-Desktop-on-Windows setup; bare-metal is the only currently-verified-working path for local Ollama. Documented as commented-out/opt-in in `docker-compose.yml`.

## Notes

- Real per-project data (this repo dogfooding itself) has repeatedly surfaced actual bugs during live testing that static review missed — keep verifying live (build + test + actually run it) rather than trusting compilation alone before marking anything done here.
- Comparison reference for memory/retrieval design: [codemem](https://github.com/kunickiaj/codemem) — FTS5+BM25 lexical, sqlite-vec semantic, hybrid re-rank with kind bonus, structured Summary/Facts/Narrative/Concepts per memory item, `files_read`/`files_modified` provenance per memory, batched raw-event ingestion before its observer runs (the same reasoning behind this project's event-count threshold above).
