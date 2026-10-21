$ErrorActionPreference = 'Stop'
$testDirectory = $PSScriptRoot
$sourceDirectory = Join-Path (Split-Path -Parent $testDirectory) 'src'
$testExecutable = Join-Path $testDirectory 'studio-gemini-api-tests.exe'
$frameworkDirectory = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkDirectory 'csc.exe'
$arguments = @('/nologo', '/warn:4', '/langversion:5', '/target:exe', '/define:STUDIO_GEMINI_API_TESTS', '/main:StudioGeminiApiTests', '/utf8output', ('/out:' + $testExecutable))
foreach ($reference in @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll', 'System.Security.dll', 'System.Net.Http.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Xaml.dll')) {
    $arguments += '/reference:' + (Join-Path $frameworkDirectory $reference)
}
foreach ($reference in @('PresentationFramework.dll', 'PresentationCore.dll', 'WindowsBase.dll')) {
    $arguments += '/reference:' + (Join-Path $frameworkDirectory ('WPF\' + $reference))
}
foreach ($source in @('StudioAgentClient.cs', 'StudioQuota.cs', 'StudioCodexClient.cs', 'StudioFailure.cs', 'StudioStore.cs', 'StudioHandoff.cs', 'StudioProviderCatalog.cs', 'StudioGeminiApiClient.cs', 'StudioGeminiSecrets.cs')) {
    $arguments += Join-Path $sourceDirectory $source
}
$arguments += Join-Path $testDirectory 'studio-gemini-api-tests.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Gemini API adapter tests did not compile.' }
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw 'Gemini API adapter tests failed.' }
