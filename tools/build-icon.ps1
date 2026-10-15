param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $projectRoot 'assets\codex-usage-bar.svg'
if (-not (Test-Path -LiteralPath $sourcePath)) { throw 'Icon SVG source was not found.' }
if ([String]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $projectRoot 'assets\codex-usage-bar.ico' }
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
$svg = New-Object System.Xml.XmlDocument
$svg.Load($sourcePath)
$viewBox = $svg.DocumentElement.GetAttribute('viewBox').Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries)
if ($viewBox.Length -ne 4) { throw 'SVG must define a four-value viewBox.' }
$viewWidth = [Double]::Parse($viewBox[2], [System.Globalization.CultureInfo]::InvariantCulture)
$viewHeight = [Double]::Parse($viewBox[3], [System.Globalization.CultureInfo]::InvariantCulture)
$paths = @($svg.SelectNodes("/*[local-name()='svg']/*[local-name()='path']"))
if ($paths.Count -eq 0) { throw 'SVG contains no path elements.' }

function Convert-HexColor([string]$Value) {
    $hex = $Value.TrimStart('#')
    if ($hex.Length -ne 6) { throw ('Unsupported SVG color: ' + $Value) }
    return [System.Windows.Media.Color]::FromRgb(
        [Convert]::ToByte($hex.Substring(0, 2), 16),
        [Convert]::ToByte($hex.Substring(2, 2), 16),
        [Convert]::ToByte($hex.Substring(4, 2), 16))
}

function New-FrozenBrush([string]$Value) {
    $brush = New-Object System.Windows.Media.SolidColorBrush((Convert-HexColor $Value))
    $brush.Freeze()
    return $brush
}

function Convert-SvgToPng([int]$Size) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $drawing = $visual.RenderOpen()
    $scale = New-Object System.Windows.Media.ScaleTransform(($Size / $viewWidth), ($Size / $viewHeight))
    $drawing.PushTransform($scale)
    foreach ($path in $paths) {
        $geometry = [System.Windows.Media.Geometry]::Parse($path.GetAttribute('d'))
        $fillValue = $path.GetAttribute('fill')
        $fill = if ([String]::IsNullOrWhiteSpace($fillValue) -or $fillValue -eq 'none') { $null } else { New-FrozenBrush $fillValue }
        $strokeValue = $path.GetAttribute('stroke')
        $pen = $null
        if (-not [String]::IsNullOrWhiteSpace($strokeValue) -and $strokeValue -ne 'none') {
            $widthValue = $path.GetAttribute('stroke-width')
            $strokeWidth = if ([String]::IsNullOrWhiteSpace($widthValue)) { 1.0 } else { [Double]::Parse($widthValue, [System.Globalization.CultureInfo]::InvariantCulture) }
            $pen = New-Object System.Windows.Media.Pen((New-FrozenBrush $strokeValue), $strokeWidth)
            $pen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
            $pen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
            $pen.LineJoin = [System.Windows.Media.PenLineJoin]::Round
            $pen.Freeze()
        }
        $drawing.DrawGeometry($fill, $pen, $geometry)
    }
    $drawing.Pop()
    $drawing.Close()
    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($Size, $Size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $pngStream = New-Object System.IO.MemoryStream
    try {
        $encoder.Save($pngStream)
        return ,$pngStream.ToArray()
    }
    finally { $pngStream.Dispose() }
}

$frames = @()
foreach ($size in @(16, 32, 48, 256)) {
    $frames += [PSCustomObject]@{ Size = $size; Bytes = (Convert-SvgToPng $size) }
}

$icoStream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($icoStream)
try {
    $writer.Write([UInt16]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]$frames.Count)
    $offset = 6 + (16 * $frames.Count)
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -ge 256) { [byte]0 } else { [byte]$frame.Size }
        $writer.Write($dimension)
        $writer.Write($dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]32)
        $writer.Write([UInt32]$frame.Bytes.Length)
        $writer.Write([UInt32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
    $writer.Flush()
    [System.IO.File]::WriteAllBytes($OutputPath, $icoStream.ToArray())
}
finally {
    $writer.Dispose()
    $icoStream.Dispose()
}

Write-Output ('Built multi-size icon: ' + $OutputPath)
