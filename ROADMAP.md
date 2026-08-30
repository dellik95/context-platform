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
- **Session titles** — `SessionSummary` gets an LLM-generated short descriptive title (git-commit-subject style), shown in the Sessions list/detail instead of a bare timestamp; deterministic non-empty fallback when offline. Live-verified; also fixed a real bug found in verification (`SessionSummaryParser`'s header regex didn't tolerate the markdown headings — `### TITLE:` — this model reliably produces on real-sized sessions, silently discarding the whole parse).
- **Structured memory (Facts)** — `Memory.FactsJson`: 2-5 short, atomic, standalone bullet facts extracted alongside the existing prose `Body`, for when a search result needs to say something useful in very few tokens. Rendered as a bullet list above the body in the Memories tab. Live-verified against real extracted memories.
- **Per-file semantic summary ("file wiki")** — indexing now also generates a short natural-language description of each file (`IFileSummarizer`, no-op under the noop LLM provider) and embeds it as its own searchable chunk (`Chunk.SymbolId = null`). Live-verified: a real reindex produced real file-level search hits with natural-language snippets. File-kind hits render distinctly in the Search tab.
- Fixed `dotnet run`/`dotnet build` never actually serving the Dashboard (`Context.Host.csproj` only copied the built Angular output into `wwwroot/` at publish time, never for a plain run — found live while verifying the three features above).

## 📋 Planned (not started)

- **Graph tab visualization** — currently text-only impact lists (callers/implementers/tests). Wanted: an actual node/edge graph visualization; clicking a node shows its connections plus an info panel. Deferred — lower priority than the token/search-efficiency items above.
- **Codemem-style hybrid pack re-ranking refinements** — kind-based ranking bonus (decision/feature/bugfix prioritized) and query-intent detection (todo/recall-style queries route to a specialized retrieval path) in `ContextPackBuilder`/`HybridScorer`. Noted as a possible enhancement, not yet scheduled.
- **Embedding cache cross-provider invalidation** — `Embedding` is keyed by `ContentHash` alone, not `(ContentHash, Provider)`; switching embedding providers doesn't invalidate stale cross-provider cache entries. Known architecture gap, not yet fixed.
- **Docker container → host Ollama networking** — unresolved on this machine's Rancher-Desktop-on-Windows setup; bare-metal is the only currently-verified-working path for local Ollama. Documented as commented-out/opt-in in `docker-compose.yml`.

## Notes

- Real per-project data (this repo dogfooding itself) has repeatedly surfaced actual bugs during live testing that static review missed — keep verifying live (build + test + actually run it) rather than trusting compilation alone before marking anything done here.
- Comparison reference for memory/retrieval design: [codemem](https://github.com/kunickiaj/codemem) — FTS5+BM25 lexical, sqlite-vec semantic, hybrid re-rank with kind bonus, structured Summary/Facts/Narrative/Concepts per memory item, `files_read`/`files_modified` provenance per memory, batched raw-event ingestion before its observer runs (the same reasoning behind this project's event-count threshold above).
