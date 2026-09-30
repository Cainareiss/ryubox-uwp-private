param(
    [Parameter(Mandatory = $true)]
    [string] $SourceLogo,

    [string] $AssetsDirectory = (Join-Path $PSScriptRoot '..\src\Ryujinx.Uwp.App\Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$source = [System.Drawing.Bitmap]::FromFile((Resolve-Path -LiteralPath $SourceLogo).Path)
$assets = [System.IO.Path]::GetFullPath($AssetsDirectory)
[System.IO.Directory]::CreateDirectory($assets) | Out-Null

function New-Canvas {
    param([int] $Width, [int] $Height)

    return [System.Drawing.Bitmap]::new(
        $Width,
        $Height,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
}

function Set-Quality {
    param([System.Drawing.Graphics] $Graphics)

    $Graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $Graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $Graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $Graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
}

function New-TransparentMaster {
    param([int] $Size)

    # The generated source badge is centered at (628, 578), with an 816px
    # square crop enclosing the complete circular artwork.
    $crop = [System.Drawing.Rectangle]::new(220, 170, 816, 816)
    $temporary = New-Canvas $Size $Size
    $temporaryGraphics = [System.Drawing.Graphics]::FromImage($temporary)
    Set-Quality $temporaryGraphics
    $temporaryGraphics.Clear([System.Drawing.Color]::Transparent)
    $temporaryGraphics.DrawImage(
        $source,
        [System.Drawing.Rectangle]::new(0, 0, $Size, $Size),
        $crop,
        [System.Drawing.GraphicsUnit]::Pixel)
    $temporaryGraphics.Dispose()

    $master = New-Canvas $Size $Size
    $graphics = [System.Drawing.Graphics]::FromImage($master)
    Set-Quality $graphics
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $inset = [Math]::Max(2, [int]($Size * 0.018))
    $ellipse = [System.Drawing.Rectangle]::new($inset, $inset, $Size - ($inset * 2), $Size - ($inset * 2))
    $texture = [System.Drawing.TextureBrush]::new($temporary, [System.Drawing.Drawing2D.WrapMode]::Clamp)
    $graphics.FillEllipse($texture, $ellipse)
    $texture.Dispose()
    $graphics.Dispose()
    $temporary.Dispose()
    return $master
}

function Add-DarkBackground {
    param(
        [System.Drawing.Graphics] $Graphics,
        [int] $Width,
        [int] $Height)

    $background = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.Rectangle]::new(0, 0, $Width, $Height),
        [System.Drawing.Color]::FromArgb(255, 5, 31, 22),
        [System.Drawing.Color]::FromArgb(255, 10, 64, 43),
        35.0)
    $Graphics.FillRectangle($background, 0, 0, $Width, $Height)
    $background.Dispose()
}

function Save-SquareAsset {
    param([string] $Name, [int] $Size, [double] $Scale)

    $bitmap = New-Canvas $Size $Size
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    Set-Quality $graphics
    Add-DarkBackground $graphics $Size $Size
    $logoSize = [int]($Size * $Scale)
    $offset = [int](($Size - $logoSize) / 2)
    $graphics.DrawImage($master, $offset, $offset, $logoSize, $logoSize)
    $graphics.Dispose()
    $bitmap.Save((Join-Path $assets $Name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

function Save-WideAsset {
    param([string] $Name, [int] $Width, [int] $Height, [double] $Scale)

    $bitmap = New-Canvas $Width $Height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    Set-Quality $graphics
    Add-DarkBackground $graphics $Width $Height
    $logoSize = [int]($Height * $Scale)
    $x = [int](($Width - $logoSize) / 2)
    $y = [int](($Height - $logoSize) / 2)
    $graphics.DrawImage($master, $x, $y, $logoSize, $logoSize)
    $graphics.Dispose()
    $bitmap.Save((Join-Path $assets $Name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

try {
    $master = New-TransparentMaster 1024
    $master.Save((Join-Path $assets 'RyuboxLogoMaster.png'), [System.Drawing.Imaging.ImageFormat]::Png)

    Save-SquareAsset 'Square44x44Logo.png' 44 0.92
    Save-SquareAsset 'StoreLogo.png' 50 0.92
    Save-SquareAsset 'Square150x150Logo.png' 150 0.94
    Save-WideAsset 'Wide310x150Logo.png' 310 150 0.94
    Save-WideAsset 'SplashScreen.png' 620 300 0.90
}
finally {
    if ($null -ne $master) {
        $master.Dispose()
    }

    $source.Dispose()
}

Write-Host "Ryubox UWP assets generated in $assets"
