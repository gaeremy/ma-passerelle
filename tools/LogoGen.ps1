<#
  Produit les images du paquet MSIX (src\MaPasserelle.App\Assets) à partir de l'icône
  assets\icon\ma-passerelle-1024.png (tuile crème comprise).
  Depuis Windows PowerShell : powershell -File tools\LogoGen.ps1
#>
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$source = [System.Drawing.Image]::FromFile((Join-Path $root 'assets\icon\ma-passerelle-1024.png'))
$assets = Join-Path $root 'src\MaPasserelle.App\Assets'

function Save-Png([int]$w, [int]$h, [int]$icon, [string]$name) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.SmoothingMode = 'AntiAlias'
    $g.DrawImage($source, [int](($w - $icon) / 2), [int](($h - $icon) / 2), $icon, $icon)
    $g.Dispose()
    $bmp.Save((Join-Path $assets $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

Save-Png 44 44 44 'Square44x44Logo.png'
# Barre des tâches et menu Démarrer : variantes « sans plaque » (sinon Windows pose l'accent derrière).
foreach ($t in 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256) {
    Save-Png $t $t $t "Square44x44Logo.targetsize-$t.png"
    Save-Png $t $t $t "Square44x44Logo.targetsize-${t}_altform-unplated.png"
    Save-Png $t $t $t "Square44x44Logo.targetsize-${t}_altform-lightunplated.png"
}
Save-Png 150 150 120 'Square150x150Logo.png'
Save-Png 310 150 130 'Wide310x150Logo.png'
Save-Png 50 50 50 'StoreLogo.png'
Save-Png 620 300 200 'SplashScreen.png'
$source.Dispose()
'Images du paquet : ' + $assets
