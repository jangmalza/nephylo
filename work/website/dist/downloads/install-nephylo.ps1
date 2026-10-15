# Nephylo web installer. Windows PowerShell 5.1 / PowerShell 7 on Windows.
# Inspect this code before running. It does not change Windows security settings.
[CmdletBinding()]
param([switch]$DownloadOnly, [ValidateSet('ko','en')][string]$Language = 'ko')

function Get-NephyloSigningKeyXml {
    return '<RSAKeyValue><Modulus>40lqzpT1+ERhlW7rt3X4ptu877W5Rn2ulQRj6GeKtLZNBjOppKJKg6Y3RejlzjgvqE8HwH2dam8bTVjz6NRspBhAH2CwdbSi2XzbKlD/TbtmADC8HvvuVTDGQKL0iRMod6fgXHKZS56qeWx2FFf0/GKcLrDWGd+B9cDdiR7grYERKMysdUhF12wVD2KcDoXnV6LwyJIkRDL/VQrL7coS8iPm4Shydh503wjpAvsmzFSFjVrYhPw4F5PtoxyBsG7RKgk+fLayoWMxe233surDuhax1Bv+3csHNGA2V32EvyC/pL007YSxbU1Jfhjxjg2ZxIPvDvwAFK4TguXQiMU14Q==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>'
}

function Read-NephyloJsonObject([string]$Json, [string[]]$Fields) {
    if ($Json -notmatch '\A\s*\{' -or $Json -notmatch '\}\s*\z') { throw 'Invalid release JSON object.' }
    # Our signed schema has no nested objects or escaped property names.
    # Count raw keys as well as parsed fields so duplicate keys cannot collapse.
    $keys = [regex]::Matches($Json, '"(?<name>(?:\\.|[^"\\])*)"\s*:')
    if ($keys.Count -ne $Fields.Count) { throw 'Unexpected or duplicate release fields.' }
    foreach ($key in $keys) { if ($Fields -cnotcontains $key.Groups['name'].Value) { throw 'Unexpected release field.' } }
    $value = ConvertFrom-Json -InputObject $Json -ErrorAction Stop
    $names = @($value.PSObject.Properties.Name)
    if ($names.Count -ne $Fields.Count -or (Compare-Object -CaseSensitive ($names | Sort-Object) ($Fields | Sort-Object))) { throw 'Unexpected release fields.' }
    return $value
}

function Read-NephyloManifest([string]$Json) {
    if ([string]::IsNullOrEmpty($Json) -or [Text.Encoding]::UTF8.GetByteCount($Json) -gt 16384) { throw 'Release information is too large.' }
    $envelope = Read-NephyloJsonObject $Json @('payloadBase64','signatureBase64')
    if ($envelope.payloadBase64 -isnot [string] -or $envelope.signatureBase64 -isnot [string]) { throw 'Invalid release signature.' }
    $payload = [Convert]::FromBase64String($envelope.payloadBase64)
    $signature = [Convert]::FromBase64String($envelope.signatureBase64)
    if ($payload.Length -lt 1 -or $payload.Length -gt 4096 -or $signature.Length -ne 256 -or
        [Convert]::ToBase64String($payload) -cne $envelope.payloadBase64 -or [Convert]::ToBase64String($signature) -cne $envelope.signatureBase64) { throw 'Invalid release signature encoding.' }
    $parameters = [Security.Cryptography.CspParameters]::new()
    $parameters.ProviderType = 24
    $parameters.Flags = [Security.Cryptography.CspProviderFlags]::CreateEphemeralKey
    $rsa = [Security.Cryptography.RSACryptoServiceProvider]::new(2048, $parameters)
    try {
        $rsa.PersistKeyInCsp = $false
        $rsa.FromXmlString((Get-NephyloSigningKeyXml))
        if ($rsa.KeySize -ne 2048 -or -not $rsa.VerifyData($payload, [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'), $signature)) { throw 'Release signature verification failed.' }
    } finally { $rsa.Dispose() }
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($payload)
    $value = Read-NephyloJsonObject $text @('schemaVersion','version','fileName','bytes','sha256')
    if (($value.schemaVersion -isnot [int] -and $value.schemaVersion -isnot [long]) -or $value.schemaVersion -ne 1) { throw 'Unsupported release schema.' }
    if ($value.version -isnot [string] -or $value.version -cnotmatch '\A(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})\z' -or
        @($value.version.Split('.') | Where-Object { [int]$_ -gt 65535 }).Count -gt 0) { throw 'Invalid release version.' }
    if ($value.fileName -isnot [string] -or $value.fileName -cne ('Nephylo-Setup-' + $value.version + '.exe')) { throw 'Invalid installer filename.' }
    if (($value.bytes -isnot [int] -and $value.bytes -isnot [long]) -or $value.bytes -lt 64 -or $value.bytes -gt 104857600) { throw 'Invalid installer size.' }
    if ($value.sha256 -isnot [string] -or $value.sha256 -cnotmatch '\A[a-f0-9]{64}\z') { throw 'Invalid installer digest.' }
    return $value
}

function Assert-NephyloLocalPath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (([IO.File]::Exists($current) -or [IO.Directory]::Exists($current)) -and
            (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw 'A linked installation download path is not supported.' }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
}

function Receive-NephyloFile([string]$Uri, [string]$Destination, [long]$MaxBytes, [long]$ExpectedBytes = 0) {
    $address = [uri]$Uri
    if ($address.Scheme -cne 'https' -or $address.Host -cne 'nephylo.com' -or $address.Port -ne 443 -or $address.UserInfo -or $address.Query -or $address.Fragment -or
        $address.AbsolutePath -cnotmatch '\A/downloads/(?:update\.json|Nephylo-Setup-[0-9]+\.[0-9]+\.[0-9]+\.exe)\z') { throw 'Only official Nephylo downloads are supported.' }
    if ($MaxBytes -lt 1 -or $MaxBytes -gt 104857600 -or $ExpectedBytes -lt 0 -or $ExpectedBytes -gt $MaxBytes) { throw 'Invalid download limit.' }
    Assert-NephyloLocalPath $Destination
    Add-Type -AssemblyName System.Net.Http -ErrorAction Stop
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseCookies = $false
    $handler.CheckCertificateRevocationList = $true
    $handler.AutomaticDecompression = [Net.DecompressionMethods]::None
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(30)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('Nephylo-PowerShell-Installer/1.0')
    $client.DefaultRequestHeaders.CacheControl = [Net.Http.Headers.CacheControlHeaderValue]::new()
    $client.DefaultRequestHeaders.CacheControl.NoCache = $true
    $client.DefaultRequestHeaders.CacheControl.NoStore = $true
    $response = $null; $inputStream = $null; $outputStream = $null
    $deadlineCancellation = [Threading.CancellationTokenSource]::new()
    $deadline = [Threading.Tasks.Task]::Delay(180000, $deadlineCancellation.Token)
    try {
        $response = $client.GetAsync($address, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        if ([int]$response.StatusCode -ne 200 -or $response.Content.Headers.ContentEncoding.Count -gt 0) { throw 'The download response was not accepted.' }
        $length = $response.Content.Headers.ContentLength
        if ($null -ne $length -and ($length -gt $MaxBytes -or ($ExpectedBytes -gt 0 -and $length -ne $ExpectedBytes))) { throw 'Unexpected download size.' }
        $inputStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $outputStream = [IO.FileStream]::new($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $buffer = [byte[]]::new(65536); [long]$written = 0
        while ($true) {
            $read = $inputStream.ReadAsync($buffer, 0, $buffer.Length)
            $finished = [Threading.Tasks.Task]::WhenAny([Threading.Tasks.Task[]]@($read, $deadline)).GetAwaiter().GetResult()
            if ([object]::ReferenceEquals($finished, $deadline)) { throw 'The download timed out.' }
            $count = $read.GetAwaiter().GetResult()
            if ($count -eq 0) { break }
            $written += $count
            if ($written -gt $MaxBytes) { throw 'The download exceeded its size limit.' }
            $outputStream.Write($buffer, 0, $count)
        }
        if ($written -eq 0 -or ($ExpectedBytes -gt 0 -and $written -ne $ExpectedBytes)) { throw 'The download was incomplete.' }
        $outputStream.Flush($true)
    } finally {
        $deadlineCancellation.Cancel(); $deadlineCancellation.Dispose()
        if ($outputStream) { $outputStream.Dispose() }
        if ($response) { $response.Dispose() }
        if ($inputStream) { $inputStream.Dispose() }
        $client.Dispose()
    }
}

function Start-NephyloSetup([string]$Path, [ValidateSet('ko','en')][string]$Language = 'ko') {
    $setup = Start-Process -FilePath $Path -ArgumentList ('/LANG=' + $Language) -PassThru
    try { $setup.WaitForExit(); return $setup.ExitCode } finally { $setup.Dispose() }
}

function Invoke-NephyloWebInstall([switch]$DownloadOnly, [ValidateSet('ko','en')][string]$Language = 'ko') {
    $ErrorActionPreference = 'Stop'
    if ($env:OS -ne 'Windows_NT') { throw 'Nephylo requires Windows.' }
    # TLS 1.2 remains enabled for Windows PowerShell; certificate checks stay enabled.
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $stage = [IO.Path]::Combine([IO.Path]::GetTempPath(), 'nephylo-install-' + [Guid]::NewGuid().ToString('N'))
    Assert-NephyloLocalPath $stage
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    $metadataPath = Join-Path $stage 'update.json'
    Write-Host $(if ($Language -eq 'en') { 'Checking the official Nephylo release...' } else { 'Nephylo 공식 배포 정보를 확인하고 있습니다...' })
    Receive-NephyloFile 'https://nephylo.com/downloads/update.json' $metadataPath 16384
    $json = [Text.UTF8Encoding]::new($false, $true).GetString([IO.File]::ReadAllBytes($metadataPath))
    $manifest = Read-NephyloManifest $json
    $installerPath = Join-Path $stage $manifest.fileName
    Write-Host $(if ($Language -eq 'en') { 'Downloading and verifying the installer...' } else { '설치 파일을 다운로드하고 검증하고 있습니다...' })
    Receive-NephyloFile ('https://nephylo.com/downloads/' + $manifest.fileName) $installerPath $manifest.bytes $manifest.bytes
    Assert-NephyloLocalPath $installerPath
    $stream = [IO.FileStream]::new($installerPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ($stream.Length -ne $manifest.bytes -or $stream.ReadByte() -ne 77 -or $stream.ReadByte() -ne 90) { throw 'Installer verification failed.' }
        $stream.Position = 60; $offset = [byte[]]::new(4)
        if ($stream.Read($offset, 0, 4) -ne 4) { throw 'Invalid installer header.' }
        $peOffset = [BitConverter]::ToUInt32($offset, 0)
        if ($peOffset -lt 64 -or $peOffset -gt $stream.Length - 4) { throw 'Invalid installer header.' }
        $stream.Position = $peOffset
        if ($stream.Read($offset, 0, 4) -ne 4 -or [BitConverter]::ToUInt32($offset, 0) -ne 17744) { throw 'Invalid installer header.' }
        $stream.Position = 0; $sha = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() } finally { $sha.Dispose() }
        if ($hash -cne $manifest.sha256) { throw 'Installer digest verification failed.' }
        Write-Host ('Nephylo ' + $manifest.version + ' / SHA-256 ' + $hash)
        $exitCode = $null
        if (-not $DownloadOnly) {
            Write-Host $(if ($Language -eq 'en') { 'Follow the setup window to finish installation. Windows security warnings may still appear.' } else { '설치 안내 창에서 설치를 마무리해 주세요. Windows 보안 경고가 나타날 수 있습니다.' })
            # Keep the verified file locked against replacement until Setup exits.
            $exitCode = Start-NephyloSetup $installerPath $Language
            if ($exitCode -ne 0) { throw 'Setup did not finish. Use the official EXE download to retry if needed.' }
        }
        return [pscustomobject]@{ Version = $manifest.version; InstallerPath = $installerPath; Sha256 = $hash; Bytes = $manifest.bytes; InstallStarted = (-not $DownloadOnly); ExitCode = $exitCode }
    } finally { $stream.Dispose() }
}

# Dot-sourcing exposes helpers for isolated tests without downloading or installing.
if ($MyInvocation.InvocationName -ne '.') { Invoke-NephyloWebInstall -DownloadOnly:$DownloadOnly -Language $Language }
