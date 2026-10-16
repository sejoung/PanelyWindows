# tools/*.ps1 공용 함수. 직접 실행하지 않고 dot-source 한다:  . "$PSScriptRoot\_common.ps1"

$ErrorActionPreference = 'Stop'
$script:RepoRoot = Split-Path -Parent $PSScriptRoot
$script:AppProject = Join-Path $script:RepoRoot 'src\Panely.App\Panely.App.csproj'
$script:SolutionFile = Join-Path $script:RepoRoot 'Panely.sln'
$script:TestProject = Join-Path $script:RepoRoot 'tests\Panely.Tests\Panely.Tests.csproj'
# 사용자별 설치 위치(관리자 권한 불필요). 사용자 데이터(%LOCALAPPDATA%\Panely)와 분리한다.
$script:DefaultInstallDir = Join-Path $env:LOCALAPPDATA 'Programs\Panely'

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

function Write-Step([string]$message) {
    Write-Host ''
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Write-Ok([string]$message) { Write-Host "    $message" -ForegroundColor Green }
function Write-Note([string]$message) { Write-Host "    $message" -ForegroundColor DarkGray }

# global.json이 요구하는 .NET 10 SDK를 찾는다: PATH의 dotnet → 사용자 설치(%LOCALAPPDATA%\Microsoft\dotnet).
function Get-Dotnet {
    $candidates = @()
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += $onPath.Source }
    $candidates += (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')
    $candidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
    foreach ($c in ($candidates | Select-Object -Unique)) {
        if (-not (Test-Path $c)) { continue }
        $sdks = & $c --list-sdks 2>$null
        if ($sdks -match '^10\.') { return $c }
    }
    throw @"
.NET 10 SDK를 찾지 못했습니다. 다음 중 하나로 설치하세요.
  winget install Microsoft.DotNet.SDK.10
  또는 https://dotnet.microsoft.com/download/dotnet/10.0
"@
}

function Invoke-Dotnet([string]$dotnet, [string[]]$arguments) {
    Write-Note ("dotnet " + ($arguments -join ' '))
    & $dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($arguments[0]) 실패 (exit $LASTEXITCODE)" }
}

# 지정 폴더에서 실행 중인 Panely를 닫는다(먼저 정상 종료 요청 → 위치 저장, 시간 초과 시 강제 종료).
function Stop-PanelyIn([string]$directory, [int]$timeoutSeconds = 10) {
    if (-not (Test-Path $directory)) { return }
    $full = (Resolve-Path $directory).Path.TrimEnd('\') + '\'
    $running = @(Get-Process -Name Panely -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and $_.Path.StartsWith($full, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
    })
    if ($running.Count -eq 0) { return }
    Write-Note "실행 중인 Panely $($running.Count)개를 닫습니다…"
    foreach ($p in $running) { [void]$p.CloseMainWindow() }
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline -and ($running | Where-Object { -not $_.HasExited })) { Start-Sleep -Milliseconds 300 }
    foreach ($p in $running) { if (-not $p.HasExited) { Write-Note "응답이 없어 강제 종료: PID $($p.Id)"; $p.Kill() } }
}

function Get-GitShortHash {
    try {
        $hash = & git -C $script:RepoRoot rev-parse --short=8 HEAD 2>$null
        if ($LASTEXITCODE -eq 0 -and $hash) {
            $dirty = & git -C $script:RepoRoot status --porcelain 2>$null
            if ($dirty) { return "$hash-dirty" }
            return $hash
        }
    } catch { }
    return 'nogit'
}
