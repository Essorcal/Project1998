<#
.SYNOPSIS
Run the shared workflow CLI with Python 3.9+ (no packages required).
.EXAMPLE
& C:\Repo\NexusTK\Scripts\Workflow.ps1 status
#>
$ErrorActionPreference = 'Stop'
$python = $env:P1998_PYTHON
if (-not $python) {
    $command = Get-Command python -ErrorAction SilentlyContinue
    if ($command) { $python = $command.Source }
}
if (-not $python) {
    $bundled = Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
    if (Test-Path -LiteralPath $bundled) { $python = $bundled }
}
if (-not $python) { throw 'Python 3.9+ not found. Set P1998_PYTHON to its executable.' }
& $python (Join-Path $PSScriptRoot 'workflow.py') @args
exit $LASTEXITCODE
