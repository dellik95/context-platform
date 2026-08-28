<#
.SYNOPSIS
  Stops and removes a Context.Host Windows Service created by install-windows-service.ps1.

.EXAMPLE
  .\uninstall-windows-service.ps1 -ServiceName 'ContextPlatform-MyRepo'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ServiceName
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated (Administrator) PowerShell session."
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Host "No service named '$ServiceName' exists — nothing to do."
    return
}

if ($service.Status -ne 'Stopped') {
    Stop-Service -Name $ServiceName -Force
}

# New-Service has no counterpart cmdlet for removal; sc.exe delete is the standard way.
& sc.exe delete $ServiceName | Out-Null

Write-Host "Removed service '$ServiceName'."
