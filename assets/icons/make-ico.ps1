# Builds a multi-size .ico from the rendered 256x256 PNG.
#
# Layout used, for maximum shell compatibility:
#   16/24/32/48/64  -> 32-bit BMP (DIB) entries with an AND mask
#   128/256         -> PNG entries
# Windows 10/11 accept PNG at every size, but legacy shell paths still expect DIB
# below 128, and writing both costs nothing.
$ErrorActionPreference = 'Stop'

$src = $args[0]
$out = $args[1]
if (-not $src -or -not $out) { throw 'usage: make-ico.ps1 <source.png> <out.ico>' }
if (-not (Test-Path -LiteralPath $src)) { throw "source not found: $src" }

Add-Type -AssemblyName System.Drawing

$sizesBmp = @(16, 24, 32, 48, 64)
$sizesPng = @(128, 256)
$sizes = $sizesBmp + $sizesPng

$img = [System.Drawing.Image]::FromFile($src)
$entries = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.DrawImage($img, 0, 0, $s, $s)
    $g.Dispose()

    if ($sizesPng -contains $s) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $entries += [pscustomobject]@{ Size = $s; Kind = 'png'; Bytes = $ms.ToArray() }
        $ms.Dispose()
    }
    else {
        # 32-bit DIB: header + bottom-up BGRA rows + 1bpp AND mask (all zero; alpha rules)
        $rect = New-Object System.Drawing.Rectangle(0, 0, $s, $s)
        $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $stride = $data.Stride
        $pixels = New-Object byte[] ($stride * $s)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
        $bmp.UnlockBits($data)

        $maskStride = [int][math]::Floor(($s + 31) / 32) * 4
        $mask = New-Object byte[] ($maskStride * $s)

        $ms = New-Object System.IO.MemoryStream
        $bw = New-Object System.IO.BinaryWriter($ms)
        $bw.Write([int]40)                 # biSize
        $bw.Write([int]$s)                 # biWidth
        $bw.Write([int]($s * 2))           # biHeight (doubled for ICO)
        $bw.Write([int16]1)                # biPlanes
        $bw.Write([int16]32)               # biBitCount
        $bw.Write([int]0)                  # biCompression
        $bw.Write([int]($s * $s * 4))      # biSizeImage
        $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
        for ($y = $s - 1; $y -ge 0; $y--) {
            $bw.Write($pixels, $y * $stride, $s * 4)
        }
        $bw.Write($mask, 0, $mask.Length)
        $bw.Flush()
        $entries += [pscustomobject]@{ Size = $s; Kind = 'bmp'; Bytes = $ms.ToArray() }
        $bw.Dispose(); $ms.Dispose()
    }
    $bmp.Dispose()
}
$img.Dispose()

$fs = [System.IO.File]::Create($out)
$bw2 = New-Object System.IO.BinaryWriter($fs)
$bw2.Write([int16]0)                 # reserved
$bw2.Write([int16]1)                 # type: icon
$bw2.Write([int16]$entries.Count)
$offset = 6 + (16 * $entries.Count)
foreach ($e in $entries) {
    $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $bw2.Write([byte]$dim)           # width
    $bw2.Write([byte]$dim)           # height
    $bw2.Write([byte]0)              # palette
    $bw2.Write([byte]0)              # reserved
    $bw2.Write([int16]1)             # planes
    $bw2.Write([int16]32)            # bpp
    $bw2.Write([int]$e.Bytes.Length) # size
    $bw2.Write([int]$offset)         # offset
    $offset += $e.Bytes.Length
}
foreach ($e in $entries) { $bw2.Write($e.Bytes, 0, $e.Bytes.Length) }
$bw2.Flush(); $bw2.Dispose(); $fs.Dispose()

Write-Host ("ico written: {0}  ({1:N0} bytes, {2} sizes: {3})" -f $out, (Get-Item $out).Length,
    $entries.Count, (($entries | ForEach-Object { "$($_.Size)$(if($_.Kind -eq 'png'){'(png)'}else{'(bmp)'})" }) -join ', '))
