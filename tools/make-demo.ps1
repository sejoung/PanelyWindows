<#
.SYNOPSIS
  README 스크린샷용 데모 만화 "Moonlit Station"(합성 그림, 영어 말풍선)을 만든다.
  저작권 문제가 없는 그림이라 스크린샷을 공개 저장소에 올릴 수 있다. 3권 × 6쪽 CBZ.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\make-demo.ps1 -Out artifacts\readme\demo
#>
param(
    [Parameter(Mandatory = $true)][string]$Out
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression.FileSystem

$W = 1200; $H = 1700
$ink = [System.Drawing.Color]::FromArgb(255, 20, 22, 30)

function C($r, $g, $b) { [System.Drawing.Color]::FromArgb(255, $r, $g, $b) }
function Brush($r, $g, $b) { New-Object System.Drawing.SolidBrush (C $r $g $b) }
function Grad($x, $y, $w, $h, $c1, $c2) {
    New-Object System.Drawing.Drawing2D.LinearGradientBrush ((New-Object System.Drawing.Rectangle $x, $y, $w, $h), $c1, $c2, 90.0)
}
function Centered { $sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'; return $sf }

# 밤하늘 + 달 + 도시 실루엣. kind: station(역 표지판) / train(열차) / interior(달 없음)
function Draw-Scene($g, $x, $y, $w, $h, $seed, [string]$kind) {
    $rnd = New-Object System.Random $seed
    $g.SetClip((New-Object System.Drawing.Rectangle $x, $y, $w, $h))
    $g.FillRectangle((Grad $x $y $w $h (C 18 24 60) (C 92 70 140)), $x, $y, $w, $h)
    for ($i = 0; $i -lt 60; $i++) {
        $s = 2 + $rnd.Next(4)
        $g.FillEllipse((Brush 255 250 220), ($x + $rnd.Next($w)), ($y + $rnd.Next([int]($h * 0.6))), $s, $s)
    }
    if ($kind -ne 'interior') {
        $mx = $x + [int]($w * (0.55 + 0.3 * $rnd.NextDouble())); $my = $y + [int]($h * 0.12); $md = [int]($w * 0.18)
        $g.FillEllipse((Brush 255 244 200), $mx, $my, $md, $md)
        $g.FillEllipse((Brush 240 226 175), ($mx + [int]($w * 0.05)), ($my + [int]($w * 0.06)), [int]($w * 0.035), [int]($w * 0.035))
    }
    $bx = $x
    while ($bx -lt $x + $w) {
        $bw = 50 + $rnd.Next(90); $bh = [int]($h * (0.18 + 0.3 * $rnd.NextDouble()))
        $g.FillRectangle((Brush 28 30 52), $bx, ($y + $h - $bh), $bw, $bh)
        for ($wy = $y + $h - $bh + 14; $wy -lt $y + $h - 20; $wy += 26) {
            for ($wx = $bx + 10; $wx -lt $bx + $bw - 14; $wx += 22) {
                if ($rnd.NextDouble() -lt 0.45) { $g.FillRectangle((Brush 255 214 120), $wx, $wy, 10, 14) }
            }
        }
        $bx += $bw + 6
    }
    if ($kind -eq 'train') {
        $ty = $y + [int]($h * 0.58)
        $g.FillRectangle((Brush 60 64 96), $x, ($ty + 120), $w, 18)
        $g.FillRectangle((Brush 230 236 245), ($x + 40), $ty, ($w - 80), 120)
        $g.FillRectangle((Brush 214 92 92), ($x + 40), ($ty + 84), ($w - 80), 16)
        for ($wx = $x + 70; $wx -lt $x + $w - 120; $wx += 110) { $g.FillRectangle((Brush 255 220 140), $wx, ($ty + 22), 80, 46) }
    }
    if ($kind -eq 'station') {
        $px = $x + [int]($w * 0.15); $py = $y + [int]($h * 0.25)
        $g.FillRectangle((Brush 70 74 110), $x, ($y + $h - 90), $w, 90)
        $g.FillRectangle((Brush 40 44 70), $px, $py, 16, $h)
        $g.FillRectangle((Brush 245 245 235), ($px - 120), $py, 260, 70)
        $f = New-Object System.Drawing.Font 'Arial', 34, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
        $g.DrawString('LUNA  12', $f, (Brush 40 44 70), (New-Object System.Drawing.RectangleF ($px - 120), $py, 260, 70), (Centered))
    }
    $g.ResetClip()
    $g.DrawRectangle((New-Object System.Drawing.Pen $ink, 7), $x, $y, $w, $h)
}

# 흰 타원 말풍선 + 꼬리
function Draw-Balloon($g, $x, $y, $w, $h, [string]$text, [int]$tailX, [int]$tailY) {
    $pen = New-Object System.Drawing.Pen $ink, 5
    $mid = $x + $w / 2
    $tail = [System.Drawing.Point[]]@(
        (New-Object System.Drawing.Point ([int]($mid - 30)), ($y + $h - 30)),
        (New-Object System.Drawing.Point ([int]($mid + 30)), ($y + $h - 30)),
        (New-Object System.Drawing.Point $tailX, $tailY))
    $g.FillPolygon([System.Drawing.Brushes]::White, $tail); $g.DrawPolygon($pen, $tail)
    $g.FillEllipse([System.Drawing.Brushes]::White, $x, $y, $w, $h); $g.DrawEllipse($pen, $x, $y, $w, $h)
    $g.FillPolygon([System.Drawing.Brushes]::White, [System.Drawing.Point[]]@($tail[0], $tail[1], (New-Object System.Drawing.Point ([int]$mid), ($y + $h - 60))))
    $font = New-Object System.Drawing.Font 'Arial', 36, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $g.DrawString($text, $font, (New-Object System.Drawing.SolidBrush $ink), (New-Object System.Drawing.RectangleF ($x + $w * 0.14), ($y + $h * 0.14), ($w * 0.72), ($h * 0.72)), (Centered))
}

# 내레이션 상자
function Draw-Caption($g, $x, $y, $w, $h, [string]$text) {
    $g.FillRectangle((Brush 252 246 226), $x, $y, $w, $h)
    $g.DrawRectangle((New-Object System.Drawing.Pen $ink, 4), $x, $y, $w, $h)
    $font = New-Object System.Drawing.Font 'Georgia', 34, ([System.Drawing.FontStyle]::Italic), ([System.Drawing.GraphicsUnit]::Pixel)
    $g.DrawString($text, $font, (New-Object System.Drawing.SolidBrush $ink), (New-Object System.Drawing.RectangleF $x, $y, $w, $h), (Centered))
}

function New-Canvas {
    $b = New-Object System.Drawing.Bitmap $W, $H
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::White)
    return $b, $g
}

function Save-Canvas($b, $g, [string]$path) { $g.Dispose(); $b.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose() }

function New-Cover([string]$path, [string]$volume) {
    $b, $g = New-Canvas
    Draw-Scene $g 0 0 $W $H 7 'train'
    $title = New-Object System.Drawing.Font 'Georgia', 130, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $sub = New-Object System.Drawing.Font 'Georgia', 56, ([System.Drawing.FontStyle]::Italic), ([System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'
    $g.DrawString("MOONLIT`nSTATION", $title, (Brush 255 244 200), (New-Object System.Drawing.RectangleF 0, 360, $W, 400), $sf)
    $g.DrawString($volume, $sub, (Brush 230 220 255), (New-Object System.Drawing.RectangleF 0, 700, $W, 100), $sf)
    Save-Canvas $b $g $path
}

# 쪽마다 [장면, 시드, 내레이션, 대사 3개]. 번역 화면에서 보이도록 짧고 쉬운 영어로 쓴다.
$pages = @(
    @('station', 11, 'The last train leaves at midnight.', 'Did we miss it?', 'Not yet. Look!', 'It is coming!'),
    @('train', 12, 'And so the journey began.', 'Where does it go?', 'To the moon station.', 'Let us go together.'),
    @('interior', 13, 'Inside, it was warm and quiet.', 'Tickets, please.', 'We do not have any...', 'Your smile will do.'),
    @('train', 14, 'Above the clouds...', 'The stars are so close!', 'Hold on tight.', 'This is the best night.'),
    @('station', 15, 'Next stop: Luna.', 'Are we there yet?', 'Almost. Get ready.', 'I will never forget this.')
)

function New-StoryPage([string]$path, $p) {
    $b, $g = New-Canvas
    Draw-Scene $g 50 50 1100 760 $p[1] $p[0]
    Draw-Scene $g 50 850 530 800 ($p[1] + 100) 'interior'
    Draw-Scene $g 620 850 530 800 ($p[1] + 200) $p[0]
    Draw-Caption $g 80 80 620 90 $p[2]
    Draw-Balloon $g 740 180 380 280 $p[3] 760 560
    Draw-Balloon $g 90 900 440 320 $p[4] 230 1360
    Draw-Balloon $g 660 900 450 320 $p[5] 840 1380
    Save-Canvas $b $g $path
}

$series = Join-Path $Out 'Moonlit Station'
$build = Join-Path $Out '_pages'
New-Item -ItemType Directory -Force $series | Out-Null
foreach ($v in 1..3) {
    $volume = 'Volume {0:00}' -f $v
    $dir = Join-Path $build $volume
    New-Item -ItemType Directory -Force $dir | Out-Null
    New-Cover (Join-Path $dir '001.png') $volume
    for ($i = 0; $i -lt $pages.Count; $i++) { New-StoryPage (Join-Path $dir ('{0:000}.png' -f ($i + 2))) $pages[$i] }
    $zip = Join-Path $series "$volume.cbz"
    if (Test-Path $zip) { Remove-Item $zip }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($dir, $zip)
}
Remove-Item $build -Recurse -Force
Write-Output $series
