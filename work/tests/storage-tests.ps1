$ErrorActionPreference = 'Stop'
$testDirectory = $PSScriptRoot
$sourceFile = Join-Path (Split-Path -Parent $testDirectory) 'src\Settings.cs'
$testFile = Join-Path $testDirectory 'storage-tests.cs'
$testExecutable = Join-Path $testDirectory 'storage-tests.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /warn:4 /langversion:5 /target:exe "/out:$testExecutable" /r:System.Web.Extensions.dll /r:System.Security.dll $sourceFile (Join-Path (Split-Path -Parent $testDirectory) 'src/WidgetDataProfile.cs') $testFile
if ($LASTEXITCODE -ne 0) { throw 'Storage tests did not compile.' }
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw 'Storage tests failed.' }
