$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testExecutable = Join-Path $PSScriptRoot 'profile-migration-tests.exe'
$sources = Join-Path (Split-Path -Parent $PSScriptRoot) 'src'
& $compiler /nologo /warn:4 /debug /langversion:5 /target:exe /r:System.Security.dll /r:System.Web.Extensions.dll "/out:$testExecutable" (Join-Path $sources 'WidgetDataProfile.cs') (Join-Path $PSScriptRoot 'profile-migration-tests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Profile migration tests did not compile.' }
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw 'Profile migration tests failed.' }
