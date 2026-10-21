param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$scratch = Join-Path $repo ('outputs\widget-isolation-tests\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
$Executable = (Resolve-Path -LiteralPath $Executable).Path

# A copied build must fail specifically at the product boundary, before it needs
# any icons/configuration or compiles the foreign source.
$guardWork = Join-Path $scratch 'guard\work'
New-Item -ItemType Directory -Path (Join-Path $guardWork 'src') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'work\build.ps1') -Destination (Join-Path $guardWork 'build.ps1')
[IO.File]::WriteAllText((Join-Path $guardWork 'src\StudioUnexpected.cs'), 'class StudioUnexpected {}')
$ps = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$guardInfo = New-Object Diagnostics.ProcessStartInfo
$guardInfo.FileName = $ps
$guardInfo.Arguments = '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File "' + (Join-Path $guardWork 'build.ps1') + '"'
$guardInfo.UseShellExecute = $false; $guardInfo.CreateNoWindow = $true
$guardInfo.RedirectStandardOutput = $true; $guardInfo.RedirectStandardError = $true
$guard = [Diagnostics.Process]::Start($guardInfo)
$null = $guard.Handle
$outTask = $guard.StandardOutput.ReadToEndAsync(); $errTask = $guard.StandardError.ReadToEndAsync()
if (-not $guard.WaitForExit(10000)) { $guard.Kill(); throw 'Widget build boundary probe timed out.' }
$guard.WaitForExit()
$guardText = $outTask.GetAwaiter().GetResult() + $errTask.GetAwaiter().GetResult()
if ($guard.ExitCode -eq 0 -or -not $guardText.Contains('widget-only-build:')) { throw 'A foreign ADE source did not fail at the Widget build boundary.' }
$guard.Dispose()
[IO.File]::WriteAllText((Join-Path $scratch 'build-boundary.txt'), 'PASS: foreign ADE source rejected before compilation.')

$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$test = Join-Path $scratch 'widget-isolation-tests.exe'
$arguments = @('/nologo','/warn:4','/langversion:5','/target:exe','/main:WidgetIsolationTests','/utf8output',('/out:' + $test))
foreach ($reference in @('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Security.dll','System.Net.Http.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xaml.dll')) { $arguments += '/reference:' + (Join-Path $framework $reference) }
foreach ($reference in @('PresentationFramework.dll','PresentationCore.dll','WindowsBase.dll')) { $arguments += '/reference:' + (Join-Path $framework ('WPF\' + $reference)) }
$arguments += @(Get-ChildItem -LiteralPath (Join-Path $repo 'work\src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$arguments += Join-Path $PSScriptRoot 'widget-isolation-tests.cs'
& (Join-Path $framework 'csc.exe') @arguments
if ($LASTEXITCODE -ne 0) { throw 'Widget isolation tests did not compile.' }
& $test $Executable
if ($LASTEXITCODE -ne 0) { throw 'Widget isolation tests failed.' }
