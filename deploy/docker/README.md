# Running Context Platform in Docker

This is the fastest path to a working instance, and — per the plan going forward — the primary
way this project verifies itself in "production": indexing and searching its own codebase inside
the same container image that would run against any other repo.

The image (built from the `Dockerfile` at the repo root) bundles both `Context.Host` (the always-on
API/MCP/worker process) and `Context.Cli`, published for `linux-x64`, on top of the official
`mcr.microsoft.com/dotnet/aspnet:10.0` runtime image. It needs no external services — SQLite and
the tree-sitter native grammars are both self-contained inside the .NET packages the app already
depends on, and every AI provider defaults to its offline/no-op implementation.

## Quick start (indexing this repo itself)

```bash
docker compose up -d --build
```

This builds the image, starts `Context.Host` bound to `127.0.0.1:47923` on your machine (see
"Network exposure" below), and mounts the repo root (`.`) at `/workspace` inside the container —
`docker-compose.yml`'s `HOST_PROJECT_PATH` defaults to `.`, so out of the box it's set up to index
Context Platform's own source tree.

Then drive it exactly like the bare-metal CLI, via the `context` wrapper baked into the image:

```bash
docker compose exec context-platform context project create --name self --path /workspace
docker compose exec context-platform context claude install --project <id-from-above>
docker compose exec context-platform context index --project <id> --full
docker compose exec context-platform context search "job dispatcher" --project <id>
docker compose exec context-platform context doctor --project <id>
```

(`context claude install` writes `.mcp.json`/`.claude/` into the *mounted* `/workspace`, i.e. back
onto your host repo — the same file Claude Code on your host machine reads, which is exactly what
lets you point Claude Code at a Context Platform instance running in a container instead of a
bare-metal `dotnet run`.)

## Point it at a different repo

```bash
HOST_PROJECT_PATH=/path/to/some/other/repo docker compose up -d --build
```

or create a `.env` file next to `docker-compose.yml`:

```
HOST_PROJECT_PATH=/path/to/some/other/repo
CONTEXT_PLATFORM_PORT=47923
```

## Network exposure

`docker-compose.yml` publishes the container's port bound to `127.0.0.1` on the host
(`127.0.0.1:47923:47923`) specifically to preserve the same loopback-only guarantee the bare-metal
deployment has — nothing outside this machine can reach it either way. Inside the container,
Kestrel itself binds `0.0.0.0` (see `ContextHostOptions.BindHost`'s remarks — loopback *inside* a
container means "unreachable from outside the container", so the real boundary has to move to the
host-side port publish instead) — don't change the compose file's port mapping to `0.0.0.0:...`
unless you specifically intend to expose it beyond this machine.

## Using real providers

By default every provider is offline (`noop`/local heuristics) — nothing leaves the container.  To
use OpenAI embeddings and/or Anthropic for summarization/memory extraction, uncomment the relevant
lines in `docker-compose.yml`'s `environment:` block and provide the API keys via a `.env` file
(never commit one) or your shell environment before `docker compose up`.

## Rebuilding after code changes

```bash
docker compose up -d --build
```

`docker compose build` alone also works if you don't want it to (re)start the container.

## Logs and data

- Logs: `docker compose logs -f context-platform`, or directly under `<mounted-repo>/.context/logs/`
  since `.context/` lives inside the mounted volume exactly like the bare-metal deployment.
- Database/backups: `<mounted-repo>/.context/context.db` and `.../backups/` — same as bare-metal,
  nothing container-specific; stopping/removing the container never loses data as long as the bind
  mount stays the same path.

## Tearing down

```bash
docker compose down          # stop and remove the container, keep the image and mounted data
docker compose down --rmi local  # also remove the built image
```
