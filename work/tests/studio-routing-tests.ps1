$ErrorActionPreference = 'Stop'
$testDirectory = $PSScriptRoot
$sourceDirectory = Join-Path (Split-Path -Parent $testDirectory) 'src'
$testExecutable = Join-Path $testDirectory 'studio-routing-tests.exe'
$frameworkDirectory = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$arguments = @('/nologo','/warn:4','/langversion:5','/target:exe','/main:StudioRoutingTests','/utf8output',('/out:' + $testExecutable))
foreach ($reference in @('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Security.dll','System.Net.Http.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xaml.dll')) {
    $arguments += '/reference:' + (Join-Path $frameworkDirectory $reference)
}
foreach ($reference in @('PresentationFramework.dll','PresentationCore.dll','WindowsBase.dll')) {
    $arguments += '/reference:' + (Join-Path $frameworkDirectory ('WPF\' + $reference))
}
$arguments += @(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' | ForEach-Object { $_.FullName })
$arguments += Join-Path $testDirectory 'studio-routing-tests.cs'
& (Join-Path $frameworkDirectory 'csc.exe') @arguments
if ($LASTEXITCODE -ne 0) { throw 'Studio routing tests did not compile.' }
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw 'Studio routing tests failed.' }
