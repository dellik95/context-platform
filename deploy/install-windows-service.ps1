<#
.SYNOPSIS
  Installs Context.Dashboard.Server as a Windows Service for one project.

.DESCRIPTION
  Requires an elevated (Administrator) PowerShell session, and a published, self-contained
  Context.Dashboard.Server.exe:
    dotnet publish apps\host\Context.Dashboard\Context.Dashboard.Server -c Release -r win-x64 --self-contained `
      -p:PublishSingleFile=true -o C:\ContextPlatform

  Windows services don't take arbitrary per-process environment variables the way a systemd
  EnvironmentFile does, so this script writes CONTEXT_PLATFORM_ROOT / CONTEXT_PLATFORM_PORT into
  the service's own registry Environment value (HKLM\SYSTEM\CurrentControlSet\Services\<name>),
  which Windows applies to the process before it starts — no wrapper script needed.

.EXAMPLE
  .\install-windows-service.ps1 -ProjectRoot 'C:\Projects\MyRepo' -ServiceName 'ContextPlatform-MyRepo'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectRoot,

    [Parameter(Mandatory = $true)]
    [string]$ServiceName,

    [int]$Port = 47923,

    [string]$ExePath = 'C:\ContextPlatform\Context.Dashboard.Server.exe'
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated (Administrator) PowerShell session."
}

if (-not (Test-Path $ExePath)) {
    throw "$ExePath not found. Publish it first: dotnet publish apps\host\Context.Dashboard\Context.Dashboard.Server -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o $(Split-Path $ExePath -Parent)"
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "A service named '$ServiceName' already exists. Run uninstall-windows-service.ps1 first if you want to reinstall it."
}

New-Service -Name $ServiceName `
    -BinaryPathName "`"$ExePath`"" `
    -DisplayName "Context Platform ($ServiceName)" `
    -Description "Persistent context / code-intelligence host for Claude Code (project: $ProjectRoot)." `
    -StartupType Automatic | Out-Null

$envValue = @(
    "CONTEXT_PLATFORM_ROOT=$ProjectRoot",
    "CONTEXT_PLATFORM_PORT=$Port"
)
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name Environment -Value $envValue -Type MultiString

Start-Service -Name $ServiceName

Write-Host "Installed and started service '$ServiceName' for project '$ProjectRoot' on port $Port."
Write-Host "Data dir (db/logs/backups): CONTEXT_PLATFORM_DATA_DIR if set, else this account's %LocalAppData%\ContextPlatform (LocalSystem by default: C:\Windows\System32\config\systemprofile\AppData\Local\ContextPlatform) — no longer under the project root."
Write-Host "Manage it with: Get-Service $ServiceName | Stop-Service / Start-Service"
