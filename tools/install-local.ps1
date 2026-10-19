<#
.SYNOPSIS
  빌드 → (테스트) → self-contained 게시 → 사용자 폴더에 설치 → 시작 메뉴 바로 가기 → 실행.
  관리자 권한이 필요 없고, 설치본은 .NET 런타임 없이 실행된다.
  사용자 데이터(%LOCALAPPDATA%\Panely)는 건드리지 않는다.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\install-local.ps1                     # 테스트 후 설치하고 실행
  powershell -ExecutionPolicy Bypass -File tools\install-local.ps1 -SkipTests          # 빠르게 설치만
  powershell -ExecutionPolicy Bypass -File tools\install-local.ps1 -Open "D:\Comics\Series A"
  powershell -ExecutionPolicy Bypass -File tools\install-local.ps1 -Samples            # QA 샘플 라이브러리를 만들어 열기
  powershell -ExecutionPolicy Bypass -File tools\install-local.ps1 -Isolated           # 실제 데이터와 분리된 데이터 폴더로 실행
  powershell -ExecutionPolicy Bypass -File tools\install-local.ps1 -NoLaunch -DesktopShortcut
#>
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateSet('auto', 'win-x64', 'win-arm64')][string]$Runtime = 'auto',
    [string]$InstallDir = '',
    [switch]$SkipTests,
    # 시작이 조금 빠른 ReadyToRun 게시(빌드 시간은 더 걸림). 배포본과 같은 조건으로 확인할 때 사용.
    [switch]$ReadyToRun,
    [switch]$NoLaunch,
    [string]$Open = '',
    [switch]$Samples,
    # 실행할 때만 PANELY_DATA_ROOT를 %LOCALAPPDATA%\Panely-dev로 바꿔 실제 읽기 기록/설정과 섞이지 않게 한다.
    [switch]$Isolated,
    [string]$DataRoot = '',
    [switch]$NoShortcut,
    [switch]$DesktopShortcut
)

. "$PSScriptRoot\_common.ps1"
$started = Get-Date
if (-not $InstallDir) { $InstallDir = $DefaultInstallDir }
if ($Runtime -eq 'auto') {
    $Runtime = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
}

$dotnet = Get-Dotnet
Write-Note "SDK: $dotnet ($(& $dotnet --version))"
$commit = Get-GitShortHash
# SemVer 숫자 식별자는 앞자리 0을 허용하지 않는다("0950"이면 NuGet 복원이 오류 없이 실패). 시각은 정수로 적어 순서는 유지한다.
$now = Get-Date
$stamp = '{0}.{1}' -f $now.ToString('yyyyMMdd'), [int]$now.ToString('HHmm')
$versionSuffix = "dev.$stamp"

# 1) 테스트
if (-not $SkipTests) {
    Write-Step "빌드 + 테스트 ($Configuration)"
    Invoke-Dotnet $dotnet @('build', $SolutionFile, '-c', $Configuration, '-nologo', '-v', 'minimal')
    Invoke-Dotnet $dotnet @('test', $TestProject, '-c', $Configuration, '--no-build', '-nologo')
    Write-Ok '테스트 통과'
}

# 2) self-contained 게시(임시 폴더)
$staging = Join-Path $RepoRoot "artifacts\local-install\$Runtime"
Write-Step "게시 ($Runtime, $Configuration, 버전 접미사 $versionSuffix)"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
$publishArgs = @(
    'publish', $AppProject,
    '-c', $Configuration, '-r', $Runtime, '--self-contained', 'true',
    "-p:PublishReadyToRun=$([bool]$ReadyToRun)".ToLowerInvariant(),
    "-p:VersionSuffix=$versionSuffix",
    '-p:DebugType=embedded',
    '-o', $staging, '-nologo', '-v', 'minimal'
)
Invoke-Dotnet $dotnet $publishArgs
Copy-Item (Join-Path $RepoRoot 'THIRD-PARTY-NOTICES.md') $staging -Force
Copy-Item (Join-Path $RepoRoot 'docs\windows\assets\LICENSE-original.txt') (Join-Path $staging 'LICENSE-original-assets.txt') -Force
Write-Ok "게시됨: $staging"

# 3) 실행 중인 설치본 종료 후 교체
Write-Step "설치: $InstallDir"
Stop-PanelyIn $InstallDir
New-Item -ItemType Directory -Force $InstallDir | Out-Null
# /MIR: 이전 빌드에만 있던 파일까지 정리. robocopy 종료 코드 0~7은 성공.
& robocopy $staging $InstallDir /MIR /NFL /NDL /NJH /NJS /NP /R:2 /W:1 | Out-Null
if ($LASTEXITCODE -ge 8) { throw "파일 복사 실패 (robocopy exit $LASTEXITCODE). 설치 폴더의 Panely가 아직 실행 중인지 확인하세요." }
$global:LASTEXITCODE = 0

$exe = Join-Path $InstallDir 'Panely.exe'
$info = [ordered]@{
    version       = (Get-Item $exe).VersionInfo.ProductVersion
    commit        = $commit
    configuration = $Configuration
    runtime       = $Runtime
    readyToRun    = [bool]$ReadyToRun
    installedAt   = (Get-Date).ToUniversalTime().ToString('o')
    source        = $RepoRoot
}
$info | ConvertTo-Json | Set-Content (Join-Path $InstallDir 'install-info.json') -Encoding UTF8
Write-Ok "설치됨: $exe"
Write-Note "버전: $($info.version)"

# 4) 바로 가기(사용자 시작 메뉴/바탕 화면)
function New-Shortcut([string]$path) {
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($path)
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $InstallDir
    $lnk.IconLocation = "$exe,0"
    $lnk.Description = 'Panely (local build)'
    $lnk.Save()
}
if (-not $NoShortcut) {
    $startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Panely (Local).lnk'
    New-Shortcut $startMenu
    Write-Ok "시작 메뉴: $startMenu"
}
if ($DesktopShortcut) {
    $desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Panely (Local).lnk'
    New-Shortcut $desktop
    Write-Ok "바탕 화면: $desktop"
}

# 5) 샘플 준비
if ($Samples) {
    $samplesDir = Join-Path $env:TEMP 'PanelySamples'
    if (-not (Test-Path (Join-Path $samplesDir 'Library'))) {
        Write-Step "QA 샘플 생성: $samplesDir"
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'make-samples.ps1') -Out $samplesDir
        if ($LASTEXITCODE -ne 0) { throw '샘플 생성 실패' }
    }
    if (-not $Open) { $Open = $samplesDir }
}

# 6) 실행
if (-not $NoLaunch) {
    Write-Step '실행'
    $launch = @{ FilePath = $exe; WorkingDirectory = $InstallDir }
    if ($Open) {
        if (-not (Test-Path $Open)) { throw "열 경로가 없습니다: $Open" }
        $launch.ArgumentList = '"' + (Resolve-Path $Open).Path + '"'
        Write-Note "열기: $($launch.ArgumentList)"
    }
    if ($Isolated -and -not $DataRoot) { $DataRoot = Join-Path $env:LOCALAPPDATA 'Panely-dev' }
    # ShellExecute(Start-Process)로 띄워 콘솔 핸들을 물려주지 않는다. 그래야 cmd/파이프라인이 앱 종료를 기다리지 않는다.
    # 환경 변수는 현재 프로세스에 잠깐 설정해 자식에게 전달하고 바로 되돌린다.
    $previousDataRoot = $env:PANELY_DATA_ROOT
    try {
        if ($DataRoot) {
            $env:PANELY_DATA_ROOT = $DataRoot
            Write-Note "데이터 폴더: $DataRoot"
        } else {
            Remove-Item Env:PANELY_DATA_ROOT -ErrorAction SilentlyContinue
        }
        Start-Process @launch
    } finally {
        if ($null -ne $previousDataRoot) { $env:PANELY_DATA_ROOT = $previousDataRoot } else { Remove-Item Env:PANELY_DATA_ROOT -ErrorAction SilentlyContinue }
    }
    Write-Ok 'Panely를 시작했습니다.'
}

Write-Step ("완료 ({0:n0}초)" -f ((Get-Date) - $started).TotalSeconds)
Write-Note '제거: tools\uninstall-local.ps1'
