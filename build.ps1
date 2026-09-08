#Requires -Version 5.1
<#
.SYNOPSIS
    Compiles CaretLangIndicator.exe with the C# compiler that ships with
    Windows - nothing needs to be installed.
#>
[CmdletBinding()]
param(
    [string]$Out = (Join-Path $PSScriptRoot 'CaretLangIndicator.exe')
)

$ErrorActionPreference = 'Stop'
if (-not $PSScriptRoot) { $PSScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path }

$fw  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }

$wpf = Join-Path $fw 'WPF'
$refs = @(
    "$wpf\PresentationFramework.dll"
    "$wpf\PresentationCore.dll"
    "$wpf\WindowsBase.dll"
    "$wpf\UIAutomationClient.dll"
    "$wpf\UIAutomationTypes.dll"
    "$fw\System.Xaml.dll"
    "$fw\System.Windows.Forms.dll"
    "$fw\System.Drawing.dll"
    "$fw\System.dll"
    "$fw\System.Core.dll"
)

$src = Join-Path $PSScriptRoot 'src\Indicator.cs'
if (-not (Test-Path $src)) { throw "source not found: $src" }

# /target:winexe keeps the console window away
$cscArgs = @('/nologo', '/target:winexe', '/optimize+', "/out:$Out") +
           ($refs | ForEach-Object { "/reference:$_" }) +
           $src

& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "compilation failed with exit code $LASTEXITCODE" }

$info = Get-Item $Out
'built {0}  ({1:N0} KB)' -f $info.FullName, ($info.Length / 1KB)
