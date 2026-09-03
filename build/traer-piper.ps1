<#
.SYNOPSIS
    Deja el motor de lectura en `build\.piper`, bajándolo si hace falta.

.DESCRIPTION
    Existe por dos consumidores, y por un bug que solo aparecía en uno de ellos.

    `publicar.ps1` siempre lo necesitó. Lo que faltaba era el otro lado: Otto.App.csproj
    copia esta caché a su carpeta de salida al compilar, así que `dotnet run` también
    puede leer en voz alta. Sin eso, la lectura corriendo desde el código fuente NO PODÍA
    ANDAR NUNCA — piper.exe solo aparecía en el staging del publish — y el síntoma era una
    tecla que no hacía nada visible.

    Es idempotente: si el zip ya está en la caché no toca la red. Por eso el csproj lo
    puede llamar sin volver cada compilación una descarga.

.PARAMETER Force
    Vuelve a bajarlo aunque ya esté en la caché.
#>
[CmdletBinding()]
param([switch]$Force)

$ErrorActionPreference = 'Stop'

# La misma URL y la misma caché que usaba publicar.ps1 antes de que esto se separara.
# Una segunda copia de la versión es una segunda copia que puede divergir, así que
# publicar.ps1 ahora llama acá en vez de repetirla.
$cache = Join-Path $PSScriptRoot '.piper'
$zip = Join-Path $cache 'piper_windows_amd64.zip'
$url = 'https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip'

# No vive en el repositorio, y eso es deliberado. CLAUDE.md pone el listón alto para un
# tercer binario commiteado: el personaje está porque es arte autoral y las tipografías
# porque bajarlas al arrancar rompería la promesa de andar sin internet. Un ejecutable de
# terceros de 21 MB no pasa ese listón — es un artefacto de release, del mismo tipo que
# los modelos, que tampoco se commitean.
if ($Force -and (Test-Path $zip)) { Remove-Item $zip -Force }

if (-not (Test-Path $zip)) {
    Write-Host "==> Bajando Piper (~21 MB, una sola vez)"
    New-Item -ItemType Directory -Force $cache | Out-Null

    # A un archivo temporal primero: un script cortado a la mitad no puede dejar un zip
    # truncado que la próxima corrida dé por bueno. Es la misma convención del .part que
    # usa ModelDownloader, por el mismo motivo.
    $parcial = "$zip.part"

    try {
        Invoke-WebRequest -Uri $url -OutFile $parcial -MaximumRedirection 5
        Move-Item $parcial $zip -Force
    }
    catch {
        if (Test-Path $parcial) { Remove-Item $parcial -Force }

        throw @"
No se pudo bajar Piper, que es el motor de la lectura en voz alta:

    $url

Sin esto la lectura queda muerta: Otto la ofrece en Ajustes, la casilla se prende,
y no suena nada. Preferimos fallar acá.

Detalle: $($_.Exception.Message)
"@
    }
}

# Descomprimido en la caché, no en el destino. Los dos consumidores copian desde acá, y
# descomprimir una vez sola es lo que hace que compilar no pague 21 MB de Expand-Archive
# en cada corrida.
#
# El zip trae adentro una carpeta `piper/`, así que esto deja `build\.piper\piper\piper.exe`,
# que es la forma que TtsOptions.EngineDirectory espera: AppContext.BaseDirectory\piper.
$motor = Join-Path $cache 'piper'
$exe = Join-Path $motor 'piper.exe'
$espeak = Join-Path $motor 'espeak-ng-data'

if (-not (Test-Path $exe) -or -not (Test-Path $espeak)) {
    Expand-Archive -Path $zip -DestinationPath $cache -Force
}

# Las dos mitades, no una. Un piper.exe sin su espeak-ng-data arranca, sale con código 0
# y produce un WAV mudo — el modo de falla que más cuesta diagnosticar de toda esta
# función, y el único que un chequeo acá puede cerrar de antemano.
if (-not (Test-Path $exe)) { throw "El zip de Piper no dejo piper.exe en $exe" }
if (-not (Test-Path $espeak)) { throw "El zip de Piper no dejo espeak-ng-data en $espeak" }

# La ruta a la carpeta lista, para que quien llame no tenga que reconstruirla.
$motor
