# Running Context Platform in Docker

This is the fastest path to a working instance, and — per the plan going forward — the primary
way this project verifies itself in "production": indexing and searching its own codebase inside
the same container image that would run against any other repo.

The image (built from the `Dockerfile` at the repo root) bundles both `Context.Dashboard.Server` (the always-on
API/MCP/worker process) and `Context.Cli`, published for `linux-x64`, on top of the official
`mcr.microsoft.com/dotnet/aspnet:10.0` runtime image. It needs no external services — SQLite and
the tree-sitter native grammars are both self-contained inside the .NET packages the app already
depends on, and every AI provider defaults to its offline/no-op implementation.

## Quick start (indexing this repo itself)

```bash
docker compose up -d --build
```

This builds the image, starts `Context.Dashboard.Server` bound to `127.0.0.1:47923` on your machine (see
"Network exposure" below), and mounts the repo root (`.`) at `/workspace` inside the container —
`docker-compose.yml`'s `HOST_PROJECT_PATH` defaults to `.`, so out of the box it's set up to index
Context Platform's own source tree.

Then drive it exactly like the bare-metal CLI, via the `context` wrapper baked into the image:

```bash
docker compose exec context-platform context project create --name self --path /workspace
docker compose exec context-platform context index --project <id> --full
docker compose exec context-platform context search "job dispatcher" --project <id>
docker compose exec context-platform context doctor --project <id>
```

`context claude install` is **not** part of this quick start — see "Read-only workspace mount"
below for why, and what to do if you want it.

Project creation above needs an admin token: `docker-compose.yml` requires
`CONTEXT_PLATFORM_ADMIN_TOKEN` to be set (`POST /api/projects` is gated by it — see
`AdminTokenAuth`), and `docker compose exec` inherits it from the container's own environment, so
`context project create` above picks it up with no extra flags.

## Read-only workspace mount

`/workspace` is bind-mounted `:ro` and the container runs as a non-root user (see `Dockerfile`):
indexing/search/embedding only ever needs to *read* the source tree, so nothing in this container
can modify the real repo it's indexing — a bug or a compromised dependency here can't corrupt your
working copy.

One consequence: `context claude install` (and the dashboard's "write settings locally" option)
write `.mcp.json`/`.claude/context-platform.local.json` *into the project root* — that's how a
host-side Claude Code picks them up — and that write now fails against the read-only mount. If you
want that integration:

- run `context claude install` from a bare-metal `dotnet run`/`context` instead (same shared
  database format, so switching between bare-metal and container is safe), or
- temporarily drop the `:ro` in `docker-compose.yml`'s workspace volume line, run `context claude
  install` once, then put it back.

The app's own data (see "Logs and data" below) is unaffected either way: it lives on its own
writable volume, not under this read-only mount.

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

The database (`context.db`), Serilog logs, and VACUUM INTO backups live under
`CONTEXT_PLATFORM_DATA_DIR=/data/context-platform` (set in the `Dockerfile` — see
`SharedDataLocation`), a plain top-level path outside `/workspace`, not part of the mounted repo.
`docker-compose.yml` mounts a named volume (`context-data`) directly at that path.

- Logs: `docker compose logs -f context-platform`, or `docker compose exec context-platform tail -f
  /data/context-platform/logs/context-<date>.log`.
- Database/backups: `docker compose exec context-platform ls /data/context-platform` /
  `.../backups/`. To copy the database out to the host, e.g. for inspection:
  `docker compose cp context-platform:/data/context-platform/context.db ./context.db`.
- The `context-data` volume persists across `docker compose down`/`up` and container
  recreation — it's a normal Docker named volume, so `docker volume inspect context-data` (and, on
  plain Linux Docker Engine, its `Mountpoint`) works the usual way; on Docker Desktop/Rancher
  Desktop that path is inside the Desktop VM, not directly on the Windows/macOS host, so prefer
  `docker compose exec`/`docker compose cp` above.

## Tearing down

```bash
docker compose down          # stop and remove the container, keep the image, mounted repo, and context-data volume
docker compose down -v       # also remove the context-data volume (drops the index/db/logs)
docker compose down --rmi local  # also remove the built image
```
