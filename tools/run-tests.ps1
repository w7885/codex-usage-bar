param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -Tests }
$validationRoot = Join-Path $projectRoot 'validation'
New-Item -ItemType Directory -Path $validationRoot -Force | Out-Null
$result = & (Join-Path $projectRoot 'dist\CodexUsageBar.Tests.exe') 2>&1
$code = $LASTEXITCODE
$result | Set-Content -LiteralPath (Join-Path $validationRoot 'test-output.txt') -Encoding utf8
$result | Write-Output
if ($code -ne 0) { throw ('Tests failed with exit code ' + $code) }
