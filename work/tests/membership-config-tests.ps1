$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../membership/configuration.ps1')
$fixture = Join-Path $PSScriptRoot ('membership-config-' + [Guid]::NewGuid().ToString('N') + '.tmp')
$checks = 0
function Read-Fixture($Value) {
    [IO.File]::WriteAllText($fixture,($Value | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    Read-MembershipPublicConfiguration $fixture
}
function Assert-Fixture($Value, [bool]$ExpectedGoogle, [bool]$ExpectedEmail = $true) {
    $result = Read-Fixture $Value
    if ($result.GoogleEnabled -ne $ExpectedGoogle) { throw 'Unexpected Google feature configuration.' }
    if ($result.EmailEnabled -ne $ExpectedEmail) { throw 'Unexpected email feature configuration.' }
    if (@($result.Keys).Count -ne 4) { throw 'Public config returned unexpected fields.' }
    $script:checks++
}
function Reject-Fixture($Value) {
    $rejected = $false
    try { $null = Read-Fixture $Value } catch { $rejected = $true }
    if (-not $rejected) { throw 'Invalid public configuration was accepted.' }
    $script:checks++
}
try {
    Assert-Fixture @{SupabaseUrl='';PublishableKey=''} $false
    Assert-Fixture @{SupabaseUrl='';PublishableKey='';GoogleEnabled=$false} $false
    Assert-Fixture @{SupabaseUrl='https://fixture.supabase.co';PublishableKey='sb_publishable_fixture_only'} $false
    Assert-Fixture @{SupabaseUrl='https://fixture.supabase.co';PublishableKey='sb_publishable_fixture_only';GoogleEnabled=$true} $true
    Assert-Fixture @{SupabaseUrl='https://fixture.supabase.co';PublishableKey='sb_publishable_fixture_only';GoogleEnabled=$false;EmailEnabled=$false} $false $false
    Assert-Fixture @{SupabaseUrl='https://fixture.supabase.co';PublishableKey='sb_publishable_fixture_only';GoogleEnabled=$true;EmailEnabled=$false} $true $false
    Assert-Fixture @{SupabaseUrl='';PublishableKey='';GoogleEnabled=$false;EmailEnabled=$false} $false $false
    foreach ($invalid in @('true','false',1,0,$null)) {
        Reject-Fixture @{SupabaseUrl='https://fixture.supabase.co';PublishableKey='sb_publishable_fixture_only';GoogleEnabled=$invalid}
        Reject-Fixture @{SupabaseUrl='https://fixture.supabase.co';PublishableKey='sb_publishable_fixture_only';EmailEnabled=$invalid}
    }
    Reject-Fixture @{SupabaseUrl='';PublishableKey='';GoogleEnabled=$true}
    Reject-Fixture @{SupabaseUrl='https://fixture.supabase.co';PublishableKey='sb_secret_fixture_only';GoogleEnabled=$true}
    Reject-Fixture @{SupabaseUrl='https://fixture.supabase.co';PublishableKey='sb_publishable_fixture_only';GoogleClientSecret='not_allowed'}
    Reject-Fixture @{SupabaseUrl='https://fixture.supabase.co';PublishableKey='sb_publishable_fixture_only';GoogleEnabled=$false;EmailEnabled=$false;SmtpPassword='not_allowed'}
    Reject-Fixture @{SupabaseUrl='https://fixture.supabase.co/path';PublishableKey='sb_publishable_fixture_only';GoogleEnabled=$true}
    Reject-Fixture @{SupabaseUrl='https://user:password@fixture.supabase.co';PublishableKey='sb_publishable_fixture_only';GoogleEnabled=$true}
    Write-Output "PASS: $checks public membership configuration checks. No account or network used."
} finally { if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Force } }
