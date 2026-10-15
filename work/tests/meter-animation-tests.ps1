$ErrorActionPreference = 'Stop'
$testDirectory = $PSScriptRoot
$sourceDirectory = Join-Path (Split-Path -Parent $testDirectory) 'src'
$testExecutable = Join-Path $testDirectory 'meter-animation-tests.exe'
$frameworkDirectory = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkDirectory 'csc.exe'
$arguments = @('/nologo', '/warn:4', '/langversion:5', '/target:exe', '/main:MeterAnimationTests', '/utf8output', ('/out:' + $testExecutable))
foreach ($reference in @('System.dll', 'System.Core.dll', 'System.Xaml.dll')) {
    $arguments += '/reference:' + (Join-Path $frameworkDirectory $reference)
}
foreach ($reference in @('PresentationFramework.dll', 'PresentationCore.dll', 'WindowsBase.dll')) {
    $arguments += '/reference:' + (Join-Path $frameworkDirectory ('WPF\' + $reference))
}
$arguments += Join-Path $sourceDirectory 'WidgetPaper.cs'
$arguments += Join-Path $sourceDirectory 'WidgetPalette.cs'
$arguments += Join-Path $testDirectory 'meter-animation-tests.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Meter animation tests did not compile.' }
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw 'Meter animation tests failed.' }
