<#
.SYNOPSIS
  사용 설명서(docs\manual*.md) 스크린샷(docs\images\manual\*.png)을 다시 만든다.
  readme-screenshots.ps1과 같은 데모 만화·격리 데이터 폴더를 쓰고, 메뉴는 UI Automation으로 실행한다(키 입력 없음).
  말풍선 팝오버 화면만 실제 마우스로 한 번 클릭하며, 그 위치가 Panely 창일 때만 누른다.

  준비: Release 빌드(또는 install-local.cmd)와 언어팩(말풍선 탐지, PP-OCR, 번역).
  찍는 동안 Panely 창이 여러 번 열리고 닫힌다. 마우스를 움직이지 말고 기다린다(약 3분).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\manual-screenshots.ps1
  powershell -ExecutionPolicy Bypass -File tools\manual-screenshots.ps1 -Languages ko -Only settings-general
#>
param(
    [string]$Exe = '',
    [string]$ModelsFrom = (Join-Path $env:LOCALAPPDATA 'Panely\Models'),
    [string]$WorkDir = '',
    [string]$OutDir = '',
    [string[]]$Languages = @('en', 'ko'),
    # 이 이름의 화면만 찍는다(예: settings-general). 비우면 전부
    [string[]]$Only = @(),
    [int]$TranslateTimeout = 240
)

. "$PSScriptRoot\_common.ps1"
$started = Get-Date
# -File로 실행하면 "a,b"가 문자열 하나로 들어온다
$Languages = @($Languages | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
if (-not $Exe) {
    $built = Join-Path $RepoRoot 'src\Panely.App\bin\Release\net10.0-windows10.0.19041.0\Panely.exe'
    $Exe = if (Test-Path $built) { $built } else { Join-Path $DefaultInstallDir 'Panely.exe' }
}
if (-not $WorkDir) { $WorkDir = Join-Path $RepoRoot 'artifacts\manual' }
if (-not $OutDir) { $OutDir = Join-Path $RepoRoot 'docs\images\manual' }
if (-not (Test-Path $Exe)) { throw "Panely.exe가 없습니다: $Exe" }
$Exe = (Resolve-Path $Exe).Path
# 빌드 출력(프레임워크 의존)은 사용자 폴더에 설치된 .NET도 찾을 수 있게 DOTNET_ROOT를 알려 준다
if (-not (Test-Path (Join-Path (Split-Path $Exe) 'coreclr.dll'))) { $env:DOTNET_ROOT = Split-Path (Get-Dotnet) }

Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class ManualShot {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr p);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  // 화면 위에서 아래 순서(Z-order)로 프로세스의 보이는 최상위 창
  public static List<IntPtr> TopWindows(uint pid) {
    var list = new List<IntPtr>();
    EnumWindows((h, p) => { uint q; GetWindowThreadProcessId(h, out q); if (q == pid && IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
    return list;
  }
}
"@
[ManualShot]::SetProcessDPIAware() | Out-Null
$scale = [ManualShot]::GetDpiForSystem() / 96.0
$windowW = [int](1400 * $scale); $windowH = [int](880 * $scale)
# README 이미지와 같은 배율: 1400 DIP 창이 1600px
$outputScale = 1600.0 / $windowW

$names = @{
    en = @{ File = 'File'; View = 'View'; Window = 'Window'; Settings = 'Settings…'; General = 'General'; Experimental = 'Experimental'
            EditBalloons = 'Edit Balloons'; Export = 'Export Translated Book…'; Jobs = 'Jobs'; ChooseLanguage = 'Choose Language'
            Panel = 'Translation Panel…'; AiChat = 'Improve with an AI Chat…' }
    ko = @{ File = '파일'; View = '보기'; Window = '창'; Settings = '설정…'; General = '일반'; Experimental = '실험적 기능'
            EditBalloons = '말풍선 편집'; Export = '번역본 내보내기…'; Jobs = '작업'; ChooseLanguage = '언어 선택'
            Panel = '번역 패널…'; AiChat = 'AI 채팅으로 다듬기…' }
}

# ── 데모 만화와 격리 데이터 폴더 ─────────────────────────────
Write-Step '데모 만화 만들기'
if (Test-Path $WorkDir) { Remove-Item $WorkDir -Recurse -Force }
$series = & "$PSScriptRoot\make-demo.ps1" -Out (Join-Path $WorkDir 'demo')
$book = Join-Path $series 'Volume 01.cbz'
$pageCount = 6
$data = Join-Path $WorkDir 'data'
$statePath = Join-Path $data 'Data\state.json'
$translationCache = Join-Path $data 'Cache\translation'

Write-Step '번역 모델 연결'
if (-not (Test-Path $ModelsFrom)) { throw "언어팩이 없습니다: $ModelsFrom" }
$from = (Resolve-Path $ModelsFrom).Path
foreach ($f in Get-ChildItem $from -Recurse -File) {
    $target = Join-Path $data ('Models' + $f.FullName.Substring($from.Length))
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    try { New-Item -ItemType HardLink -Path $target -Target $f.FullName | Out-Null } catch { Copy-Item $f.FullName $target }
}

function Start-Panely([string]$path) {
    $env:PANELY_DATA_ROOT = $data
    $p = Start-Process $Exe -ArgumentList "`"$path`"" -PassThru
    for ($i = 0; $i -lt 80 -and $p.MainWindowHandle -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 250; $p.Refresh() }
    if ($p.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Panely 창이 열리지 않았습니다.' }
    [ManualShot]::MoveWindow($p.MainWindowHandle, 40, 40, $windowW, $windowH, $true) | Out-Null
    return $p
}

function Stop-Panely($p) {
    if ($p.HasExited) { return }
    # 번역·내보내기 중 종료 확인 창이 뜰 수 있으므로 기다리지 않고 끝낸다
    $p.Kill(); $p.WaitForExit(5000) | Out-Null
}

# 첫 실행: 시리즈 폴더를 열어 라이브러리 루트와 첫 권을 상태 파일에 등록한다
Write-Step '첫 실행'
$p = Start-Panely $series
Start-Sleep -Seconds 5
[void]$p.CloseMainWindow(); $p.WaitForExit(15000) | Out-Null
if (-not (Test-Path $statePath)) { throw "상태 파일이 만들어지지 않았습니다: $statePath" }

function Set-State([string]$language, [bool]$translate, [string]$layout = 'double', [int]$pageIndex = 1) {
    $state = [IO.File]::ReadAllText($statePath) | ConvertFrom-Json
    $s = $state.settings
    $s.appLanguage = $language
    $s.layout = $layout; $s.doublePageCoverAlone = $true
    $s.sidebarPinned = $true; $s.toolbarPinned = $true; $s.thumbnailSidebarVisible = $true
    $s.translationEnabled = $true
    $s.translationTargetLanguage = 'ko'
    $s.translationOverlayVisible = $translate
    $bookId = ($state.sources | Where-Object { $_.kind -eq 'archive' -and $_.title -eq 'Volume 01' }).id
    $state.positions."source:$bookId".pageIndex = $pageIndex
    $pref = [pscustomobject]@{
        direction = $null; layout = $layout; fitMode = 'fitScreen'
        translationSource = 'en'; translationTarget = 'ko'; autoTranslate = $translate
        updatedAt = (Get-Date).ToUniversalTime().ToString('o')
    }
    $state.seriesPreferences = [pscustomobject]@{ "series:$($state.lastLibraryRoot)" = $pref }
    [IO.File]::WriteAllText($statePath, ($state | ConvertTo-Json -Depth 30), (New-Object Text.UTF8Encoding $false))
}

function Wait-Translation {
    $deadline = (Get-Date).AddSeconds($TranslateTimeout)
    while ((Get-Date) -lt $deadline) {
        if (@(Get-ChildItem $translationCache -Recurse -File -ErrorAction SilentlyContinue).Count -ge $pageCount) { Start-Sleep -Seconds 3; return }
        Start-Sleep -Seconds 1
    }
    throw "번역이 ${TranslateTimeout}초 안에 끝나지 않았습니다. 로그: $(Join-Path $data 'Diagnostics')"
}

# ── UI Automation ────────────────────────────────────────
$UIA = [System.Windows.Automation.AutomationElement]
$Tree = [System.Windows.Automation.TreeScope]

function Find-Element($root, [string]$name, $type = $null, [int]$timeoutMs = 5000) {
    $cond = New-Object System.Windows.Automation.PropertyCondition ($UIA::NameProperty, $name)
    if ($type) { $cond = New-Object System.Windows.Automation.AndCondition ($cond, (New-Object System.Windows.Automation.PropertyCondition ($UIA::ControlTypeProperty, $type))) }
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    do {
        $roots = if ($root -is [System.Diagnostics.Process]) {
            [ManualShot]::TopWindows([uint32]$root.Id) | ForEach-Object { $UIA::FromHandle($_) }
        } else { @($root) }
        foreach ($r in $roots) { $found = $r.FindFirst($Tree::Descendants, $cond); if ($found) { return $found } }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    throw "UI 요소를 찾지 못했습니다: '$name'"
}

function Invoke-Element($element) {
    $pattern = $null
    if ($element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke(); return }
    if ($element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select(); return }
    if ($element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) { $pattern.Toggle(); return }
    throw "실행할 수 없는 요소: $($element.Current.Name)"
}

# 메뉴 막대의 메뉴를 펼친 뒤 하위 항목을 실행한다
function Invoke-Menu($p, [string]$menu, [string]$item) {
    $top = Find-Element $UIA::FromHandle($p.MainWindowHandle) $menu ([System.Windows.Automation.ControlType]::MenuItem)
    $top.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Invoke-Element (Find-Element $p $item ([System.Windows.Automation.ControlType]::MenuItem))
}

# 툴바 버튼으로 연 메뉴의 항목을 실행한다
function Invoke-ButtonMenu($p, [string]$button, [string]$item) {
    Invoke-Element (Find-Element $UIA::FromHandle($p.MainWindowHandle) $button)
    Invoke-Element (Find-Element $p $item ([System.Windows.Automation.ControlType]::MenuItem))
}

# 메인 창이 아닌 가장 위의 창(대화 상자, 설정 창 등)을 기다린다
function Wait-OtherWindow($p, [int]$timeoutMs = 8000) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    do {
        $other = [ManualShot]::TopWindows([uint32]$p.Id) | Where-Object { $_ -ne $p.MainWindowHandle } |
            Where-Object { $r = New-Object ManualShot+RECT; [ManualShot]::GetWindowRect($_, [ref]$r) | Out-Null; ($r.R - $r.L) -gt 300 } |
            Select-Object -First 1
        if ($other) { Start-Sleep -Milliseconds 800; return $other }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    throw '새 창이 열리지 않았습니다.'
}

# 화면 좌표(물리 픽셀)를 클릭한다. 그 지점이 Panely 창이 아니면 누르지 않는다.
function Click-InWindow($p, [int]$x, [int]$y) {
    $topmost = [IntPtr](-1); $notTopmost = [IntPtr](-2)
    [ManualShot]::SetWindowPos($p.MainWindowHandle, $topmost, 0, 0, 0, 0, 0x0003) | Out-Null
    try {
        Start-Sleep -Milliseconds 300
        $pt = New-Object ManualShot+POINT; $pt.X = $x; $pt.Y = $y
        $hit = [ManualShot]::GetAncestor([ManualShot]::WindowFromPoint($pt), 2)
        if ($hit -ne $p.MainWindowHandle) { throw "클릭할 위치($x,$y)가 Panely 창이 아니어서 중단합니다." }
        [ManualShot]::SetCursorPos($x, $y) | Out-Null
        Start-Sleep -Milliseconds 150
        [ManualShot]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 80
        [ManualShot]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        # 커서가 말풍선 위에 남아 툴팁이 덮지 않도록 창 밖으로 뺀다
        Start-Sleep -Milliseconds 200
        [ManualShot]::SetCursorPos(5, 5) | Out-Null
    } finally {
        [ManualShot]::SetWindowPos($p.MainWindowHandle, $notTopmost, 0, 0, 0, 0, 0x0003) | Out-Null
    }
}

# 화면 좌표(물리 픽셀)에서 끌어 사각형을 그린다. 버튼을 누른 채로 $whileHeld를 실행한다(선택 사각형 찍기).
function Drag-InWindow($p, [int]$x1, [int]$y1, [int]$x2, [int]$y2, [scriptblock]$whileHeld = $null) {
    $topmost = [IntPtr](-1); $notTopmost = [IntPtr](-2)
    [ManualShot]::SetWindowPos($p.MainWindowHandle, $topmost, 0, 0, 0, 0, 0x0003) | Out-Null
    try {
        Start-Sleep -Milliseconds 300
        foreach ($pt in @(@($x1, $y1), @($x2, $y2))) {
            $at = New-Object ManualShot+POINT; $at.X = $pt[0]; $at.Y = $pt[1]
            if ([ManualShot]::GetAncestor([ManualShot]::WindowFromPoint($at), 2) -ne $p.MainWindowHandle) { throw "끌 위치($($pt[0]),$($pt[1]))가 Panely 창이 아니어서 중단합니다." }
        }
        [ManualShot]::SetCursorPos($x1, $y1) | Out-Null
        Start-Sleep -Milliseconds 150
        [ManualShot]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        try {
            for ($i = 1; $i -le 12; $i++) {
                [ManualShot]::SetCursorPos([int]($x1 + ($x2 - $x1) * $i / 12), [int]($y1 + ($y2 - $y1) * $i / 12)) | Out-Null
                Start-Sleep -Milliseconds 30
            }
            Start-Sleep -Milliseconds 300
            if ($whileHeld) { & $whileHeld }
        } finally {
            [ManualShot]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        }
        Start-Sleep -Milliseconds 200
        [ManualShot]::SetCursorPos(5, 5) | Out-Null
    } finally {
        [ManualShot]::SetWindowPos($p.MainWindowHandle, $notTopmost, 0, 0, 0, 0, 0x0003) | Out-Null
    }
}

function Get-Rect([IntPtr]$hwnd) {
    $r = New-Object ManualShot+RECT
    [ManualShot]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    return $r
}

function Capture-Window([IntPtr]$hwnd) {
    $r = Get-Rect $hwnd
    $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc(); [ManualShot]::PrintWindow($hwnd, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc); $g.Dispose()
    return $bmp
}

# 창 하나를 PrintWindow로 찍어 출력 배율로 줄여 저장한다.
# 메인 창이면 그 위에 뜬 팝업(번역 패널, 말풍선 팝오버 등 WPF Popup은 별도 창)을 Z-order대로 겹쳐 그린다.
function Save-Window([IntPtr]$hwnd, [string]$name, $process = $null) {
    $r = Get-Rect $hwnd
    $w = $r.R - $r.L; $h = $r.B - $r.T
    $raw = Capture-Window $hwnd
    if ($process -and $hwnd -eq $process.MainWindowHandle) {
        $g = [System.Drawing.Graphics]::FromImage($raw)
        $popups = @([ManualShot]::TopWindows([uint32]$process.Id) | Where-Object { $_ -ne $hwnd })
        [array]::Reverse($popups)
        foreach ($popup in $popups) {
            $pr = Get-Rect $popup
            if ($pr.R -le $r.L -or $pr.L -ge $r.R -or $pr.B -le $r.T -or $pr.T -ge $r.B) { continue }
            $layer = Capture-Window $popup
            $g.DrawImage($layer, ($pr.L - $r.L), ($pr.T - $r.T), $layer.Width, $layer.Height); $layer.Dispose()
        }
        $g.Dispose()
    }
    $ow = [int]($w * $outputScale); $oh = [int]($h * $outputScale)
    $bmp = New-Object System.Drawing.Bitmap $ow, $oh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    $g.DrawImage($raw, 0, 0, $ow, $oh); $g.Dispose(); $raw.Dispose()
    $path = Join-Path $OutDir "$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    Write-Ok "$name.png (${ow}x${oh})"
}

function Wants([string]$name) { return $Only.Count -eq 0 -or $Only -contains $name }

# 화면 하나: 상태 설정 → 실행 → 동작 → 찍기 → 종료
function Shoot([string]$name, [string]$lang, [scriptblock]$action, [bool]$translate = $true, [string]$layout = 'double', [int]$pageIndex = 1) {
    if (-not (Wants $name)) { return }
    Set-State $lang $translate $layout $pageIndex
    # 앞 화면에서 고치거나 추가한 말풍선이 남지 않게 한다
    Remove-Item (Join-Path $data 'Data\edits') -Recurse -Force -ErrorAction SilentlyContinue
    $p = Start-Panely $book
    try {
        if ($translate) { Wait-Translation } else { Start-Sleep -Seconds 4 }
        Start-Sleep -Seconds 1
        $hwnd = & $action $p
        if (-not $hwnd) { $hwnd = $p.MainWindowHandle }
        Save-Window $hwnd "$name-$lang" $p
    } finally { Stop-Panely $p }
}

# 화면 구성: README 화면에 영역 번호를 그린다(1600px 기준 좌표)
function Save-Overview([string]$lang) {
    $source = Join-Path $RepoRoot "docs\images\reader-$lang.png"
    $img = [System.Drawing.Image]::FromFile($source)
    $bmp = New-Object System.Drawing.Bitmap $img.Width, $img.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.DrawImage($img, 0, 0, $img.Width, $img.Height); $img.Dispose()
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $accent = [System.Drawing.Color]::FromArgb(255, 255, 159, 10)
    $pen = New-Object System.Drawing.Pen $accent, 4
    $brush = New-Object System.Drawing.SolidBrush $accent
    $font = New-Object System.Drawing.Font 'Segoe UI', 20, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
    # 번호, 영역(x, y, w, h), 번호 위치(x, y)
    $regions = @(
        @(1, (New-Object System.Drawing.Rectangle 12, 36, 316, 30), 342, 51),
        @(2, (New-Object System.Drawing.Rectangle 10, 70, 270, 926), 140, 470),
        @(3, (New-Object System.Drawing.Rectangle 296, 82, 1114, 46), 1150, 105),
        @(4, (New-Object System.Drawing.Rectangle 300, 146, 1106, 774), 852, 520),
        @(5, (New-Object System.Drawing.Rectangle 296, 936, 1114, 50), 1250, 948),
        @(6, (New-Object System.Drawing.Rectangle 1432, 70, 156, 926), 1510, 600)
    )
    foreach ($r in $regions) {
        $g.DrawRectangle($pen, $r[1])
        $d = 40; $cx = $r[2]; $cy = $r[3]
        $g.FillEllipse($brush, $cx - $d / 2, $cy - $d / 2, $d, $d)
        $g.DrawString([string]$r[0], $font, [System.Drawing.Brushes]::Black, (New-Object System.Drawing.RectangleF ($cx - $d / 2), ($cy - $d / 2 + 1), $d, $d), $sf)
    }
    $g.Dispose()
    $bmp.Save((Join-Path $OutDir "overview-$lang.png"), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    Write-Ok "overview-$lang.png"
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
foreach ($lang in $Languages) {
    $n = $names[$lang]
    Write-Step "화면 찍기: $lang"
    if (Wants 'overview') { Save-Overview $lang }

    # 작업 창의 "완료됨"을 보이도록 이 실행에서 번역한다
    if (Wants 'jobs') {
        if (Test-Path $translationCache) { Remove-Item $translationCache -Recurse -Force }
        Shoot 'jobs' $lang { param($p) Invoke-Menu $p $n.Window $n.Jobs; Wait-OtherWindow $p }
    }
    Shoot 'settings-general' $lang { param($p) Invoke-Menu $p $n.File $n.Settings; $w = Wait-OtherWindow $p
        Invoke-Element (Find-Element $UIA::FromHandle($w) $n.General ([System.Windows.Automation.ControlType]::TabItem)); Start-Sleep -Milliseconds 600; $w }
    Shoot 'settings-experimental' $lang { param($p) Invoke-Menu $p $n.File $n.Settings; $w = Wait-OtherWindow $p
        Invoke-Element (Find-Element $UIA::FromHandle($w) $n.Experimental ([System.Windows.Automation.ControlType]::TabItem)); Start-Sleep -Milliseconds 800; $w }
    # 같은 탭을 언어팩 목록이 보이도록 내린다
    Shoot 'settings-packs' $lang { param($p) Invoke-Menu $p $n.File $n.Settings; $w = Wait-OtherWindow $p
        Invoke-Element (Find-Element $UIA::FromHandle($w) $n.Experimental ([System.Windows.Automation.ControlType]::TabItem)); Start-Sleep -Milliseconds 800
        $scrollable = New-Object System.Windows.Automation.PropertyCondition ($UIA::IsScrollPatternAvailableProperty, $true)
        $viewer = $UIA::FromHandle($w).FindFirst($Tree::Descendants, $scrollable)
        $viewer.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).SetScrollPercent(-1, 62); Start-Sleep -Milliseconds 600; $w }
    Shoot 'translation-panel' $lang { param($p) Invoke-ButtonMenu $p $n.ChooseLanguage $n.Panel; Start-Sleep -Milliseconds 800 }
    Shoot 'edit-balloons' $lang { param($p)
        Invoke-Menu $p $n.View $n.EditBalloons; Start-Sleep -Milliseconds 800
        # 펼침면 왼쪽 위 말풍선("Did we miss it?")
        $k = $windowW / 1600.0
        Click-InWindow $p ([int](40 + 724 * $k)) ([int](40 + 300 * $k)); Start-Sleep -Milliseconds 800 }
    # 번역되지 않은 역 표지판("LUNA 12")을 끌어 말풍선을 추가한다. 끄는 중(add-balloon-drag)과 추가한 말풍선의 팝오버(add-balloon)
    Shoot 'add-balloon' $lang { param($p)
        Invoke-Menu $p $n.View $n.EditBalloons; Start-Sleep -Milliseconds 800
        $k = $windowW / 1600.0
        Drag-InWindow $p ([int](40 + 316 * $k)) ([int](40 + 234 * $k)) ([int](40 + 462 * $k)) ([int](40 + 286 * $k)) {
            Save-Window $p.MainWindowHandle "add-balloon-drag-$lang" $p }
        # 글자 인식과 번역이 끝나 말풍선에 번역이 그려질 때까지
        Start-Sleep -Seconds 8
        Click-InWindow $p ([int](40 + 389 * $k)) ([int](40 + 260 * $k)); Start-Sleep -Milliseconds 800 }
    Shoot 'ai-chat' $lang { param($p) Invoke-ButtonMenu $p $n.ChooseLanguage $n.AiChat; Wait-OtherWindow $p }
    Shoot 'export' $lang { param($p) Invoke-Menu $p $n.File $n.Export; Wait-OtherWindow $p }
    # 마지막 펼침(6쪽)의 다음 권 카드
    Shoot 'up-next' $lang { param($p) Start-Sleep -Milliseconds 500 } -translate $false -pageIndex 5
    Shoot 'vertical' $lang { param($p) Start-Sleep -Milliseconds 500 } -translate $false -layout 'vertical' -pageIndex 0
}

Write-Step "완료 ($([int]((Get-Date) - $started).TotalSeconds)초): $OutDir"
