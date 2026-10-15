function Read-MembershipPublicConfiguration([string]$Path) {
    $value = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $names = @($value.PSObject.Properties.Name)
    if ($names.Count -lt 2 -or $names.Count -gt 4 -or $names -notcontains 'SupabaseUrl' -or $names -notcontains 'PublishableKey' -or @($names | Where-Object { $_ -notin @('SupabaseUrl','PublishableKey','GoogleEnabled','EmailEnabled') }).Count) { throw 'Membership configuration allows only SupabaseUrl, PublishableKey, and optional GoogleEnabled and EmailEnabled.' }
    $googleEnabled = $false
    if ($names -contains 'GoogleEnabled') {
        if ($value.GoogleEnabled -isnot [bool]) { throw 'GoogleEnabled must be a JSON boolean.' }
        $googleEnabled = $value.GoogleEnabled
    }
    $emailEnabled = $true
    if ($names -contains 'EmailEnabled') {
        if ($value.EmailEnabled -isnot [bool]) { throw 'EmailEnabled must be a JSON boolean.' }
        $emailEnabled = $value.EmailEnabled
    }
    $url = [string]$value.SupabaseUrl
    $key = [string]$value.PublishableKey
    if (-not $url -and -not $key) {
        if ($googleEnabled) { throw 'Configure the Supabase project before enabling Google login.' }
        return [ordered]@{SupabaseUrl='';PublishableKey='';GoogleEnabled=$false;EmailEnabled=$emailEnabled}
    }
    $uri = $null
    if (-not [Uri]::TryCreate($url,[UriKind]::Absolute,[ref]$uri) -or $uri.Scheme -ne 'https' -or $uri.IsLoopback -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.AbsolutePath -ne '/') { throw 'Use the HTTPS Supabase project origin, without a path, query, or credentials.' }
    $isPublic = $key -match '^sb_publishable_[A-Za-z0-9_-]{8,}$'
    if (-not $isPublic -and $key -match '^[A-Za-z0-9_-]+\.([A-Za-z0-9_-]+)\.[A-Za-z0-9_-]+$') {
        try {
            $payload = $Matches[1].Replace('-','+').Replace('_','/')
            $payload = $payload.PadRight($payload.Length + ((4 - $payload.Length % 4) % 4),'=')
            $claims = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) | ConvertFrom-Json
            $isPublic = $claims.role -eq 'anon'
        } catch { $isPublic = $false }
    }
    if (-not $isPublic) { throw 'Only a publishable key or legacy anon key is allowed. Never use a secret or service_role key.' }
    return [ordered]@{SupabaseUrl=$uri.GetLeftPart([UriPartial]::Authority);PublishableKey=$key;GoogleEnabled=$googleEnabled;EmailEnabled=$emailEnabled}
}
