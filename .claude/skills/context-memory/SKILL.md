---
name: context-memory
description: Use this project's Context Platform MCP tools for code lookup and cross-session memory instead of re-deriving context from scratch.
---

# Context Platform memory & code intelligence

This project has Context Platform's MCP tools registered:

- `context_search` - hybrid (lexical + semantic) search over code and docs, merged with ranked project memory into a token-budgeted context pack.
- `code_search` - raw ranked code/text search hits, no memory merge or budgeting.
- `symbol_search` - finds symbols (classes, functions, methods, ...) by name pattern.
- `memory_search` - searches persistent memories (decisions, discoveries, bug fixes, open issues, ...).
- `memory_get` - fetches one memory by id.
- `memory_remember` - explicitly stores something right now, bypassing the normal extraction triggers.
- `memory_forget` - permanently deletes one memory by id. No undo.
- `graph_neighbors` - lists graph nodes reachable from a node within N hops, optionally filtered by edge kind.
- `graph_path` - finds the shortest path between two graph nodes.
- `graph_impact` - blast-radius analysis for a symbol: transitive callers, implementers, and covering tests.
- `session_search` - lists recent Claude Code sessions for this project.
- `session_get` - fetches one session's metadata, event log, and summary.
- `project_info` - aggregate stats: files indexed, symbols indexed, memory count, last-indexed time.

## When to use them

- Prefer `context_search` or `code_search` over re-reading files from scratch when looking for how something in the codebase works.
- Check `memory_search` for prior decisions, bugfixes, or discoveries before re-investigating a question that may already be answered from an earlier session.
- Call `graph_impact` on a symbol before changing it, to see its blast radius (callers, implementers, tests).

## Storing memories (`memory_remember`)

Don't wait only for the user to literally say "remember that..." — the normal N-event/session-end
extraction pipeline already catches routine work, so `memory_remember` exists for the moments that
pipeline shouldn't have to wait for. Call it right away when, in the current exchange:

- An explicit request to remember/note something is made ("remember that...", "note for later...",
  a standing instruction or preference).
- A real architectural or design decision gets made (a library/approach/pattern chosen over
  alternatives, and why).
- A non-obvious discovery surfaces about the codebase — a root cause, a gotcha, a behavior that
  would otherwise cost someone real time to re-find.
- The user shares significant standing context you'd otherwise have to ask for again: a constraint,
  a deadline, who owns what, where something is tracked externally.
- A feature/bugfix/refactor of real consequence completes (not "ran the tests" or "reviewed the
  file" — an outcome, not routine narration).

Fill in `title`/`topic`/`facts`/`concepts`/`filesRead`/`filesModified` yourself when you call it —
you already have the full context right now, so there's no reason to leave this an unstructured
narrative dump the way a bare `content` string would. Be as selective here as the periodic
extraction pipeline is: skip trivia, in-progress work with no conclusion yet, and anything that's
just process narration with no durable outcome. Not every exchange needs a memory.

## Updating a memory

There is no separate "update" tool. Call `memory_remember` again describing the corrected/current
state — the consolidator dedups against existing memories on the same topic and reinforces or
supersedes the old one automatically, preserving history rather than silently overwriting it.

## Deleting a memory (`memory_forget`)

Reach for this only when a memory shouldn't exist at all, not merely when it's outdated (that's
what re-remembering above is for): the user explicitly asks to forget/remove/delete something, or
you find an existing memory (via `memory_search`/`memory_get`) that is now flatly wrong or
describes an approach that's since been replaced, and leaving it would mislead a future session.
It permanently deletes with no undo, so don't call it on a hunch.