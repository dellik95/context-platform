# Running Context.Dashboard.Server as a background service

`Context.Dashboard.Server` is a normal ASP.NET Core app (`apps/host/Context.Dashboard/Context.Dashboard.Server`), and works unchanged
under `dotnet run` for local development. For always-on use it can also run as a native OS
service on either platform — `Program.cs` calls both `UseWindowsService()` and `UseSystemd()`
unconditionally; each is a no-op unless the process was actually launched by that platform's
service manager, so the same code (and, once published, the same binary) works everywhere.

Configuration is entirely environment-variable driven (`ContextHostOptions`), which is why both
service mechanisms below can configure an instance without any code changes — a systemd
`EnvironmentFile` and a Windows service's registry `Environment` value both just set process
environment variables before Windows/systemd launches the executable.

## 1. Publish a self-contained binary

Context.Dashboard.Server.csproj no longer bundles the Angular Dashboard into `wwwroot/`
automatically (it only carries the dev-only SpaProxy wiring now, see its own remarks) — build the
Dashboard and copy its output into the publish folder yourself, same as Dockerfile's
`dashboard-build` stage does:

```bash
# Linux / Windows (same either way — npm ci only needed once, or after a package.json change)
cd apps/host/Context.Dashboard/context.dashboard.client
npm ci
npm run build
cd ../../../..
```

```bash
# Linux
dotnet publish apps/host/Context.Dashboard/Context.Dashboard.Server -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -o /opt/context-platform
cp -r apps/host/Context.Dashboard/context.dashboard.client/dist/dashboard/browser/* /opt/context-platform/wwwroot/

# Windows
dotnet publish apps\host\Context.Dashboard\Context.Dashboard.Server -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -o C:\ContextPlatform
robocopy apps\host\Context.Dashboard\context.dashboard.client\dist\dashboard\browser C:\ContextPlatform\wwwroot /E
```

## 2. Install the service

The host is multi-tenant (Phase 3): one running instance serves every project you add to it
afterwards via the Dashboard or `POST /api/projects` — there's no per-project install step, just
one shared, always-on service.

**Linux (systemd)**, run as root:

```bash
sudo ./deploy/install-systemd-service.sh
# optional: --port 47923 --data-dir /path/to/data
# uninstall: sudo ./deploy/uninstall-systemd-service.sh
```

**Windows**, from an elevated PowerShell — this script still predates the multi-tenant host and
takes a `-ProjectRoot`/`-ServiceName` pair it no longer strictly needs (it's only used to name the
service and populate its description); any value works:

```powershell
.\deploy\install-windows-service.ps1 -ProjectRoot 'C:\Projects\MyRepo' -ServiceName 'ContextPlatform'
# uninstall: .\deploy\uninstall-windows-service.ps1 -ServiceName 'ContextPlatform'
```

Both scripts require the publish step above to have already happened at the path they expect
(`/opt/context-platform` / `C:\ContextPlatform` by default) and print the exact next steps
(log location, service-management commands) once done. Neither script runs with elevated
privileges implicitly picked up from this repo — you run them yourself, deliberately, as root /
Administrator.

## 3. Add a project and point Claude Code at it

Open the Dashboard (`http://127.0.0.1:47923`), create a project pointing at the repo you want
indexed, then run `context claude install --project <id>` (Context.Cli) from that repo — it writes
`.mcp.json` and the hook scripts assuming Context.Dashboard.Server listens on `127.0.0.1:47923` (or
`CONTEXT_PLATFORM_PORT` if you overrode it).

## Multi-instance job dispatch (optional)

A single instance needs nothing here -- `JobDispatcher` defaults to an in-process signal and works
exactly as before. Running more than one `Context.Dashboard.Server` process against the *same*
database (e.g. for more embedding/indexing throughput than one process's worker pool gives you)
needs a shared way for each instance to learn "something new is ready to claim" -- set
`CONTEXT_PLATFORM_REDIS` (e.g. `localhost:6379`) on every instance pointed at the same Redis, and
each will claim actual work from the database itself (`IJobRepository.DequeueNextAsync`'s atomic
claim), Redis only relays the wake-up. Nothing here installs Redis for you:

```bash
# Linux (systemd) -- installs Redis locally, points this instance at it
sudo apt-get install -y redis-server
sudo ./deploy/install-systemd-service.sh --redis localhost:6379
```

If Redis is down or unreachable when this process starts, it does not fail to start -- job dispatch
just falls back to its own poll-timer cadence (a few seconds slower to notice new work, see
`JobDispatcher`'s remarks) until Redis comes back.

## Running under WSL2, consumed from Windows

WSL2 forwards ports a process binds inside the distro on `127.0.0.1` to Windows' own `localhost`
automatically (its default NAT networking — no port-proxy/`.wslconfig` needed, unless you've
switched to mirrored networking mode). So a systemd-managed instance installed inside WSL2 per
step 2 above is reachable from Windows at `http://127.0.0.1:47923` (Dashboard, `.mcp.json`, hook
scripts) exactly as if it were running natively on Windows — no separate Windows service needed.

Two things to know:

- **WSL2 doesn't start on its own at Windows boot.** Until the distro is running, systemd (and the
  forwarded port) isn't there to hit. Either open a WSL terminal once per Windows session, or
  schedule `wsl.exe -d <Distro>` to run at logon (Task Scheduler) so it's always up.
- **Point a project's root at wherever Windows already has it checked out**, e.g.
  `/mnt/c/Projects/MyRepo` — not a separate WSL-side clone — so the same working copy Windows edits
  is what gets indexed. `/mnt/c/...` (DrvFs) is slower for heavy file I/O than a native WSL
  filesystem path, which matters for the initial full reindex of a large repo but not for normal
  incremental use.
