<#
.SYNOPSIS
  install-local.ps1로 설치한 Panely를 제거한다. 기본으로 사용자 데이터는 남긴다.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\uninstall-local.ps1
  powershell -ExecutionPolicy Bypass -File tools\uninstall-local.ps1 -RemoveDevData   # %LOCALAPPDATA%\Panely-dev 삭제
  powershell -ExecutionPolicy Bypass -File tools\uninstall-local.ps1 -RemoveData      # %LOCALAPPDATA%\Panely 삭제(확인 후)
#>
param(
    [string]$InstallDir = '',
    [switch]$RemoveDevData,
    [switch]$RemoveData,
    [switch]$Force
)

. "$PSScriptRoot\_common.ps1"
if (-not $InstallDir) { $InstallDir = $DefaultInstallDir }

Write-Step "제거: $InstallDir"
if (Test-Path $InstallDir) {
    if (-not (Test-Path (Join-Path $InstallDir 'install-info.json'))) {
        throw "install-local.ps1로 설치한 폴더가 아닙니다(install-info.json 없음): $InstallDir"
    }
    Stop-PanelyIn $InstallDir
    Remove-Item $InstallDir -Recurse -Force
    Write-Ok '프로그램 폴더 삭제됨'
} else {
    Write-Note '설치 폴더가 없습니다.'
}

foreach ($lnk in @(
        (Join-Path ([Environment]::GetFolderPath('Programs')) 'Panely (Local).lnk'),
        (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Panely (Local).lnk'))) {
    if (Test-Path $lnk) { Remove-Item $lnk -Force; Write-Ok "바로 가기 삭제: $lnk" }
}

function Remove-DataFolder([string]$path) {
    if (-not (Test-Path $path)) { Write-Note "없음: $path"; return }
    if (-not $Force) {
        $answer = Read-Host "'$path' 를 삭제합니다(읽기 기록·북마크·설정·번역 수정·모델 포함). 계속할까요? (y/N)"
        if ($answer -notmatch '^(y|yes)$') { Write-Note '건너뜀'; return }
    }
    Remove-Item $path -Recurse -Force
    Write-Ok "삭제됨: $path"
}

if ($RemoveDevData) { Remove-DataFolder (Join-Path $env:LOCALAPPDATA 'Panely-dev') }
if ($RemoveData) { Remove-DataFolder (Join-Path $env:LOCALAPPDATA 'Panely') }
if (-not $RemoveData) { Write-Note "사용자 데이터는 남겼습니다: $(Join-Path $env:LOCALAPPDATA 'Panely')" }
