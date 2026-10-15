$ErrorActionPreference = 'Stop'
$source = Split-Path -Parent $PSScriptRoot
$signer = Join-Path $source 'sign-release.ps1'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('NephyloReleaseSigningTests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporary) | Out-Null
$checks = 0
function Assert-Release([bool]$Value,[string]$Name) { $script:checks++; if (-not $Value) { throw $Name } }
function Expect-Rejected([scriptblock]$Operation,[string]$Name) { $failed=$false; try { & $Operation | Out-Null } catch { $failed=$true }; Assert-Release $failed $Name }
function Get-Envelope([string]$Path) { return [IO.File]::ReadAllText($Path) | ConvertFrom-Json }
function Verify-Envelope($Envelope,[Security.Cryptography.RSACryptoServiceProvider]$Rsa) {
    return $Rsa.VerifyData([Convert]::FromBase64String($Envelope.payloadBase64),[Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'),[Convert]::FromBase64String($Envelope.signatureBase64))
}
try {
    $public = Join-Path $temporary 'public.xml'; $private = Join-Path $temporary 'private.xml.dpapi'
    & $signer -InitializeOnly -PublicKeyPath $public -PrivateKeyPath $private | Out-Null
    $privateOriginal = (Get-FileHash -LiteralPath $private -Algorithm SHA256).Hash
    $publicOriginal = [IO.File]::ReadAllText($public)
    & $signer -InitializeOnly -PublicKeyPath $public -PrivateKeyPath $private | Out-Null
    Assert-Release ((Get-FileHash -LiteralPath $private -Algorithm SHA256).Hash -eq $privateOriginal) 'Reinitialization must preserve the original signing key.'
    Assert-Release ($publicOriginal -notmatch '<(?:D|P|Q|DP|DQ|InverseQ)>') 'Public key must not contain private XML fields.'
    Assert-Release (-not [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($private)).Contains('RSAKeyValue')) 'Private key is encrypted at rest.'
    $rsa = New-Object Security.Cryptography.RSACryptoServiceProvider
    $rsa.PersistKeyInCsp = $false
    $rsa.FromXmlString($publicOriginal)
    Assert-Release ($rsa.PublicOnly -and $rsa.KeySize -eq 2048) 'Public asset contains only the RSA 2048-bit verification key.'

    $installer = Join-Path $temporary 'Nephylo-Setup-1.21.0.exe'
    [byte[]]$fixture = New-Object byte[] 256
    $fixture[0]=0x4d; $fixture[1]=0x5a; $fixture[0x3c]=0x80; $fixture[0x80]=0x50; $fixture[0x81]=0x45
    [IO.File]::WriteAllBytes($installer,$fixture)
    $manifest = Join-Path $temporary 'update.json'
    & $signer -Version '1.21.0' -InstallerPath $installer -OutputPath $manifest -PublicKeyPath $public -PrivateKeyPath $private | Out-Null
    $envelope = Get-Envelope $manifest
    Assert-Release (($envelope.PSObject.Properties.Name | Sort-Object) -join ',' -eq 'payloadBase64,signatureBase64') 'Envelope exposes only the two signed fields.'
    Assert-Release (Verify-Envelope $envelope $rsa) 'New metadata verifies with the checked public key.'
    $originalManifest = [IO.File]::ReadAllText($manifest)
    [IO.File]::WriteAllText($manifest, '{"interrupted":true}')
    & $signer -Version '1.21.0' -InstallerPath $installer -OutputPath $manifest -PublicKeyPath $public -PrivateKeyPath $private | Out-Null
    Assert-Release ([IO.File]::ReadAllText($manifest) -ceq $originalManifest) 'Re-signing atomically replaces an existing output with valid metadata.'
    Assert-Release (Verify-Envelope (Get-Envelope $manifest) $rsa) 'Replaced metadata retains a valid release signature.'
    $payload = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($envelope.payloadBase64)) | ConvertFrom-Json
    Assert-Release ($payload.schemaVersion -eq 1 -and $payload.version -ceq '1.21.0' -and $payload.fileName -ceq 'Nephylo-Setup-1.21.0.exe') 'Payload schema, version and immutable filename match.'
    Assert-Release ($payload.bytes -eq (Get-Item -LiteralPath $installer).Length -and $payload.sha256 -ceq (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()) 'Payload size and digest bind the exact installer artifact.'
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    $probe = Join-Path $temporary 'release-signing-probe.exe'
    & $compiler /nologo /langversion:5 /target:exe /r:System.Net.Http.dll /r:System.Web.Extensions.dll ("/out:" + $probe) ("/resource:" + $public + ',UsageWidget.UpdateSigningPublicKey.xml') (Join-Path $source 'src/AutoUpdateClient.cs') (Join-Path $PSScriptRoot 'release-signing-probe.cs')
    Assert-Release ($LASTEXITCODE -eq 0) 'Application verification probe compiles with the temporary embedded public resource.'
    & $probe $manifest $installer
    Assert-Release ($LASTEXITCODE -eq 0) 'The actual C# updater parser verifies the PowerShell signature and bound installer.'
    [byte[]]$changedPayload = [Convert]::FromBase64String($envelope.payloadBase64)
    $changedPayload[$changedPayload.Length - 2] = $changedPayload[$changedPayload.Length - 2] -bxor 1
    $tampered = [pscustomobject]@{payloadBase64=[Convert]::ToBase64String($changedPayload);signatureBase64=$envelope.signatureBase64}
    Assert-Release (-not (Verify-Envelope $tampered $rsa)) 'Changed payload cannot verify.'
    [byte[]]$changedSignature = [Convert]::FromBase64String($envelope.signatureBase64); $changedSignature[0] = $changedSignature[0] -bxor 1
    $tampered.signatureBase64=[Convert]::ToBase64String($changedSignature); $tampered.payloadBase64=$envelope.payloadBase64
    Assert-Release (-not (Verify-Envelope $tampered $rsa)) 'Changed signature cannot verify.'
    $fixture[255]=1; [IO.File]::WriteAllBytes($installer,$fixture)
    Assert-Release ($payload.sha256 -cne (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()) 'An installer changed after signing fails the bound digest.'
    & $probe $manifest $installer
    Assert-Release ($LASTEXITCODE -ne 0) 'The actual C# updater rejects an installer changed after signing.'
    foreach ($version in @('01.21.0','1.21','1.21.0.0','65536.0.0','-1.0.0')) {
        Expect-Rejected { & $signer -Version $version -InstallerPath $installer -OutputPath $manifest -PublicKeyPath $public -PrivateKeyPath $private } 'Noncanonical release version is rejected.'
    }
    $otherPublic = Join-Path $temporary 'other-public.xml'; $otherPrivate = Join-Path $temporary 'other-private.xml.dpapi'
    & $signer -InitializeOnly -PublicKeyPath $otherPublic -PrivateKeyPath $otherPrivate | Out-Null
    Expect-Rejected { & $signer -InitializeOnly -PublicKeyPath $public -PrivateKeyPath $otherPrivate } 'A different private key cannot replace the checked public key.'
    Expect-Rejected { & $signer -InitializeOnly -PublicKeyPath $public -PrivateKeyPath (Join-Path $temporary 'missing-private.dpapi') } 'A missing private key cannot regenerate an existing public key.'
    Assert-Release ([IO.File]::ReadAllText($public) -ceq $publicOriginal) 'Rejected key changes preserve the checked public key.'
    $unsafePublic = Join-Path $temporary 'unsafe-public.xml'
    [IO.File]::WriteAllText($unsafePublic,$publicOriginal.Replace('</RSAKeyValue>','<D>private</D></RSAKeyValue>'))
    Expect-Rejected { & $signer -InitializeOnly -PublicKeyPath $unsafePublic -PrivateKeyPath $private } 'Public assets must reject private XML fields.'
    [IO.File]::WriteAllText($unsafePublic,'<!-- private material -->' + $publicOriginal)
    Expect-Rejected { & $signer -InitializeOnly -PublicKeyPath $unsafePublic -PrivateKeyPath $private } 'Public assets must reject comments and undeclared extra content.'
    Expect-Rejected { & $signer -Version '1.21.1' -InstallerPath $installer -OutputPath $manifest -PublicKeyPath $public -PrivateKeyPath $private } 'Installer filename must be bound to the selected version.'
    Expect-Rejected { & $signer -Version '1.21.0' -InstallerPath $installer -OutputPath $installer -PublicKeyPath $public -PrivateKeyPath $private } 'Metadata cannot overwrite the installer.'
    $fixture[0]=0; [IO.File]::WriteAllBytes($installer,$fixture)
    Expect-Rejected { & $signer -Version '1.21.0' -InstallerPath $installer -OutputPath $manifest -PublicKeyPath $public -PrivateKeyPath $private } 'Non-PE files cannot be signed as installers.'
    Write-Output ('PASS: ' + $checks + ' signed release checks (DPAPI key custody, matching public key, exact artifact digest, tamper detection, canonical metadata).')
} finally {
    if ($rsa) { $rsa.Dispose() }
    $resolved = [IO.Path]::GetFullPath($temporary)
    $expectedRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($expectedRoot,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('NephyloReleaseSigningTests-') -and (Test-Path -LiteralPath $resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
