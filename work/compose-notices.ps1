param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$license=[IO.File]::ReadAllText((Join-Path $repo 'LICENSE'))
$thirdParty=[IO.File]::ReadAllText((Join-Path $repo 'THIRD-PARTY-NOTICES.txt'))
if(-not $license.Contains('MIT License') -or -not $license.Contains('Nephylo contributors')){throw 'Project license is missing or unexpected.'}
$target=[IO.Path]::GetFullPath($OutputPath)
if($target -eq (Join-Path $repo 'LICENSE') -or $target -eq (Join-Path $repo 'THIRD-PARTY-NOTICES.txt')){throw 'Do not overwrite source notices.'}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))|Out-Null
$text="Nephylo project license`r`n=======================`r`n`r`n"+$license.Trim()+"`r`n`r`nThird-party notices`r`n===================`r`n`r`n"+$thirdParty.Trim()+"`r`n"
[IO.File]::WriteAllText($target,$text,[Text.UTF8Encoding]::new($true))
