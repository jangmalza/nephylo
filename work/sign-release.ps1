param(
    [switch]$InitializeOnly,
    [string]$Version = '',
    [string]$InstallerPath = '',
    [string]$OutputPath = '',
    [string]$PublicKeyPath = (Join-Path $PSScriptRoot 'assets/update-signing-public.xml'),
    [string]$PrivateKeyPath = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'NephyloBuild/release-signing-key.xml.dpapi')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$utf8 = New-Object Text.UTF8Encoding($false)
$entropy = $utf8.GetBytes('Nephylo.ReleaseSigning.v1')
$privateFullPath = [IO.Path]::GetFullPath($PrivateKeyPath)
$workspacePath = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\') + '\'
if ($privateFullPath.StartsWith($workspacePath,[StringComparison]::OrdinalIgnoreCase) -or $privateFullPath -ieq [IO.Path]::GetFullPath($PublicKeyPath)) { throw 'The protected private signing key must remain outside the source workspace.' }

function New-ReleaseRsa {
    $parameters = New-Object Security.Cryptography.CspParameters
    $parameters.ProviderType = 24
    $parameters.Flags = [Security.Cryptography.CspProviderFlags]::CreateEphemeralKey
    $rsa = New-Object Security.Cryptography.RSACryptoServiceProvider(2048,$parameters)
    $rsa.PersistKeyInCsp = $false
    return $rsa
}

function Write-NewKeyFile([string]$Path,[byte[]]$Bytes) {
    $parent = Split-Path -Parent ([IO.Path]::GetFullPath($Path))
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    $stream = New-Object IO.FileStream($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $stream.Write($Bytes,0,$Bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
}

function Read-PublicReleaseXml([string]$Path) {
    if ((Get-Item -LiteralPath $Path).Length -gt 8192) { throw 'Unexpected public signing key size.' }
    $xml = [IO.File]::ReadAllText($Path)
    if ($xml -cnotmatch '\A\s*<RSAKeyValue>\s*<Modulus>[A-Za-z0-9+/=]+</Modulus>\s*<Exponent>[A-Za-z0-9+/=]+</Exponent>\s*</RSAKeyValue>\s*\z') { throw 'Public signing asset must contain only canonical public RSA fields.' }
    $document = New-Object Xml.XmlDocument
    $document.XmlResolver = $null
    $document.LoadXml($xml)
    if ($document.DocumentElement.Name -ne 'RSAKeyValue' -or $document.DocumentElement.Attributes.Count -ne 0) { throw 'Invalid public signing key.' }
    $fields = @($document.DocumentElement.ChildNodes | Where-Object { $_.NodeType -eq [Xml.XmlNodeType]::Element })
    if ($fields.Count -ne 2 -or @($fields | Where-Object { $_.Name -notin @('Modulus','Exponent') }).Count -ne 0 -or @($fields | Select-Object -ExpandProperty Name -Unique).Count -ne 2) { throw 'Public signing asset must contain only Modulus and Exponent.' }
    return $document.OuterXml
}

$rsa = $null
$privateBytes = $null
try {
    $publicExists = Test-Path -LiteralPath $PublicKeyPath -PathType Leaf
    $privateExists = Test-Path -LiteralPath $PrivateKeyPath -PathType Leaf
    if ($publicExists -and -not $privateExists) { throw 'The release signing private key is missing. Restore it under the original Windows user; do not generate a replacement for this public key.' }
    $rsa = New-ReleaseRsa
    if ($privateExists) {
        if ((Get-Item -LiteralPath $PrivateKeyPath).Length -gt 16384) { throw 'Unexpected protected signing key size.' }
        try {
            $privateBytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($PrivateKeyPath),$entropy,[Security.Cryptography.DataProtectionScope]::CurrentUser)
            $rsa.FromXmlString($utf8.GetString($privateBytes))
        } catch { throw 'Cannot unlock the release signing key for this Windows user.' }
        if ($rsa.PublicOnly -or $rsa.KeySize -ne 2048) { throw 'The signing key must be a private RSA 2048-bit key.' }
    } else {
        # This is the only creation path. No private XML is written to disk or output.
        $privateBytes = $utf8.GetBytes($rsa.ToXmlString($true))
        $protected = [Security.Cryptography.ProtectedData]::Protect($privateBytes,$entropy,[Security.Cryptography.DataProtectionScope]::CurrentUser)
        Write-NewKeyFile $PrivateKeyPath $protected
    }
    $publicXml = $rsa.ToXmlString($false)
    if ($publicExists) {
        $checked = New-ReleaseRsa
        try {
            $checked.FromXmlString((Read-PublicReleaseXml $PublicKeyPath))
            if ($checked.KeySize -ne 2048 -or $checked.ToXmlString($false) -cne $publicXml) { throw 'The private signing key does not match the checked-in public key. No release was signed.' }
        } finally { $checked.Dispose() }
    } else {
        Write-NewKeyFile $PublicKeyPath ($utf8.GetBytes($publicXml + "`n"))
    }
    if ($InitializeOnly) { Write-Output 'Release signing key is ready; public asset matches the protected private key.'; return }

    if ($Version -cnotmatch '^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$' -or @($Version.Split('.') | Where-Object { [int]$_ -gt 65535 }).Count -gt 0) { throw 'Invalid canonical release version.' }
    $expectedName = 'Nephylo-Setup-' + $Version + '.exe'
    if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf) -or [IO.Path]::GetFileName($InstallerPath) -cne $expectedName) { throw 'The immutable installer filename must match the release version.' }
    if ([string]::IsNullOrWhiteSpace($OutputPath)) { throw 'An update metadata output path is required.' }
    $installer = Get-Item -LiteralPath $InstallerPath
    if ($installer.Length -lt 64 -or $installer.Length -gt 104857600) { throw 'Installer size is outside the supported range.' }
    $stream = [IO.File]::Open($installer.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        $reader = New-Object IO.BinaryReader($stream)
        if ($reader.ReadUInt16() -ne 0x5a4d) { throw 'Installer must have a valid PE header.' }
        $stream.Position = 0x3c; $peOffset = $reader.ReadUInt32()
        if ($peOffset -lt 64 -or $peOffset -gt $stream.Length - 4) { throw 'Installer must have a valid PE header.' }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { throw 'Installer must have a valid PE header.' }
        $stream.Position = 0
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $digest = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
        $payload = [ordered]@{ schemaVersion=1; version=$Version; fileName=$expectedName; bytes=[long]$stream.Length; sha256=$digest }
        $payloadBytes = $utf8.GetBytes(($payload | ConvertTo-Json -Compress))
        $signature = $rsa.SignData($payloadBytes,[Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'))
        if (-not $rsa.VerifyData($payloadBytes,[Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'),$signature)) { throw 'Release signature verification failed.' }
        $envelope = [ordered]@{ payloadBase64=[Convert]::ToBase64String($payloadBytes); signatureBase64=[Convert]::ToBase64String($signature) }
        $destination = [IO.Path]::GetFullPath($OutputPath)
        if ($destination -ieq $installer.FullName -or $destination -ieq [IO.Path]::GetFullPath($PrivateKeyPath) -or $destination -ieq [IO.Path]::GetFullPath($PublicKeyPath)) { throw 'Metadata output must be separate from the installer and signing keys.' }
        [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
        $temporary = $destination + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        try {
            [IO.File]::WriteAllText($temporary,($envelope | ConvertTo-Json -Compress),$utf8)
            if ([IO.File]::Exists($destination)) { [IO.File]::Replace($temporary,$destination,[System.Management.Automation.Language.NullString]::Value) } else { [IO.File]::Move($temporary,$destination) }
        } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
        Write-Output ('Signed update metadata: ' + $Version + ' (' + $stream.Length + ' bytes).')
    } finally { $stream.Dispose() }
} finally {
    if ($privateBytes) { [Array]::Clear($privateBytes,0,$privateBytes.Length) }
    if ($rsa) { $rsa.Dispose() }
}
