# Offline checks. Download and launch helpers are replaced before any install flow runs.
param([string]$InstallerScript = '')
$ErrorActionPreference = 'Stop'
$workRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not $InstallerScript) { $InstallerScript = Join-Path $workRoot 'website/dist/downloads/install-nephylo.ps1' }
$InstallerScript = [IO.Path]::GetFullPath($InstallerScript)
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$testRoot = Join-Path $temporaryRoot ('nephylo-web-install-tests-' + [Guid]::NewGuid().ToString('N'))
$utf8 = New-Object Text.UTF8Encoding($false)
$strictUtf8 = New-Object Text.UTF8Encoding($false, $true)
$assertions = 0
$rsa = New-Object Security.Cryptography.RSACryptoServiceProvider(2048, (New-Object Security.Cryptography.CspParameters -Property @{ ProviderType = 24 }))
$rsa.PersistKeyInCsp = $false
$script:downloadCalls = New-Object 'Collections.Generic.List[object]'
$script:launchCalls = New-Object 'Collections.Generic.List[string]'
$script:launchLanguages = New-Object 'Collections.Generic.List[string]'
$script:createdDirectories = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$script:mockDownloadMode = 'valid'
$script:mockEnvelope = ''
$script:fixtureSetupExitCode = 0
$script:verifiedFileLockObserved = $false

function Assert-That([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:assertions++
    Write-Output ('PASS ' + $Message)
}
function Assert-Rejected([scriptblock]$Action, [string]$Message) {
    $rejected = $false
    try { $null = & $Action } catch { $rejected = $true }
    Assert-That $rejected $Message
}
function Get-Digest([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
function New-EnvelopeFromBytes([byte[]]$Bytes) {
    $signature = $rsa.SignData($Bytes, [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'))
    return ([ordered]@{ payloadBase64 = [Convert]::ToBase64String($Bytes); signatureBase64 = [Convert]::ToBase64String($signature) } | ConvertTo-Json -Compress)
}
function New-Envelope([string]$PayloadJson) {
    return New-EnvelopeFromBytes ($utf8.GetBytes($PayloadJson))
}
function New-Payload([byte[]]$Bytes) {
    return [ordered]@{ schemaVersion = 1; version = '3.4.5'; fileName = 'Nephylo-Setup-3.4.5.exe'; bytes = $Bytes.LongLength; sha256 = (Get-Digest $Bytes) }
}
function Set-Fixture([byte[]]$Bytes, [string]$Mode = 'valid') {
    $script:fixtureBytes = $Bytes
    $script:mockEnvelope = New-Envelope ((New-Payload $Bytes) | ConvertTo-Json -Compress)
    $script:mockDownloadMode = $Mode
    $script:downloadCalls.Clear()
    $script:launchCalls.Clear()
    $script:launchLanguages.Clear()
}

try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    $scriptBytes = [IO.File]::ReadAllBytes($InstallerScript)
    Assert-That ($scriptBytes.Length -ge 3 -and $scriptBytes[0] -eq 0xef -and $scriptBytes[1] -eq 0xbb -and $scriptBytes[2] -eq 0xbf) 'Public installer retains Korean text in Windows PowerShell 5.1 with UTF-8 BOM.'
    $source = [IO.File]::ReadAllText($InstallerScript, $strictUtf8)
    Assert-That ($source -notmatch '(?i)\bInvoke-Expression\b|\bIEX\b') 'Public installer does not evaluate downloaded scripts.'
    Assert-That ($source -notmatch '(?i)ExecutionPolicy\s+Bypass|Set-ExecutionPolicy|Set-MpPreference|Add-MpPreference|DisableRealtimeMonitoring') 'Public installer does not disable execution policy or antivirus protection.'
    . $InstallerScript

    $assetKey = [IO.File]::ReadAllText((Join-Path $workRoot 'assets/update-signing-public.xml')).Trim()
    Assert-That ((Get-NephyloSigningKeyXml).Trim() -ceq $assetKey) 'Public installer pins the release RSA public key.'
    $productionJson = [IO.File]::ReadAllText((Join-Path $workRoot 'website/dist/downloads/update.json'), $strictUtf8)
    $production = Read-NephyloManifest $productionJson
    $productionPayload = $strictUtf8.GetString([Convert]::FromBase64String(($productionJson | ConvertFrom-Json).payloadBase64)) | ConvertFrom-Json
    Assert-That ($production.version -ceq $productionPayload.version) 'Production signed manifest verifies with the embedded release key.'
    Assert-That ($production.fileName -ceq $productionPayload.fileName -and $production.bytes -eq $productionPayload.bytes -and $production.sha256 -ceq $productionPayload.sha256) 'Verified production manifest preserves signed installer identity.'

    # Use an ephemeral fixture key, never the production private key or OS certificate store.
    $script:fixturePublicKey = $rsa.ToXmlString($false)
    function Get-NephyloSigningKeyXml { return $script:fixturePublicKey }
    $pe = New-Object byte[] 256
    $pe[0] = 0x4d; $pe[1] = 0x5a
    [BitConverter]::GetBytes([int]64).CopyTo($pe, 0x3c)
    $pe[64] = 0x50; $pe[65] = 0x45
    $payload = New-Payload $pe
    $validJson = $payload | ConvertTo-Json -Compress
    $validEnvelope = New-Envelope $validJson
    $manifest = Read-NephyloManifest $validEnvelope
    Assert-That ($manifest.version -ceq '3.4.5' -and $manifest.bytes -eq 256) 'Ephemeral RSA fixture verifies before negative checks.'

    foreach ($outerJson in @('', 'null', '[]', '{}', '{', (' ' * 16385))) {
        Assert-Rejected { Read-NephyloManifest $outerJson } 'Empty, malformed, wrong-shaped or oversized envelope is rejected.'
    }
    $outer = $validEnvelope | ConvertFrom-Json
    Assert-Rejected { Read-NephyloManifest ('{"payloadBase64":"' + $outer.payloadBase64 + '","signatureBase64":"' + $outer.signatureBase64 + '","extra":1}') } 'Unsigned envelope fields are rejected.'
    Assert-Rejected { Read-NephyloManifest ('{"payloadBase64":"' + $outer.payloadBase64 + '","payloadBase64":"' + $outer.payloadBase64 + '","signatureBase64":"' + $outer.signatureBase64 + '"}') } 'Duplicate envelope fields are rejected.'
    Assert-Rejected { Read-NephyloManifest ('{"payloadBase64":1,"signatureBase64":"' + $outer.signatureBase64 + '"}') } 'Envelope fields must be strings.'
    foreach ($field in @('payloadBase64', 'signatureBase64')) {
        foreach ($badBase64 in @('', '!', ($outer.$field + ' '), (' ' + $outer.$field))) {
            $changed = [ordered]@{ payloadBase64 = $outer.payloadBase64; signatureBase64 = $outer.signatureBase64 }
            $changed[$field] = $badBase64
            $changedJson = $changed | ConvertTo-Json -Compress
            Assert-Rejected { Read-NephyloManifest $changedJson } ($field + ' must be nonempty canonical Base64.')
        }
    }
    $shortSignature = [ordered]@{ payloadBase64 = $outer.payloadBase64; signatureBase64 = [Convert]::ToBase64String((New-Object byte[] 128)) } | ConvertTo-Json -Compress
    Assert-Rejected { Read-NephyloManifest $shortSignature } 'Signature length must match the pinned RSA key.'
    $tamperedPayload = [ordered]@{ payloadBase64 = [Convert]::ToBase64String($utf8.GetBytes($validJson.Replace('3.4.5', '3.4.6'))); signatureBase64 = $outer.signatureBase64 } | ConvertTo-Json -Compress
    Assert-Rejected { Read-NephyloManifest $tamperedPayload } 'Payload tampering fails RSA verification.'
    $signatureBytes = [Convert]::FromBase64String($outer.signatureBase64)
    $signatureBytes[0] = $signatureBytes[0] -bxor 1
    $tamperedSignature = [ordered]@{ payloadBase64 = $outer.payloadBase64; signatureBase64 = [Convert]::ToBase64String($signatureBytes) } | ConvertTo-Json -Compress
    Assert-Rejected { Read-NephyloManifest $tamperedSignature } 'Signature tampering is rejected.'

    $payloadCases = @(
        'null', '[]', '{}', '{',
        ($validJson.TrimEnd('}') + ',"extra":true}'),
        ($validJson.TrimEnd('}') + ',"version":"3.4.5"}'),
        ($validJson.Replace('"schemaVersion":1', '"schemaVersion":2')),
        ($validJson.Replace('"schemaVersion":1', '"schemaVersion":"1"')),
        ($validJson.Replace('"schemaVersion":1', '"schemaVersion":1.5')),
        ($validJson.Replace('"bytes":256', '"bytes":63')),
        ($validJson.Replace('"bytes":256', '"bytes":104857601')),
        ($validJson.Replace('"bytes":256', '"bytes":256.5')),
        ($validJson.Replace('"bytes":256', '"bytes":"256"')),
        ($validJson.Replace('"sha256":"', '"sha256":"A')),
        ($validJson.Replace('Nephylo-Setup-3.4.5.exe', '../Nephylo-Setup-3.4.5.exe')),
        ($validJson.Replace('Nephylo-Setup-3.4.5.exe', 'https://other.example/Nephylo-Setup-3.4.5.exe')),
        ($validJson.Replace('Nephylo-Setup-3.4.5.exe', 'Nephylo-Setup.exe'))
    )
    foreach ($badJson in $payloadCases) {
        $signedBadJson = New-Envelope $badJson
        Assert-Rejected { Read-NephyloManifest $signedBadJson } 'Signed malformed, unknown-field or invalid-schema payload is rejected.'
    }
    foreach ($version in @('01.4.5', '3.04.5', '3.4.05', '3.4', '3.4.5.6', '65536.4.5', '-1.4.5', '3.4.5/evil', '3.4.5 ')) {
        $badJson = $validJson.Replace('3.4.5', $version)
        $signedBadJson = New-Envelope $badJson
        Assert-Rejected { Read-NephyloManifest $signedBadJson } ('Noncanonical or out-of-range version is rejected: ' + $version)
    }
    $invalidUtf8 = [byte[]]@(0xc3, 0x28)
    $signedInvalidUtf8 = New-EnvelopeFromBytes $invalidUtf8
    Assert-Rejected { Read-NephyloManifest $signedInvalidUtf8 } 'Signed invalid UTF-8 is rejected.'
    $oversizePayload = New-Envelope (' ' * 4097)
    Assert-Rejected { Read-NephyloManifest $oversizePayload } 'Signed payload is bounded before deserialization.'

    # Stop at the assembly-loading boundary even if a future URI guard regresses.
    # These calls exercise the real origin/size guards without creating any transport.
    $script:transportReached = $false
    function Add-Type { $script:transportReached = $true; throw 'Offline transport fence.' }
    foreach ($badUri in @(
        'http://nephylo.com/downloads/update.json',
        'https://other.example/downloads/update.json',
        'https://nephylo.com.other.example/downloads/update.json',
        'https://nephylo.com:444/downloads/update.json',
        'https://user@nephylo.com/downloads/update.json',
        'https://nephylo.com/downloads/update.json?key=value',
        'https://nephylo.com/downloads/update.json#fragment',
        'https://nephylo.com/other/update.json',
        'https://nephylo.com/downloads/Nephylo-Setup.exe',
        'https://nephylo.com/downloads/Nephylo-Setup-3.4.5.exe/extra'
    )) {
        Assert-Rejected { Receive-NephyloFile $badUri (Join-Path $testRoot 'guard.tmp') 16384 } 'Real download guard rejects an unofficial URI.'
        Assert-That (-not $script:transportReached) 'Invalid URI is rejected before any network transport can be created.'
    }
    foreach ($limits in @(@(0, 0), @(104857601, 0), @(256, -1), @(256, 257))) {
        Assert-Rejected { Receive-NephyloFile 'https://nephylo.com/downloads/update.json' (Join-Path $testRoot 'guard.tmp') $limits[0] $limits[1] } 'Real download guard rejects invalid byte limits.'
        Assert-That (-not $script:transportReached) 'Invalid byte limits are rejected before network transport.'
    }
    Remove-Item -LiteralPath Function:\Add-Type
    Assert-That ($source -match '\$handler\.AllowAutoRedirect\s*=\s*\$false') 'Real HTTP transport explicitly rejects redirects.'
    Assert-That ($source -match '\$handler\.UseCookies\s*=\s*\$false') 'Real HTTP transport does not send or store cookies.'
    Assert-That ($source -match '\$handler\.CheckCertificateRevocationList\s*=\s*\$true') 'Real HTTP transport verifies certificate revocation.'
    Assert-That ($source -match '-ArgumentList\s+\(''/LANG=''\s*\+\s*\$Language\)') 'Setup launcher passes the selected language to the official installer.'

    # All subsequent installer flows remain offline and cannot spawn an installer.
    function Receive-NephyloFile([string]$Uri, [string]$Destination, [long]$MaxBytes, [long]$ExpectedBytes = 0) {
        $parsedUri = New-Object Uri($Uri)
        if ($parsedUri.Scheme -cne 'https' -or $parsedUri.Host -cne 'nephylo.com' -or $parsedUri.Port -ne 443 -or $parsedUri.UserInfo -or $parsedUri.Query -or $parsedUri.Fragment) { throw 'Fixture download URL must remain on the fixed HTTPS origin.' }
        $resolved = [IO.Path]::GetFullPath($Destination)
        $parent = [IO.Path]::GetDirectoryName($resolved)
        if (-not $parent.StartsWith($temporaryRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture download destination must remain in the temporary directory.' }
        $script:createdDirectories.Add($parent) | Out-Null
        [IO.Directory]::CreateDirectory($parent) | Out-Null
        $script:downloadCalls.Add([pscustomobject]@{ Uri = $Uri; Destination = $resolved; MaxBytes = $MaxBytes; ExpectedBytes = $ExpectedBytes })
        if ($parsedUri.AbsolutePath -ceq '/downloads/update.json') {
            [IO.File]::WriteAllText($resolved, $script:mockEnvelope, $utf8)
            return
        }
        if ($parsedUri.AbsolutePath -cne '/downloads/Nephylo-Setup-3.4.5.exe') { throw 'Fixture setup URL must use the signed immutable filename.' }
        $bytes = [byte[]]$script:fixtureBytes.Clone()
        switch ($script:mockDownloadMode) {
            'tamper' { $bytes[$bytes.Length - 1] = $bytes[$bytes.Length - 1] -bxor 1 }
            'truncated' { $bytes = [byte[]]$bytes[0..($bytes.Length - 2)] }
            'network-error' { throw 'Offline fixture transport error.' }
        }
        [IO.File]::WriteAllBytes($resolved, $bytes)
    }
    function Start-NephyloSetup([string]$Path, [ValidateSet('ko','en')][string]$Language = 'ko') {
        $script:launchCalls.Add([IO.Path]::GetFullPath($Path))
        $script:launchLanguages.Add($Language)
        $unexpectedWritableHandle = $null
        try { $unexpectedWritableHandle = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite) }
        catch [IO.IOException] { $script:verifiedFileLockObserved = $true }
        finally { if ($unexpectedWritableHandle) { $unexpectedWritableHandle.Dispose() } }
        return $script:fixtureSetupExitCode
    }

    Set-Fixture $pe
    $downloadResult = Invoke-NephyloWebInstall -DownloadOnly -Language ko
    Assert-That ($script:launchCalls.Count -eq 0) 'DownloadOnly never launches an installer.'
    Assert-That ($script:downloadCalls.Count -eq 2) 'A successful install downloads only the signed metadata and immutable setup.'
    Assert-That ($script:downloadCalls[0].Uri -ceq 'https://nephylo.com/downloads/update.json') 'Metadata uses the fixed official HTTPS endpoint.'
    Assert-That ($script:downloadCalls[1].Uri -ceq 'https://nephylo.com/downloads/Nephylo-Setup-3.4.5.exe') 'Setup uses the signed versioned filename.'
    Assert-That ($script:downloadCalls[0].MaxBytes -eq 16384 -and $script:downloadCalls[1].MaxBytes -le 104857600 -and $script:downloadCalls[1].ExpectedBytes -eq 256) 'Transport receives bounded metadata and exact signed installer length.'
    $downloadOnlyPath = $script:downloadCalls[1].Destination
    Assert-That ((Test-Path -LiteralPath $downloadOnlyPath) -and (Get-FileHash -LiteralPath $downloadOnlyPath -Algorithm SHA256).Hash.ToLowerInvariant() -ceq (Get-Digest $pe)) 'DownloadOnly retains the verified setup file.'
    Assert-That ($downloadResult.Version -ceq '3.4.5' -and $downloadResult.InstallerPath -ceq $downloadOnlyPath -and -not $downloadResult.InstallStarted -and $null -eq $downloadResult.ExitCode) 'DownloadOnly reports the verified path without claiming installation.'

    Set-Fixture $pe
    $installResult = Invoke-NephyloWebInstall -Language en
    Assert-That ($script:launchCalls.Count -eq 1) 'Default flow launches exactly once after verification.'
    Assert-That ($script:launchCalls[0] -ceq $script:downloadCalls[1].Destination) 'Launch target is exactly the downloaded verified file.'
    Assert-That ($script:launchLanguages.Count -eq 1 -and $script:launchLanguages[0] -ceq 'en') 'English flow forwards English to the setup launcher.'
    Assert-That ($installResult.InstallStarted -and $installResult.ExitCode -eq 0 -and $installResult.Bytes -eq 256 -and $installResult.Sha256 -ceq (Get-Digest $pe)) 'Default flow reports verified identity and successful setup exit.'
    Assert-That $script:verifiedFileLockObserved 'Verified setup remains locked against writes while the setup helper runs.'
    Set-Fixture $pe
    $koreanInstallResult = Invoke-NephyloWebInstall -Language ko
    Assert-That ($script:launchCalls.Count -eq 1 -and $koreanInstallResult.InstallStarted -and $script:launchLanguages.Count -eq 1 -and $script:launchLanguages[0] -ceq 'ko') 'Korean flow forwards Korean to the setup launcher.'
    Set-Fixture $pe
    $script:fixtureSetupExitCode = 1
    Assert-Rejected { Invoke-NephyloWebInstall -Language en } 'Nonzero setup exit is reported as failure.'
    Assert-That ($script:launchCalls.Count -eq 1) 'Failed setup is not launched repeatedly.'
    $script:fixtureSetupExitCode = 0
    foreach ($mode in @('tamper', 'truncated', 'network-error')) {
        Set-Fixture $pe $mode
        Assert-Rejected { Invoke-NephyloWebInstall -Language ko } ($mode + ' setup fails before launch.')
        Assert-That ($script:launchCalls.Count -eq 0) ($mode + ' setup never launches.')
    }
    foreach ($badHeader in @('MZ', 'PE', 'PE-offset-before-header', 'PE-offset-past-file')) {
        $bytes = [byte[]]$pe.Clone()
        switch ($badHeader) {
            'MZ' { $bytes[0] = 0 }
            'PE' { $bytes[64] = 0 }
            'PE-offset-before-header' { [BitConverter]::GetBytes([int]32).CopyTo($bytes, 0x3c) }
            'PE-offset-past-file' { [BitConverter]::GetBytes([int]255).CopyTo($bytes, 0x3c) }
        }
        Set-Fixture $bytes
        Assert-Rejected { Invoke-NephyloWebInstall -DownloadOnly } ('Correctly signed setup with invalid ' + $badHeader + ' is rejected.')
        Assert-That ($script:launchCalls.Count -eq 0) ('Invalid ' + $badHeader + ' file never launches.')
    }
    Set-Fixture $pe
    $script:mockEnvelope = $tamperedPayload
    Assert-Rejected { Invoke-NephyloWebInstall } 'Invalid metadata aborts before an installer download.'
    Assert-That ($script:downloadCalls.Count -eq 1 -and $script:launchCalls.Count -eq 0) 'Invalid metadata cannot trigger a setup download or launch.'
    Write-Output ('PowerShell web installer offline checks passed: ' + $assertions + ' assertions; no network or actual installer execution.')
} finally {
    $rsa.Dispose()
    # Every created directory was observed from this fixture's download destinations.
    foreach ($directory in $script:createdDirectories) {
        $resolved = [IO.Path]::GetFullPath($directory)
        $name = [IO.Path]::GetFileName($resolved)
        if ([IO.Path]::GetDirectoryName($resolved) -cne $temporaryRoot -or $name -notmatch '^nephylo-(web-)?install-[a-f0-9]{32}$') { throw ('Unsafe installer fixture cleanup path: ' + $name) }
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    if ([IO.Path]::GetDirectoryName($resolvedTestRoot) -cne $temporaryRoot -or [IO.Path]::GetFileName($resolvedTestRoot) -notmatch '^nephylo-web-install-tests-[a-f0-9]{32}$') { throw 'Unsafe test fixture cleanup path.' }
    if (Test-Path -LiteralPath $resolvedTestRoot) { Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force }
}
