param(
    [string]$SourcePath = (Join-Path $PSScriptRoot "../src/Madi.GUI/Assets/Madi-source.png"),
    [string]$PngPath = (Join-Path $PSScriptRoot "../src/Madi.GUI/Assets/Madi.png"),
    [string]$IconPath = (Join-Path $PSScriptRoot "../src/Madi.GUI/Assets/Madi.ico")
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

function New-ResizedPngBytes {
    param(
        [System.Drawing.Image]$Source,
        [int]$Size
    )

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.DrawImage($Source, 0, 0, $Size, $Size)
        }
        finally {
            $graphics.Dispose()
        }

        $stream = [System.IO.MemoryStream]::new()
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            return ,$stream.ToArray()
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $bitmap.Dispose()
    }
}

$resolvedSource = (Resolve-Path -LiteralPath $SourcePath).Path
$assetDirectory = Split-Path -Parent $PngPath
[System.IO.Directory]::CreateDirectory($assetDirectory) | Out-Null

$source = [System.Drawing.Image]::FromFile($resolvedSource)
try {
    $pngBytes = New-ResizedPngBytes -Source $source -Size 512
    [System.IO.File]::WriteAllBytes($PngPath, $pngBytes)

    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    $images = foreach ($size in $sizes) {
        [pscustomobject]@{
            Size = $size
            Bytes = New-ResizedPngBytes -Source $source -Size $size
        }
    }

    $stream = [System.IO.File]::Create($IconPath)
    try {
        $writer = [System.IO.BinaryWriter]::new($stream)
        try {
            $writer.Write([uint16]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]$images.Count)

            $offset = 6 + (16 * $images.Count)
            foreach ($image in $images) {
                $dimension = if ($image.Size -eq 256) { 0 } else { $image.Size }
                $writer.Write([byte]$dimension)
                $writer.Write([byte]$dimension)
                $writer.Write([byte]0)
                $writer.Write([byte]0)
                $writer.Write([uint16]1)
                $writer.Write([uint16]32)
                $writer.Write([uint32]$image.Bytes.Length)
                $writer.Write([uint32]$offset)
                $offset += $image.Bytes.Length
            }

            foreach ($image in $images) {
                $writer.Write([byte[]]$image.Bytes)
            }
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}
finally {
    $source.Dispose()
}

Write-Host "Generated PNG: $PngPath"
Write-Host "Generated ICO: $IconPath"
