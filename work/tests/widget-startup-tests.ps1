param([switch]$WmiProbe)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testExecutable = Join-Path $PSScriptRoot 'widget-startup-tests.exe'
$sources = Join-Path (Split-Path -Parent $PSScriptRoot) 'src'
& $compiler /nologo /warn:4 /debug /langversion:5 /target:exe /r:System.Security.dll /r:System.Web.Extensions.dll ("/out:" + $testExecutable) (Join-Path $sources 'WidgetDataProfile.cs') (Join-Path $sources 'WidgetStartup.cs') (Join-Path $PSScriptRoot 'widget-startup-tests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Widget startup tests did not compile.' }
if ($WmiProbe) { & $testExecutable --wmi-probe } else { & $testExecutable }
if ($LASTEXITCODE -ne 0) { throw 'Widget startup tests failed.' }
