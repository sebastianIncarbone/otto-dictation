<#
.SYNOPSIS
    Genera Otto.ico.

.DESCRIPTION
    El .ico nunca se commitea: es un archivo derivado, y se arma acá en cada
    publicación. De dónde sale el dibujo depende de qué haya:

    - Si existe src\Otto.App\Assets\otto.png, se usa el personaje. Ese PNG sí es
      arte autoral y sí va al repositorio: la regla de "nada de binarios" existía
      porque un círculo dibujado no necesita un archivo, no como dogma.
    - Si no está, se dibuja el mismo círculo verde que la bandeja muestra cuando
      Otto está listo. Así el build nunca depende de un asset que puede faltar.

    Se escriben entradas DIB de 32 bits sin comprimir. Un .ico con PNG adentro
    sale más chico, pero exige deflate y CRC32 a mano en PowerShell para ahorrar
    unos cientos de kilobytes en un instalador de 47 MB: no vale el riesgo de un
    archivo mal formado que Windows dibuja como un cuadrado en blanco.

.EXAMPLE
    .\build\icono.ps1

.EXAMPLE
    .\build\icono.ps1 -Source src\Otto.App\Assets\otto-cabeza.png
#>
[CmdletBinding()]
param(
    [string]$Path = "$PSScriptRoot\..\src\Otto.App\Otto.ico",

    [string]$Source = "$PSScriptRoot\..\src\Otto.App\Assets\otto.png",

    # A 16 y 32 píxeles el personaje entero es una mancha: la cola, las patas y la
    # cara se hacen puré. Un .ico admite arte distinto por tamaño justamente para
    # esto, así que los chicos llevan la cabeza sola, que conserva silueta.
    [string]$SourceSmall = "$PSScriptRoot\..\src\Otto.App\Assets\otto-cabeza.png",

    [int]$SmallUpTo = 32,

    # 16 para la barra de tareas, 32 para el escritorio, 48 para la lista de
    # programas, 256 para la vista de iconos grandes del Explorador.
    [int[]]$Sizes = @(16, 32, 48, 256)
)

$ErrorActionPreference = 'Stop'

# Verde "listo" de TrayIcons.cs. Si cambia allá, cambia acá.
$R = 0x4C
$G = 0xAF
$B = 0x76

function Get-CircleDib([int]$size) {
    # Bottom-up y BGRA, que es como un DIB guarda los píxeles. El alfa va
    # derecho, sin premultiplicar: los íconos de 32 bits de Windows lo esperan
    # así, al revés que el framebuffer de Avalonia en TrayIcons.cs.
    $pixels = [byte[]]::new($size * $size * 4)

    $centre = ($size - 1) / 2.0
    $radius = $size / 2.0 - [Math]::Max(1.0, $size / 16.0)

    for ($row = 0; $row -lt $size; $row++) {
        $y = $size - 1 - $row

        for ($x = 0; $x -lt $size; $x++) {
            $dx = $x - $centre
            $dy = $y - $centre
            $distance = [Math]::Sqrt($dx * $dx + $dy * $dy)

            # Un píxel de difuminado en el borde, igual que en la bandeja: sin
            # esto el círculo se lee como un borrón dentado a 16 px.
            $coverage = [Math]::Max(0.0, [Math]::Min(1.0, $radius - $distance + 0.5))

            $offset = ($row * $size + $x) * 4

            $pixels[$offset + 0] = $B
            $pixels[$offset + 1] = $G
            $pixels[$offset + 2] = $R
            $pixels[$offset + 3] = [byte]($coverage * 255)
        }
    }

    # La coma no es decorativa: sin ella PowerShell desenrolla el arreglo en la
    # salida y el que llama recibe Object[], que BinaryWriter no reconoce como
    # byte[] — escribe basura de dos bytes y produce un .ico con el directorio
    # correcto y ninguna imagen adentro.
    return , $pixels
}

# El lienzo que exporta un editor casi nunca tiene el dibujo centrado —
# a Otto lo corre la cola hacia la derecha. Encuadrar por el lienzo dejaría el
# ícono visiblemente descentrado, así que se encuadra por lo que está pintado.
function Get-ContentBounds([System.Drawing.Bitmap]$bitmap) {
    $rect = [System.Drawing.Rectangle]::new(0, 0, $bitmap.Width, $bitmap.Height)
    $data = $bitmap.LockBits($rect, 'ReadOnly', [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    try {
        $buffer = [byte[]]::new($data.Stride * $bitmap.Height)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buffer, 0, $buffer.Length)

        $x0 = $bitmap.Width; $y0 = $bitmap.Height; $x1 = -1; $y1 = -1

        for ($y = 0; $y -lt $bitmap.Height; $y++) {
            $row = $y * $data.Stride

            for ($x = 0; $x -lt $bitmap.Width; $x++) {
                # 24 y no 0: el antialias del borde deja un halo casi invisible
                # que, tomado como contenido, agrandaría la caja sin motivo.
                if ($buffer[$row + $x * 4 + 3] -le 24) { continue }

                if ($x -lt $x0) { $x0 = $x }
                if ($x -gt $x1) { $x1 = $x }
                if ($y -lt $y0) { $y0 = $y }
                if ($y -gt $y1) { $y1 = $y }
            }
        }
    }
    finally { $bitmap.UnlockBits($data) }

    if ($x1 -lt 0) { return $rect }

    return [System.Drawing.Rectangle]::new($x0, $y0, $x1 - $x0 + 1, $y1 - $y0 + 1)
}

function Get-SourceDib([System.Drawing.Bitmap]$source, [System.Drawing.Rectangle]$content, [int]$size) {
    # Un margen chico para que el ícono no toque el borde: Windows lo muestra
    # pegado a otros y sin aire se lee como si estuviera recortado.
    $box = $size * 0.92

    $scale = [Math]::Min($box / $content.Width, $box / $content.Height)
    $w = [int][Math]::Round($content.Width * $scale)
    $h = [int][Math]::Round($content.Height * $scale)

    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

        try {
            $graphics.CompositingMode = 'SourceCopy'
            $graphics.InterpolationMode = 'HighQualityBicubic'
            $graphics.PixelOffsetMode = 'HighQuality'
            $graphics.SmoothingMode = 'HighQuality'
            $graphics.Clear([System.Drawing.Color]::Transparent)

            $destino = [System.Drawing.Rectangle]::new(
                [int](($size - $w) / 2), [int](($size - $h) / 2), $w, $h)

            $graphics.DrawImage($source, $destino, $content, [System.Drawing.GraphicsUnit]::Pixel)
        }
        finally { $graphics.Dispose() }

        $rect = [System.Drawing.Rectangle]::new(0, 0, $size, $size)
        $data = $bitmap.LockBits($rect, 'ReadOnly', [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

        try {
            # Format32bppArgb en memoria ya es BGRA, que es lo que quiere un DIB.
            # Lo único que falta es dar vuelta las filas: el .ico las guarda de
            # abajo hacia arriba.
            $pixels = [byte[]]::new($size * $size * 4)

            for ($row = 0; $row -lt $size; $row++) {
                $origen = [IntPtr]::Add($data.Scan0, ($size - 1 - $row) * $data.Stride)
                [System.Runtime.InteropServices.Marshal]::Copy($origen, $pixels, $row * $size * 4, $size * 4)
            }

            return , $pixels
        }
        finally { $bitmap.UnlockBits($data) }
    }
    finally { $bitmap.Dispose() }
}

function Get-MaskBytes([int]$size) {
    # La máscara AND sigue siendo obligatoria aunque el alfa la vuelva
    # redundante. En cero significa "todo opaco" y Windows usa el canal alfa.
    $rowBytes = [Math]::Floor(($size + 31) / 32) * 4
    return , [byte[]]::new($rowBytes * $size)
}

$directory = Split-Path -Parent $Path
if ($directory -and -not (Test-Path $directory)) {
    New-Item -ItemType Directory -Force $directory | Out-Null
}

# Se llama $arte y no $source por una razón concreta: PowerShell no distingue
# mayúsculas en los nombres de variable, así que un local llamado $source pisaría
# el parámetro $Source y el script usaría el círculo teniendo el PNG al lado.
function Open-Arte([string]$path) {
    if (-not (Test-Path $path)) { return $null }

    # Si el arte está, tiene que entrar. Caerse al círculo por no poder decodificar
    # un PNG que sí existe sería publicar un ícono equivocado sin que nadie mire.
    try {
        Add-Type -AssemblyName System.Drawing
        $bitmap = [System.Drawing.Bitmap]::new((Resolve-Path $path).Path)

        return [pscustomobject]@{
            Bitmap  = $bitmap
            Content = Get-ContentBounds $bitmap
            Nombre  = Split-Path -Leaf $path
        }
    }
    catch {
        throw "Existe $path pero no se pudo abrir como imagen: $($_.Exception.Message)"
    }
}

$arte = Open-Arte $Source
$arteChico = if ($arte) { Open-Arte $SourceSmall } else { $null }

try {
    $images = foreach ($size in $Sizes) {
        $elegido = if ($arteChico -and $size -le $SmallUpTo) { $arteChico } else { $arte }

        [pscustomobject]@{
            Size   = $size
            Origen = $(if ($elegido) { $elegido.Nombre } else { 'circulo' })
            Pixels = $(if ($elegido) {
                Get-SourceDib $elegido.Bitmap $elegido.Content $size
            } else {
                Get-CircleDib $size
            })
            Mask   = Get-MaskBytes $size
        }
    }
}
finally {
    if ($arte) { $arte.Bitmap.Dispose() }
    if ($arteChico) { $arteChico.Bitmap.Dispose() }
}

$origen = ($images | ForEach-Object { "$($_.Size):$($_.Origen)" }) -join ' '

$stream = [System.IO.File]::Create($Path)

try {
    $writer = [System.IO.BinaryWriter]::new($stream)

    # ICONDIR
    $writer.Write([uint16]0)                 # reservado
    $writer.Write([uint16]1)                 # tipo: 1 = ícono
    $writer.Write([uint16]$images.Count)

    # Las imágenes van después del directorio completo, así que el primer
    # desplazamiento se calcula sabiendo cuántas entradas hay.
    $offset = 6 + (16 * $images.Count)

    foreach ($image in $images) {
        $bytesInRes = 40 + $image.Pixels.Length + $image.Mask.Length

        # 0 significa 256: el campo es de un solo byte y 256 no entra.
        $writer.Write([byte]($(if ($image.Size -ge 256) { 0 } else { $image.Size })))
        $writer.Write([byte]($(if ($image.Size -ge 256) { 0 } else { $image.Size })))
        $writer.Write([byte]0)               # colores de la paleta: ninguna
        $writer.Write([byte]0)               # reservado
        $writer.Write([uint16]1)             # planos
        $writer.Write([uint16]32)            # bits por píxel
        $writer.Write([uint32]$bytesInRes)
        $writer.Write([uint32]$offset)

        $offset += $bytesInRes
    }

    foreach ($image in $images) {
        # BITMAPINFOHEADER. El alto va duplicado porque describe la imagen y la
        # máscara apiladas, que es la rareza que define el formato .ico.
        $writer.Write([uint32]40)
        $writer.Write([int32]$image.Size)
        $writer.Write([int32]($image.Size * 2))
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]0)             # sin compresión
        $writer.Write([uint32]$image.Pixels.Length)
        $writer.Write([int32]0); $writer.Write([int32]0)
        $writer.Write([uint32]0); $writer.Write([uint32]0)

        $writer.Write($image.Pixels)
        $writer.Write($image.Mask)
    }

    $writer.Flush()
}
finally {
    $stream.Dispose()
}

$kb = (Get-Item $Path).Length / 1KB
Write-Host ("    + {0} ({1:N0} KB)  {2}" -f (Split-Path -Leaf $Path), $kb, $origen)
