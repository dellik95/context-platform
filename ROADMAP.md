# Roadmap

Short overview of completed capabilities and potential next improvements for Context Platform.

## Completed

- **Hybrid code search & packing:** SQLite FTS5 (BM25 + identifier splitting), `sqlite-vec` ANN vectors, graph proximity scoring, reranking, and token-budgeted `ContextPackBuilder` with session delivery deduplication.
- **Tree-sitter indexing & call graph:** native AST parsing (`C#`, `TypeScript`, `TSX`, `JavaScript`, `Python`), incremental `SymbolDiffer`, proactive vector pruning on reindex/delete (`VectorPruningChunkRepository`), and cross-file `BlastRadiusAsync`.
- **Long-term memory & consolidation:** Claude Code hooks (`SessionStart`, `UserPromptSubmit`, `PostToolUse`, `PostCompact`, `Stop`, `SessionEnd`), structured facts + checkpoints, online duplicate merging, periodic cluster reconsolidation, and code verification.
- **Multi-project host & MCP server:** `.NET 11` ASP.NET Core server exposing REST, SignalR, and 13 MCP tools with per-project SHA-256 bearer tokens and hot-swapped AI providers (`NoOp`, `Ollama`, `Foundry Local`, `OpenAI`, `Anthropic`).
- **Modern Angular 22 dashboard:** Tailwind CSS v4 UI with custom SVG charts (`DonutChart`, `ActivityBarChart`), interactive `vis-network` graph explorer, live job controls, and mobile layout.
- **Bilingual documentation:** plain English and Ukrainian guides ([docs/en/](docs/en/USER_GUIDE.md) and [docs/uk/](docs/uk/USER_GUIDE.md)).

## Potential Next Steps

- **Additional Tree-sitter grammars:** Go, Rust, or Java parsers on top of the shared `TreeSitterLanguage` pipeline.
- **Richer semantic reranking:** optional cross-encoder reranker integration alongside `LexicalOverlapReranker`.
- **Workspace multi-repo linking:** shared architecture search across connected frontend/backend repositories.
