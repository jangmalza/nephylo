$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$testExecutable = Join-Path $PSScriptRoot 'refresh-schedule-tests.exe'
$source = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/ProviderRefreshSchedule.cs'
& $compiler /nologo /warn:4 /langversion:5 /target:exe "/out:$testExecutable" $source (Join-Path $PSScriptRoot 'refresh-schedule-tests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Refresh schedule tests did not compile.' }
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw 'Refresh schedule tests failed.' }
