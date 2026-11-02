<#
.SYNOPSIS
  install-local.ps1로 설치한 Panely를 제거한다. 기본으로 사용자 데이터는 남긴다.
  설정에서 등록한 파일 연결·폴더 메뉴(HKCU)는 이 설치본을 가리킬 때만 함께 지운다.

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

# FileAssociation.Register가 쓴 명령 값이 이 실행 파일을 가리키는지.
function Test-CommandTargetsExe([string]$command, [string]$exe) {
    return [bool]$command -and ($command -ieq ('"{0}" "%1"' -f $exe))
}

function Get-UserRegistryDefault([string]$subKey) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey)
    if ($null -eq $key) { return $null }
    try { return [string]$key.GetValue('') } finally { $key.Close() }
}

# Shell\FileAssociation.cs의 Unregister와 같은 항목을 지운다. 다른 빌드(개발 빌드 등)를 가리키는 등록은 건드리지 않는다.
function Remove-ShellIntegration([string]$exe) {
    $user = [Microsoft.Win32.Registry]::CurrentUser
    $progId = 'Panely.ComicArchive'
    $removed = $false
    if (Test-CommandTargetsExe (Get-UserRegistryDefault "Software\Classes\$progId\shell\open\command") $exe) {
        foreach ($ext in '.cbz', '.cbr', '.zip', '.rar') {
            $extKey = $user.OpenSubKey("Software\Classes\$ext", $true)
            if ($null -eq $extKey) { continue }
            try {
                $openWith = $extKey.OpenSubKey('OpenWithProgids', $true)
                if ($null -ne $openWith) {
                    try { $openWith.DeleteValue($progId, $false) } finally { $openWith.Close() }
                }
                if ($extKey.GetValue('') -eq $progId) { $extKey.DeleteValue('', $false) }
            } finally { $extKey.Close() }
        }
        $user.DeleteSubKeyTree("Software\Classes\$progId", $false)
        $removed = $true
    }
    if (Test-CommandTargetsExe (Get-UserRegistryDefault 'Software\Classes\Directory\shell\Panely\command') $exe) {
        $user.DeleteSubKeyTree('Software\Classes\Directory\shell\Panely', $false)
        $removed = $true
    }
    if ($removed) {
        Add-Type -Namespace PanelyTools -Name Shell -MemberDefinition '[DllImport("shell32.dll")] public static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);'
        [PanelyTools.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
        Write-Ok '파일 연결·폴더 메뉴 등록 삭제됨'
    }
}

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

# 폴더가 이미 없어도 남은 등록은 지운다. 실패해도 나머지 제거는 계속한다.
try {
    $installedExe = Join-Path $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($InstallDir).TrimEnd('\') 'Panely.exe'
    Remove-ShellIntegration $installedExe
} catch {
    Write-Warning "파일 연결 등록을 지우지 못했습니다. 설정에서 해제하거나 직접 삭제하세요: $($_.Exception.Message)"
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
