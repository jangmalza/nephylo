$ErrorActionPreference = 'Stop'
$frameworkDirectory = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkDirectory 'csc.exe'
$sourceDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'src'
$testExecutable = Join-Path $PSScriptRoot 'installation-tests.exe'
$arguments = @('/nologo','/warn:4','/langversion:5','/target:exe',('/out:' + $testExecutable))
foreach ($reference in @('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Security.dll','Microsoft.CSharp.dll','System.Xaml.dll')) { $arguments += '/reference:' + (Join-Path $frameworkDirectory $reference) }
foreach ($reference in @('PresentationFramework.dll','PresentationCore.dll','WindowsBase.dll')) { $arguments += '/reference:' + (Join-Path $frameworkDirectory ('WPF\' + $reference)) }
$arguments += @(Join-Path $sourceDirectory 'Settings.cs'; Join-Path $sourceDirectory 'WidgetDataProfile.cs'; Join-Path $sourceDirectory 'Localization.cs'; Join-Path $sourceDirectory 'WidgetInstallation.cs'; Join-Path $PSScriptRoot 'installation-tests.cs')
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Installation tests did not compile.' }
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw 'Installation tests failed.' }
