param([switch]$LiveCodex)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testExecutable = Join-Path $PSScriptRoot 'BackendTests.exe'
$sourceFile = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Backend.cs'
$testFile = Join-Path $PSScriptRoot 'BackendTests.cs'
& $compiler /nologo /langversion:5 /target:exe /r:System.Net.Http.dll /r:System.Web.Extensions.dll "/out:$testExecutable" $sourceFile (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\CodexInstallationDiscovery.cs') (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\WslRuntime.cs') (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\ConnectionDiagnostics.cs') $testFile
if ($LASTEXITCODE -ne 0) { throw 'Backend tests did not compile.' }
if ($LiveCodex) { & $testExecutable --live } else { & $testExecutable }
if ($LASTEXITCODE -ne 0) { throw 'Backend tests failed.' }
