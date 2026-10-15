$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testExecutable = Join-Path $PSScriptRoot 'connection-tests.exe'
$sources = Join-Path (Split-Path -Parent $PSScriptRoot) 'src'
& $compiler /nologo /warn:4 /langversion:5 /target:exe /r:System.Net.Http.dll /r:System.Web.Extensions.dll "/out:$testExecutable" (Join-Path $sources 'Backend.cs') (Join-Path $sources 'CodexInstallationDiscovery.cs') (Join-Path $sources 'WslRuntime.cs') (Join-Path $sources 'ConnectionDiagnostics.cs') (Join-Path $sources 'OpenAiUsage.cs') (Join-Path $PSScriptRoot 'connection-tests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Connection diagnostics tests did not compile.' }
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw 'Connection diagnostics tests failed.' }
