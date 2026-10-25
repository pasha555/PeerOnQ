<#
Generates the complete PeerOnQ visual-identity asset set from one normalized vector geometry.
The Q-link mark uses one continuous Q path and two equal peer endpoint nodes.
#>
[CmdletBinding()]
param(
    [string]$IconOutputPath,
    [string]$WixAssetDirectory,
    [string]$WebAssetDirectory,
    [string]$FaviconOutputPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($IconOutputPath)) {
    $IconOutputPath = Join-Path $scriptDirectory '..\..\src\PeerOnQ.App\Assets\PeerOnQ.ico'
}
if ([string]::IsNullOrWhiteSpace($WixAssetDirectory)) {
    $WixAssetDirectory = Join-Path $scriptDirectory '..\..\installer\Assets'
}
if ([string]::IsNullOrWhiteSpace($WebAssetDirectory)) {
    $WebAssetDirectory = Join-Path $scriptDirectory '..\..\artifacts\peeronq\public\brand'
}
if ([string]::IsNullOrWhiteSpace($FaviconOutputPath)) {
    $FaviconOutputPath = Join-Path $scriptDirectory '..\..\artifacts\peeronq\public\favicon.svg'
}

$primaryHex = '#198754'
$navyHex = '#0F2E46'
$whiteHex = '#FFFFFF'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$invariantCulture = [Globalization.CultureInfo]::InvariantCulture

function ConvertTo-SvgNumber([double]$Value) {
    return $Value.ToString('0.###', $invariantCulture)
}

function Write-GeneratedText([string]$Path, [string]$Content) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fullPath)) | Out-Null
    [IO.File]::WriteAllText($fullPath, ($Content.Trim() + [Environment]::NewLine), $utf8NoBom)
}

function Get-MarkSvgFragment([string]$StrokeColor, [string]$NodeColor) {
    return @"
  <circle cx="11.5" cy="11.5" r="7.5" fill="none" stroke="$StrokeColor" stroke-width="2.5"/>
  <path d="M14.75 14.75 19.5 19.5" fill="none" stroke="$StrokeColor" stroke-width="2.5" stroke-linecap="round"/>
  <circle cx="6.2" cy="6.2" r="2.25" fill="$NodeColor"/>
  <circle cx="19.5" cy="19.5" r="2.25" fill="$NodeColor"/>
"@
}

function New-MarkSvg([string]$Color) {
    $fragment = Get-MarkSvgFragment $Color $Color
    return @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none">
$fragment</svg>
"@
}

function Convert-GraphicsPathToSvgPath([System.Drawing.Drawing2D.GraphicsPath]$Path) {
    $points = $Path.PathPoints
    $types = $Path.PathTypes
    $builder = New-Object Text.StringBuilder
    $index = 0

    while ($index -lt $points.Length) {
        $kind = $types[$index] -band 0x07
        switch ($kind) {
            0 {
                [void]$builder.Append('M')
                [void]$builder.Append((ConvertTo-SvgNumber $points[$index].X))
                [void]$builder.Append(' ')
                [void]$builder.Append((ConvertTo-SvgNumber $points[$index].Y))
                if (($types[$index] -band 0x80) -ne 0) { [void]$builder.Append('Z') }
                $index++
            }
            1 {
                [void]$builder.Append('L')
                [void]$builder.Append((ConvertTo-SvgNumber $points[$index].X))
                [void]$builder.Append(' ')
                [void]$builder.Append((ConvertTo-SvgNumber $points[$index].Y))
                if (($types[$index] -band 0x80) -ne 0) { [void]$builder.Append('Z') }
                $index++
            }
            3 {
                if (($index + 2) -ge $points.Length) {
                    throw 'Unexpected incomplete Bezier segment in the PeerOnQ wordmark outline.'
                }
                [void]$builder.Append('C')
                foreach ($pointIndex in @($index, ($index + 1), ($index + 2))) {
                    [void]$builder.Append((ConvertTo-SvgNumber $points[$pointIndex].X))
                    [void]$builder.Append(' ')
                    [void]$builder.Append((ConvertTo-SvgNumber $points[$pointIndex].Y))
                    if ($pointIndex -ne ($index + 2)) { [void]$builder.Append(' ') }
                }
                if (($types[$index + 2] -band 0x80) -ne 0) { [void]$builder.Append('Z') }
                $index += 3
            }
            default {
                throw "Unsupported graphics path point type: $kind"
            }
        }
    }

    return $builder.ToString()
}

function New-WordmarkOutline {
    $fontFamily = New-Object System.Drawing.FontFamily('Segoe UI')
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    try {
        $path.AddString(
            'PeerOnQ',
            $fontFamily,
            [int][System.Drawing.FontStyle]::Bold,
            20.0,
            (New-Object System.Drawing.PointF(0, 0)),
            [System.Drawing.StringFormat]::GenericTypographic)
        $bounds = $path.GetBounds()
        return [pscustomobject]@{
            PathData = Convert-GraphicsPathToSvgPath $path
            X = [double]$bounds.X
            Y = [double]$bounds.Y
            Width = [double]$bounds.Width
            Height = [double]$bounds.Height
        }
    } finally {
        $path.Dispose()
        $fontFamily.Dispose()
    }
}

function New-WordmarkSvg($Outline, [string]$Color) {
    $padding = 0.5
    $viewX = ConvertTo-SvgNumber ($Outline.X - $padding)
    $viewY = ConvertTo-SvgNumber ($Outline.Y - $padding)
    $viewWidth = ConvertTo-SvgNumber ($Outline.Width + (2 * $padding))
    $viewHeight = ConvertTo-SvgNumber ($Outline.Height + (2 * $padding))
    return @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="$viewX $viewY $viewWidth $viewHeight">
  <path d="$($Outline.PathData)" fill="$Color"/>
</svg>
"@
}

function New-LockupSvg($Outline, [string]$MarkColor, [string]$WordmarkColor) {
    $wordmarkHeight = 16.5
    $scale = $wordmarkHeight / $Outline.Height
    $wordmarkWidth = $Outline.Width * $scale
    $lockupWidth = 32 + $wordmarkWidth
    $wordmarkY = (24 - $wordmarkHeight) / 2
    $scaleText = ConvertTo-SvgNumber $scale
    $translateX = ConvertTo-SvgNumber (-1 * $Outline.X)
    $translateY = ConvertTo-SvgNumber (-1 * $Outline.Y)
    $positionY = ConvertTo-SvgNumber $wordmarkY
    $viewWidth = ConvertTo-SvgNumber $lockupWidth
    $fragment = Get-MarkSvgFragment $MarkColor $MarkColor
    return @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 $viewWidth 24" fill="none">
$fragment  <g transform="translate(32 $positionY)">
    <g transform="scale($scaleText)">
      <g transform="translate($translateX $translateY)">
        <path d="$($Outline.PathData)" fill="$WordmarkColor"/>
      </g>
    </g>
  </g>
</svg>
"@
}

function New-AppIconSvg {
    $ring = Get-MarkSvgFragment $whiteHex $primaryHex
    return @"
<svg xmlns="http://www.w3.org/2000/svg" width="180" height="180" viewBox="0 0 24 24" fill="none">
  <rect x="1" y="1" width="22" height="22" rx="5.5" fill="$navyHex"/>
$ring</svg>
"@
}

function New-RoundedRectanglePath(
    [float]$X,
    [float]$Y,
    [float]$Width,
    [float]$Height,
    [float]$Radius
) {
    $diameter = 2 * $Radius
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc(($X + $Width - $diameter), $Y, $diameter, $diameter, 270, 90)
    $path.AddArc(($X + $Width - $diameter), ($Y + $Height - $diameter), $diameter, $diameter, 0, 90)
    $path.AddArc($X, ($Y + $Height - $diameter), $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function Draw-PeerOnQMark(
    [System.Drawing.Graphics]$Graphics,
    [float]$X,
    [float]$Y,
    [float]$Size,
    [System.Drawing.Color]$StrokeColor,
    [System.Drawing.Color]$NodeColor
) {
    $scale = $Size / 24.0
    $pen = New-Object System.Drawing.Pen($StrokeColor, (2.5 * $scale))
    try {
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $Graphics.DrawEllipse($pen, ($X + (4 * $scale)), ($Y + (4 * $scale)), (15 * $scale), (15 * $scale))
        $Graphics.DrawLine(
            $pen,
            ($X + (14.75 * $scale)),
            ($Y + (14.75 * $scale)),
            ($X + (19.5 * $scale)),
            ($Y + (19.5 * $scale)))
    } finally {
        $pen.Dispose()
    }

    $brush = New-Object System.Drawing.SolidBrush($NodeColor)
    try {
        $radius = 2.25 * $scale
        foreach ($center in @(@(6.2, 6.2), @(19.5, 19.5))) {
            $Graphics.FillEllipse(
                $brush,
                ($X + ($center[0] * $scale) - $radius),
                ($Y + ($center[1] * $scale) - $radius),
                (2 * $radius),
                (2 * $radius))
        }
    } finally {
        $brush.Dispose()
    }
}

function Set-HighQualityDrawing([System.Drawing.Graphics]$Graphics) {
    $Graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $Graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $Graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
}

$fullWebAssetDirectory = [IO.Path]::GetFullPath($WebAssetDirectory)
[IO.Directory]::CreateDirectory($fullWebAssetDirectory) | Out-Null
$wordmark = New-WordmarkOutline

Write-GeneratedText (Join-Path $fullWebAssetDirectory 'peeronq-mark.svg') (New-MarkSvg $primaryHex)
Write-GeneratedText (Join-Path $fullWebAssetDirectory 'peeronq-mark-dark.svg') (New-MarkSvg $navyHex)
Write-GeneratedText (Join-Path $fullWebAssetDirectory 'peeronq-mark-light.svg') (New-MarkSvg $whiteHex)
Write-GeneratedText (Join-Path $fullWebAssetDirectory 'peeronq-wordmark.svg') (New-WordmarkSvg $wordmark $navyHex)
Write-GeneratedText (Join-Path $fullWebAssetDirectory 'peeronq-wordmark-light.svg') (New-WordmarkSvg $wordmark $whiteHex)
Write-GeneratedText (Join-Path $fullWebAssetDirectory 'peeronq-lockup.svg') (New-LockupSvg $wordmark $primaryHex $navyHex)
Write-GeneratedText (Join-Path $fullWebAssetDirectory 'peeronq-lockup-light.svg') (New-LockupSvg $wordmark $whiteHex $whiteHex)
Write-GeneratedText $FaviconOutputPath (New-AppIconSvg)

$primaryColor = [System.Drawing.ColorTranslator]::FromHtml($primaryHex)
$navyColor = [System.Drawing.ColorTranslator]::FromHtml($navyHex)
$whiteColor = [System.Drawing.ColorTranslator]::FromHtml($whiteHex)
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$images = New-Object 'System.Collections.Generic.List[byte[]]'

try {
    foreach ($size in $sizes) {
        $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([System.Drawing.Color]::Transparent)
                Set-HighQualityDrawing $graphics
                $scale = $size / 24.0
                $tile = New-RoundedRectanglePath (1 * $scale) (1 * $scale) (22 * $scale) (22 * $scale) (5.5 * $scale)
                $tileBrush = New-Object System.Drawing.SolidBrush($navyColor)
                try {
                    $graphics.FillPath($tileBrush, $tile)
                } finally {
                    $tileBrush.Dispose()
                    $tile.Dispose()
                }
                Draw-PeerOnQMark $graphics 0 0 $size $whiteColor $primaryColor
            } finally {
                $graphics.Dispose()
            }

            $stream = New-Object System.IO.MemoryStream
            try {
                $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
                $images.Add($stream.ToArray())
            } finally {
                $stream.Dispose()
            }
        } finally {
            $bitmap.Dispose()
        }
    }

    $fullIconOutputPath = [IO.Path]::GetFullPath($IconOutputPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fullIconOutputPath)) | Out-Null
    $file = [IO.File]::Open($fullIconOutputPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $writer = New-Object IO.BinaryWriter($file)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$images.Count)
        $offset = 6 + (16 * $images.Count)
        for ($index = 0; $index -lt $images.Count; $index++) {
            $size = $sizes[$index]
            $encodedSize = if ($size -eq 256) { 0 } else { $size }
            $writer.Write([byte]$encodedSize)
            $writer.Write([byte]$encodedSize)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$images[$index].Length)
            $writer.Write([uint32]$offset)
            $offset += $images[$index].Length
        }
        foreach ($image in $images) { $writer.Write($image) }
    } finally {
        $writer.Dispose()
        $file.Dispose()
    }

    $fullWixAssetDirectory = [IO.Path]::GetFullPath($WixAssetDirectory)
    [IO.Directory]::CreateDirectory($fullWixAssetDirectory) | Out-Null

    $bannerPath = Join-Path $fullWixAssetDirectory 'WixUIBanner.png'
    $banner = New-Object System.Drawing.Bitmap(493, 58, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($banner)
        try {
            $graphics.Clear([System.Drawing.Color]::White)
            Set-HighQualityDrawing $graphics
            Draw-PeerOnQMark $graphics 448 11 36 $primaryColor $primaryColor
        } finally {
            $graphics.Dispose()
        }
        $banner.Save($bannerPath, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $banner.Dispose()
    }

    $dialogPath = Join-Path $fullWixAssetDirectory 'WixUIDialog.png'
    $dialog = New-Object System.Drawing.Bitmap(493, 312, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($dialog)
        try {
            $graphics.Clear([System.Drawing.Color]::White)
            Set-HighQualityDrawing $graphics
            $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
            $panelBrush = New-Object System.Drawing.SolidBrush($navyColor)
            $accentBrush = New-Object System.Drawing.SolidBrush($primaryColor)
            try {
                $graphics.FillRectangle($panelBrush, 0, 0, 164, 312)
                $graphics.FillRectangle($accentBrush, 0, 0, 6, 312)
            } finally {
                $accentBrush.Dispose()
                $panelBrush.Dispose()
            }
            Draw-PeerOnQMark $graphics 46 86 72 $whiteColor $primaryColor

            $font = New-Object System.Drawing.Font('Segoe UI', 21, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            $textBrush = New-Object System.Drawing.SolidBrush($whiteColor)
            $format = New-Object System.Drawing.StringFormat
            try {
                $format.Alignment = [System.Drawing.StringAlignment]::Center
                $format.LineAlignment = [System.Drawing.StringAlignment]::Center
                $graphics.DrawString('PeerOnQ', $font, $textBrush, (New-Object System.Drawing.RectangleF(6, 178, 158, 42)), $format)
            } finally {
                $format.Dispose()
                $textBrush.Dispose()
                $font.Dispose()
            }
        } finally {
            $graphics.Dispose()
        }
        $dialog.Save($dialogPath, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $dialog.Dispose()
    }

    Write-Host "Generated PeerOnQ icon: $fullIconOutputPath"
    Write-Host "Generated WiX banner: $bannerPath"
    Write-Host "Generated WiX dialog image: $dialogPath"
    Write-Host "Generated web brand assets: $fullWebAssetDirectory"
    Write-Host "Generated favicon: $([IO.Path]::GetFullPath($FaviconOutputPath))"
} finally {
    foreach ($image in $images) { [Array]::Clear($image, 0, $image.Length) }
}
