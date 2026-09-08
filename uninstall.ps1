#Requires -Version 5.1
<#
.SYNOPSIS
    Removes Caret Language Indicator: stops it, drops the Startup shortcut and
    deletes the installed copy.
.DESCRIPTION
    Only touches what install.ps1 created. Nothing else on the system was
    changed, so there is nothing else to undo.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$target = Join-Path $env:LOCALAPPDATA 'CaretLangIndicator'
$lnk    = Join-Path ([Environment]::GetFolderPath('Startup')) 'Caret Language Indicator.lnk'

Get-Process -Name 'CaretLangIndicator' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Stopping pid $($_.Id)"
    try { $_.Kill(); $_.WaitForExit(5000) } catch { }
}

if (Test-Path $lnk) {
    Remove-Item $lnk -Force
    Write-Host "Removed Startup shortcut"
} else {
    Write-Host "No Startup shortcut found"
}

if (Test-Path $target) {
    Remove-Item $target -Recurse -Force
    Write-Host "Removed $target"
} else {
    Write-Host "Nothing installed at $target"
}

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
