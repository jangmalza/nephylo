param([string]$Version = '1.21.0')
$ErrorActionPreference = 'Stop'
$work = Split-Path -Parent $PSScriptRoot
$application = [IO.Path]::GetFullPath((Join-Path $work ('release-' + $Version + '/UsageWidget.exe')))
$assembly = [Reflection.Assembly]::LoadFrom($application)
$override = $assembly.GetType('UsageWidget.SettingsStore').GetField('DataDirectoryOverride',[Reflection.BindingFlags]'NonPublic,Static')
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$scratch = Join-Path $temporaryRoot ('nephylo-public-update-' + [Guid]::NewGuid().ToString('N'))
$override.SetValue($null,$scratch)
$client = [UsageWidget.AutoUpdateClient]::new()
try {
    $manifest = $client.CheckAsync([version]'0.0.0',[Threading.CancellationToken]::None).GetAwaiter().GetResult()
    if (-not $manifest -or $manifest.VersionText -cne $Version) { throw 'Public signed version did not match the release.' }
    $prepared = $client.DownloadAsync($manifest,[Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $manifest.VerifyInstaller($prepared.InstallerPath)
    $local = Join-Path $work ('website/dist/downloads/' + $manifest.FileName)
    if ((Get-FileHash -LiteralPath $local -Algorithm SHA256).Hash.ToLowerInvariant() -cne $manifest.Sha256) { throw 'Published installer differs from the local release.' }
    $alreadyCurrent = $client.CheckAsync([version]$Version,[Threading.CancellationToken]::None).GetAwaiter().GetResult()
    if ($null -ne $alreadyCurrent) { throw 'Current app version must not offer another update.' }
    [ordered]@{version=$Version;signatureVerified=$true;downloadVerified=$true;currentVersionNotRepeated=$true;installer=$manifest.FileName;bytes=$manifest.Bytes;sha256=$manifest.Sha256;executedInstaller=$false} | ConvertTo-Json
} finally {
    $client.Dispose()
    $override.SetValue($null,$null)
    $resolved = [IO.Path]::GetFullPath($scratch)
    if ([IO.Path]::GetDirectoryName($resolved) -cne $temporaryRoot -or [IO.Path]::GetFileName($resolved) -notmatch '^nephylo-public-update-[a-f0-9]{32}$') { throw 'Unsafe smoke-test cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
