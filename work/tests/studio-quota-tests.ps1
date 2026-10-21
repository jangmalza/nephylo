$ErrorActionPreference = 'Stop'
$testDirectory = $PSScriptRoot
$sourceDirectory = Join-Path (Split-Path -Parent $testDirectory) 'src'
$testExecutable = Join-Path $testDirectory 'studio-quota-tests.exe'
$frameworkDirectory = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkDirectory 'csc.exe'
$arguments = @('/nologo', '/warn:4', '/langversion:5', '/target:exe', '/define:STUDIO_QUOTA_TESTS', '/main:StudioQuotaTests', '/utf8output', ('/out:' + $testExecutable))
foreach ($reference in @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll', 'System.Security.dll')) {
    $arguments += '/reference:' + (Join-Path $frameworkDirectory $reference)
}
foreach ($source in @('StudioQuota.cs', 'StudioCodexClient.cs', 'StudioFailure.cs', 'StudioAgentClient.cs', 'StudioStore.cs', 'StudioHandoff.cs', 'StudioProviderCatalog.cs')) {
    $arguments += Join-Path $sourceDirectory $source
}
$arguments += Join-Path $testDirectory 'studio-quota-tests.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Studio quota tests did not compile.' }
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw 'Studio quota tests failed.' }
