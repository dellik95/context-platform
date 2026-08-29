# syntax=docker/dockerfile:1

# ---- dashboard build --------------------------------------------------------
# Built separately from the .NET stage below so a source-only change on one side doesn't bust the
# other's restore/npm-ci cache layer.
FROM node:22-slim AS dashboard-build
WORKDIR /dashboard
COPY apps/dashboard/package.json apps/dashboard/package-lock.json ./
RUN npm ci
COPY apps/dashboard/ ./
RUN npx ng build

# ---- build ----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy just the files that drive restore first so dependency restore is cached
# across rebuilds that only touch source, not project references or package versions.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY apps/host/Context.Host/Context.Host.csproj apps/host/Context.Host/
COPY apps/cli/Context.Cli/Context.Cli.csproj apps/cli/Context.Cli/
COPY src/ src/
RUN dotnet restore apps/host/Context.Host/Context.Host.csproj -r linux-x64 \
 && dotnet restore apps/cli/Context.Cli/Context.Cli.csproj -r linux-x64

COPY apps/ apps/

# SkipDashboardBuild=true: the dotnet SDK image has no Node/npm, and the dashboard-build stage
# above already produced the exact same output (copied into wwwroot in the runtime stage below) —
# Context.Host.csproj's own dashboard-build MSBuild target is for a plain local `dotnet build`/
# `publish` outside Docker, where Node is expected to already be on PATH.
RUN dotnet publish apps/host/Context.Host/Context.Host.csproj \
      -c Release -r linux-x64 --self-contained false --no-restore -o /app/host -p:SkipDashboardBuild=true \
 && dotnet publish apps/cli/Context.Cli/Context.Cli.csproj \
      -c Release -r linux-x64 --self-contained false --no-restore -o /app/cli

# ---- runtime ----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/host ./host
COPY --from=build /app/cli ./cli
COPY --from=dashboard-build /dashboard/dist/dashboard/browser ./host/wwwroot

# `context ...` on PATH so `docker exec <container> context index` etc. work exactly like the
# bare-metal CLI, without needing to know the published dll's path.
RUN printf '#!/bin/sh\nexec dotnet /app/cli/Context.Cli.dll "$@"\n' > /usr/local/bin/context \
 && chmod +x /usr/local/bin/context

# CONTEXT_PLATFORM_ROOT points at the mounted project (see docker-compose.yml); Context.Host and
# every `context ...` CLI invocation via the wrapper above default to it, so no --root flag is
# needed inside the container. ContextHostOptions.FromEnvironment also auto-detects
# DOTNET_RUNNING_IN_CONTAINER (set by this base image) and binds Kestrel to 0.0.0.0 instead of
# 127.0.0.1 for that reason alone — do not rely on that as the security boundary, publish the
# container's port bound to the *host's* 127.0.0.1 instead (see docker-compose.yml).
ENV CONTEXT_PLATFORM_ROOT=/workspace
ENV CONTEXT_PLATFORM_PORT=47923
WORKDIR /workspace
VOLUME ["/workspace"]
EXPOSE 47923

ENTRYPOINT ["dotnet", "/app/host/Context.Host.dll"]
