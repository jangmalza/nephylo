$ErrorActionPreference = 'Stop'
$frameworkDirectory = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkDirectory 'csc.exe'
$testExecutable = Join-Path $PSScriptRoot 'google-login-tests.exe'
$arguments = @('/nologo', '/warn:4', '/langversion:5', '/target:exe', '/main:GoogleLoginTests', '/utf8output', ('/out:' + $testExecutable))
foreach ($reference in @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll', 'System.Security.dll', 'System.Net.Http.dll')) {
    $arguments += '/reference:' + (Join-Path $frameworkDirectory $reference)
}
$arguments += Join-Path (Split-Path -Parent $PSScriptRoot) 'src/MembershipClient.cs'
$arguments += Join-Path (Split-Path -Parent $PSScriptRoot) 'src/Localization.cs'
$arguments += Join-Path $PSScriptRoot 'google-login-tests.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Google login tests did not compile.' }
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw 'Google login tests failed.' }
