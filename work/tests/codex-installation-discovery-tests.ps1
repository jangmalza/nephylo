$ErrorActionPreference = 'Stop'
$sourceDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'src'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testExecutable = Join-Path $PSScriptRoot 'codex-installation-discovery-tests.exe'
& $compiler /nologo /warn:4 /langversion:5 /target:exe /r:System.Core.dll "/out:$testExecutable" (Join-Path $sourceDirectory 'CodexInstallationDiscovery.cs') (Join-Path $PSScriptRoot 'codex-installation-discovery-tests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Codex installation discovery tests did not compile.' }
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw 'Codex installation discovery tests failed.' }
