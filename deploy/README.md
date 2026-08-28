# Running Context.Host as a background service

`Context.Host` is a normal ASP.NET Core app (`apps/host/Context.Host`), and works unchanged
under `dotnet run` for local development. For always-on use it can also run as a native OS
service on either platform — `Program.cs` calls both `UseWindowsService()` and `UseSystemd()`
unconditionally; each is a no-op unless the process was actually launched by that platform's
service manager, so the same code (and, once published, the same binary) works everywhere.

Configuration is entirely environment-variable driven (`ContextHostOptions`), which is why both
service mechanisms below can configure an instance without any code changes — a systemd
`EnvironmentFile` and a Windows service's registry `Environment` value both just set process
environment variables before Windows/systemd launches the executable.

## 1. Publish a self-contained binary

```bash
# Linux
dotnet publish apps/host/Context.Host -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -o /opt/context-platform

# Windows
dotnet publish apps\host\Context.Host -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -o C:\ContextPlatform
```

## 2. Install the service

**Linux (systemd)** — one instance per project, run as root:

```bash
sudo ./deploy/install-systemd-service.sh --project-root /path/to/repo --name myproject
# uninstall: sudo ./deploy/uninstall-systemd-service.sh --name myproject
```

**Windows** — one service per project, from an elevated PowerShell:

```powershell
.\deploy\install-windows-service.ps1 -ProjectRoot 'C:\Projects\MyRepo' -ServiceName 'ContextPlatform-MyRepo'
# uninstall: .\deploy\uninstall-windows-service.ps1 -ServiceName 'ContextPlatform-MyRepo'
```

Both scripts require the publish step above to have already happened at the path they expect
(`/opt/context-platform` / `C:\ContextPlatform` by default) and print the exact next steps
(log location, service-management commands) once done. Neither script runs with elevated
privileges implicitly picked up from this repo — you run them yourself, deliberately, as root /
Administrator, which is also why `context claude install` (Context.Cli) does not attempt to
invoke them automatically.

## 3. Point Claude Code at it

`context claude install` (or `context claude install --project <id>`) writes `.mcp.json` and the
hook scripts assuming Context.Host listens on `127.0.0.1:47923` (or `CONTEXT_PLATFORM_PORT` if
you overrode it) — run it once per project after the service is up, same as the local `dotnet
run` workflow.
