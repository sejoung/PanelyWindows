<#
.SYNOPSIS
  README 스크린샷(docs\images\*.png)을 다시 만든다.
  데모 만화(make-demo.ps1)를 설치된 Panely로 열어, 실제 데이터와 분리된 데이터 폴더에서
  영어/한국어 UI × 원본/번역 화면을 찍고, 원본·번역 비교 이미지를 만든다. 키 입력 없이 상태 파일과 실행 인자만 쓴다.

  준비: install-local.cmd로 앱을 설치하고, 번역 화면을 찍으려면 앱에서 언어팩
  (말풍선 탐지, PP-OCR, 번역 빠름)을 받아 둔다. 모델은 실제 데이터 폴더에서 하드 링크(안 되면 복사)로 가져온다.
  찍는 동안 Panely 창이 몇 번 열리고 닫힌다.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\readme-screenshots.ps1
  powershell -ExecutionPolicy Bypass -File tools\readme-screenshots.ps1 -Exe src\Panely.App\bin\Release\net10.0-windows10.0.19041.0\Panely.exe
#>
param(
    [string]$Exe = '',
    # 언어팩이 설치된 Models 폴더
    [string]$ModelsFrom = (Join-Path $env:LOCALAPPDATA 'Panely\Models'),
    [string]$WorkDir = '',
    [string]$OutDir = '',
    # 번역이 끝나기를 기다리는 최대 시간(초). 첫 실행은 모델을 불러오느라 오래 걸린다.
    [int]$TranslateTimeout = 180
)

. "$PSScriptRoot\_common.ps1"
$started = Get-Date
if (-not $Exe) { $Exe = Join-Path $DefaultInstallDir 'Panely.exe' }
if (-not $WorkDir) { $WorkDir = Join-Path $RepoRoot 'artifacts\readme' }
if (-not $OutDir) { $OutDir = Join-Path $RepoRoot 'docs\images' }
if (-not (Test-Path $Exe)) { throw "Panely.exe가 없습니다: $Exe`n먼저 install-local.cmd로 설치하거나 -Exe로 경로를 지정하세요." }
$Exe = (Resolve-Path $Exe).Path

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class PanelyShot {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);
}
"@
# 창 크기·잘라낼 영역을 물리 픽셀로 다루기 위해 DPI 인식으로 전환한다
[PanelyShot]::SetProcessDPIAware() | Out-Null
$scale = [PanelyShot]::GetDpiForSystem() / 96.0
# 창은 논리 크기 1400×880 DIP로 고정한다(레이아웃이 같아야 비교 이미지 잘라내기 영역이 맞는다)
$windowW = [int](1400 * $scale); $windowH = [int](880 * $scale)

# 1) 데모 만화와 격리 데이터 폴더
Write-Step '데모 만화 만들기'
if (Test-Path $WorkDir) { Remove-Item $WorkDir -Recurse -Force }
$series = & "$PSScriptRoot\make-demo.ps1" -Out (Join-Path $WorkDir 'demo')
$book = Join-Path $series 'Volume 01.cbz'
$pageCount = 6
$data = Join-Path $WorkDir 'data'
Write-Ok $series

Write-Step '번역 모델 연결'
$canTranslate = $false
if (Test-Path $ModelsFrom) {
    $from = (Resolve-Path $ModelsFrom).Path
    $files = @(Get-ChildItem $from -Recurse -File)
    foreach ($f in $files) {
        $target = Join-Path $data ('Models' + $f.FullName.Substring($from.Length))
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        try { New-Item -ItemType HardLink -Path $target -Target $f.FullName | Out-Null }
        catch { Copy-Item $f.FullName $target }
    }
    $canTranslate = $files.Count -gt 0
}
if ($canTranslate) { Write-Ok "$($files.Count)개 파일: $ModelsFrom" }
else { Write-Warning "언어팩이 없어 번역 화면은 건너뜁니다: $ModelsFrom (앱의 번역 패널에서 언어팩을 받으세요)" }

function Start-Panely([string]$path) {
    $env:PANELY_DATA_ROOT = $data
    $p = Start-Process $Exe -ArgumentList "`"$path`"" -PassThru
    for ($i = 0; $i -lt 80 -and $p.MainWindowHandle -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 250; $p.Refresh() }
    if ($p.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Panely 창이 열리지 않았습니다.' }
    return $p
}

function Stop-Panely($p) {
    [void]$p.CloseMainWindow()
    if (-not $p.WaitForExit(15000)) { $p.Kill() }
}

# 2) 첫 실행: 시리즈 폴더를 열어 라이브러리 루트(시리즈 설정 키)와 첫 권을 상태 파일에 등록한다.
#    cbz를 바로 열면 라이브러리 루트가 없어 시리즈 설정(자동 번역)이 적용되지 않는다.
Write-Step '첫 실행'
$p = Start-Panely $series
Start-Sleep -Seconds 5
Stop-Panely $p
$statePath = Join-Path $data 'Data\state.json'
if (-not (Test-Path $statePath)) { throw "상태 파일이 만들어지지 않았습니다: $statePath" }
$registered = [System.IO.File]::ReadAllText($statePath) | ConvertFrom-Json
if (-not $registered.lastLibraryRoot -or -not ($registered.sources | Where-Object { $_.title -eq 'Volume 01' })) {
    throw '첫 실행에서 라이브러리와 첫 권이 등록되지 않았습니다.'
}

function Set-State([string]$language, [bool]$translate) {
    $state = [System.IO.File]::ReadAllText($statePath) | ConvertFrom-Json
    $s = $state.settings
    $s.appLanguage = $language
    $s.layout = 'double'; $s.doublePageCoverAlone = $true
    $s.sidebarPinned = $true; $s.toolbarPinned = $true; $s.thumbnailSidebarVisible = $true
    $s.translationEnabled = $translate
    $s.translationTargetLanguage = $(if ($translate) { 'ko' } else { $null })
    # 표지 다음의 2-3쪽 펼침
    $bookId = ($state.sources | Where-Object { $_.kind -eq 'archive' -and $_.title -eq 'Volume 01' }).id
    $state.positions."source:$bookId".pageIndex = 1
    $pref = [pscustomobject]@{
        direction = $null; layout = 'double'; fitMode = 'fitScreen'
        translationSource = 'en'; translationTarget = 'ko'; autoTranslate = $translate
        updatedAt = (Get-Date).ToUniversalTime().ToString('o')
    }
    $state.seriesPreferences = [pscustomobject]@{ "series:$($state.lastLibraryRoot)" = $pref }
    [System.IO.File]::WriteAllText($statePath, ($state | ConvertTo-Json -Depth 30), (New-Object System.Text.UTF8Encoding $false))
}

function Wait-Translation {
    $cache = Join-Path $data 'Cache\translation'
    $deadline = (Get-Date).AddSeconds($TranslateTimeout)
    while ((Get-Date) -lt $deadline) {
        $done = @(Get-ChildItem $cache -Recurse -File -ErrorAction SilentlyContinue).Count
        if ($done -ge $pageCount) { Start-Sleep -Seconds 3; return }
        Start-Sleep -Seconds 1
    }
    throw "번역이 ${TranslateTimeout}초 안에 끝나지 않았습니다. 진단 로그: $(Join-Path $data 'Diagnostics')"
}

# 창을 열고 찍어 PNG(물리 픽셀)로 저장한다
function Save-Shot([string]$name, [string]$language, [bool]$translate) {
    Set-State $language $translate
    $p = Start-Panely $book
    try {
        [PanelyShot]::MoveWindow($p.MainWindowHandle, 40, 40, $windowW, $windowH, $true) | Out-Null
        if ($translate) { Wait-Translation } else { Start-Sleep -Seconds 5 }
        $r = New-Object PanelyShot+RECT
        [PanelyShot]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
        $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $hdc = $g.GetHdc(); [PanelyShot]::PrintWindow($p.MainWindowHandle, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc); $g.Dispose()
        $path = Join-Path $WorkDir "$name.png"
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
        Write-Ok "$name ($($r.R - $r.L)x$($r.B - $r.T))"
        return $path
    }
    finally { Stop-Panely $p }
}

Write-Step '화면 찍기'
$shots = [ordered]@{}
$shots['reader-en'] = Save-Shot 'reader-en' 'en' $false
$shots['reader-ko'] = Save-Shot 'reader-ko' 'ko' $false
if ($canTranslate) {
    $shots['translation-en'] = Save-Shot 'translation-en' 'en' $true
    $shots['translation-ko'] = Save-Shot 'translation-ko' 'ko' $true
}

# 3) README용으로 줄이고 비교 이미지 만들기
Write-Step "이미지 저장: $OutDir"
New-Item -ItemType Directory -Force $OutDir | Out-Null

function Save-Resized([string]$source, [string]$target, [int]$width) {
    $img = [System.Drawing.Image]::FromFile($source)
    $height = [int]($img.Height * $width / $img.Width)
    $bmp = New-Object System.Drawing.Bitmap $width, $height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    $g.DrawImage($img, 0, 0, $width, $height)
    $g.Dispose(); $img.Dispose()
    $bmp.Save($target, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    Write-Ok (Split-Path $target -Leaf)
}

foreach ($name in $shots.Keys) { Save-Resized $shots[$name] (Join-Path $OutDir "$name.png") 1600 }

if ($canTranslate) {
    # 펼침면의 왼쪽 쪽(창 기준 논리 좌표 260,126 크기 472×681)을 원본/번역으로 나란히 놓는다
    $crop = New-Object System.Drawing.Rectangle ([int](260 * $scale)), ([int](126 * $scale)), ([int](472 * $scale)), ([int](681 * $scale))
    $before = [System.Drawing.Image]::FromFile($shots['reader-en'])
    $after = [System.Drawing.Image]::FromFile($shots['translation-en'])
    $pad = 40; $gap = 56; $cw = 560; $ch = [int]($crop.Height * $cw / $crop.Width)
    $bmp = New-Object System.Drawing.Bitmap ($pad * 2 + $cw * 2 + $gap), ($pad * 2 + $ch)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(255, 17, 19, 24))
    $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.SmoothingMode = 'AntiAlias'
    $g.DrawImage($before, (New-Object System.Drawing.Rectangle $pad, $pad, $cw, $ch), $crop, 'Pixel')
    $g.DrawImage($after, (New-Object System.Drawing.Rectangle ($pad + $cw + $gap), $pad, $cw, $ch), $crop, 'Pixel')
    $mx = $pad + $cw + $gap / 2; $my = $pad + $ch / 2
    $arrow = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ($mx - 12), ($my - 18)),
        (New-Object System.Drawing.PointF ($mx + 14), $my),
        (New-Object System.Drawing.PointF ($mx - 12), ($my + 18)))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 10, 132, 255))), $arrow)
    $g.Dispose(); $before.Dispose(); $after.Dispose()
    $bmp.Save((Join-Path $OutDir 'before-after.png'), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    Write-Ok 'before-after.png'
}

Write-Step "완료 ($([int]((Get-Date) - $started).TotalSeconds)초)"
Write-Note "원본 크기 화면과 데모 데이터: $WorkDir"
