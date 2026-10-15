$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testExecutable = Join-Path $PSScriptRoot 'higgsfield-tests.exe'
$sources = Join-Path (Split-Path -Parent $PSScriptRoot) 'src'
& $compiler /nologo /warn:4 /langversion:5 /target:exe /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.Security.dll "/out:$testExecutable" (Join-Path $sources 'Backend.cs') (Join-Path $sources 'CodexInstallationDiscovery.cs') (Join-Path $sources 'WslRuntime.cs') (Join-Path $sources 'ConnectionDiagnostics.cs') (Join-Path $sources 'Settings.cs') (Join-Path $sources 'WidgetDataProfile.cs') (Join-Path $sources 'HiggsfieldUsage.cs') (Join-Path $sources 'ServicePlan.cs') (Join-Path $PSScriptRoot 'higgsfield-tests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Higgsfield tests did not compile.' }
$testRun = Start-Process -FilePath $testExecutable -Wait -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $PSScriptRoot 'higgsfield-tests.out') -RedirectStandardError (Join-Path $PSScriptRoot 'higgsfield-tests.err')
Get-Content -LiteralPath (Join-Path $PSScriptRoot 'higgsfield-tests.out') -Encoding UTF8
Get-Content -LiteralPath (Join-Path $PSScriptRoot 'higgsfield-tests.err') -Encoding UTF8
if ($testRun.ExitCode -ne 0) { throw 'Higgsfield tests failed.' }
