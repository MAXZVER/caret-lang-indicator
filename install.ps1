#Requires -Version 5.1
<#
.SYNOPSIS
    Installs Caret Language Indicator for the current user and starts it.
.DESCRIPTION
    Copies the exe into %LOCALAPPDATA%\CaretLangIndicator, puts a shortcut in
    the Startup folder so it comes back after a reboot, and launches it.

    No administrator rights, no service, no registry keys outside the shortcut,
    nothing written outside your own profile. Undo with uninstall.ps1.
.PARAMETER Arguments
    Options passed to the indicator, e.g. '-OnlyWhenCaps' or '-Anchor Corner'.
    They are stored in the Startup shortcut, so they survive reboots.
.PARAMETER NoAutostart
    Install and run, but do not add it to Startup.
.EXAMPLE
    .\install.ps1
    .\install.ps1 -Arguments '-OnlyWhenCaps'
    .\install.ps1 -NoAutostart
#>
[CmdletBinding()]
param(
    [string]$Arguments = '',
    [switch]$NoAutostart
)

$ErrorActionPreference = 'Stop'
if (-not $PSScriptRoot) { $PSScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path }

$exeName = 'CaretLangIndicator.exe'
$source  = Join-Path $PSScriptRoot $exeName

if (-not (Test-Path $source)) {
    # not in the release layout - try building from source next to this script
    $build = Join-Path $PSScriptRoot 'build.ps1'
    if (Test-Path $build) {
        Write-Host 'Executable not found, building it...'
        & $build | Out-Host
    }
    if (-not (Test-Path $source)) {
        throw "$exeName not found next to this script, and it could not be built. Download it from the release."
    }
}

$target = Join-Path $env:LOCALAPPDATA 'CaretLangIndicator'
New-Item -ItemType Directory -Force -Path $target | Out-Null
$installed = Join-Path $target $exeName

# stop a running copy, otherwise the file is locked
Get-Process -Name 'CaretLangIndicator' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Stopping running instance (pid $($_.Id))"
    try { $_.Kill(); $_.WaitForExit(5000) } catch { }
}

Copy-Item $source $installed -Force
Write-Host "Installed to $installed"

if (-not $NoAutostart) {
    $startup = [Environment]::GetFolderPath('Startup')
    $lnk = Join-Path $startup 'Caret Language Indicator.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $sc = $shell.CreateShortcut($lnk)
    $sc.TargetPath       = $installed
    $sc.Arguments        = $Arguments
    $sc.WorkingDirectory = $target
    $sc.Description      = 'Keyboard layout and Caps Lock badge next to the caret'
    $sc.Save()
    Write-Host "Added to Startup: $lnk"
} else {
    Write-Host 'Skipped Startup registration (-NoAutostart)'
}

if ($Arguments) { Start-Process -FilePath $installed -ArgumentList $Arguments }
else            { Start-Process -FilePath $installed }

Start-Sleep -Milliseconds 1500
$running = Get-Process -Name 'CaretLangIndicator' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host ''
    Write-Host "Running (pid $($running.Id)). Click into any text field to see the badge." -ForegroundColor Green
    Write-Host 'Quit it from the tray icon, or run uninstall.ps1 to remove it.'
} else {
    Write-Warning 'The process did not stay running. Try launching the exe directly to see the error.'
}
