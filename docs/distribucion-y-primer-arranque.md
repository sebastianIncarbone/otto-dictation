# Distribución y primer arranque

> Este documento responde dos preguntas que son distintas y suelen confundirse:
> **¿corre todo local?** y **¿lo puede instalar y usar otra persona?**
> La primera es una propiedad de diseño y está resuelta. La segunda es un problema
> de ingeniería que tiene trampas concretas, y acá están todas.
>
> Contexto: [Visión de producto](vision-producto.md) · [ADR 0001](adr/0001-stack-tecnologico.md)

---

## Parte 1 — ¿Corre todo localmente?

**Sí, y es una propiedad no negociable del producto.** No es un efecto secundario
de las herramientas elegidas: es la razón por la que se eligieron.

| Componente | Dónde corre | Toca la red |
|---|---|---|
| Captura de audio | Local (WASAPI) | No |
| Transcripción (Whisper.net) | Local, en tu CPU/GPU | No |
| Modelo de Whisper | Archivo en `%LOCALAPPDATA%\Otto\models\` | **Solo la descarga inicial** |
| Post-procesamiento (Ollama) | Local, `http://localhost:11434` | No — es loopback, no sale de la máquina |
| Notas e historial (SQLite) | Archivo en `%LOCALAPPDATA%\Otto\otto.db` | No |
| Configuración | Archivo en `%APPDATA%\Otto\config.json` | No |

**La única conexión de red en toda la vida de la aplicación es la descarga del
modelo la primera vez.** Después de eso, Otto funciona con el cable desenchufado.

### Reglas que protegen esa promesa

Si el posicionamiento del producto es *offline*, la afirmación tiene que ser
literalmente cierta. Entonces:

1. **Sin telemetría. Sin analytics. Sin reportes de error automáticos.** Nada.
2. **El chequeo de actualizaciones no corre solo.** Un "buscar actualizaciones" al
   arrancar es una llamada a un servidor, y convierte "funciona sin internet" en
   una verdad a medias.

   Resuelto en dos niveles: hay un **botón** en la configuración, siempre
   disponible, y un **chequeo automático opt-in que viene apagado de fábrica**. Que
   una persona decida mirar no es lo mismo que la aplicación decida avisar. Quien
   nunca toque ese checkbox tiene una herramienta que, después de la descarga
   inicial, jamás abre un socket.
3. **`localhost` no es "la nube".** Ollama corriendo en la misma máquina es local.
   Vale la pena aclararlo en el README porque la gente ve "HTTP" y asume servidor
   remoto.
4. Esto se puede **demostrar**: el README debería invitar a cortar la red y usar
   la herramienta. Es la prueba más contundente que tiene el proyecto.

---

## Parte 2 — ¿Lo puede instalar y usar otra persona?

Esta es la parte que **todavía no está resuelta del todo**. La arquitectura lo
permite, pero hay siete cosas concretas entre "yo lo compilo y anda" y "un
desconocido lo baja y le funciona".

### Trampa 1 — Whisper.net necesita el Visual C++ Redistributable

**Verificado en la documentación de Whisper.net.** Las librerías nativas de
`whisper.cpp` dependen del runtime de MSVC. En una máquina que no lo tenga, Otto
explota con `DllNotFoundException` al cargar el modelo — o sea, en el peor momento
posible: parece que la app arrancó bien y después falla.

Tu máquina de desarrollo lo tiene instalado (te lo puso Visual Studio hace años).
La de un desconocido puede que no.

**Solución:** desplegar el runtime **junto a la aplicación**. Microsoft lo permite
explícitamente: se copian `msvcp140.dll`, `vcruntime140.dll` y
`vcruntime140_1.dll` al lado del ejecutable. Así el ZIP sigue siendo portable de
verdad.

**Además:** envolver la primera llamada a `WhisperFactory` y usar
`WhisperFactory.GetRuntimeInfo()` para diagnosticar. Si falla, un mensaje que
diga qué falta, no un stack trace.

### Trampa 2 — SmartScreen va a frenar la instalación

Un ejecutable sin firmar dispara la pantalla azul de "Windows protegió tu PC". El
usuario tiene que hacer clic en "Más información" → "Ejecutar de todas formas".
Mucha gente no lo hace: cierra y se va.

Peor: la reputación de SmartScreen se calcula **por hash de binario**. Cada release
nueva vuelve a empezar de cero. No es que se resuelve una vez.

**Opciones reales:**

- **Documentarlo y bancársela.** Gratis. El README muestra la pantalla con una
  captura y explica los dos clics. Es lo que hacen casi todos los proyectos open
  source chicos. Aceptable para v1.
- **Firmar con un certificado de código.** Entre 200 y 400 dólares por año, y un
  certificado OV **igual** arranca sin reputación. Solo un EV la tiene desde el
  día uno, y sale bastante más.

**Decisión para v1:** documentarlo. Pero decirlo de frente en el README, arriba,
no escondido — que alguien se cruce la pantalla sin aviso es peor que advertirlo.

### Trampa 3 — Los antivirus tienen razón en sospechar

Otto registra hotkeys globales, sintetiza pulsaciones de teclado y lee el
portapapeles. Eso es, funcionalmente, la descripción de un keylogger. La detección
heurística lo va a marcar en algunas máquinas.

**Mitigaciones:**

- El modo de hotkey por defecto usa sondeo, no `WH_KEYBOARD_LL` (ver ADR §5.2). El
  hook de bajo nivel, que es lo que más llama la atención, queda como opción.
- **Publicar el análisis de VirusTotal de cada release.** Transparencia proactiva:
  "sé que esto parece sospechoso, acá está el análisis, acá está el código fuente".

  Resuelto sin depender de que nadie se acuerde. El link no va al README —ahí
  quedaría viejo en la release siguiente— sino a las notas de cada release, y lo
  arma el CI a partir del SHA-256 del artefacto, que es cómo VirusTotal direcciona
  sus informes. Si el archivo todavía no fue analizado, esa misma URL es donde se
  lo sube.

  El hash se publica al lado del link, y eso no es decoración: sin él, el análisis
  podría ser de cualquier otro archivo y no habría forma de saberlo.

  **El informe no va a salir limpio, y se publica igual.** Otto registra un atajo
  global y sintetiza pulsaciones de teclado; algunos motores heurísticos lo van a
  marcar. Publicar solo cuando da 0 detecciones sería marketing, no transparencia.
- Que el código sea abierto es, literalmente, la respuesta a esta objeción.

### Trampa 4 — La promesa de latencia depende del hardware ajeno

Esta es la más importante de todas, y la que más fácil se pasa por alto. **Ya está
medida** ([hito 0](hito-0-resultados.md)) y es peor de lo que parecía:

| `large-v3-turbo` | GPU (Vulkan) | CPU | |
|---|---:|---:|---|
| Frase de 5 s | 0,67 s | 17,44 s | 26× |
| Dictado de 30 s | 7,18 s | 122,67 s | 17× |

Entre 17 y 37 veces. No es "un poco más lento": es la diferencia entre una
herramienta y algo inusable.

Vos vas a desarrollar sobre una RTX 4060. Vas a medir latencias buenísimas con
`large-v3-turbo`. Vas a poner esos números en el README. Y después alguien lo va a
correr en una notebook sin GPU dedicada, donde tarda diecisiete segundos.

> **Nota de runtime, resuelta midiendo:** CUDA **no se puede distribuir**. Necesita
> `cudart64` y `cublas64`, que vienen con el CUDA Toolkit y no con el driver — ni
> siquiera cargó en la máquina de desarrollo, que tiene una RTX 4060. Vulkan solo
> necesita `vulkan-1.dll`, que ya viene con cualquier driver. Por eso el artefacto
> lleva **CPU + Vulkan**, nunca CUDA. Ver [ADR §5.4](adr/0001-stack-tecnologico.md).

Esa persona no concluye "mi hardware es modesto". Concluye **"esta herramienta no
funciona"**.

**Solución: detección de hardware en el primer arranque.** Otto tiene que:

1. Detectar qué runtime cargó realmente. **Ojo: `WhisperFactory.GetRuntimeInfo()`
   no sirve para esto** — devuelve el mismo string con Vulkan que con CPU, porque
   solo reporta features del procesador. Hay que intentar cargar y ver si falla,
   que es lo que hace el comando `probe` de `Otto.Bench`.
2. **Recomendar el modelo según eso**, no imponer `large-v3-turbo` a todos:
   - GPU disponible → `large-v3-turbo`
   - Solo CPU → **`base`** (no `small`: medido en 3,5 s para un clip corto)
3. Ofrecer una **prueba de latencia opcional** ahí mismo: graba tres segundos,
   transcribe, muestra el número real **de esa máquina**. Diez segundos de
   onboarding que convierten una decepción en una expectativa calibrada.
4. Y en el README, la tabla de benchmarks va **por hardware**, con una fila de CPU
   sin GPU. El brief ya pedía esa tabla; esto la vuelve obligatoria.

### Trampa 5 — La descarga del modelo son 1,5 GB

Es el primer contacto del usuario con la aplicación, así que no puede ser frágil.

- Barra de progreso con velocidad y tiempo estimado (el brief ya lo pide).
- **Descarga reanudable.** Que se corte a los 1,4 GB y haya que empezar de nuevo es
  una razón perfectamente válida para desinstalar algo.
- **Verificación de checksum** al terminar. Un modelo corrupto falla de formas
  rarísimas y confusas.
- Permitir **elegir la carpeta** — no todo el mundo tiene 1,5 GB libres en `C:`.
- Permitir **apuntar a un archivo ya descargado**, para quien ya tiene modelos GGML
  de otra herramienta.

### Trampa 6 — El micrófono puede estar bloqueado por Windows

Windows 10 y 11 tienen un permiso de privacidad global: *"Permitir que las
aplicaciones de escritorio accedan al micrófono"*. Si está apagado, la captura no
falla con un error claro — **devuelve silencio**. Otto transcribiría la nada y
parecería roto sin ninguna pista de por qué.

**Solución:** detectar el caso (audio capturado con amplitud en cero durante toda
la grabación) y mostrar un mensaje concreto que linkee directo a la pantalla de
configuración de privacidad de Windows.

### Trampa 7 — ZIP portable significa que no hay instalador

**Resuelta.** Y la lección está en cómo se había planteado mal.

El brief decidió, con buen criterio, que **no hay MSI firmado**. En algún momento
eso se leyó como *no hay instalador*, y son dos cosas distintas: lo caro y lo
inviable era el certificado, no el instalador. Un instalador sin firmar es
perfectamente posible — trae exactamente la misma advertencia de SmartScreen que
un ZIP sin firmar, ni una más.

Con esa distinción hecha, la decisión se vuelve fácil: **Inno Setup**, gratis, un
solo `.exe` de salida, y el script vive en `build/otto.iss`.

| Lo que un instalador haría | Quién lo hace ahora |
|---|---|
| Entrada en el menú Inicio | El instalador, con checkbox |
| Acceso directo en el escritorio | El instalador, con checkbox |
| Entrada en "Agregar o quitar programas" | El instalador |
| Desinstalación | El desinstalador de Windows, que además pregunta si borrar los datos |
| Arranque con Windows | **Otto, desde su configuración.** Deliberadamente NO está en el instalador |
| Asociaciones de archivos | No aplica |

#### Por qué es por usuario y no en Archivos de programa

Otto ya es una aplicación por usuario de punta a punta: configuración en
`%APPDATA%`, notas y modelo en `%LOCALAPPDATA%`, arranque automático en `HKCU`.
Instalar en `Archivos de programa` no compartiría **nada** de eso — cada usuario
igual se bajaría su propio modelo de 1,6 GB — y sumaría un aviso de UAC encima del
de SmartScreen. Dos pantallas de miedo seguidas antes de ver la aplicación es peor
producto, no más seriedad.

Va entonces a `%LOCALAPPDATA%\Programs\Otto`, que es donde instalan las
aplicaciones por usuario en Windows moderno. Es, sin ir más lejos, dónde termina el
propio Inno Setup si lo instalás con `winget` sin elevar.

#### La separación que hace que reinstalar no duela

```
%LOCALAPPDATA%\Programs\Otto   la aplicación   ← la borra el desinstalador
%LOCALAPPDATA%\Otto            notas, modelos  ← sobreviven salvo que digas que sí
%APPDATA%\Otto                 configuración   ← ídem
```

Son carpetas **hermanas**, no anidadas, y eso es a propósito. Si la aplicación
viviera dentro de `%LOCALAPPDATA%\Otto`, el botón de desinstalar que Otto ya tenía
—que borra esa carpeta entera— estaría borrando el ejecutable en ejecución.

#### El arranque automático no va en el instalador

Es la opción que todo instalador ofrece y acá se dejó afuera. Otto ya tiene ese
checkbox en su configuración, y ese checkbox refleja el estado real de la clave
`Run`. Si el instalador también la escribiera, habría dos fuentes de verdad que se
pisan la primera vez que alguien toque *Guardar* en la configuración y
`Autostart.Apply(false)` borre lo que el instalador había puesto.

#### Dos formas de sacarlo, y no se pueden contradecir

Ahora Otto se distribuye de dos maneras, así que hay dos caminos de desinstalación.
`Uninstaller.InstalledUninstaller()` los distingue leyendo la clave que Inno deja en
`HKCU`:

- **Instalado:** Windows manda. El botón de la configuración abre el desinstalador
  real y Otto se corre a un costado. Borrar los datos desde adentro dejaría los
  archivos del programa y la entrada de "Agregar o quitar programas" huérfanas —
  un desastre peor que el que ese botón existía para evitar.
- **Portable:** no hay nadie más que lo haga, así que lo hace Otto, como siempre.

#### La trampa dentro de la trampa: desinstalar en silencio

El desinstalador pregunta si borrar las notas y el modelo. La versión "cuidadosa"
de ese código —preguntar siempre— tiene un agujero que muerde fuerte:

> **Inno responde que sí a los cuadros de Sí/No cuando se los suprime con
> `/SUPPRESSMSGBOXES`.** No respeta el botón por defecto.

O sea que cualquiera que desinstale desde un script o una herramienta de
administración perdería las notas y 1,6 GB de modelo sin pantalla y sin haberlo
pedido. Por eso `CurUninstallStepChanged` chequea `WizardSilent()` y **conserva**
cuando no hay nadie mirando. Quien de verdad quiera limpiar todo lo dice con
`/DELETEDATA`.

#### Lo que el instalador NO arregla

**SmartScreen.** Sigue igual, porque el problema es la firma, no el formato. Ver
trampa 2.

#### El ZIP portable se mantiene

No lo reemplaza: convive. Sirve para un pendrive, para una máquina con políticas
que no dejan instalar, y para quien simplemente no quiere que un instalador le
toque nada. Los dos artefactos salen del mismo `publicar.ps1` y de la misma carpeta
publicada, así que no hay forma de que diverjan.

Un detalle que se olvida siempre y que **sigue vigente para la versión portable**:
si el usuario mueve la carpeta después de activar el arranque automático, la
entrada del registro queda apuntando a la nada. `Autostart.RepairIfMoved()` valida
la ruta al arrancar y la reescribe si cambió.

---

## El primer arranque, de punta a punta

Así se ve la experiencia que hay que construir. Es un hito en sí mismo, no algo
que sale solo:

```
  Baja Otto-Setup.exe y lo ejecuta
            ↓
  SmartScreen  →  documentado en el README con captura
            ↓
  Instalador: elige accesos directos, acepta. Sin UAC.
            ↓
  Otto arranca solo al terminar la instalación
            ↓
  Otto detecta el hardware
  "Encontré una GPU NVIDIA. Te recomiendo large-v3-turbo."
  "No encontré GPU. Te recomiendo small — large sería lento acá."
            ↓
  Descarga del modelo, con progreso, reanudable, verificada
            ↓
  Prueba de latencia opcional (3 segundos de audio)
  "En tu máquina: 0,9 s. Listo."
            ↓
  Elegir hotkey  →  probarla ahí mismo, en la misma pantalla
            ↓
  Otto se minimiza a la bandeja. Aparece el personaje.
            ↓
  Funciona. Y a partir de acá, sin internet.
```

## Impacto en el plan

Esto agrega trabajo que no estaba en los hitos del ADR:

- **Hito 0:** medir también por CPU sin GPU, no solo con la 4060. Ese número es el
  que define qué modelo se le recomienda a la mayoría de la gente.
- **Hito 6 (empaquetado):** deja de ser "comprimir una carpeta". Incluye el
  asistente de primer arranque, el despliegue app-local del VC++ runtime, la
  descarga reanudable con checksum, y la detección de hardware.

## Checklist de "otra persona lo puede usar"

Sirve como criterio de corte del hito 6. Idealmente, probado en una máquina que
**no** sea la de desarrollo — sin Visual Studio, sin CUDA Toolkit, sin .NET
instalado.

- [x] Se instala sin pedir administrador — por usuario, en `%LOCALAPPDATA%\Programs\Otto`
- [x] Deja acceso directo en el menú Inicio y en el escritorio, ambos opcionales
- [x] Aparece en "Agregar o quitar programas" y se desinstala desde ahí sin dejar nada
- [x] Desinstalar no borra las notas ni el modelo salvo que el usuario diga que sí
- [x] Desinstalar en silencio **conserva** los datos — `WizardSilent()`, porque `/SUPPRESSMSGBOXES` contesta que sí
- [x] Los accesos directos tienen ícono — generado en build, no commiteado
- [x] Se descomprime y ejecuta sin instalar .NET — publicado autocontenido
- [x] Se descomprime y ejecuta sin instalar el VC++ Redistributable — las tres DLL van al lado del ejecutable
- [x] Arranca en una máquina sin GPU dedicada y recomienda un modelo usable — `HardwareProbe` decide antes de descargar
- [x] La descarga del modelo sobrevive a un corte de red — verificado cortando a los 40 MB y reanudando
- [x] Da un mensaje claro si el micrófono está bloqueado por privacidad de Windows — bandera `Silent` de WASAPI
- [x] Funciona con el WiFi apagado después de la primera vez
- [x] Funciona sin Ollama instalado — se desactiva el post-proceso al arrancar
- [x] El README advierte sobre SmartScreen **antes** de que aparezca
- [x] Se puede desinstalar sin dejar archivos sueltos — botón en la configuración
- [x] Hay un link a VirusTotal de la release — lo arma el CI desde el SHA-256, junto al hash
- [ ] Probado en una máquina que no es la de desarrollo
