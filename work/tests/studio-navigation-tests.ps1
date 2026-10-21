$ErrorActionPreference = 'Stop'
$sourceFile = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\StudioStore.cs'
$testExecutable = Join-Path $PSScriptRoot 'studio-navigation-tests.exe'
$frameworkDirectory = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkDirectory 'csc.exe'
$arguments = @('/nologo', '/warn:4', '/langversion:5', '/target:exe', '/main:StudioNavigationTests', '/utf8output', ('/out:' + $testExecutable))
foreach ($reference in @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll', 'System.Security.dll')) {
    $arguments += '/reference:' + (Join-Path $frameworkDirectory $reference)
}
$arguments += $sourceFile
$arguments += Join-Path (Split-Path -Parent $PSScriptRoot) 'src\StudioHandoff.cs'
$arguments += Join-Path (Split-Path -Parent $PSScriptRoot) 'src\StudioProviderCatalog.cs'
$arguments += Join-Path $PSScriptRoot 'studio-navigation-tests.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Studio navigation tests did not compile.' }
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw 'Studio navigation tests failed.' }
