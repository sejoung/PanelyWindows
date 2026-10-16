<#
.SYNOPSIS
  린트·빌드·테스트·smoke·게시본 확인을 한 번에 실행하고 단계별 결과를 요약한다.
  하나라도 실패하면 종료 코드 1. 실패해도 나머지 단계는 계속 돌려 전체 상태를 보여준다.

.DESCRIPTION
  1. SDK        .NET 10 SDK 확인
  2. restore    패키지 복원
  3. format     dotnet format 검사(공백·코드 스타일·분석기 경고 수준, .editorconfig)
  4. build      Release 빌드, 분석기 경고를 오류로(-warnaserror)
  5. test       단위·계약 테스트(Category!=Smoke) + 코드 커버리지 요약
  6. scripts    PowerShell 구문/인코딩, .cmd 줄바꿈, JSON 문법, (설치되어 있으면) PSScriptAnalyzer
  7. smoke      실제 Panely.exe를 격리 데이터 폴더로 띄우는 종단 간 테스트
  8. publish    self-contained 게시 후 게시본으로 smoke 재실행(.NET 미설치 PC 조건)

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\verify.ps1              # 전체
  powershell -ExecutionPolicy Bypass -File tools\verify.ps1 -Quick       # smoke·게시 생략(빠른 확인)
  powershell -ExecutionPolicy Bypass -File tools\verify.ps1 -Fix         # 형식 자동 수정 후 검증
  powershell -ExecutionPolicy Bypass -File tools\verify.ps1 -MinCoverage 60
#>
param(
    [switch]$Quick,
    [switch]$Fix,
    [switch]$NoSmoke,
    [switch]$NoPublish,
    [switch]$NoCoverage,
    [double]$MinCoverage = 0
)

. "$PSScriptRoot\_common.ps1"
$ErrorActionPreference = 'Continue'
$started = Get-Date
$results = New-Object System.Collections.Generic.List[object]
$artifacts = Join-Path $RepoRoot 'artifacts\verify'
New-Item -ItemType Directory -Force $artifacts | Out-Null
$script:dotnet = $null

function Invoke-Stage([string]$name, [scriptblock]$body, [switch]$Skip, [string]$SkipReason = '') {
    if ($Skip) {
        $results.Add([pscustomobject]@{ Stage = $name; Result = 'SKIP'; Seconds = 0; Detail = $SkipReason })
        Write-Step "$name  (건너뜀: $SkipReason)"
        return
    }
    Write-Step $name
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $detail = ''
    $ok = $true
    try {
        $out = & $body
        if ($out -is [string]) { $detail = $out } elseif ($out) { $detail = ($out | Select-Object -Last 1) -as [string] }
    } catch {
        $ok = $false
        $detail = $_.Exception.Message
        Write-Host "    실패: $detail" -ForegroundColor Red
    }
    $sw.Stop()
    $results.Add([pscustomobject]@{ Stage = $name; Result = $(if ($ok) { 'PASS' } else { 'FAIL' }); Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1); Detail = $detail })
    if ($ok) { Write-Ok "통과 ($([math]::Round($sw.Elapsed.TotalSeconds,1))s) $detail" }
}

function Exec([string[]]$arguments) {
    Write-Note ("dotnet " + ($arguments -join ' '))
    # Out-Host: 진행 출력은 화면에 보여주고 단계 결과 문자열에는 섞지 않는다
    & $script:dotnet @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($arguments[0]) 실패 (exit $LASTEXITCODE)" }
}

# ── 1. SDK ──────────────────────────────────────────────
Invoke-Stage 'SDK' {
    $script:dotnet = Get-Dotnet
    "$(& $script:dotnet --version) ($script:dotnet)"
}
if (-not $script:dotnet) {
    Write-Host 'SDK가 없어 중단합니다.' -ForegroundColor Red
    exit 1
}

# ── 2. restore ──────────────────────────────────────────
Invoke-Stage 'restore' { Exec @('restore', $SolutionFile, '-nologo', '-v', 'quiet') }

# ── 3. format / lint ────────────────────────────────────
Invoke-Stage 'format' {
    if ($Fix) {
        Exec @('format', $SolutionFile, '--no-restore', '--severity', 'warn')
        Write-Note '자동 수정을 적용했습니다. 변경 사항을 확인하세요.'
    }
    Write-Note "dotnet format $SolutionFile --verify-no-changes --severity warn"
    $log = Join-Path $artifacts 'format.log'
    & $script:dotnet format $SolutionFile --no-restore --verify-no-changes --severity warn *> $log
    if ($LASTEXITCODE -ne 0) {
        Get-Content $log | Select-String 'error|warning' | Select-Object -First 20 | ForEach-Object { Write-Host "    $_" -ForegroundColor Yellow }
        throw "형식/스타일 위반이 있습니다. 'tools\verify.ps1 -Fix' 또는 'dotnet format Panely.sln --severity warn'로 고치세요. (전체: $log)"
    }
    '위반 없음'
}

# ── 4. 빌드(경고=오류) ───────────────────────────────────
Invoke-Stage 'build (warnaserror)' {
    Exec @('build', $SolutionFile, '-c', 'Release', '--no-restore', '-warnaserror', '-nologo', '-v', 'minimal')
    '경고 0'
}
$buildOk = ($results | Where-Object Stage -eq 'build (warnaserror)').Result -eq 'PASS'

# ── 5. 단위·계약 테스트 + 커버리지 ─────────────────────────
function Get-CoverageSummary([string]$resultsDir) {
    # 테스트 프로젝트마다 나온 cobertura를 합친다(같은 파일·줄은 한 번만 센다).
    $covered = @{}; $total = @{}
    foreach ($file in Get-ChildItem $resultsDir -Recurse -Filter 'coverage.cobertura.xml') {
        [xml]$xml = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
        foreach ($pkg in $xml.coverage.packages.package) {
            $name = $pkg.name
            if (-not $total.ContainsKey($name)) { $total[$name] = @{}; $covered[$name] = @{} }
            foreach ($cls in $pkg.classes.class) {
                foreach ($line in $cls.lines.line) {
                    $key = "$($cls.filename):$($line.number)"
                    $total[$name][$key] = $true
                    if ([int]$line.hits -gt 0) { $covered[$name][$key] = $true }
                }
            }
        }
    }
    $rows = foreach ($name in ($total.Keys | Sort-Object)) {
        $t = $total[$name].Count; $c = $covered[$name].Count
        [pscustomobject]@{ Assembly = $name; Lines = $t; Covered = $c; Percent = $(if ($t) { [math]::Round(100.0 * $c / $t, 1) } else { 0 }) }
    }
    return $rows
}

Invoke-Stage 'test' -Skip:(-not $buildOk) -SkipReason '빌드 실패' {
    $resultsDir = Join-Path $artifacts 'test-results'
    if (Test-Path $resultsDir) { Remove-Item $resultsDir -Recurse -Force }
    $testArgs = @('test', $SolutionFile, '-c', 'Release', '--no-build', '-nologo', '--filter', 'Category!=Smoke',
        '--results-directory', $resultsDir, '--logger', 'trx')
    if (-not $NoCoverage) { $testArgs += @('--collect', 'XPlat Code Coverage') }
    Exec $testArgs
    $summary = ''
    if (-not $NoCoverage) {
        $rows = @(Get-CoverageSummary $resultsDir | Where-Object { ($_.Assembly -eq 'Panely' -or $_.Assembly -like 'Panely.*') -and $_.Assembly -notlike '*.Tests' } | ForEach-Object { if ($_.Assembly -eq 'Panely') { $_.Assembly = 'Panely (App)' }; $_ })
        if ($rows.Count -gt 0) {
            Write-Host ''
            $rows | Format-Table -AutoSize | Out-String | ForEach-Object { Write-Host $_.TrimEnd() }
            $allLines = ($rows | Measure-Object Lines -Sum).Sum
            $allCovered = ($rows | Measure-Object Covered -Sum).Sum
            $percent = [math]::Round(100.0 * $allCovered / [math]::Max(1, $allLines), 1)
            $logic = @($rows | Where-Object { $_.Assembly -in 'Panely.Core', 'Panely.Domain' })
            $logicPercent = [math]::Round(100.0 * ($logic | Measure-Object Covered -Sum).Sum / [math]::Max(1, ($logic | Measure-Object Lines -Sum).Sum), 1)
            # UI 계층(Panely App)은 단위 테스트보다 smoke 테스트(별도 프로세스, 커버리지 미집계)로 확인한다
            $summary = "커버리지 전체 $($percent)%, Core+Domain $($logicPercent)%"
            if ($MinCoverage -gt 0 -and $percent -lt $MinCoverage) { throw "커버리지 $percent%가 기준 $MinCoverage%보다 낮습니다." }
        }
    }
    $trx = Get-ChildItem $resultsDir -Recurse -Filter *.trx
    $passed = 0; $failed = 0
    foreach ($t in $trx) {
        [xml]$x = Get-Content -LiteralPath $t.FullName -Raw -Encoding UTF8
        $passed += [int]$x.TestRun.ResultSummary.Counters.passed
        $failed += [int]$x.TestRun.ResultSummary.Counters.failed
    }
    "테스트 $($passed)개 통과, $($failed)개 실패. $summary"
}

# ── 6. 스크립트·데이터 파일 린트 ───────────────────────────
Invoke-Stage 'scripts & data' {
    $problems = New-Object System.Collections.Generic.List[string]
    $ps1 = Get-ChildItem (Join-Path $RepoRoot 'tools') -Filter *.ps1
    foreach ($f in $ps1) {
        $tokens = $null; $errors = $null
        [void][System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$tokens, [ref]$errors)
        foreach ($e in $errors) { $problems.Add("$($f.Name):$($e.Extent.StartLineNumber) $($e.Message)") }
        # Windows PowerShell 5.1은 BOM 없는 UTF-8을 ANSI로 읽는다: 한글이 있으면 BOM 필수
        $bytes = [IO.File]::ReadAllBytes($f.FullName)
        $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
        if (-not $hasBom -and ($bytes | Where-Object { $_ -ge 0x80 } | Select-Object -First 1)) { $problems.Add("$($f.Name): 비ASCII 문자가 있는데 UTF-8 BOM이 없습니다(PowerShell 5.1에서 깨짐)") }
    }
    foreach ($f in Get-ChildItem $RepoRoot -Filter *.cmd) {
        $text = [IO.File]::ReadAllText($f.FullName)
        if ($text -match "[^\r]`n") { $problems.Add("$($f.Name): CRLF가 아닌 줄바꿈") }
    }
    $jsonFiles = @(Get-ChildItem (Join-Path $RepoRoot 'src') -Recurse -Filter *.json | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }) +
                 @(Get-ChildItem (Join-Path $RepoRoot 'docs') -Recurse -Filter *.json)
    foreach ($f in $jsonFiles) {
        # 문구 파일은 줄 단위 // 주석을 허용한다(앱도 주석을 건너뛰고 읽음)
        $text = [IO.File]::ReadAllText($f.FullName, [Text.Encoding]::UTF8) -replace '(?m)^\s*//.*$', ''
        try { $null = $text | ConvertFrom-Json -ErrorAction Stop }
        catch { $problems.Add("$($f.Name): JSON 오류 $($_.Exception.Message)") }
    }
    $analyzer = ''
    if (Get-Module -ListAvailable PSScriptAnalyzer) {
        $findings = @(Invoke-ScriptAnalyzer -Path (Join-Path $RepoRoot 'tools') -Severity Error, Warning -ExcludeRule PSAvoidUsingWriteHost, PSUseShouldProcessForStateChangingFunctions)
        foreach ($x in $findings | Where-Object Severity -eq 'Error') { $problems.Add("$($x.ScriptName):$($x.Line) [$($x.RuleName)] $($x.Message)") }
        $warn = @($findings | Where-Object Severity -eq 'Warning').Count
        $analyzer = ", PSScriptAnalyzer 경고 $warn"
    } else {
        $analyzer = ', PSScriptAnalyzer 미설치(Install-Module PSScriptAnalyzer -Scope CurrentUser)'
    }
    if ($problems.Count -gt 0) {
        $problems | ForEach-Object { Write-Host "    $_" -ForegroundColor Yellow }
        throw "$($problems.Count)개 문제"
    }
    "ps1 $($ps1.Count)개, json $($jsonFiles.Count)개$analyzer"
}

# ── 7. smoke(빌드 산출물) ──────────────────────────────────
$skipSmoke = $Quick -or $NoSmoke
Invoke-Stage 'smoke' -Skip:($skipSmoke -or -not $buildOk) -SkipReason $(if (-not $buildOk) { '빌드 실패' } else { '-Quick/-NoSmoke' }) {
    Exec @('test', (Join-Path $RepoRoot 'tests\Panely.App.Tests\Panely.App.Tests.csproj'), '-c', 'Release', '--no-build', '-nologo', '--filter', 'Category=Smoke')
    '실제 앱 실행·종료·상태 저장·single-instance 전달'
}

# ── 8. 게시본 smoke ───────────────────────────────────────
$skipPublish = $Quick -or $NoPublish -or $NoSmoke
Invoke-Stage 'publish smoke' -Skip:($skipPublish -or -not $buildOk) -SkipReason $(if (-not $buildOk) { '빌드 실패' } else { '-Quick/-NoPublish' }) {
    $out = Join-Path $artifacts 'publish'
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    Exec @('publish', $AppProject, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishReadyToRun=false', '-o', $out, '-nologo', '-v', 'quiet')
    $env:PANELY_SMOKE_EXE = Join-Path $out 'Panely.exe'
    try {
        Exec @('test', (Join-Path $RepoRoot 'tests\Panely.App.Tests\Panely.App.Tests.csproj'), '-c', 'Release', '--no-build', '-nologo', '--filter', 'Category=Smoke')
    } finally {
        Remove-Item Env:PANELY_SMOKE_EXE -ErrorAction SilentlyContinue
    }
    $mb = [math]::Round(((Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 0)
    "self-contained 게시본 ${mb}MB로 smoke 통과"
}

# ── 요약 ─────────────────────────────────────────────────
Write-Host ''
Write-Host '──────────────── 검증 요약 ────────────────' -ForegroundColor Cyan
foreach ($r in $results) {
    $color = switch ($r.Result) { 'PASS' { 'Green' } 'FAIL' { 'Red' } default { 'DarkGray' } }
    Write-Host ("  {0,-5} {1,-22} {2,6}s  {3}" -f $r.Result, $r.Stage, $r.Seconds, $r.Detail) -ForegroundColor $color
}
$failedStages = @($results | Where-Object Result -eq 'FAIL')
$elapsed = [math]::Round(((Get-Date) - $started).TotalSeconds, 0)
if ($failedStages.Count -gt 0) {
    Write-Host "실패 $($failedStages.Count)단계 (${elapsed}s). 로그: $artifacts" -ForegroundColor Red
    exit 1
}
Write-Host "모든 검증 통과 (${elapsed}s)" -ForegroundColor Green
exit 0
