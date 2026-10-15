$ErrorActionPreference = 'Stop'
$testDirectory = $PSScriptRoot
$sourceDirectory = Join-Path (Split-Path -Parent $testDirectory) 'src'
$testFile = Join-Path $testDirectory 'extra-storage-tests.cs'
$testExecutable = Join-Path $testDirectory 'extra-storage-tests.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /warn:4 /langversion:5 /target:exe "/out:$testExecutable" /r:System.Web.Extensions.dll /r:System.Security.dll (Join-Path $sourceDirectory 'Settings.cs') (Join-Path $sourceDirectory 'WidgetDataProfile.cs') (Join-Path $sourceDirectory 'ExtraSettings.cs') $testFile
if ($LASTEXITCODE -ne 0) { throw 'Extra storage tests did not compile.' }
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw 'Extra storage tests failed.' }
