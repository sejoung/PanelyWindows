<#
.SYNOPSIS
  09-delivery-and-qa.md의 테스트 fixture를 생성한다(재배포 가능한 합성 이미지만 사용).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\make-samples.ps1 -Out D:\PanelySamples
#>
param(
    [Parameter(Mandatory = $true)][string]$Out
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function New-Page([string]$path, [int]$w, [int]$h, [string]$label, [string]$format = 'png') {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAlias'
    $hue = ([Math]::Abs($label.GetHashCode()) % 360)
    $bg = [System.Drawing.Color]::FromArgb(255, 200 + ($hue % 55), 190 + (($hue * 7) % 60), 190 + (($hue * 13) % 60))
    $g.Clear($bg)
    # 경계 색: 위=빨강, 아래=파랑, 왼쪽=초록, 오른쪽=주황 (자동 이동/정렬 검증)
    $t = [Math]::Max(6, [int]($w / 80))
    $g.FillRectangle([System.Drawing.Brushes]::Red, 0, 0, $w, $t)
    $g.FillRectangle([System.Drawing.Brushes]::Blue, 0, $h - $t, $w, $t)
    $g.FillRectangle([System.Drawing.Brushes]::Green, 0, 0, $t, $h)
    $g.FillRectangle([System.Drawing.Brushes]::Orange, $w - $t, 0, $t, $h)
    $size = [Math]::Max(12, [Math]::Min($w, $h) / 6)
    $font = New-Object System.Drawing.Font 'Segoe UI', $size, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $small = New-Object System.Drawing.Font 'Segoe UI', ([Math]::Max(10, $size / 3)), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
    $g.DrawString($label, $font, [System.Drawing.Brushes]::Black, (New-Object System.Drawing.RectangleF 0, 0, $w, $h), $sf)
    $sf.LineAlignment = 'Near'
    $g.DrawString('TOP', $small, [System.Drawing.Brushes]::DarkRed, (New-Object System.Drawing.RectangleF 0, ($t + 4), $w, $h), $sf)
    $sf.LineAlignment = 'Far'
    $g.DrawString("BOTTOM  ${w}x${h}", $small, [System.Drawing.Brushes]::DarkBlue, (New-Object System.Drawing.RectangleF 0, 0, $w, ($h - $t - 4)), $sf)
    $g.Dispose()
    $dir = Split-Path $path -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    if ($format -eq 'jpg') { $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Jpeg) }
    elseif ($format -eq 'bmp') { $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Bmp) }
    elseif ($format -eq 'gif') { $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Gif) }
    elseif ($format -eq 'tif') { $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Tiff) }
    else { $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}

function New-Book([string]$dir, [int]$count, [int]$w = 720, [int]$h = 1080, [string]$prefix = '') {
    for ($i = 1; $i -le $count; $i++) {
        New-Page (Join-Path $dir ('{0:D3}.png' -f $i)) $w $h "$prefix$i"
    }
}

function Compress-Folder([string]$dir, [string]$zipPath) {
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    $parent = Split-Path $zipPath -Parent
    if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Force $parent | Out-Null }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($dir, $zipPath, [System.IO.Compression.CompressionLevel]::Fastest, $false)
}

$root = New-Item -ItemType Directory -Force $Out
$tmp = Join-Path $env:TEMP ("panely-samples-" + [Guid]::NewGuid())
New-Item -ItemType Directory -Force $tmp | Out-Null

# single-one: 이미지 1개
New-Book (Join-Path $Out 'single-one') 1

# order-24: 자연 정렬과 숨김 제외
$order = Join-Path $Out 'order-24'
$names = @('1', '2', '10', '01x', '3', '4', '5', '6', '7', '8', '9', '11', '12', '20', '21', '100', 'a', 'B', 'c', '한글', 'Ａ', 'page２', 'page10', 'z')
foreach ($n in $names) { New-Page (Join-Path $order "$n.png") 600 900 $n }
New-Page (Join-Path $order '.hidden.png') 600 900 'HIDDEN'
New-Item -ItemType Directory -Force (Join-Path $order '__MACOSX') | Out-Null
New-Page (Join-Path $order '__MACOSX\._1.png') 600 900 'MAC'
Set-Content -Path (Join-Path $order 'notes.txt') -Value 'not a page'
$hiddenAttr = Join-Path $order 'system-hidden.png'
New-Page $hiddenAttr 600 900 'SYS'
(Get-Item $hiddenAttr).Attributes = 'Hidden'

# double-odd/even: 크기 다른 좌우
$odd = Join-Path $Out 'double-odd'
for ($i = 1; $i -le 5; $i++) { New-Page (Join-Path $odd ('{0:D2}.png' -f $i)) (600 + ($i % 2) * 200) (900 + ($i % 2) * 300) "$i" }
$even = Join-Path $Out 'double-even'
for ($i = 1; $i -le 6; $i++) { New-Page (Join-Path $even ('{0:D2}.jpg' -f $i)) (600 + ($i % 2) * 200) (900 + ($i % 2) * 300) "$i" 'jpg' }

# tall-webtoon: 720x12000 + 720x576 조각
$web = Join-Path $Out 'tall-webtoon'
New-Page (Join-Path $web '001.png') 720 12000 'TALL'
for ($i = 2; $i -le 12; $i++) { New-Page (Join-Path $web ('{0:D3}.png' -f $i)) 720 576 "$i" }

# short-tail: 긴 중간쪽 + 매우 짧은 마지막쪽
$tail = Join-Path $Out 'short-tail'
New-Page (Join-Path $tail '001.png') 720 3000 '1'
New-Page (Join-Path $tail '002.png') 720 3000 '2'
New-Page (Join-Path $tail '003.png') 720 80 'END'

# formats: 확장자 다양
$fmt = Join-Path $Out 'formats'
New-Page (Join-Path $fmt '01.png') 600 900 'PNG'
New-Page (Join-Path $fmt '02.jpg') 600 900 'JPG' 'jpg'
New-Page (Join-Path $fmt '03.bmp') 600 900 'BMP' 'bmp'
New-Page (Join-Path $fmt '04.gif') 600 900 'GIF' 'gif'
New-Page (Join-Path $fmt '05.tif') 600 900 'TIF' 'tif'
[System.IO.File]::WriteAllBytes((Join-Path $fmt '06.png'), [byte[]](1, 2, 3, 4, 5))  # 손상 이미지 → 오류 타일

# Series A: 3권 CBZ
$series = Join-Path $Out 'Library\Series A'
New-Item -ItemType Directory -Force $series | Out-Null
for ($v = 1; $v -le 3; $v++) {
    $src = Join-Path $tmp "seriesA-$v"
    New-Book $src 24 720 1080 "V$v-"
    Compress-Folder $src (Join-Path $series ('Volume {0:D2}.cbz' -f $v))
}

# series-wrapper: Series/Vol01.zip -> 722/722 pages/*.jpg + Vol02
$wrap = Join-Path $Out 'Library\Wrapper Series'
New-Item -ItemType Directory -Force $wrap | Out-Null
foreach ($v in 1, 2) {
    $src = Join-Path $tmp "wrap-$v"
    New-Book (Join-Path $src '722\722 pages') 6 600 900 "W$v-"
    Compress-Folder $src (Join-Path $wrap ('Vol{0:D2}.zip' -f $v))
}

# zip named .cbr
$fake = Join-Path $tmp 'fake-cbr'
New-Book $fake 4 600 900 'Z'
Compress-Folder $fake (Join-Path $Out 'Library\One-shots\zip-named.cbr')

# nested: zip-in-zip
$nestedRoot = Join-Path $tmp 'nested'
foreach ($v in 1, 2) {
    $inner = Join-Path $tmp "nested-inner-$v"
    New-Book $inner 5 600 900 "N$v-"
    New-Item -ItemType Directory -Force $nestedRoot | Out-Null
    Compress-Folder $inner (Join-Path $nestedRoot ('Inner Volume {0}.cbz' -f $v))
}
Compress-Folder $nestedRoot (Join-Path $Out 'Library\One-shots\nested-pack.zip')

# translation-en: 영어 말풍선 페이지(흰 풍선·검은 풍선·내레이션 상자·그림 위 글자). Windows 영어 OCR로 E2E 확인용.
function New-BalloonPage([string]$path, [string[]]$lines) {
    $w = 1200; $h = 1700
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::White)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::Black), 6
    $sky = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 214, 230, 245))
    $hill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 130, 145, 160))
    $g.FillRectangle($sky, 60, 60, 1080, 760); $g.DrawRectangle($pen, 60, 60, 1080, 760)
    $g.FillEllipse($hill, -100, 600, 1400, 500)
    $g.FillRectangle($sky, 60, 860, 520, 780); $g.DrawRectangle($pen, 60, 860, 520, 780)
    $g.FillRectangle($sky, 620, 860, 520, 780); $g.DrawRectangle($pen, 620, 860, 520, 780)
    $font = New-Object System.Drawing.Font 'Arial', 38, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
    # 내레이션 상자
    $g.FillRectangle([System.Drawing.Brushes]::WhiteSmoke, 90, 90, 560, 90); $g.DrawRectangle((New-Object System.Drawing.Pen ([System.Drawing.Color]::Black), 3), 90, 90, 560, 90)
    $g.DrawString($lines[0], $font, [System.Drawing.Brushes]::Black, (New-Object System.Drawing.RectangleF 90, 90, 560, 90), $sf)
    # 흰 풍선
    $g.FillEllipse([System.Drawing.Brushes]::White, 720, 160, 380, 300); $g.DrawEllipse($pen, 720, 160, 380, 300)
    $g.DrawString($lines[1], $font, [System.Drawing.Brushes]::Black, (New-Object System.Drawing.RectangleF 770, 200, 280, 220), $sf)
    $g.FillEllipse([System.Drawing.Brushes]::White, 110, 960, 420, 340); $g.DrawEllipse($pen, 110, 960, 420, 340)
    $g.DrawString($lines[2], $font, [System.Drawing.Brushes]::Black, (New-Object System.Drawing.RectangleF 160, 1000, 320, 260), $sf)
    # 검은 풍선(흰 글씨)
    $g.FillEllipse([System.Drawing.Brushes]::Black, 670, 960, 420, 340)
    $g.DrawString($lines[3], $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 720, 1000, 320, 260), $sf)
    $g.Dispose()
    $dir = Split-Path $path -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
$tr = Join-Path $Out 'translation-en'
New-BalloonPage (Join-Path $tr '001.png') @('That morning, the town was quiet.', 'Where am I?', 'You are finally awake.', 'I had a strange dream.')
New-BalloonPage (Join-Path $tr '002.png') @('Later that day...', 'Are you hungry?', 'Let us go home together.', 'Thank you very much.')

# empty folder
New-Item -ItemType Directory -Force (Join-Path $Out 'failures\empty') | Out-Null
# corrupt archive
[System.IO.File]::WriteAllBytes((Join-Path $Out 'failures\corrupt.cbz'), [byte[]](0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4, 5, 6))

Remove-Item -Recurse -Force $tmp
Write-Host "Samples written to $Out"
