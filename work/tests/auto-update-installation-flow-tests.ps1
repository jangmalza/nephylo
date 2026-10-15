# Real child helper + signed fixture + silent Inno installer + restart, isolated at compile time.
param([string]$ReleaseAssets = '')
$ErrorActionPreference = 'Stop'
$work = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not $ReleaseAssets) { $ReleaseAssets = Join-Path $work 'release-1.20.0' }
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$inno = Join-Path $work 'tools/innosetup-7.1.0/ISCC.exe'
$utf8 = [Text.UTF8Encoding]::new($false)
$testId = 'update-flow-' + [Guid]::NewGuid().ToString('N').Substring(0,12)
$fixture = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $testId))
if ([IO.Path]::GetDirectoryName($fixture) -ne [IO.Path]::GetFullPath($PSScriptRoot)) { throw 'Unsafe fixture root' }
$assertions = 0
$rsa = [Security.Cryptography.RSACryptoServiceProvider]::new(2048,[Security.Cryptography.CspParameters]@{ ProviderType = 24 })
$rsa.PersistKeyInCsp = $false
$caseDirectories = [Collections.Generic.List[string]]::new()
function Assert-That([bool]$Condition,[string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:assertions++
    Write-Output ('PASS ' + $Message)
}
function Write-Text([string]$Path,[string]$Text) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path,$Text,$utf8)
}
function Compile-App([string]$Directory,[string]$Version,[string]$Root,[string]$Stage,[string]$Key) {
    [IO.Directory]::CreateDirectory($Directory) | Out-Null
    $source = Join-Path $Directory 'fixture.cs'
    $dataLiteral = (Join-Path $Root 'data').Replace('"','""')
    $stageLiteral = $Stage.Replace('"','""')
    $rootLiteral = $Root.Replace('"','""')
    $mutexLiteral = 'Local\Nephylo.SetupTest.' + $testId + '-' + [IO.Path]::GetFileName($Root)
    $signalReady = if ([IO.Path]::GetFileName($Root) -eq 'startup-failure') { 'false' } else { 'true' }
    $code = @"
using System; using System.IO; using System.Reflection; using System.Threading;
[assembly: AssemblyProduct("Nephylo")]
[assembly: AssemblyVersion("$Version.0")]
[assembly: AssemblyFileVersion("$Version.0")]
namespace UsageWidget {
internal static class AutoUpdateInstallerTestPaths { internal static readonly string DataDirectory = @"$dataLiteral"; internal static readonly string InstallMutex = @"$mutexLiteral"; internal static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5); }
internal static class SettingsStore { internal static string InstallDirectory { get { return DataDirectory; } } internal static string DataDirectory { get { return AutoUpdateInstallerTestPaths.DataDirectory; } } }
internal static class Fixture {
 [STAThread] public static void Main(string[] args) {
  if (AutoUpdateInstaller.TryRunWorker(args)) return;
  if (args.Length == 1 && args[0] == "--test-update") {
   try {
    string stage = @"$stageLiteral";
    var manifest = AutoUpdateManifest.ParseAndVerify(File.ReadAllText(Path.Combine(stage,"update.json")));
    var update = new PreparedUpdate { Manifest=manifest, DirectoryPath=stage, ManifestJsonPath=Path.Combine(stage,"update.json"), InstallerPath=Path.Combine(stage,manifest.FileName) };
    AutoUpdateInstaller.StartPreparedUpdate(update);
    File.WriteAllText(Path.Combine(@"$rootLiteral","helper-ready.txt"),"ready");
    Thread.Sleep(1500);
    File.WriteAllText(Path.Combine(@"$rootLiteral","parent-exited.txt"),"flushed");
   } catch(Exception error) { File.WriteAllText(Path.Combine(@"$rootLiteral","parent-error.txt"),error.ToString()); }
  } else {
   File.WriteAllText(Path.Combine(@"$rootLiteral","restarted.txt"),Assembly.GetExecutingAssembly().GetName().Version.ToString());
   if ($signalReady && Assembly.GetExecutingAssembly().GetName().Version >= new Version(1,25,8,0)) WidgetStartup.MarkReady(args);
  }
 }
}}
"@
    Write-Text $source $code
    $arguments = @('/nologo','/target:winexe','/langversion:5','/define:NEPHYLO_UPDATE_INSTALLER_TESTS',('/out:' + (Join-Path $Directory 'UsageWidget.exe')),('/resource:' + $Key + ',UsageWidget.UpdateSigningPublicKey.xml'))
    foreach ($reference in @('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Security.dll','System.Net.Http.dll')) { $arguments += '/reference:' + (Join-Path $framework $reference) }
    $arguments += @($source,(Join-Path $work 'src/AutoUpdateClient.cs'),(Join-Path $work 'src/AutoUpdateInstaller.cs'),(Join-Path $work 'src/WidgetStartup.cs'),(Join-Path $work 'src/WidgetDataProfile.cs'))
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Updater fixture compilation failed' }
    Copy-Item -LiteralPath (Join-Path $work 'src/UsageWidget.exe.config') -Destination $Directory
}
function Wait-File([string]$Path,[int]$Seconds = 30) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $Path) -and $watch.Elapsed.TotalSeconds -lt $Seconds) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path -LiteralPath $Path)) { throw ('Timed out waiting for isolated marker: ' + $Path) }
}
function Wait-FinalResult([string]$Path) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt 15) {
        if (Test-Path -LiteralPath $Path) {
            # The observer must allow the helper's atomic File.Replace. The
            # default Get-Content sharing can lock last-result.json mid-poll.
            $stream = [IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            $reader = [IO.StreamReader]::new($stream,[Text.Encoding]::UTF8)
            try { $json = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $value = $json | ConvertFrom-Json
            if ($value.code -ne 'installation-complete') { return $value }
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'Updater never reported first-run readiness result'
}
try {
    [IO.Directory]::CreateDirectory($fixture) | Out-Null
    $publicKey = Join-Path $fixture 'test-public.xml'
    Write-Text $publicKey ($rsa.ToXmlString($false))
    foreach ($scenario in @('success','installer-failure','tamper','startup-failure')) {
        $root = Join-Path $fixture $scenario
        $caseDirectories.Add($root)
        $source = Join-Path $root 'source'
        $app = Join-Path $root 'data/App'
        $stage = Join-Path $root ('data/Updates/' + [Guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($stage) | Out-Null
        Compile-App $app '1.25.7' $root $stage $publicKey
        Compile-App $source '1.25.8' $root $stage $publicKey
        foreach ($name in @('widget.ico','widget.png')) { Copy-Item -LiteralPath (Join-Path $ReleaseAssets $name) -Destination $source }
        Write-Text (Join-Path $source 'README.txt') 'new fixture readme'
        Write-Text (Join-Path $source 'THIRD-PARTY-NOTICES.txt') 'fixture notices'
        Write-Text (Join-Path $app 'README.txt') 'old fixture readme'
        Write-Text (Join-Path $app 'unknown-file.txt') 'preserve this unknown app file'
        $data = Join-Path $root 'data'
        $preserved = @{}
        foreach ($name in @('settings.json','membership-session.dat','usage-journal.json','elevenlabs-key.dat')) {
            $path = Join-Path $data $name
            Write-Text $path ('preserve-' + $name)
            $preserved[$path] = (Get-FileHash -LiteralPath $path).Hash
        }
        $setupOut = Join-Path $root 'setup'
        [IO.Directory]::CreateDirectory($setupOut) | Out-Null
        $id = $testId + '-' + $scenario
        & $inno '/Q' ('/DSourceDir=' + $source) ('/DOutputDir=' + $setupOut) '/DAppVersion=1.25.8' ('/DTestRoot=' + $root) ('/DTestId=' + $id) (Join-Path $work 'installer/Nephylo.iss')
        if ($LASTEXITCODE -ne 0) { throw 'Isolated updater installer did not compile' }
        $installer = Join-Path $stage 'Nephylo-Setup-1.25.8.exe'
        Copy-Item -LiteralPath (Join-Path $setupOut 'Nephylo-Setup.exe') -Destination $installer
        $payload = [ordered]@{ schemaVersion = 1; version = '1.25.8'; fileName = 'Nephylo-Setup-1.25.8.exe'; bytes = (Get-Item -LiteralPath $installer).Length; sha256 = (Get-FileHash -LiteralPath $installer).Hash.ToLowerInvariant() }
        $payloadBytes = $utf8.GetBytes(($payload | ConvertTo-Json -Compress))
        $signature = $rsa.SignData($payloadBytes,[Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'))
        $envelope = [ordered]@{ payloadBase64 = [Convert]::ToBase64String($payloadBytes); signatureBase64 = [Convert]::ToBase64String($signature) } | ConvertTo-Json -Compress
        Write-Text (Join-Path $stage 'update.json') $envelope
        if ($scenario -eq 'tamper') { [IO.File]::AppendAllText($installer,'tamper') }
        if ($scenario -eq 'installer-failure') {
            $shortcut = Join-Path $root 'shortcuts/start-menu/Nephylo.lnk'
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($shortcut)) | Out-Null
            $shell = New-Object -ComObject WScript.Shell
            $link = $shell.CreateShortcut($shortcut)
            try { $link.TargetPath = (Join-Path $env:WINDIR 'notepad.exe'); $link.Save() }
            finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
        }
        $installed = Join-Path $app 'UsageWidget.exe'
        $oldHash = (Get-FileHash -LiteralPath $installed).Hash
        $parent = Start-Process -FilePath $installed -ArgumentList '--test-update' -PassThru -WindowStyle Hidden
        if ($scenario -eq 'tamper') {
            if (-not $parent.WaitForExit(15000)) { throw 'Tampered update parent did not exit' }
            Assert-That (Test-Path -LiteralPath (Join-Path $root 'parent-error.txt')) 'Tampered signed payload rejected before shutdown'
            Assert-That (-not (Test-Path -LiteralPath (Join-Path $stage 'NephyloUpdater.exe'))) 'Tampered installer never creates helper'
            Assert-That ((Get-FileHash -LiteralPath $installed).Hash -eq $oldHash) 'Tamper rejection leaves installed binary unchanged'
        } else {
            Wait-File (Join-Path $root 'helper-ready.txt') 15
            Assert-That (-not $parent.HasExited) ($scenario + ': helper acknowledges readiness while parent is alive')
            Assert-That ([Diagnostics.FileVersionInfo]::GetVersionInfo($installed).FileVersion -eq '1.25.7.0') ($scenario + ': installer waits for parent exit')
            if (-not $parent.WaitForExit(15000)) { throw 'Fixture parent failed to exit' }
            Wait-File (Join-Path $root 'restarted.txt') 45
            $result = Wait-FinalResult (Join-Path $data 'Updates/last-result.json')
            Assert-That (Test-Path -LiteralPath (Join-Path $root 'parent-exited.txt')) ($scenario + ': parent flush marker exists')
            if ($scenario -eq 'success') {
                Assert-That $result.success 'Signed update completed through real silent installer'
                Assert-That ($result.code -eq 'installed') 'Successful helper records installed result'
                Assert-That ((Get-Content -LiteralPath (Join-Path $root 'restarted.txt') -Raw) -eq '1.25.8.0') 'Helper restarts the new version automatically'
                $readiness = Get-Content -LiteralPath (Join-Path $data '.nephylo-startup.json') -Raw | ConvertFrom-Json
                Assert-That ($readiness.version -eq '1.25.8.0' -and $readiness.status -eq 'ready' -and $readiness.nonce.Length -eq 32) 'Success waits for version-matched startup readiness event'
                Assert-That ((Get-FileHash -LiteralPath $installed).Hash -eq (Get-FileHash -LiteralPath (Join-Path $source 'UsageWidget.exe')).Hash) 'Installed bytes match new fixture executable'
            } elseif ($scenario -eq 'startup-failure') {
                Assert-That (-not $result.success -and $result.code -eq 'startup-failed') 'Missing startup readiness cannot report installation success'
                Assert-That (-not (Test-Path -LiteralPath (Join-Path $data '.nephylo-startup.json'))) 'Exited non-ready child does not produce a ready receipt'
                Assert-That ((Get-FileHash -LiteralPath $installed).Hash -eq (Get-FileHash -LiteralPath (Join-Path $source 'UsageWidget.exe')).Hash) 'Startup failure preserves installed payload without unsafe concurrent rollback'
                Assert-That (Test-Path -LiteralPath (Join-Path $stage 'previous/UsageWidget.exe')) 'Startup failure retains previous payload for recovery'
            } else {
                Assert-That (-not $result.success -and $result.code -eq 'install-failed') 'Installer failure is recorded without retry loop'
                Assert-That ((Get-Content -LiteralPath (Join-Path $root 'restarted.txt') -Raw) -eq '1.25.7.0') 'Failed installer restarts the previous version'
                Assert-That ((Get-FileHash -LiteralPath $installed).Hash -eq $oldHash) 'Failed installer preserves exact previous executable'
            }
        }
        foreach ($path in $preserved.Keys) { Assert-That ((Get-FileHash -LiteralPath $path).Hash -eq $preserved[$path]) ($scenario + ': preserved ' + [IO.Path]::GetFileName($path)) }
        Assert-That ((Get-Content -LiteralPath (Join-Path $app 'unknown-file.txt') -Raw) -eq 'preserve this unknown app file') ($scenario + ': unknown App files preserved')
        $parent.Dispose()
    }
    Write-Output ('Automatic installation flow: ' + $assertions + ' assertions passed.')
} finally {
    $rsa.Dispose()
    foreach ($root in $caseDirectories) {
        $scenario = [IO.Path]::GetFileName($root)
        $id = $testId + '-' + $scenario
        $uninstaller = Join-Path $root 'data/App/unins000.exe'
        if (Test-Path -LiteralPath $uninstaller) {
            $uninstall = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -PassThru -WindowStyle Hidden
            [void]$uninstall.WaitForExit(30000)
            $uninstall.Dispose()
        }
        [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree(('Software\UsageWidget\Tests\Setup\' + $id),$false)
    }
    $resolved = [IO.Path]::GetFullPath($fixture)
    if ([IO.Path]::GetDirectoryName($resolved) -eq [IO.Path]::GetFullPath($PSScriptRoot) -and [IO.Path]::GetFileName($resolved) -eq $testId) {
        Start-Sleep -Milliseconds 500
        try { Remove-Item -LiteralPath $resolved -Recurse -Force } catch { Write-Warning 'Isolated update fixture retained because a helper still has a file open.' }
    }
}
