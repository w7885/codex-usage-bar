param([switch]$Tests, [switch]$Staging)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.8 x64 compiler was not found.' }
$outputRoot = Join-Path $projectRoot 'dist'
$iconPath = Join-Path $projectRoot 'assets\codex-usage-bar.ico'
if (-not (Test-Path -LiteralPath $iconPath)) { throw 'Application icon was not found. Run tools/build-icon.ps1 first.' }
$iconResource = '/resource:' + $iconPath + ',CodexQuota.UsageBar.ico'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$references = @('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Windows.Forms.dll','System.Drawing.dll','System.Xaml.dll','System.Runtime.Serialization.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkRoot $_) }
$references += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkRoot ('WPF\' + $_)) }
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$executable = Join-Path $outputRoot $(if ($Staging) { 'CodexUsageBar.next.exe' } else { 'CodexUsageBar.exe' })
& $compiler /nologo /utf8output /codepage:65001 /langversion:5 /target:winexe /platform:x64 /optimize+ /warn:4 ('/win32manifest:' + (Join-Path $projectRoot 'src\app.manifest')) ('/win32icon:' + $iconPath) $iconResource ('/out:' + $executable) $references $sources
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'src\app.config') -Destination ($executable + '.config') -Force
if ($Tests) {
    $testFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tests') -Filter '*.cs' -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    if ($testFiles.Count -eq 0) { throw 'No tests found.' }
    & $compiler /nologo /utf8output /codepage:65001 /langversion:5 /target:exe /platform:x64 /warn:4 /main:CodexQuota.TestRunner $iconResource ('/out:' + (Join-Path $outputRoot 'CodexUsageBar.Tests.exe')) $references $sources $testFiles
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'src\app.config') -Destination (Join-Path $outputRoot 'CodexUsageBar.Tests.exe.config') -Force
}
Write-Output ('Built: ' + $executable)
