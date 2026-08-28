# Product Requirements Document

## Persistent Context & Code Intelligence Platform for Claude Code

**Version:** 0.1
**Status:** Draft
**Date:** 28 August 2026

---

# 1. Product Overview

## 1.1. Working name

**Context Platform**

Робоча назва може бути змінена пізніше.

## 1.2. One-line description

**Local-first платформа persistent context для AI coding agents, яка об'єднує історію роботи, persistent memory, AST/code graph та semantic search, щоб Claude Code не втрачав контекст між сесіями та швидше орієнтувався в кодовій базі.**

## 1.3. Problem

Під час роботи з Claude Code значна частина контексту втрачається між сесіями.

Нова сесія часто змушує агента повторно:

* досліджувати структуру repository;
* знаходити потрібні файли;
* розуміти залежності між компонентами;
* відновлювати рішення, прийняті в попередніх сесіях;
* згадувати, які проблеми вже досліджувалися;
* визначати, чому певна архітектура була реалізована саме так.

Це призводить до:

1. дублювання роботи;
2. збільшення кількості використаних tokens;
3. повільнішої роботи агента;
4. втрати історичного контексту;
5. повторного аналізу тієї самої codebase;
6. гіршої якості рішень при довготривалій розробці.

## 1.4. Product vision

Система повинна створити **довготривалу пам'ять про software project**, яка поєднує:

```text
Code
  +
AST
  +
Graph
  +
Semantic Search
  +
Sessions
  +
Memories
  +
Decisions
  +
Project Configuration
```

та надає Claude Code лише той контекст, який необхідний для поточної задачі.

---

# 2. Goals

## 2.1. Primary goals

### G1. Persistent project memory

Зберігати важливий контекст між Claude Code sessions.

Система повинна пам'ятати:

* architectural decisions;
* discoveries;
* bug investigations;
* implemented features;
* refactorings;
* important changes;
* unresolved issues;
* важливі findings із попередніх сесій.

### G2. Codebase intelligence

Побудувати структуроване представлення repository:

* files;
* directories;
* symbols;
* imports;
* exports;
* references;
* calls;
* inheritance;
* implementations;
* dependencies.

### G3. Fast semantic navigation

Дозволити Claude Code знаходити релевантний код за природномовним запитом.

Наприклад:

> "Where is authentication handled?"

система повинна знаходити не тільки файл `auth.ts`, а всю релевантну частину architecture.

### G4. Token efficiency

Зменшити кількість tokens, необхідних Claude для розуміння project context.

Система повинна використовувати:

* progressive disclosure;
* hybrid retrieval;
* graph expansion;
* summarization;
* context ranking;
* token budgets.

### G5. Claude Code integration

Інтеграція повинна бути максимально прозорою для користувача.

Після initialization project Claude Code повинен автоматично отримати:

* MCP integration;
* необхідні hooks;
* skills;
* configuration.

### G6. Project management

Користувач повинен мати можливість створювати та налаштовувати projects з Dashboard.

---

# 3. Non-goals

У першій версії система **не повинна**:

* бути cloud-first;
* вимагати PostgreSQL;
* вимагати Redis;
* вимагати Kafka;
* використовувати microservices;
* залежати від Neo4j;
* замінювати Git;
* замінювати CLAUDE.md;
* зберігати всю історію Claude як prompt context;
* автоматично змінювати код без явної дії Claude;
* підтримувати десятки мов на першому етапі.

---

# 4. Target Users

## Primary user

Software developer, який використовує Claude Code для довготривалої роботи над одним або кількома repositories.

Типовий workflow:

```text
Monday:
implement feature

Tuesday:
debug feature

Wednesday:
refactor architecture

Thursday:
new Claude session

Friday:
continue old investigation
```

Система повинна дозволити developer продовжити роботу без повторного пояснення project context.

---

# 5. Core User Experience

Ключовий UX:

```text
Create Project
      ↓
Select repository
      ↓
Configure indexing
      ↓
Configure AI providers
      ↓
Initialize Claude Code
      ↓
Initial indexing
      ↓
Project Ready
```

Після цього:

```text
Claude Code Session
      ↓
SessionStart
      ↓
Retrieve relevant context
      ↓
User task
      ↓
MCP semantic search
      ↓
Code + Graph + Memory
      ↓
Context Pack
      ↓
Claude works
      ↓
Events collected
      ↓
Session summary
      ↓
Persistent memory
```

---

# 6. Product Architecture

Система є **modular monolith**.

```text
                         ┌──────────────────┐
                         │ Angular Dashboard │
                         └────────┬─────────┘
                                  │
                             REST/SignalR
                                  │
                                  ▼
┌─────────────────────────────────────────────────────────┐
│                    .NET Application                     │
│                                                         │
│  Project Management                                     │
│  Indexing                                               │
│  AST / Graph                                            │
│  Retrieval                                              │
│  Memory                                                 │
│  Claude Integration                                     │
│                                                         │
└──────────────────────────┬──────────────────────────────┘
                           │
                           ▼
                    ┌──────────────┐
                    │    SQLite    │
                    │              │
                    │ EF Core      │
                    │ FTS5         │
                    │ Vector Index │
                    │ Graph        │
                    └──────────────┘
                           ▲
                           │
                      ┌────┴─────┐
                      │   MCP    │
                      └────┬─────┘
                           │
                           ▼
                      Claude Code
```

---

# 7. Technology Stack

## Backend

* .NET 10
* ASP.NET Core
* C#
* Entity Framework Core
* Microsoft.Data.Sqlite
* SQLite
* BackgroundService
* SignalR
* MCP SDK / MCP implementation for .NET

**Dapper не використовується.**

EF Core є єдиним persistence abstraction.

Для складних queries допускається використання raw SQL через EF Core.

## Frontend

* Angular
* TypeScript
* RxJS / Angular Signals
* Angular Material або equivalent component library
* Monaco Editor
* graph visualization library

## Storage

* SQLite
* EF Core migrations
* SQLite FTS5
* vector storage через підтримуваний SQLite vector extension/backend

---

# 8. Monorepo

Repository structure:

```text
context-platform/
│
├── apps/
│   ├── api/
│   │   └── Context.Api/
│   │
│   ├── mcp/
│   │   └── Context.Mcp/
│   │
│   ├── worker/
│   │   └── Context.Worker/
│   │
│   └── dashboard/
│
├── src/
│   ├── Context.Domain/
│   ├── Context.Application/
│   ├── Context.Infrastructure/
│   ├── Context.Indexing/
│   ├── Context.Graph/
│   ├── Context.Retrieval/
│   ├── Context.Memory/
│   ├── Context.Providers/
│   ├── Context.Claude/
│   └── Context.Shared/
│
├── tests/
│   ├── Context.Domain.Tests/
│   ├── Context.Application.Tests/
│   ├── Context.Indexing.Tests/
│   ├── Context.Graph.Tests/
│   ├── Context.Retrieval.Tests/
│   ├── Context.Memory.Tests/
│   └── Context.IntegrationTests/
│
├── docs/
│   ├── architecture/
│   ├── adr/
│   └── product/
│
├── tools/
├── Directory.Build.props
├── Directory.Packages.props
├── global.json
└── ContextPlatform.sln
```

---

# 9. Functional Requirements

# 9.1. Project Management

## FR-PROJECT-001

User can create a new project from Dashboard.

Required information:

* project name;
* repository path.

## FR-PROJECT-002

System automatically detects:

* Git repository;
* languages;
* package managers;
* framework;
* existing `.claude`;
* `CLAUDE.md`;
* `.gitignore`;
* project structure.

## FR-PROJECT-003

User can configure:

* indexing;
* ignored files;
* embedding provider;
* reranking provider;
* summarization provider;
* Claude integration.

## FR-PROJECT-004

System can initialize project.

Initialization may install/register:

* MCP configuration;
* Claude hooks;
* skills;
* project-specific configuration.

Existing configuration must never be blindly overwritten.

System must perform safe merge and preferably create backup before modification.

---

# 9.2. File Indexing

## FR-INDEX-001

System scans project filesystem.

## FR-INDEX-002

System supports ignore patterns.

Sources:

```text
.gitignore
.contextignore
custom patterns
```

## FR-INDEX-003

Ignore rules apply consistently to:

* indexing;
* AST parsing;
* graph;
* chunking;
* embeddings;
* search.

## FR-INDEX-004

Indexing is incremental.

File identity is based on content hash rather than path alone.

If content is unchanged:

```text
SKIP
```

If changed:

```text
REINDEX
```

## FR-INDEX-005

System detects:

* created files;
* modified files;
* deleted files;
* renamed files.

---

# 9.3. AST Indexing

System parses supported source files into AST.

Initial language support:

* TypeScript;
* JavaScript;
* C#;
* Python;
* Go;
* Rust.

Architecture must allow additional languages through `ILanguageParser`.

System extracts:

* classes;
* interfaces;
* functions;
* methods;
* variables;
* types;
* imports;
* exports;
* references;
* calls;
* inheritance;
* implementations.

---

# 9.4. Code Graph

System builds a graph representing project structure.

Node types:

```text
Project
Directory
File
Module
Class
Interface
Function
Method
Type
Variable
Package
Test
```

Edge types:

```text
CONTAINS
IMPORTS
EXPORTS
CALLS
REFERENCES
EXTENDS
IMPLEMENTS
USES
TESTS
DEPENDS_ON
```

Graph must support incremental updates.

A modified file must not require rebuilding the entire graph.

---

# 9.5. Semantic Search

User/Claude can search using natural language.

Example:

```text
"Where is authentication implemented?"
```

Search combines:

```text
Lexical search
+
Semantic search
+
Graph relevance
+
Recency
+
Memory relevance
```

Search pipeline:

```text
Query
 ↓
Candidate retrieval
 ↓
Hybrid scoring
 ↓
Graph expansion
 ↓
Reranking
 ↓
Token-aware selection
 ↓
Context Pack
```

---

# 9.6. Hybrid Retrieval

System supports:

### Lexical

SQLite FTS5 / BM25.

### Semantic

Vector embeddings.

### Graph

Related symbols/files/relationships.

### Memory

Historical project knowledge.

The retrieval layer must expose a unified interface:

```text
Search(query)
```

rather than forcing Claude to independently query multiple systems.

---

# 9.7. Context Packs

Context Pack is the primary output of the retrieval engine.

Example:

```text
Query:
"How does authentication work?"

Architecture:
AuthMiddleware
 → AuthService
 → TokenService
 → SessionRepository

Primary files:
AuthMiddleware.ts
AuthService.ts
TokenService.ts

Relevant symbols:
AuthService.authenticate()
TokenService.verify()

Historical context:
JWT migration was completed in session #182.

Open issue:
Refresh token race condition.

Estimated tokens:
1,650
```

Context Pack must support a configurable token budget.

---

# 9.8. Progressive Disclosure

Search results have three levels.

### Level 0

Identifiers and metadata.

### Level 1

Summaries and relevant snippets.

### Level 2

Full file/symbol content.

Claude should receive the smallest sufficient amount of information.

---

# 9.9. Session Tracking

System records Claude Code sessions.

Session contains:

```text
Session
├── start/end
├── project
├── prompts
├── tool calls
├── files read
├── files changed
├── commands
├── errors
└── events
```

Raw session events are stored for historical analysis but are not automatically injected into future prompts.

---

# 9.10. Session Summaries

System periodically generates summaries.

Summary contains:

```text
Completed
Discovered
Decisions
Changed files
Problems
Open issues
Next steps
```

Example:

```text
Session #183

Completed:
- implemented JWT refresh flow

Discovered:
- refresh tokens are persisted in Redis

Decision:
- use token rotation

Changed:
- AuthService.cs
- TokenService.cs

Open:
- add transaction around token rotation

Next:
- implement concurrency protection
```

---

# 9.11. Persistent Memory

System extracts durable memories from sessions.

Memory types:

```text
DECISION
DISCOVERY
BUGFIX
FEATURE
REFACTOR
CHANGE
EXPLORATION
ARCHITECTURE
ISSUE
```

Each memory may reference:

* files;
* symbols;
* sessions;
* other memories;
* technologies;
* tags.

Example:

```text
Memory:
"Authentication uses JWT refresh token rotation"

Related files:
AuthService.cs
TokenService.cs

Related symbols:
AuthService.Refresh()
TokenService.Rotate()

Origin:
Session #183
```

---

# 9.12. Memory Graph

Memories are connected to code.

Example:

```text
Memory
  │
  ├── relates_to → Session
  ├── mentions → File
  ├── mentions → Symbol
  └── related_to → Memory
```

This allows historical knowledge to be traversed together with the code graph.

---

# 9.13. Claude Code Integration

Integration uses:

* MCP;
* hooks;
* skills;
* project configuration.

Important lifecycle events:

```text
SessionStart
UserPromptSubmit
PostToolUse
Stop
SessionEnd
```

### SessionStart

System retrieves relevant previous context.

### UserPromptSubmit

System may perform contextual retrieval.

### PostToolUse

System captures:

* file reads;
* modifications;
* commands;
* important tool activity.

### SessionEnd

System generates:

* session summary;
* memories;
* relationships;
* indexing jobs.

---

# 9.14. MCP

Initial MCP tools:

```text
context_search
context_get

code_search
symbol_search

memory_search
memory_get

graph_neighbors
graph_path

session_search
session_get

project_info
```

MCP tools should return structured, concise results.

The MCP layer must use the same Application/Context Engine as Dashboard and CLI.

---

# 9.15. AI Provider Management

System must support configurable providers.

Provider categories:

```text
Embeddings
Reranking
Summarization
Memory extraction
```

Provider abstraction:

```text
IEmbeddingProvider
IRerankerProvider
ILlmProvider
```

Initial implementation may support multiple providers, but provider-specific logic must remain isolated from core application logic.

---

# 9.16. Dashboard

Dashboard is a first-class product surface.

Main sections:

```text
Projects
Search
Files
Graph
Memories
Sessions
Indexing
Providers
Claude Code
Settings
```

---

# 9.17. Project Dashboard

Must display:

```text
Files
Symbols
Memories
Sessions
Graph nodes
Graph edges
Index status
Last indexing time
Claude integration status
```

---

# 9.18. Search Dashboard

Global search interface must support:

```text
All
Code
Files
Symbols
Memories
Sessions
Graph
```

Results should expose relevance and relationships.

---

# 9.19. Graph Dashboard

Graph viewer supports:

```text
AST
Dependencies
Memory
Combined
```

User can:

* zoom;
* pan;
* select node;
* inspect node;
* expand neighbors;
* navigate to source file;
* view related memories.

---

# 9.20. Memory Dashboard

User can:

* browse memories;
* search memories;
* inspect source session;
* inspect related files;
* inspect related symbols;
* mark memory as important;
* edit/delete memory.

---

# 9.21. Indexing Dashboard

User can see:

```text
Files discovered
Files indexed
Files ignored
AST parsing
Graph generation
Embeddings
Pending jobs
Failed jobs
```

System should expose current worker status.

---

# 10. Background Processing

Indexing and memory processing must be asynchronous.

Architecture:

```text
Filesystem
    ↓
Watcher
    ↓
Job Queue
    ↓
Background Worker
    ↓
Indexer / Graph / Embedding / Memory
```

Initial jobs:

```text
INDEX_FILE
DELETE_FILE
PARSE_FILE
UPDATE_GRAPH
EMBED_CHUNK
SUMMARIZE_SESSION
EXTRACT_MEMORY
```

SQLite-backed job queue is sufficient for the initial version.

---

# 11. Data Model

Core entities:

```text
Project
File
Directory
FileVersion

Symbol
SymbolRelation

Chunk
Embedding

Session
SessionEvent
SessionSummary

Memory
MemoryRelation
MemoryFile
MemorySymbol

Job

Provider
ProviderModel

ClaudeIntegration
```

EF Core is the only persistence layer.

Normal queries use LINQ.

Complex FTS5, graph and aggregation queries may use EF Core raw SQL.

---

# 12. Security & Privacy

The system is **local-first**.

By default:

* project source code stays on the user's machine;
* SQLite database stays local;
* session history stays local.

External providers are contacted only when explicitly configured.

For external AI providers the system must clearly indicate:

```text
Provider
Model
What data is sent
```

Potentially sensitive files must be excluded through ignore rules.

---

# 13. Performance Requirements

## Indexing

Incremental indexing must avoid reprocessing unchanged files.

## Search

Typical local search should feel interactive.

Target:

```text
P50 < 200 ms
P95 < 1 s
```

for lexical/hybrid searches on normal development repositories, excluding external reranking/LLM calls.

## Memory

Session summarization must run asynchronously and never block normal Claude interaction.

## Dashboard

UI must receive indexing updates through SignalR rather than aggressive polling.

---

# 14. Reliability Requirements

A failed embedding provider must not invalidate the entire index.

A failed file parser must not stop indexing of the project.

A failed memory extraction must not lose raw session events.

Every asynchronous job must have:

```text
status
attempts
error
created_at
started_at
finished_at
```

The system must support retry.

---

# 15. Configuration

Configuration has three levels:

```text
Global
Project
Claude integration
```

Project configuration may contain:

```yaml
project:
  name: my-project
  root: /path/to/project

indexing:
  enabled: true

ignore:
  files:
    - .gitignore
    - .contextignore

embeddings:
  provider: openai
  model: ...

memory:
  enabled: true
  summarization: true
```

Exact configuration format is a technical design decision.

---

# 16. CLI

Although Dashboard is the primary management interface, CLI is required.

Initial commands:

```bash
context project list

context project create

context project init

context project status

context index

context search "authentication flow"

context memory search "JWT"

context graph "AuthService"

context claude install
```

CLI and Dashboard must use the same application services.

---

# 17. Observability

Dashboard must expose system health:

```text
Database
Indexer
Worker
MCP
Claude integration
Embedding provider
LLM provider
```

Logs should be structured.

Important metrics:

```text
index_duration
files_indexed
files_skipped
embedding_duration
search_duration
memory_extraction_duration
jobs_failed
mcp_requests
context_pack_tokens
```

One of the most important product metrics is:

**tokens saved by Context Platform.**

---

# 18. Product Metrics

## Primary metrics

### M1. Context reuse

Percentage of sessions that retrieve relevant previous context.

### M2. Search usefulness

Percentage of top-ranked results that are relevant.

### M3. Token reduction

Average tokens supplied through Context Packs versus raw repository exploration.

### M4. Session continuity

Percentage of sessions where Claude successfully continues previous work without rediscovering project context.

### M5. Retrieval latency

Time required to produce a useful Context Pack.

---

# 19. MVP

The MVP should provide one complete vertical workflow:

```text
Create Project
      ↓
Initialize
      ↓
Index repository
      ↓
Build AST
      ↓
Build graph
      ↓
FTS search
      ↓
Semantic search
      ↓
MCP
      ↓
Claude Code
      ↓
Capture session
      ↓
Generate summary
      ↓
Persist memory
      ↓
Use memory in next session
```

### MVP features

1. Project creation.
2. Project initialization.
3. `.gitignore` / `.contextignore`.
4. Incremental file indexing.
5. AST indexing.
6. Basic code graph.
7. SQLite FTS5.
8. Embedding provider abstraction.
9. Hybrid search.
10. MCP server.
11. Claude hooks.
12. Session tracking.
13. Session summaries.
14. Persistent memories.
15. Basic Dashboard.
16. Basic graph visualization.

---

# 20. MVP Language Support

Initial priority:

```text
1. TypeScript
2. JavaScript
3. C#
4. Python
```

Then:

```text
5. Go
6. Rust
```

Architecture must allow adding languages without modifying core indexing logic.

---

# 21. Roadmap

## Phase 1 — Foundation

```text
.NET solution
Angular
SQLite
EF Core
Project management
Configuration
CLI
Dashboard shell
```

## Phase 2 — Code Intelligence

```text
File scanner
Ignore engine
Tree-sitter
AST
Symbols
Incremental indexing
```

## Phase 3 — Graph

```text
Imports
References
Calls
Dependencies
Graph traversal
Graph UI
```

## Phase 4 — Retrieval

```text
FTS5
Chunks
Embeddings
Hybrid search
Reranking
Context Packs
Token budgeting
```

## Phase 5 — Claude

```text
MCP
Hooks
Skills
Claude initialization
SessionStart
PostToolUse
SessionEnd
```

## Phase 6 — Memory

```text
Session storage
Session summaries
Memory extraction
Memory search
Memory ↔ Code relationships
```

## Phase 7 — Production UX

```text
Provider management
Diagnostics
Health
Backups
Import/export
Packaging
```

---

# 22. Architectural Principles

## 22.1. Modular monolith

Не використовувати microservices на старті.

## 22.2. EF Core only

EF Core є єдиним persistence abstraction.

```text
LINQ
+
EF Core raw SQL
        ↓
      SQLite
```

Dapper не використовується.

## 22.3. Local-first

Основний state зберігається локально.

## 22.4. Incremental everything

По можливості всі дорогі операції мають бути incremental:

* indexing;
* parsing;
* graph;
* embeddings;
* memory processing.

## 22.5. Context over raw data

Система повинна оптимізувати не кількість збережених даних, а **якість контексту, який отримує agent**.

## 22.6. Progressive disclosure

Спочатку metadata/summary, потім snippets, потім full content.

## 22.7. One Context Engine

Dashboard, CLI, MCP і hooks повинні використовувати один Application/Context Engine.

```text
             Context Engine
             /      |      \
           MCP     CLI   Dashboard
```

---

# 23. Definition of Success

Продукт вважається успішним на MVP-рівні, якщо developer може:

1. Відкрити Dashboard.
2. Вибрати repository.
3. Натиснути **Initialize Project**.
4. Дочекатися індексації.
5. Відкрити Claude Code.
6. Почати роботу.
7. Закінчити session.
8. Закрити Claude Code.
9. Через день відкрити нову session.
10. Попросити продовжити попередню роботу.
11. Claude через Context Platform отримує:

    * relevant previous memories;
    * relevant session summary;
    * relevant files;
    * relevant symbols;
    * relevant graph relationships.
12. Claude продовжує роботу без повного повторного дослідження repository.

Ключовий результат:

> **Developer перестає бути “носієм пам'яті” між Claude Code sessions. Context Platform стає persistent memory та navigational layer для project.**
