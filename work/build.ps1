param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'build'),[string]$MembershipConfig = '')
$ErrorActionPreference = 'Stop'
$sourceDirectory = Join-Path $PSScriptRoot 'src'
$foreignSources = @(Get-ChildItem -LiteralPath $sourceDirectory -Filter 'Studio*.cs' -File)
if ($foreignSources.Count -ne 0) {
    throw 'widget-only-build: ADE source belongs in its separate product repository.'
}
$frameworkDirectory = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkDirectory 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework compiler not found.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$iconPath = Join-Path $OutputDirectory 'widget.ico'
& (Join-Path $PSScriptRoot 'generate-app-icon.ps1') -OutputDirectory $OutputDirectory
$references = @('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Security.dll','System.Net.Http.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xaml.dll')
$arguments = @('/nologo','/target:winexe','/platform:anycpu','/optimize+','/utf8output',('/out:' + (Join-Path $OutputDirectory 'UsageWidget.exe')),('/win32manifest:' + (Join-Path $sourceDirectory 'app.manifest')),('/win32icon:' + $iconPath),('/resource:' + $iconPath + ',UsageWidget.AppIcon.ico'))
$updatePublicKey = Join-Path $PSScriptRoot 'assets/update-signing-public.xml'
if (-not (Test-Path -LiteralPath $updatePublicKey -PathType Leaf)) { throw 'Missing update signing public key. Initialize the release signing key before building.' }
if ((Get-Item -LiteralPath $updatePublicKey).Length -gt 8192) { throw 'Unexpected update public key size.' }
$updateKeyXml = [IO.File]::ReadAllText($updatePublicKey)
if ($updateKeyXml -cnotmatch '\A\s*<RSAKeyValue>\s*<Modulus>[A-Za-z0-9+/=]+</Modulus>\s*<Exponent>[A-Za-z0-9+/=]+</Exponent>\s*</RSAKeyValue>\s*\z') { throw 'The embedded update key must contain only canonical public RSA fields.' }
$updateKeyDocument = New-Object Xml.XmlDocument
$updateKeyDocument.XmlResolver = $null
$updateKeyDocument.LoadXml($updateKeyXml)
$updateKeyFields = @($updateKeyDocument.DocumentElement.ChildNodes | Where-Object { $_.NodeType -eq [Xml.XmlNodeType]::Element })
if ($updateKeyDocument.DocumentElement.Name -ne 'RSAKeyValue' -or $updateKeyFields.Count -ne 2 -or @($updateKeyFields | Where-Object { $_.Name -notin @('Modulus','Exponent') }).Count -ne 0 -or @($updateKeyFields | Select-Object -ExpandProperty Name -Unique).Count -ne 2) { throw 'The embedded update key must contain only public Modulus and Exponent.' }
$arguments += '/resource:' + $updatePublicKey + ',UsageWidget.UpdateSigningPublicKey.xml'
. (Join-Path $PSScriptRoot 'membership/configuration.ps1')
if (-not $MembershipConfig) {
    # Accountless builds have no runtime dependency on a Nephylo auth server.
    # An explicit configuration remains validated for isolated legacy tests.
    $MembershipConfig = Join-Path $PSScriptRoot 'membership/client-config.example.json'
}
$membershipPublic = Read-MembershipPublicConfiguration $MembershipConfig
$embeddedConfig = Join-Path $OutputDirectory 'membership-public.json'
[IO.File]::WriteAllText($embeddedConfig,($membershipPublic | ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))
$arguments += '/resource:' + $embeddedConfig + ',UsageWidget.MembershipConfig.json'
foreach ($reference in $references) { $arguments += '/reference:' + (Join-Path $frameworkDirectory $reference) }
foreach ($reference in @('PresentationFramework.dll','PresentationCore.dll','WindowsBase.dll')) { $arguments += '/reference:' + (Join-Path $frameworkDirectory ('WPF\' + $reference)) }
$arguments += @(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' | ForEach-Object { $_.FullName })
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Copy-Item -LiteralPath (Join-Path $sourceDirectory 'UsageWidget.exe.config') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install.cmd') -Destination $OutputDirectory -Force
Write-Output ('Built: ' + (Join-Path $OutputDirectory 'UsageWidget.exe'))
