$ErrorActionPreference = 'Stop'
$sourceDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'src'
$testExecutable = Join-Path $PSScriptRoot 'claude-receipt-ui-tests.exe'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$arguments = @('/nologo','/warn:4','/langversion:5','/target:exe','/main:ClaudeReceiptUiTests','/utf8output',('/out:'+$testExecutable))
foreach ($reference in @('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Security.dll','System.Net.Http.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xaml.dll')) { $arguments += '/reference:'+(Join-Path $framework $reference) }
foreach ($reference in @('PresentationFramework.dll','PresentationCore.dll','WindowsBase.dll')) { $arguments += '/reference:'+(Join-Path $framework ('WPF\'+$reference)) }
$arguments += @(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' | ForEach-Object { $_.FullName })
$arguments += Join-Path $PSScriptRoot 'claude-receipt-ui-tests.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Claude receipt UI tests did not compile.' }
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw 'Claude receipt UI tests failed.' }
