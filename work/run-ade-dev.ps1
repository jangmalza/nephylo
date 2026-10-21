param(
    [string]$Profile = '',
    [ValidateSet('dark','light')][string]$Theme,
    [ValidateSet('ko','en')][string]$Language,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not $Profile) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $profileHash = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($repository.TrimEnd('\').ToUpperInvariant()))
        $Profile = 'ade-' + ([BitConverter]::ToString($profileHash).Replace('-','').ToLowerInvariant().Substring(0,12))
    } finally { $sha.Dispose() }
}
if ($Profile -cnotmatch '\A[a-z0-9][a-z0-9_-]{0,39}\z') { throw 'Profile must contain 1-40 lowercase letters, digits, underscores or hyphens.' }
$dataDirectory = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('UserProfile')) ('.nephylo-ade-dev\profile-' + $Profile)))
$sha = [Security.Cryptography.SHA256]::Create()
try { $identity = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($dataDirectory.ToUpperInvariant()))).Replace('-','').ToLowerInvariant() }
finally { $sha.Dispose() }
$wake = $null
try {
    $wake = [Threading.EventWaitHandle]::OpenExisting('Local\Nephylo.ADE.Development.' + $identity + '.Show')
    $null = $wake.Set()
    if ($Theme -or $Language) { Write-Warning 'Close the development window before applying appearance changes.' }
    [pscustomobject]@{mode='ADE development only';profile=$Profile;alreadyRunning=$true;dataDirectory=$dataDirectory}
    return
} catch [Threading.WaitHandleCannotBeOpenedException] {
    # No development owner: build and start without interacting with the widget.
} finally { if ($null -ne $wake) { $wake.Dispose() } }
$buildDirectory = Join-Path $repository 'outputs\ade-dev\app'
if (-not $NoBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $buildDirectory }
$executable = Join-Path $buildDirectory 'UsageWidget.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Build the ADE development app before using -NoBuild.' }
$launchArguments = @('--ade-dev','--ade-dev-profile',$Profile)
if ($Theme) { $launchArguments += @('--ade-dev-theme',$Theme) }
if ($Language) { $launchArguments += @('--ade-dev-language',$Language) }
# All application arguments are fixed options or validated, shell-free single tokens.
$process = Start-Process -FilePath $executable -ArgumentList $launchArguments -WorkingDirectory $buildDirectory -PassThru -WindowStyle Normal
[pscustomobject]@{
    mode = 'ADE development only'
    profile = $Profile
    processId = $process.Id
    dataDirectory = $dataDirectory
}
