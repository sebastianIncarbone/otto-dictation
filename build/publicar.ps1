<#
.SYNOPSIS
    Arma el ZIP portable de Otto.

.DESCRIPTION
    Produce una carpeta que alguien descomprime y ejecuta, sin instalar .NET ni
    el Visual C++ Redistributable. La checklist que esto tiene que satisfacer está
    en docs/distribucion-y-primer-arranque.md.

    Hay dos limpiezas que no son opcionales:

    - Los paquetes de runtime de Whisper.net copian los binarios nativos de TODOS
      los sistemas operativos, sin filtrar por plataforma. En un ZIP de Windows
      eso son 70 MB de Linux, macOS y ARM que nunca se van a ejecutar.
    - SkiaSharp y HarfBuzz traen sus .pdb: 100 MB de símbolos de depuración que a
      un usuario no le sirven de nada.

.EXAMPLE
    .\build\publicar.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Version = '',
    [string]$OutputDir = "$PSScriptRoot\..\dist"
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path "$PSScriptRoot\.."
$staging = Join-Path $OutputDir 'Otto'

Write-Host "==> Limpiando salida anterior"
if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Force $staging | Out-Null

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

Write-Host "==> Comprimiendo"
$zip = Join-Path $OutputDir 'Otto-windows-x64.zip'
Compress-Archive -Path $staging -DestinationPath $zip -CompressionLevel Optimal

$carpetaMb = (Get-ChildItem $staging -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
$zipMb = (Get-Item $zip).Length / 1MB

Write-Host ""
Write-Host ("Carpeta : {0,7:N0} MB" -f $carpetaMb)
Write-Host ("ZIP     : {0,7:N0} MB   {1}" -f $zipMb, $zip)
Write-Host ""
Write-Host "El modelo NO va adentro: se descarga en el primer arranque (~1,6 GB)."
