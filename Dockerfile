# syntax=docker/dockerfile:1

# ---- dashboard build --------------------------------------------------------
# Built separately from the .NET stage below so a source-only change on one side doesn't bust the
# other's restore/npm-ci cache layer.
FROM node:22-slim AS dashboard-build
WORKDIR /dashboard
COPY apps/host/Context.Dashboard/context.dashboard.client/package.json apps/host/Context.Dashboard/context.dashboard.client/package-lock.json ./
RUN npm ci
COPY apps/host/Context.Dashboard/context.dashboard.client/ ./
RUN npx ng build

# ---- build ----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy just the files that drive restore first so dependency restore is cached
# across rebuilds that only touch source, not project references or package versions.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY apps/host/Context.Dashboard/Context.Dashboard.Server/Context.Dashboard.Server.csproj apps/host/Context.Dashboard/Context.Dashboard.Server/
COPY apps/cli/Context.Cli/Context.Cli.csproj apps/cli/Context.Cli/
COPY src/ src/
RUN dotnet restore apps/host/Context.Dashboard/Context.Dashboard.Server/Context.Dashboard.Server.csproj -r linux-x64 \
 && dotnet restore apps/cli/Context.Cli/Context.Cli.csproj -r linux-x64

COPY apps/ apps/

# Context.Dashboard.Server.csproj carries no Angular build step of its own any more (just the
# dev-only SpaProxy wiring) — the dashboard-build stage above already produced the exact output
# this needs, copied straight into wwwroot in the runtime stage below.
RUN dotnet publish apps/host/Context.Dashboard/Context.Dashboard.Server/Context.Dashboard.Server.csproj \
      -c Release -r linux-x64 --self-contained false --no-restore -o /app/host \
 && dotnet publish apps/cli/Context.Cli/Context.Cli.csproj \
      -c Release -r linux-x64 --self-contained false --no-restore -o /app/cli

# ---- runtime ----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl for HEALTHCHECK below — not present in the base aspnet image (unlike useradd/groupadd,
# which come from the `passwd` package and are part of every Debian base, even slim). One small,
# well-cached apt layer; not worth avoiding for a single health probe.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/host ./host
COPY --from=build /app/cli ./cli
COPY --from=dashboard-build /dashboard/dist/dashboard/browser ./host/wwwroot

# `context ...` on PATH so `docker exec <container> context index` etc. work exactly like the
# bare-metal CLI, without needing to know the published dll's path.
RUN printf '#!/bin/sh\nexec dotnet /app/cli/Context.Cli.dll "$@"\n' > /usr/local/bin/context \
 && chmod +x /usr/local/bin/context

# CONTEXT_PLATFORM_ROOT points at the mounted project (see docker-compose.yml); Context.Dashboard.Server and
# every `context ...` CLI invocation via the wrapper above default to it, so no --root flag is
# needed inside the container. ContextHostOptions.FromEnvironment also auto-detects
# DOTNET_RUNNING_IN_CONTAINER (set by this base image) and binds Kestrel to 0.0.0.0 instead of
# 127.0.0.1 for that reason alone — do not rely on that as the security boundary, publish the
# container's port bound to the *host's* 127.0.0.1 instead (see docker-compose.yml).
ENV CONTEXT_PLATFORM_ROOT=/workspace
ENV CONTEXT_PLATFORM_PORT=47923

# CONTEXT_PLATFORM_DATA_DIR — where context.db, Serilog logs, and VACUUM INTO backups live (see
# SharedDataLocation, ContextHostOptions.ProjectDataDir). No longer derived from
# CONTEXT_PLATFORM_ROOT/workspace at all (Phase 2's shared-host pivot moved the database out of any
# one project's own repo) — set to a dedicated path outside /workspace so it doesn't need to sit
# inside the read-only bind mount below.
ENV CONTEXT_PLATFORM_DATA_DIR=/data/context-platform

# Dedicated non-root user — the app has no business running as root inside the container, and
# /workspace is (per docker-compose.yml) a read-only bind mount of the real source tree being
# indexed, so root privileges would buy nothing there either.
#
# CONTEXT_PLATFORM_DATA_DIR above (db/logs/backups — see SharedDataLocation, ContextHostOptions,
# BackupDbJobHandler) is the one directory this process must be able to write. Because it now lives
# outside /workspace entirely, it no longer needs the old "writable volume nested inside a
# read-only mount" trick the {ProjectRoot}/.context layout required — docker-compose.yml just mounts
# a named volume directly at this path. Pre-creating it here, owned by the app user, means Docker's
# normal "populate a freshly-created named volume from the image's own content at that path"
# behaviour gives the volume the right ownership on first run, with no extra chown step needed at
# container start.
RUN groupadd -r contextplatform \
 && useradd -r -g contextplatform -d /data/context-platform -s /usr/sbin/nologin contextplatform \
 && mkdir -p /data/context-platform \
 && chown -R contextplatform:contextplatform /data/context-platform

WORKDIR /workspace
VOLUME ["/workspace", "/data/context-platform"]
EXPOSE 47923

USER contextplatform

# curl (installed above) hits the same /health route Context.Dashboard.Server itself exposes (see
# HealthEndpoints.MapHealthEndpoints) — aggregates DB reachability and job-dispatcher liveness.
# start-period gives EF Core's startup migration (Program.cs, Database.MigrateAsync) room to finish
# before failures count against retries.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS "http://127.0.0.1:${CONTEXT_PLATFORM_PORT}/health" || exit 1

ENTRYPOINT ["dotnet", "/app/host/Context.Dashboard.Server.dll"]
