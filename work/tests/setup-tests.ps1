param([string]$Version = '1.18.1', [string]$InnoCompiler = $env:INNO_SETUP_COMPILER)
$ErrorActionPreference = 'Stop'
$work = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$workspace = Split-Path -Parent $work
$compiler = if ($InnoCompiler) { (Resolve-Path -LiteralPath $InnoCompiler -ErrorAction Stop).Path } else { Join-Path $work 'tools/innosetup-7.1.0/ISCC.exe' }
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'Inno Setup compiler missing. Use -InnoCompiler or INNO_SETUP_COMPILER.' }
$script = Join-Path $work 'installer/Nephylo.iss'
$id = 'regression-' + [Guid]::NewGuid().ToString('N').Substring(0,12)
$fixture = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('setup-isolated-' + $id)))
if (-not $fixture.StartsWith($PSScriptRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture path' }
$source = Join-Path $fixture 'source'
$target = Join-Path $fixture 'data/App'
$exe = Join-Path $target 'UsageWidget.exe'
$startLink = Join-Path $fixture 'shortcuts/start-menu/Nephylo.lnk'
$desktopLink = Join-Path $fixture 'shortcuts/desktop/Nephylo.lnk'
$legacyLink = Join-Path $fixture 'shortcuts/start-menu/Milos.lnk'
$foreignLegacyLink = Join-Path $fixture 'shortcuts/start-menu/Usage Widget.lnk'
$startupKey = 'Software\UsageWidget\Tests\Setup\' + $id + '\Run'
$uninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\Nephylo.SetupTest.' + $id + '_is1'
$assertions = 0
$runs = 0
$messages = [Collections.Generic.List[string]]::new()
$utf8 = [Text.UTF8Encoding]::new($false)

function Assert-That([bool]$Condition,[string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:assertions++
    $script:messages.Add('PASS ' + $Message)
}
function Write-Text([string]$Path,[string]$Text) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path,$Text,$utf8)
}
function Read-Startup {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($startupKey)
    if ($null -eq $key) { return $null }
    try { return $key.GetValue('UsageWidget',$null) } finally { $key.Dispose() }
}
function Write-Startup([AllowNull()][string]$Value) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($startupKey)
    try {
        if ($null -eq $Value -or $Value.Length -eq 0) { $key.DeleteValue('UsageWidget',$false) }
        else { $key.SetValue('UsageWidget',$Value,[Microsoft.Win32.RegistryValueKind]::String) }
    } finally { $key.Dispose() }
}
function Write-Link([string]$Path,[string]$Target,[string]$Arguments = '') {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($Path)
    try { $link.TargetPath = $Target; $link.Arguments = $Arguments; $link.Save() }
    finally {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link)
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
    }
}
function Read-LinkTarget([string]$Path) {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($Path)
    try { return $link.TargetPath }
    finally {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link)
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
    }
}
function Compile-Setup([string]$ReleaseVersion,[string]$OutputFolder) {
    [IO.Directory]::CreateDirectory($OutputFolder) | Out-Null
    & $compiler '/Q' ('/DSourceDir=' + $source) ('/DOutputDir=' + $OutputFolder) ('/DAppVersion=' + $ReleaseVersion) ('/DTestRoot=' + $fixture) ('/DTestId=' + $id) $script
    if ($LASTEXITCODE -ne 0) { throw 'Isolated installer compilation failed' }
    return Join-Path $OutputFolder 'Nephylo-Setup.exe'
}
function Run-Setup([string]$File,[string[]]$Options = @(),[bool]$ExpectSuccess = $true) {
    $script:runs++
    $log = Join-Path $fixture ('run-' + $script:runs + '.log')
    $args = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/LANG=en',('/LOG=' + $log)) + $Options
    $quoted = ($args | ForEach-Object { '"' + $_.Replace('"','\"') + '"' }) -join ' '
    $process = Start-Process -FilePath $File -ArgumentList $quoted -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(60000)) { throw ('Isolated setup did not finish: ' + $log) }
    $code = $process.ExitCode
    if ($ExpectSuccess -and $code -ne 0) { throw ('Setup failed with ' + $code + ': ' + $log) }
    if (-not $ExpectSuccess -and $code -eq 0) { throw ('Setup unexpectedly succeeded: ' + $log) }
    return $log
}
function Uninstall-Test {
    $uninstaller = Join-Path $target 'unins000.exe'
    if ((Test-Path -LiteralPath $uninstaller) -and (Test-Path -LiteralPath $exe)) {
        [void](Run-Setup $uninstaller)
        for ($i = 0; $i -lt 40 -and (Test-Path -LiteralPath $exe); $i++) { Start-Sleep -Milliseconds 100 }
    }
}
function Assert-Preserved {
    foreach ($file in $script:preserved.Keys) {
        Assert-That ((Test-Path -LiteralPath $file) -and ((Get-FileHash -LiteralPath $file).Hash -eq $script:preserved[$file])) ('Preserved ' + [IO.Path]::GetFileName($file))
    }
}

try {
    [IO.Directory]::CreateDirectory($source) | Out-Null
    $release = Join-Path $work ('release-' + $Version)
    foreach ($name in @('UsageWidget.exe','UsageWidget.exe.config','widget.ico','widget.png')) {
        Copy-Item -LiteralPath (Join-Path $release $name) -Destination $source
    }
    Copy-Item -LiteralPath (Join-Path $work 'README.txt') -Destination $source
    Copy-Item -LiteralPath (Join-Path $workspace 'outputs/UsageWidget/THIRD-PARTY-NOTICES.txt') -Destination $source
    Assert-That ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $source 'UsageWidget.exe')).FileVersion -eq ($Version + '.0')) 'Test payload version matches release'
    $setup = Compile-Setup $Version (Join-Path $fixture 'setup')
    $script:preserved = @{}
    foreach ($name in @('settings.json','membership-session.dat','elevenlabs-key.dat','usage-journal.json','extra-settings.json','plan-settings.json')) {
        $path = Join-Path $fixture ('data/' + $name)
        Write-Text $path ('synthetic-preserved-' + $name)
        $script:preserved[$path] = (Get-FileHash -LiteralPath $path).Hash
    }
    $unknown = Join-Path $target 'user-note.txt'
    Write-Text $unknown 'unknown destination file must survive'
    $script:preserved[$unknown] = (Get-FileHash -LiteralPath $unknown).Hash
    Write-Link $legacyLink $exe
    $foreignExe = Join-Path $fixture 'another-app.exe'
    Write-Link $foreignLegacyLink $foreignExe
    $foreignLegacyHash = (Get-FileHash -LiteralPath $foreignLegacyLink).Hash
    Write-Startup ('"' + (Join-Path $fixture 'old-portable/UsageWidget.exe') + '"')

    [void](Run-Setup $setup)
    Assert-That (Test-Path -LiteralPath $exe) 'Fresh setup installs application'
    Assert-That ((Read-Startup) -eq ('"' + $exe + '"')) 'Existing enabled startup retargets fixed test path'
    Assert-That ((Read-LinkTarget $startLink) -eq $exe) 'Start menu shortcut points at installed application'
    Assert-That (-not (Test-Path -LiteralPath $desktopLink)) 'Desktop shortcut is off by default'
    Assert-That (-not (Test-Path -LiteralPath $legacyLink)) 'Owned Milos legacy shortcut migrated'
    Assert-That ((Get-FileHash -LiteralPath $foreignLegacyLink).Hash -eq $foreignLegacyHash) 'Foreign legacy shortcut preserved'
    $registration = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($uninstallKey)
    Assert-That ($null -ne $registration) 'Apps and features entry is isolated and per user'
    if ($null -ne $registration) { $registration.Dispose() }
    Assert-Preserved

    Write-Startup $null
    [void](Run-Setup $setup)
    Assert-That ($null -eq (Read-Startup)) 'Reinstall respects startup disabled by app instead of remembered tasks'
    [void](Run-Setup $setup @('/MERGETASKS=startup'))
    Assert-That ((Read-Startup) -eq ('"' + $exe + '"')) 'Explicit startup task enables startup'
    [void](Run-Setup $setup @('/MERGETASKS=desktopicon'))
    Assert-That ((Read-Startup) -eq ('"' + $exe + '"')) 'Merging desktop task preserves currently enabled startup'
    Assert-That ((Read-LinkTarget $desktopLink) -eq $exe) 'Desktop task creates correct shortcut'
    [void](Run-Setup $setup @('/TASKS='))
    Assert-That ($null -eq (Read-Startup)) 'Explicit empty task list disables owned startup'

    $foreignStartup = '"' + $foreignExe + '" --custom'
    Write-Startup $foreignStartup
    [void](Run-Setup $setup @('/TASKS=startup'))
    Assert-That ((Read-Startup) -eq $foreignStartup) 'Foreign startup value survives explicit task request'
    $binaryHash = (Get-FileHash -LiteralPath $exe).Hash
    Write-Link $startLink $foreignExe
    $foreignStartHash = (Get-FileHash -LiteralPath $startLink).Hash
    [void](Run-Setup $setup @() $false)
    Assert-That ((Get-FileHash -LiteralPath $startLink).Hash -eq $foreignStartHash) 'Foreign same-name shortcut is not overwritten'
    Assert-That ((Get-FileHash -LiteralPath $exe).Hash -eq $binaryHash) 'Rejected shortcut collision leaves app unchanged'
    Write-Link $startLink $exe
    $redirect = Join-Path $fixture 'redirected'
    [void](Run-Setup $setup @(('/DIR=' + $redirect)) $false)
    Assert-That (-not (Test-Path -LiteralPath $redirect)) 'Runtime DIR override cannot change fixed install location'
    $oldSetup = Compile-Setup '1.0.0' (Join-Path $fixture 'old-setup')
    [void](Run-Setup $oldSetup @() $false)
    Assert-That ((Get-FileHash -LiteralPath $exe).Hash -eq $binaryHash) 'Rejected downgrade retains installed app'
    Assert-Preserved

    Write-Link $startLink $foreignExe
    Write-Link $desktopLink $exe '--custom'
    $foreignDesktopHash = (Get-FileHash -LiteralPath $desktopLink).Hash
    Uninstall-Test
    Assert-That (-not (Test-Path -LiteralPath $exe)) 'Uninstall removes installed application'
    Assert-That ((Read-Startup) -eq $foreignStartup) 'Uninstall preserves replaced startup value'
    Assert-That ((Get-FileHash -LiteralPath $startLink).Hash -eq $foreignStartHash) 'Uninstall preserves replaced Start menu shortcut'
    Assert-That ((Get-FileHash -LiteralPath $desktopLink).Hash -eq $foreignDesktopHash) 'Uninstall preserves customized desktop shortcut'
    Assert-Preserved

    Remove-Item -LiteralPath $startLink,$desktopLink
    Write-Startup $null
    [void](Run-Setup $setup @('/TASKS=startup,desktopicon'))
    Assert-That ((Read-Startup) -eq ('"' + $exe + '"')) 'Fresh reinstall can explicitly restore startup'
    Uninstall-Test
    Assert-That ($null -eq (Read-Startup)) 'Uninstall removes owned startup value'
    Assert-That (-not (Test-Path -LiteralPath $startLink)) 'Uninstall removes owned Start menu shortcut'
    Assert-That (-not (Test-Path -LiteralPath $desktopLink)) 'Uninstall removes owned desktop shortcut'
    Assert-Preserved

    $savedApp = [IO.Path]::GetFullPath((Join-Path $fixture 'saved-app'))
    $junctionTarget = [IO.Path]::GetFullPath((Join-Path $fixture 'junction-target'))
    foreach ($checkedPath in @($target,$savedApp,$junctionTarget)) {
        if (-not $checkedPath.StartsWith($fixture + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe junction test path' }
    }
    [IO.Directory]::CreateDirectory($junctionTarget) | Out-Null
    Move-Item -LiteralPath $target -Destination $savedApp
    try {
        New-Item -ItemType Junction -Path $target -Target $junctionTarget | Out-Null
        [void](Run-Setup $setup @() $false)
        Assert-That (-not (Test-Path -LiteralPath (Join-Path $junctionTarget 'UsageWidget.exe'))) 'Linked install folder is rejected without writing target'
    }
    finally {
        if (Test-Path -LiteralPath $target) { [IO.Directory]::Delete($target) }
        Move-Item -LiteralPath $savedApp -Destination $target
    }
    Assert-Preserved

    $report = Join-Path $workspace 'outputs/nephylo-setup-verification.txt'
    $text = 'Nephylo Setup regression tests passed: ' + $assertions + ' assertions, ' + $runs + ' isolated install/uninstall runs.' + [Environment]::NewLine +
        'Fixture: ' + $fixture + [Environment]::NewLine + 'No production install, shortcuts, startup value or user data touched.' + [Environment]::NewLine + ($messages -join [Environment]::NewLine)
    Write-Text $report $text
    Write-Output $text
}
finally {
    Uninstall-Test
    if (-not $startupKey.StartsWith('Software\UsageWidget\Tests\Setup\regression-',[StringComparison]::Ordinal)) { throw 'Unsafe test registry cleanup' }
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree(('Software\UsageWidget\Tests\Setup\' + $id),$false)
}
