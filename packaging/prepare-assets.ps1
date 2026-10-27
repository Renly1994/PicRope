param([string]$OutputPath = (Join-Path $PSScriptRoot '..\build\runtime-assets'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
$sourcePath = Join-Path $PSScriptRoot '..\src\assets'
foreach ($file in Get-ChildItem -LiteralPath $sourcePath -File | Where-Object Name -Match '^(basket|envelope)-.*\.png$') {
    $destination = Join-Path $OutputPath $file.Name
    if ((Test-Path -LiteralPath $destination) -and (Get-Item -LiteralPath $destination).LastWriteTimeUtc -ge $file.LastWriteTimeUtc) { continue }
    $source = [Drawing.Image]::FromFile($file.FullName)
    $image = New-Object Drawing.Bitmap(384, 384, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($image)
    try {
        $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.DrawImage($source, 0, 0, 384, 384)
        $image.Save($destination, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $graphics.Dispose(); $image.Dispose(); $source.Dispose() }
}
