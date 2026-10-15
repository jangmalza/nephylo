$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$work = Split-Path -Parent $PSScriptRoot
$test = Join-Path $PSScriptRoot 'auto-update-installer-tests.exe'
$arguments = @('/nologo','/target:exe','/langversion:5','/define:NEPHYLO_UPDATE_INSTALLER_TESTS',('/out:' + $test))
foreach ($reference in @('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Security.dll','System.Net.Http.dll','Microsoft.CSharp.dll')) { $arguments += '/reference:' + (Join-Path $framework $reference) }
$arguments += @(Join-Path $work 'src/AutoUpdateClient.cs'; Join-Path $work 'src/AutoUpdateInstaller.cs'; Join-Path $PSScriptRoot 'auto-update-installer-tests.cs')
& (Join-Path $framework 'csc.exe') @arguments
if ($LASTEXITCODE -ne 0) { throw 'Auto update installer tests did not compile.' }
& $test
if ($LASTEXITCODE -ne 0) { throw 'Auto update installer tests failed.' }
