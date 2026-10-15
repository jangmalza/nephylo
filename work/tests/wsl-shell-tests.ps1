$ErrorActionPreference = 'Stop'
$pythonExecutable = Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
if (-not (Test-Path -LiteralPath $pythonExecutable)) { $pythonExecutable = (Get-Command python -ErrorAction Stop).Source }
& $pythonExecutable -B (Join-Path $PSScriptRoot 'wsl-shell-tests.py')
if ($LASTEXITCODE -ne 0) { throw 'Isolated WSL shell tests failed.' }
