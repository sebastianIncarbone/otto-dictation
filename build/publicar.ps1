<#
.SYNOPSIS
    Arma el instalador y el ZIP portable de Otto.

.DESCRIPTION
    Produce dos artefactos a partir de la misma carpeta publicada:

    - Otto-Setup.exe, el instalador (menú Inicio, acceso directo, entrada en
      "Agregar o quitar programas"). Es lo que se le ofrece a una persona.
    - Otto-windows-x64.zip, la carpeta portable. Sirve para un pendrive, para
      una máquina donde no se puede instalar nada, y para quien no quiere que
      un instalador le toque nada.

    Ninguno de los dos necesita .NET ni el Visual C++ Redistributable
    preinstalados. La checklist que esto tiene que satisfacer está en
    docs/distribucion-y-primer-arranque.md.

    Hay dos limpiezas que no son opcionales:

    - Los paquetes de runtime de Whisper.net copian los binarios nativos de TODOS
      los sistemas operativos, sin filtrar por plataforma. En un ZIP de Windows
      eso son 70 MB de Linux, macOS y ARM que nunca se van a ejecutar.
    - SkiaSharp y HarfBuzz traen sus .pdb: 100 MB de símbolos de depuración que a
      un usuario no le sirven de nada.

    El instalador se compila con Inno Setup. Si falta ISCC.exe el script FALLA en
    vez de saltearlo: una release que sale sin instalador porque el runner no lo
    tenía instalado es exactamente el tipo de error que nadie mira hasta que un
    usuario pregunta dónde está el archivo. Para armar sólo el ZIP, -NoInstaller.

.EXAMPLE
    .\build\publicar.ps1

.EXAMPLE
    .\build\publicar.ps1 -Version 0.2.0

.EXAMPLE
    .\build\publicar.ps1 -NoInstaller
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Version = '',
    [string]$OutputDir = "$PSScriptRoot\..\dist",
    [switch]$NoInstaller
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path "$PSScriptRoot\.."
$staging = Join-Path $OutputDir 'Otto'

# Se busca antes de compilar nada. Descubrir que falta Inno Setup después de
# cuatro minutos de publish es tiempo tirado.
$iscc = $null

if (-not $NoInstaller) {
    # La tercera ruta no es rebuscada: Inno Setup instalado sin privilegios de
    # administrador — que es lo que pasa con `winget install` a secas — se pone
    # ahí. Es, de hecho, el mismo lugar donde Otto se instala a sí mismo.
    $iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $iscc) {
        $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
    }

    if (-not $iscc) {
        throw @'
No se encontró ISCC.exe (el compilador de Inno Setup), que hace falta para armar
el instalador. Instalalo con:

    winget install JRSoftware.InnoSetup

Si sólo querés el ZIP portable, volvé a correr esto con -NoInstaller.
'@
    }
}

# Piper se baja acá arriba por la misma razón que ISCC se busca acá arriba:
# enterarse de que falta después de cuatro minutos de publish es tiempo tirado.
#
# No vive en el repositorio, y eso es deliberado. CLAUDE.md pone el listón alto
# para un tercer binario commiteado: el personaje está porque es arte autoral y
# las tipografías porque bajarlas al arrancar rompería la promesa de andar sin
# internet. Un ejecutable de terceros de 21 MB no pasa ese listón — es un
# artefacto de release, del mismo tipo que los modelos que tampoco se commitean.
#
# El zip trae adentro una carpeta `piper/`, así que descomprimirlo en el staging
# deja exactamente `piper\piper.exe`, que es lo que TtsOptions.EngineDirectory
# resuelve como AppContext.BaseDirectory\piper. Y espeak-ng-data queda al lado
# del ejecutable, que es donde piper.exe la busca: la resuelve relativo al
# DIRECTORIO DE TRABAJO y no al suyo, así que si esa carpeta no viaja con él la
# lectura no falla — devuelve silencio, sin un solo error en ningún lado.
$piperCache = Join-Path $PSScriptRoot '.piper'
$piperZip = Join-Path $piperCache 'piper_windows_amd64.zip'
$piperUrl = 'https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip'

if (-not (Test-Path $piperZip)) {
    Write-Host "==> Bajando Piper (~21 MB, una sola vez)"
    New-Item -ItemType Directory -Force $piperCache | Out-Null

    # A un archivo temporal primero: un script cortado a la mitad no puede dejar
    # un zip truncado que la próxima corrida dé por bueno. Es la misma convención
    # del .part que usa ModelDownloader, por el mismo motivo.
    $parcial = "$piperZip.part"

    try {
        Invoke-WebRequest -Uri $piperUrl -OutFile $parcial -MaximumRedirection 5
        Move-Item $parcial $piperZip -Force
    }
    catch {
        if (Test-Path $parcial) { Remove-Item $parcial -Force }

        throw @"
No se pudo bajar Piper, que es el motor de la lectura en voz alta:

    $piperUrl

Sin esto el paquete se arma igual pero la lectura queda muerta: Otto la ofrece
en Ajustes, la casilla se prende, y no suena nada. Preferimos fallar acá.

Detalle: $($_.Exception.Message)
"@
    }
}

Write-Host "==> Limpiando salida anterior"
if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Force $staging | Out-Null

# Antes del publish, no después: el ícono se embebe en el ejecutable en tiempo
# de compilación, y de ahí lo heredan los accesos directos sin configurar nada.
Write-Host "==> Generando el ícono"
& "$PSScriptRoot\icono.ps1" -Path "$repo\src\Otto.App\Otto.ico"

Write-Host "==> Publicando ($Configuration, $Runtime, autocontenido)"

$publishArgs = @(
    '--configuration', $Configuration
    '--runtime', $Runtime
    '--self-contained', 'true'
    '-p:PublishReadyToRun=true'
    '-p:DebugType=none'
    '-p:DebugSymbols=false'
    '--output', $staging
    '--nologo'
)

# La etiqueta de git manda cuando el CI publica una release. Sin esto, la version
# del ensamblado queda en la del csproj y el chequeo de actualizaciones compara
# contra un numero que no corresponde a lo que se subio.
if ($Version) {
    Write-Host "    version: $Version"
    $publishArgs += "-p:Version=$Version"
}

dotnet publish "$repo\src\Otto.App" @publishArgs | Where-Object { $_ -match 'error|warning' }

if ($LASTEXITCODE -ne 0) { throw "Falló el publish" }

Write-Host "==> Sacando binarios nativos de otras plataformas"
$runtimesDir = Join-Path $staging 'runtimes'
if (Test-Path $runtimesDir) {
    # Los directorios que quedan son los que este ZIP puede llegar a cargar:
    # el runtime de CPU y el de Vulkan, ambos para Windows x64.
    Get-ChildItem $runtimesDir -Recurse -Directory |
        Where-Object { $_.Name -match '^(linux|macos|osx)' -or $_.Name -in @('win-arm64', 'win-x86') } |
        ForEach-Object {
            $mb = (Get-ChildItem $_.FullName -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
            Write-Host ("    - {0} ({1:N1} MB)" -f $_.FullName.Replace("$staging\", ''), $mb)
            Remove-Item $_.FullName -Recurse -Force
        }
}

Write-Host "==> Sacando simbolos de depuracion"
Get-ChildItem $staging -Recurse -Include *.pdb | ForEach-Object {
    Write-Host ("    - {0} ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB))
    Remove-Item $_.FullName -Force
}

# Las librerias nativas de whisper.cpp dependen del runtime de MSVC. Microsoft
# permite explicitamente desplegarlo junto a la aplicacion, y sin esto Otto
# revienta con DllNotFoundException al cargar el modelo en cualquier maquina que
# no tenga Visual Studio instalado -- o sea, casi todas.
Write-Host "==> Copiando el runtime de Visual C++ al lado del ejecutable"
$vcDlls = @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')
$faltantes = @()

foreach ($dll in $vcDlls) {
    $origen = Join-Path $env:SystemRoot "System32\$dll"
    if (Test-Path $origen) {
        Copy-Item $origen $staging -Force
        Write-Host "    + $dll"
    }
    else {
        $faltantes += $dll
    }
}

if ($faltantes) {
    Write-Warning "No se encontraron: $($faltantes -join ', '). El ZIP va a fallar en maquinas sin el VC++ Redistributable."
}

# Después de sacar los .pdb y los runtimes ajenos, nunca antes: esos dos pasos
# barren el staging con -Recurse y se llevarían medio Piper por delante.
Write-Host "==> Copiando el motor de lectura"
Expand-Archive -Path $piperZip -DestinationPath $staging -Force

$piperExe = Join-Path $staging 'piper\piper.exe'
$espeak = Join-Path $staging 'piper\espeak-ng-data'

# Las dos mitades, no una. Un piper.exe sin su espeak-ng-data arranca, sale con
# codigo 0 y produce un WAV mudo — el modo de falla que mas cuesta diagnosticar
# de toda esta funcion, y el unico que un chequeo aca puede cerrar de antemano.
if (-not (Test-Path $piperExe)) { throw "El zip de Piper no dejo piper.exe en $piperExe" }
if (-not (Test-Path $espeak)) { throw "El zip de Piper no dejo espeak-ng-data en $espeak" }

$piperMb = (Get-ChildItem (Join-Path $staging 'piper') -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("    + piper\ ({0:N0} MB)" -f $piperMb)

# Las licencias de terceros viajan con el binario, no solo con el repositorio.
# SoundTouch.Net es LGPL y es la unica dependencia que pide algo mas que el aviso:
# el usuario tiene que poder reemplazar el DLL. Eso se cumple porque publicamos
# self-contained pero NO single-file ni trimmed, asi que SoundTouch.Net.dll queda
# como ensamblado suelto al lado del ejecutable. Si alguien prende PublishSingleFile
# esa libertad desaparece y este archivo pasa a mentir, por eso el chequeo de abajo
# verifica que el DLL siga estando suelto.
Write-Host "==> Copiando los avisos de terceros"
Copy-Item (Join-Path $repo 'THIRD-PARTY-NOTICES.md') $staging -Force

$soundTouch = Join-Path $staging 'SoundTouch.Net.dll'

if (-not (Test-Path $soundTouch)) {
    throw @"
No aparecio SoundTouch.Net.dll suelto en el staging.

Es la libreria LGPL que hace el control de velocidad de la lectura, y su licencia
exige que el usuario pueda reemplazarla. Si esto falla es porque el publish la
empaqueto adentro del ejecutable (PublishSingleFile) o la recorto (PublishTrimmed):
hay que revertir eso, no sacar este chequeo.
"@
}

Write-Host "==> Comprimiendo"
$zip = Join-Path $OutputDir 'Otto-windows-x64.zip'
Compress-Archive -Path $staging -DestinationPath $zip -CompressionLevel Optimal

# La versión del instalador sale del ejecutable ya publicado, nunca del
# parámetro. Es la misma regla que hace que la etiqueta de git mande: si el
# instalador dijera una versión y la aplicación otra, "Agregar o quitar
# programas" y el chequeo de actualizaciones se contradirían en silencio.
$mostrada = (Get-Item "$staging\Otto.App.exe").VersionInfo.ProductVersion.Split('+')[0]
$numerica = ($mostrada -split '-')[0]

$setup = $null

if (-not $NoInstaller) {
    Write-Host "==> Compilando el instalador (version $mostrada)"

    & $iscc /Q `
        "/DVersion=$mostrada" `
        "/DNumericVersion=$numerica" `
        "/DStaging=$staging" `
        "$PSScriptRoot\otto.iss"

    if ($LASTEXITCODE -ne 0) { throw "Falló la compilación del instalador" }

    $setup = Join-Path $OutputDir 'Otto-Setup.exe'
    if (-not (Test-Path $setup)) { throw "Inno Setup terminó bien pero no dejó $setup" }
}

$carpetaMb = (Get-ChildItem $staging -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
$zipMb = (Get-Item $zip).Length / 1MB

Write-Host ""
Write-Host ("Version : {0,7}" -f $mostrada)
Write-Host ("Carpeta : {0,7:N0} MB" -f $carpetaMb)
Write-Host ("ZIP     : {0,7:N0} MB   {1}" -f $zipMb, $zip)

if ($setup) {
    Write-Host ("Setup   : {0,7:N0} MB   {1}" -f ((Get-Item $setup).Length / 1MB), $setup)
}

Write-Host ""
Write-Host "El modelo NO va adentro: se descarga en el primer arranque (~1,6 GB)."
