param([switch]$Probe)
$ErrorActionPreference = 'Stop'
$testDirectory = $PSScriptRoot
$sourceDirectory = Join-Path (Split-Path -Parent $testDirectory) 'src'
$testExecutable = Join-Path $testDirectory 'studio-claude-tests.exe'
$frameworkDirectory = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkDirectory 'csc.exe'
$arguments = @('/nologo', '/warn:4', '/langversion:5', '/target:exe', '/main:StudioClaudeTests', '/utf8output', ('/out:' + $testExecutable))
foreach ($reference in @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll', 'System.Security.dll', 'System.Net.Http.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Xaml.dll')) {
    $arguments += '/reference:' + (Join-Path $frameworkDirectory $reference)
}
foreach ($reference in @('PresentationFramework.dll', 'PresentationCore.dll', 'WindowsBase.dll')) {
    $arguments += '/reference:' + (Join-Path $frameworkDirectory ('WPF\' + $reference))
}
$arguments += @(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' | ForEach-Object { $_.FullName })
$arguments += Join-Path $testDirectory 'studio-claude-tests.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Claude adapter tests did not compile.' }
if ($Probe) { & $testExecutable --probe } else { & $testExecutable }
if ($LASTEXITCODE -ne 0) { throw 'Claude adapter tests failed.' }
