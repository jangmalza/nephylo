$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testExecutable = Join-Path $PSScriptRoot 'OpenAiTests.exe'
$sourceDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'src'
& $compiler /nologo /langversion:5 /target:exe /r:System.Net.Http.dll /r:System.Web.Extensions.dll "/out:$testExecutable" (Join-Path $sourceDirectory 'Backend.cs') (Join-Path $sourceDirectory 'CodexInstallationDiscovery.cs') (Join-Path $sourceDirectory 'WslRuntime.cs') (Join-Path $sourceDirectory 'ConnectionDiagnostics.cs') (Join-Path $sourceDirectory 'OpenAiUsage.cs') (Join-Path $PSScriptRoot 'openai-tests.cs')
if ($LASTEXITCODE -ne 0) { throw 'OpenAI cost tests did not compile.' }
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw 'OpenAI cost tests failed.' }
