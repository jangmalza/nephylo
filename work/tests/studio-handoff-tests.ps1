$ErrorActionPreference = 'Stop'
$testDirectory = $PSScriptRoot
$sourceDirectory = Join-Path (Split-Path -Parent $testDirectory) 'src'
$testExecutable = Join-Path $testDirectory 'studio-handoff-tests.exe'
$frameworkDirectory = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkDirectory 'csc.exe'
$arguments = @('/nologo', '/warn:4', '/langversion:5', '/target:exe', '/utf8output', ('/out:' + $testExecutable))
foreach ($reference in @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll', 'System.Security.dll')) {
    $arguments += '/reference:' + (Join-Path $frameworkDirectory $reference)
}
$arguments += Join-Path $sourceDirectory 'StudioStore.cs'
$arguments += Join-Path $sourceDirectory 'StudioHandoff.cs'
$arguments += Join-Path $sourceDirectory 'StudioProviderCatalog.cs'
$arguments += Join-Path $testDirectory 'studio-handoff-tests.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Studio handoff tests did not compile.' }
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw 'Studio handoff tests failed.' }
