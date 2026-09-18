#Requires -Version 5.1
<#
  Draws src\PathMemo\Resources\app.ico.

  The icon is generated rather than checked in as a binary nobody can review: the
  shape is twelve lines of drawing code, and regenerating it beats explaining what
  is inside a 100 KB blob. Run it only when the design changes.

  Shape: a dark rounded square with a ring showing a nearly-full disk - the one
  thing this tool is about. It has to read at 16 pixels in the Explorer list, so
  there is no text, no gradient and exactly two foreground colours.
#>
[CmdletBinding()]
param(
    [string] $Output = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\PathMemo\Resources\app.ico'),

    # How full the ring looks. 0.87 is "nearly out of space", which is when anyone
    # goes looking for a tool like this.
    [double] $Used = 0.87
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 128, 256

$card = [System.Drawing.Color]::FromArgb(255, 24, 30, 42)      # near-black slate
$rest = [System.Drawing.Color]::FromArgb(255, 58, 70, 92)      # free space, dim
$fill = [System.Drawing.Color]::FromArgb(255, 86, 214, 214)    # used space, cyan

function New-Png([int] $size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded card.
    $radius = [math]::Max(2, [int]($size * 0.22))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $max = $size - 1
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($max - $d, 0, $d, $d, 270, 90)
    $path.AddArc($max - $d, $max - $d, $d, $d, 0, 90)
    $path.AddArc(0, $max - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.SolidBrush $card
    $g.FillPath($brush, $path)
    $brush.Dispose()
    $path.Dispose()

    # Ring: full circle in the dim colour, then the used wedge over it, then the hole.
    $pad = $size * 0.20
    $box = New-Object System.Drawing.RectangleF $pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad)

    $b = New-Object System.Drawing.SolidBrush $rest
    $g.FillEllipse($b, $box)
    $b.Dispose()

    $b = New-Object System.Drawing.SolidBrush $fill
    $g.FillPie($b, $box.X, $box.Y, $box.Width, $box.Height, -90, 360 * $Used)
    $b.Dispose()

    # The hole, punched back to the card colour so the ring reads as a ring.
    $hole = $size * 0.36
    $b = New-Object System.Drawing.SolidBrush $card
    $g.FillEllipse($b, ($size - $hole) / 2, ($size - $hole) / 2, $hole, $hole)
    $b.Dispose()

    $g.Dispose()

    $stream = New-Object System.IO.MemoryStream
    $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return $stream.ToArray()
}

<#
  Classic DIB payload: BITMAPINFOHEADER with a doubled height, bottom-up BGRA rows,
  then a 1-bit AND mask (all zero - the alpha channel does the work).

  Used for every size up to 64 because PNG-compressed entries, while fine for the
  Windows shell, are not understood by GDI+ (System.Drawing.Icon) or by some older
  resource tooling. Only the 128 and 256 pixel images stay PNG, where the size
  saving is real and nothing old ever asks for them.
#>
function New-Dib([int] $size) {
    $png = New-Png $size
    $stream = New-Object System.IO.MemoryStream (,$png)
    $bmp = [System.Drawing.Bitmap]::FromStream($stream)

    $out = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $out

    $rowMask = [int](([math]::Floor(($size + 31) / 32)) * 4)   # 1bpp rows, 4-byte aligned

    $w.Write([uint32]40)                 # biSize
    $w.Write([int32]$size)               # biWidth
    $w.Write([int32]($size * 2))         # biHeight: colour bitmap + mask
    $w.Write([uint16]1)                  # biPlanes
    $w.Write([uint16]32)                 # biBitCount
    $w.Write([uint32]0)                  # biCompression: BI_RGB
    $w.Write([uint32]($size * $size * 4 + $rowMask * $size))
    $w.Write([int32]0); $w.Write([int32]0)
    $w.Write([uint32]0); $w.Write([uint32]0)

    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $p = $bmp.GetPixel($x, $y)
            $w.Write([byte]$p.B); $w.Write([byte]$p.G); $w.Write([byte]$p.R); $w.Write([byte]$p.A)
        }
    }

    $zero = New-Object byte[] ($rowMask * $size)
    $w.Write($zero, 0, $zero.Length)

    $w.Flush()
    $bytes = $out.ToArray()
    $w.Dispose()
    $bmp.Dispose()
    return $bytes
}

$images = @{}
foreach ($size in $sizes) {
    $images[$size] = if ($size -ge 128) { New-Png $size } else { New-Dib $size }
}

# ICO container: header, one directory entry per image, then the PNG payloads.
# PNG-compressed entries are what every Windows since Vista reads, and they keep
# the 256x256 image from costing 256 KB on its own.
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out

$w.Write([uint16]0)                  # reserved
$w.Write([uint16]1)                  # type: icon
$w.Write([uint16]$sizes.Count)

$offset = 6 + 16 * $sizes.Count
foreach ($size in $sizes) {
    $bytes = $images[$size]
    $w.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
    $w.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
    $w.Write([byte]0)                # palette entries
    $w.Write([byte]0)                # reserved
    $w.Write([uint16]1)              # colour planes
    $w.Write([uint16]32)             # bits per pixel
    $w.Write([uint32]$bytes.Length)
    $w.Write([uint32]$offset)
    $offset += $bytes.Length
}

foreach ($size in $sizes) {
    # The three-argument overload, explicitly: BinaryWriter.Write($array) binds to
    # Write(byte) through PowerShell's conversion rules and writes a single byte.
    $bytes = [byte[]] $images[$size]
    $w.Write($bytes, 0, $bytes.Length)
}

$w.Flush()
$dir = Split-Path -Parent $Output
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
[System.IO.File]::WriteAllBytes($Output, $out.ToArray())
$w.Dispose()

$kb = [math]::Round((Get-Item $Output).Length / 1KB, 1)
Write-Host "$Output  ($kb KB, sizes: $($sizes -join ', '))" -ForegroundColor Green
