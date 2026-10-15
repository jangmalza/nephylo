$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$scratch=Join-Path ([IO.Path]::GetTempPath()) ('NephyloLicenseTests-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch|Out-Null
$target=Join-Path $scratch 'THIRD-PARTY-NOTICES.txt'
try {
    & (Join-Path $repo 'work/compose-notices.ps1') -OutputPath $target
    $text=[IO.File]::ReadAllText($target)
    foreach($expected in @('Copyright (c) 2026 Nephylo contributors','Copyright (c) 2023 LobeHub','Permission is hereby granted','THE SOFTWARE IS PROVIDED')) {
        if(-not $text.Contains($expected)){throw 'A required project/third-party notice is missing.'}
    }
    $source=Join-Path $repo 'LICENSE';$hash=(Get-FileHash -LiteralPath $source).Hash
    $rejected=$false;try { & (Join-Path $repo 'work/compose-notices.ps1') -OutputPath $source } catch {$rejected=$true}
    if(-not $rejected -or (Get-FileHash -LiteralPath $source).Hash -ne $hash){throw 'Source license overwrite was not rejected.'}
    Write-Output 'PASS: project MIT and upstream MIT notices composed; source license remains protected.'
} finally {
    if(Test-Path -LiteralPath $target){Remove-Item -LiteralPath $target}
    if(Test-Path -LiteralPath $scratch){Remove-Item -LiteralPath $scratch}
}
