$ErrorActionPreference = 'Stop'
$testDirectory = $PSScriptRoot
$sourceDirectory = Join-Path (Split-Path -Parent $testDirectory) 'src'
$frameworkDirectory = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkDirectory 'csc.exe'
$testExecutable = Join-Path $testDirectory 'studio-message-tests.exe'
$arguments = @('/nologo', '/warn:4', '/langversion:5', '/target:exe', '/main:StudioMessageTests', '/utf8output', ('/out:' + $testExecutable))
foreach ($reference in @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll', 'System.Security.dll', 'System.Xaml.dll')) {
    $arguments += '/reference:' + (Join-Path $frameworkDirectory $reference)
}
foreach ($reference in @('PresentationFramework.dll', 'PresentationCore.dll', 'WindowsBase.dll')) {
    $arguments += '/reference:' + (Join-Path $frameworkDirectory ('WPF\' + $reference))
}
$arguments += Join-Path $sourceDirectory 'StudioStore.cs'
$arguments += Join-Path $sourceDirectory 'StudioHandoff.cs'
$arguments += Join-Path $sourceDirectory 'StudioProviderCatalog.cs'
$arguments += Join-Path $sourceDirectory 'StudioMessageView.cs'
$arguments += Join-Path $sourceDirectory 'StudioMotion.cs'
$arguments += Join-Path $testDirectory 'studio-message-tests.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Studio message presentation tests did not compile.' }
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw 'Studio message presentation tests failed.' }
