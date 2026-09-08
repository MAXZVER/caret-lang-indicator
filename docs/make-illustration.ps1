#Requires -Version 5.1
<#
.SYNOPSIS
    Draws the README illustration: a mock input field with the badge next to
    the caret, in the three states the indicator can show.
.DESCRIPTION
    This renders the picture rather than photographing the screen, so nothing
    from the desktop it runs on can leak into a published image. The badge is
    drawn with the same shape and colours the running indicator uses.
#>
[CmdletBinding()]
param(
    [string]$OutDir = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
if (-not $PSScriptRoot) { $PSScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $OutDir) { $OutDir = $PSScriptRoot }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Add-Type -AssemblyName System.Drawing

$fontText  = New-Object System.Drawing.Font('Segoe UI', 11)
$fontBadge = New-Object System.Drawing.Font('Segoe UI Semibold', 9)

$colCard   = [System.Drawing.Color]::FromArgb(255, 250, 250, 252)
$colField  = [System.Drawing.Color]::White
$colBorder = [System.Drawing.Color]::FromArgb(255, 214, 216, 222)
$colInk    = [System.Drawing.Color]::FromArgb(255,  28,  28,  30)
$colHint   = [System.Drawing.Color]::FromArgb(255, 150, 152, 158)
$colPill   = [System.Drawing.Color]::FromArgb(238,  40,  40,  43)
$colCaps   = [System.Drawing.Color]::FromArgb(245, 255, 179,  64)
$colPillTx = [System.Drawing.Color]::FromArgb(255, 245, 245, 247)
$colCapsTx = [System.Drawing.Color]::FromArgb(255,  40,  28,   0)

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Add-CapsGlyph($g, $brush, [single]$cx, [single]$cy) {
    $pts = New-Object 'System.Drawing.PointF[]' 7
    $pts[0] = New-Object System.Drawing.PointF([single]$cx,          [single]($cy - 5.5))
    $pts[1] = New-Object System.Drawing.PointF([single]($cx - 4.4),  [single]($cy - 1.0))
    $pts[2] = New-Object System.Drawing.PointF([single]($cx - 1.9),  [single]($cy - 1.0))
    $pts[3] = New-Object System.Drawing.PointF([single]($cx - 1.9),  [single]($cy + 1.8))
    $pts[4] = New-Object System.Drawing.PointF([single]($cx + 1.9),  [single]($cy + 1.8))
    $pts[5] = New-Object System.Drawing.PointF([single]($cx + 1.9),  [single]($cy - 1.0))
    $pts[6] = New-Object System.Drawing.PointF([single]($cx + 4.4),  [single]($cy - 1.0))
    $g.FillPolygon($brush, $pts)
    $bar = New-Object System.Drawing.RectangleF([single]($cx - 4.4), [single]($cy + 3.4), [single]8.8, [single]2.1)
    $g.FillRectangle($brush, $bar)
}

function New-Scene([string]$text, [string]$layout, [bool]$caps, [string]$file) {
    $W = 520; $H = 132
    $bmp = New-Object System.Drawing.Bitmap($W, $H)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
    $g.Clear($colCard)

    # the mock input field
    $fx = 28; $fy = 30; $fw = 464; $fh = 38
    $field = New-RoundedPath $fx $fy $fw $fh 8
    $b = New-Object System.Drawing.SolidBrush($colField)
    $g.FillPath($b, $field); $b.Dispose()
    $pen = New-Object System.Drawing.Pen($colBorder, 1)
    $g.DrawPath($pen, $field); $pen.Dispose()
    $field.Dispose()

    # the text already typed, and the caret after it
    $tb = New-Object System.Drawing.SolidBrush($colInk)
    $g.DrawString($text, $fontText, $tb, [single]($fx + 12), [single]($fy + 9))
    $textW = $g.MeasureString($text, $fontText).Width
    $caretX = $fx + 12 + $textW - 3
    $caretPen = New-Object System.Drawing.Pen($colInk, 1.6)
    $g.DrawLine($caretPen, [single]$caretX, [single]($fy + 8), [single]$caretX, [single]($fy + 30))
    $caretPen.Dispose(); $tb.Dispose()

    # the badge, hanging below-right of the caret exactly as the tool places it
    $label = $layout
    $glyphW = 0
    if ($caps) { $glyphW = 16 }
    $lw = $g.MeasureString($label, $fontBadge).Width
    $pw = [int]($lw + 20 + $glyphW)
    $ph = 22
    $px = $caretX + 8
    $py = $fy + 30 + 2

    for ($i = 4; $i -ge 1; $i--) {
        $sp = New-RoundedPath ($px - $i) ($py - $i + 1) ($pw + $i * 2) ($ph + $i * 2) (($ph + $i * 2) / 2)
        $sb = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb((6 - $i) * 4, 0, 0, 0))
        $g.FillPath($sb, $sp); $sb.Dispose(); $sp.Dispose()
    }

    if ($caps) { $fill = $colCaps; $ink = $colCapsTx } else { $fill = $colPill; $ink = $colPillTx }
    $pill = New-RoundedPath $px $py $pw $ph ($ph / 2)
    $pb = New-Object System.Drawing.SolidBrush($fill)
    $g.FillPath($pb, $pill); $pb.Dispose(); $pill.Dispose()

    $ib = New-Object System.Drawing.SolidBrush($ink)
    $textLeft = $px + 10
    if ($caps) {
        Add-CapsGlyph $g $ib ($px + 14) ($py + $ph / 2)
        $textLeft = $px + 10 + $glyphW
    }
    $sf = New-Object System.Drawing.StringFormat
    $sf.LineAlignment = 'Center'
    $rect = New-Object System.Drawing.RectangleF([single]$textLeft, [single]$py, [single]($lw + 4), [single]$ph)
    $g.DrawString($label, $fontBadge, $ib, $rect, $sf)
    $ib.Dispose(); $sf.Dispose()

    # caption
    $cb = New-Object System.Drawing.SolidBrush($colHint)
    $caption = if ($caps) { 'Caps Lock on' } else { "layout: $layout" }
    $g.DrawString($caption, $fontBadge, $cb, [single]$fx, [single]($H - 26))
    $cb.Dispose()

    $g.Dispose()
    $out = Join-Path $OutDir $file
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    "saved $out"
}

New-Scene 'Hello, how are you'  'EN' $false 'state-en.png'
New-Scene 'Привет, как дела'    'RU' $false 'state-ru.png'
New-Scene 'ПРИВЕТ'              'RU' $true  'state-caps.png'
