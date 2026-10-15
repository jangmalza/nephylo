param(
    [Parameter(Mandatory=$true)][string]$Version,
    [string]$SourceDirectory = '',
    [string]$OutputDirectory = '',
    [string]$Compiler = ''
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid version' }
if (-not $SourceDirectory) { $SourceDirectory = Join-Path $PSScriptRoot ('website-download-' + $Version) }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot ('setup-' + $Version) }
if (-not $Compiler) { $Compiler = $env:INNO_SETUP_COMPILER }
if (-not $Compiler) { $Compiler = Join-Path $PSScriptRoot 'tools/innosetup-7.1.0/ISCC.exe' }
if (-not (Test-Path -LiteralPath $Compiler -PathType Leaf)) {
    throw 'Inno Setup 7 compiler missing. Set INNO_SETUP_COMPILER to ISCC.exe. See installer/README.md.'
}
$SourceDirectory = (Resolve-Path -LiteralPath $SourceDirectory).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
foreach ($name in @('UsageWidget.exe','UsageWidget.exe.config','widget.ico','widget.png','README.txt','THIRD-PARTY-NOTICES.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $SourceDirectory $name) -PathType Leaf)) { throw ('Missing payload: ' + $name) }
}
$actualVersion = (Get-Item -LiteralPath (Join-Path $SourceDirectory 'UsageWidget.exe')).VersionInfo.FileVersion
if ($actualVersion -ne ($Version + '.0')) { throw ('Payload version does not match setup version: ' + $actualVersion) }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
& $Compiler ('/DAppVersion=' + $Version) ('/DSourceDir=' + $SourceDirectory) ('/DOutputDir=' + $OutputDirectory) (Join-Path $PSScriptRoot 'installer/Nephylo.iss')
if ($LASTEXITCODE -ne 0) { throw 'Setup compilation failed.' }
$setup = Join-Path $OutputDirectory 'Nephylo-Setup.exe'
if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw 'Setup compiler did not produce Nephylo-Setup.exe.' }
Write-Output ('Built installer: ' + $setup)
