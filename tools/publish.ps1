<#
.SYNOPSIS
  Panely를 self-contained로 게시한다(.NET 런타임이 없는 PC에서 실행, 관리자 권한 불필요).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\publish.ps1            # win-x64
  powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Runtime win-arm64
#>
param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release',
    [string]$Output = ''
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $repo "artifacts\Panely-$Runtime" }

$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

& $dotnet publish (Join-Path $repo 'src\Panely.App\Panely.App.csproj') `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=none `
    -o $Output
if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }

# 라이선스·고지 동봉
Copy-Item (Join-Path $repo 'docs\windows\assets\LICENSE-original.txt') (Join-Path $Output 'LICENSE-original-assets.txt') -Force
if (Test-Path (Join-Path $repo 'THIRD-PARTY-NOTICES.md')) {
    Copy-Item (Join-Path $repo 'THIRD-PARTY-NOTICES.md') $Output -Force
}

$zip = "$Output.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $Output '*') -DestinationPath $zip
Write-Host "Published: $Output"
Write-Host "Portable zip: $zip"
