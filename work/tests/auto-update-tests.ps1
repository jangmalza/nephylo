$ErrorActionPreference = 'Stop'
$testDirectory = $PSScriptRoot
$sourceDirectory = Join-Path (Split-Path -Parent $testDirectory) 'src'
$testExecutable = Join-Path $testDirectory 'auto-update-tests.exe'
$frameworkDirectory = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkDirectory 'csc.exe'
$arguments = @('/nologo', '/warn:4', '/langversion:5', '/target:exe', '/main:AutoUpdateTests', '/utf8output', ('/out:' + $testExecutable))
foreach ($reference in @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll', 'System.Security.dll', 'System.Net.Http.dll')) {
    $arguments += '/reference:' + (Join-Path $frameworkDirectory $reference)
}
$arguments += Join-Path $sourceDirectory 'AutoUpdateClient.cs'
$arguments += Join-Path $testDirectory 'auto-update-tests.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Auto-update tests did not compile.' }
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw 'Auto-update tests failed.' }
