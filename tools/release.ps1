<#
.SYNOPSIS
  버전을 올리고 검증한 뒤 태그를 만들어 push한다. 태그 push가 .github\workflows\release.yml을 실행해
  win-x64/win-arm64 ZIP을 GitHub 릴리스로 올린다.

.DESCRIPTION
  1. 확인     main 브랜치, 작업 트리 깨끗함, origin/main보다 뒤처지지 않음, 태그 중복 없음,
              릴리스 노트 docs\releases\vX.Y.Z.md가 커밋되어 있음(release.yml이 릴리스 본문으로 씀)
  2. 검증     tools\verify.ps1 전체(smoke·게시본 smoke 포함)
  3. 버전     Directory.Build.props의 VersionPrefix와 src\Panely.App\app.manifest의 assemblyIdentity version(X.Y.Z.0)을
              바꾼 뒤 커밋 "release: vX.Y.Z"
  4. 태그     annotated 태그 vX.Y.Z
  5. push     main과 태그를 한 번에(--atomic)

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\release.ps1 patch      # 1.0.0 → 1.0.1
  powershell -ExecutionPolicy Bypass -File tools\release.ps1 minor      # 1.0.1 → 1.1.0
  powershell -ExecutionPolicy Bypass -File tools\release.ps1 major      # 1.1.0 → 2.0.0
  powershell -ExecutionPolicy Bypass -File tools\release.ps1 1.0.0      # 버전 직접 지정(첫 릴리스처럼 현재 버전 그대로도 가능)
  powershell -ExecutionPolicy Bypass -File tools\release.ps1 patch -DryRun   # 검증까지만, git은 건드리지 않음
#>
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Bump,
    [switch]$SkipVerify,
    [switch]$DryRun,
    [switch]$Yes
)

. "$PSScriptRoot\_common.ps1"
$propsFile = Join-Path $RepoRoot 'Directory.Build.props'
$manifestFile = Join-Path $RepoRoot 'src\Panely.App\app.manifest'

function Invoke-Git([string[]]$arguments) {
    $out = & git -C $RepoRoot @arguments
    if ($LASTEXITCODE -ne 0) { throw "git $($arguments -join ' ') 실패 (exit $LASTEXITCODE)" }
    return $out
}

# ── 1. 확인 ─────────────────────────────────────────────
Write-Step '저장소 상태 확인'
$branch = Invoke-Git @('rev-parse', '--abbrev-ref', 'HEAD')
if ($branch -ne 'main') { throw "main 브랜치에서 실행하세요(현재: $branch)." }
if (Invoke-Git @('status', '--porcelain')) { throw '커밋하지 않은 변경이 있습니다. 커밋하거나 stash한 뒤 다시 실행하세요.' }
Invoke-Git @('fetch', 'origin', '--tags', '--quiet') | Out-Null
$behind = [int](Invoke-Git @('rev-list', '--count', 'HEAD..origin/main'))
if ($behind -gt 0) { throw "origin/main보다 커밋 $behind개 뒤처져 있습니다. 'git pull' 후 다시 실행하세요." }

$props = [IO.File]::ReadAllText($propsFile)
$match = [regex]::Match($props, '<VersionPrefix>(\d+)\.(\d+)\.(\d+)</VersionPrefix>')
if (-not $match.Success) { throw 'Directory.Build.props에서 <VersionPrefix>X.Y.Z</VersionPrefix>를 찾지 못했습니다.' }
$current = [version]"$($match.Groups[1].Value).$($match.Groups[2].Value).$($match.Groups[3].Value)"

switch -Regex ($Bump) {
    '^patch$' { $next = [version]::new($current.Major, $current.Minor, $current.Build + 1) }
    '^minor$' { $next = [version]::new($current.Major, $current.Minor + 1, 0) }
    '^major$' { $next = [version]::new($current.Major + 1, 0, 0) }
    '^\d+\.\d+\.\d+$' { $next = [version]$Bump }
    default { throw "patch, minor, major 또는 X.Y.Z 형식으로 지정하세요(입력: $Bump)." }
}
if ($next -lt $current) { throw "새 버전 $next 가 현재 버전 $current 보다 낮습니다." }
$tag = "v$next"
if (& git -C $RepoRoot tag --list $tag) { throw "태그 $tag 가 이미 있습니다." }
if (Invoke-Git @('ls-remote', '--tags', 'origin', "refs/tags/$tag")) { throw "원격에 태그 $tag 가 이미 있습니다." }
# 작업 트리가 깨끗한지 위에서 확인했으므로, 파일이 있으면 커밋된 상태다
$notesFile = "docs\releases\$tag.md"
if (-not (Test-Path (Join-Path $RepoRoot $notesFile))) { throw "릴리스 노트 $notesFile 가 없습니다. 이전 버전 노트를 참고해 작성·커밋한 뒤 다시 실행하세요." }

$manifest = [IO.File]::ReadAllText($manifestFile)
$manifestMatch = [regex]::Match($manifest, '(<assemblyIdentity\s+version=")(\d+\.\d+\.\d+\.\d+)(")')
if (-not $manifestMatch.Success) { throw 'app.manifest에서 <assemblyIdentity version="X.Y.Z.W"를 찾지 못했습니다.' }
$manifestVersion = "$next.0"

Write-Ok "현재 $current → 릴리스 $tag"
if (-not $DryRun -and -not $Yes) {
    $answer = Read-Host "    $tag 를 검증 후 커밋·태그·push합니다. 계속할까요? (y/N)"
    if ($answer -notmatch '^(y|yes)$') { Write-Note '취소했습니다.'; exit 1 }
}

# ── 2. 검증 ─────────────────────────────────────────────
if ($SkipVerify) {
    Write-Step '검증 (건너뜀: -SkipVerify)'
} else {
    Write-Step '검증 (tools\verify.ps1)'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'verify.ps1')
    if ($LASTEXITCODE -ne 0) { throw '검증이 실패해 릴리스를 중단합니다.' }
}

if ($DryRun) {
    Write-Step '완료 (-DryRun: 버전 변경·커밋·태그·push를 하지 않았습니다)'
    exit 0
}

# ── 3. 버전 ─────────────────────────────────────────────
$utf8 = New-Object Text.UTF8Encoding($false)
$changed = @()
if ($next -ne $current) {
    Write-Step "VersionPrefix $current → $next"
    $updated = $props.Substring(0, $match.Index) + "<VersionPrefix>$next</VersionPrefix>" + $props.Substring($match.Index + $match.Length)
    [IO.File]::WriteAllText($propsFile, $updated, $utf8)
    $changed += 'Directory.Build.props'
}
if ($manifestMatch.Groups[2].Value -ne $manifestVersion) {
    Write-Step "app.manifest assemblyIdentity $($manifestMatch.Groups[2].Value) → $manifestVersion"
    $group = $manifestMatch.Groups[2]
    $updated = $manifest.Substring(0, $group.Index) + $manifestVersion + $manifest.Substring($group.Index + $group.Length)
    [IO.File]::WriteAllText($manifestFile, $updated, $utf8)
    $changed += 'src/Panely.App/app.manifest'
}
if ($changed.Count -gt 0) {
    Invoke-Git (@('add') + $changed) | Out-Null
    Invoke-Git @('commit', '--quiet', '-m', "release: $tag") | Out-Null
    Write-Ok "커밋 release: $tag ($($changed -join ', '))"
} else {
    Write-Step "버전이 이미 $next 입니다(버전 커밋 없음)"
}

# ── 4. 태그 / 5. push ────────────────────────────────────
Write-Step "태그 $tag 생성 후 push"
Invoke-Git @('tag', '-a', $tag, '-m', "Panely $tag") | Out-Null
& git -C $RepoRoot push --atomic origin main "refs/tags/$tag"
if ($LASTEXITCODE -ne 0) {
    Write-Host "    push 실패. 로컬 상태를 되돌리려면:" -ForegroundColor Red
    Write-Host "      git tag -d $tag" -ForegroundColor Yellow
    if ($changed.Count -gt 0) { Write-Host '      git reset --hard HEAD~1' -ForegroundColor Yellow }
    exit 1
}

$remote = (Invoke-Git @('remote', 'get-url', 'origin')) -replace '\.git$', '' -replace '^git@github\.com:', 'https://github.com/'
Write-Ok "push 완료. 릴리스 빌드: $remote/actions"
Write-Ok "릴리스 페이지: $remote/releases/tag/$tag"
