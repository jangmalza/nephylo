$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source = Join-Path (Split-Path -Parent $PSScriptRoot) 'src'
$testExecutable = Join-Path $PSScriptRoot 'usage-tracking-tests.exe'
& $compiler /nologo /langversion:5 /target:exe /r:System.Net.Http.dll /r:System.Web.Extensions.dll "/out:$testExecutable" (Join-Path $source 'UsageTracking.cs') (Join-Path $source 'Backend.cs') (Join-Path $source 'CodexInstallationDiscovery.cs') (Join-Path $source 'WslRuntime.cs') (Join-Path $source 'ConnectionDiagnostics.cs') (Join-Path $PSScriptRoot 'usage-tracking-tests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Usage tracking tests did not compile.' }
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw 'Usage tracking tests failed.' }
