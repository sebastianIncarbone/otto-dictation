# ADR 0001 — Stack tecnológico

- **Estado:** Aceptado
- **Fecha:** 2026-08-15
- **Reemplaza:** la sección 3 del [brief técnico](../brief.md), que dejaba esta decisión abierta
- **Contexto de producto:** [Visión de producto](../vision-producto.md)
- **Evidencia:** [Hito 0 — Resultados medidos](../hito-0-resultados.md). Las secciones
  §5.4 y §5.7 dejaron de ser hipótesis: están medidas.

---

## 1. Contexto

Otto es una app de dictado por voz que corre en segundo plano, transcribe
localmente y escribe el resultado en la ventana que tenga el foco. Además guarda
el historial de transcripciones como notas editables, y muestra un personaje
animado flotando en pantalla mientras está activa.

Cuatro restricciones ordenan la decisión, en este orden:

1. **Windows es la única plataforma soportada en v1.** macOS queda afuera de forma
   permanente (no hay hardware para mantenerlo). Linux queda afuera de v1 por una
   razón técnica concreta, documentada en §6 — **no** por falta de ganas.
2. **Presupuesto de latencia.** El texto tiene que aparecer en menos de ~1,5 s
   después de soltar la tecla. Es un requisito de producto, no un deseo: una
   herramienta más lenta que esto no se usa.
3. **Distribución.** Un artefacto portable que un desconocido baja y ejecuta. Sin
   instalador, sin dependencias previas, sin `pip install`.
4. **Legibilidad como portfolio.** El repo es una muestra de trabajo. Se tiene que
   entender en dos minutos y compilar con un comando.

### Alcance y arquitectura son decisiones separadas

Esto es lo más importante de todo el documento, así que va primero.

Recortar el alcance a Windows **no** implica escribir código atado a Windows. Son
dos decisiones independientes, y confundirlas es el error caro:

- **Alcance = Windows v1.** Se implementa, se prueba y se soporta una sola
  plataforma. Menos superficie, menos bugs, se llega antes a algo usable.
- **Arquitectura = puertos y adaptadores.** El núcleo no toca el sistema operativo.
  Todo lo nativo vive detrás de una interfaz.

Lo segundo no es "impuesto de Linux": **el brief ya lo pedía** en su §4, y ya lo
necesitás para tener tests unitarios sin micrófono ni GPU. Es trabajo que hay que
hacer igual. El efecto secundario de hacerlo bien es que, si algún día aparece
Linux, es escribir un proyecto de adaptadores en vez de reescribir la app.

Dicho de otra forma: cerrar la puerta cuesta lo mismo que dejarla entornada. Así
que se deja entornada.

### Las cinco primitivas nativas

Otto necesita cinco cosas del sistema operativo que **ningún framework de UI te
da**, en ninguna plataforma:

| Primitiva | Para qué | Puerto |
|---|---|---|
| Hotkey global | Capturar la tecla estando sin foco | `IHotkeyService` |
| Inyección de teclas | Escribir en la app ajena que tiene el foco | `ITextInjector` |
| Portapapeles desde segundo plano | Estrategia de pegado rápido | `ITextInjector` |
| Detectar la ventana/proceso en foco | Post-procesamiento según contexto | `IForegroundWindow` |
| Ventana overlay siempre visible y click-through | El personaje animado | `IOverlayHost` |

Esa es la frontera real del sistema. Todo lo demás — pipeline, notas,
post-procesamiento, configuración — es código portable y testeable.

### Máquina de desarrollo (medida, no supuesta)

| | |
|---|---|
| CPU | Intel Core i7-13620H — 10 núcleos / 16 hilos |
| GPU | NVIDIA GeForce RTX 4060 Laptop + Intel UHD (híbrida) |
| Driver NVIDIA | 32.0.16.1062 |
| RAM | 32 GB |
| SDKs .NET instalados | 10.0.300, 10.0.301 |
| Python instalado | 3.14.5 |
| Toolchain de Rust | no instalado |
| Ollama | no instalado |

La RTX 4060 despeja el riesgo principal de viabilidad del brief: `large-v3-turbo`
acelerado por GPU en este hardware transcribe una frase de 15 segundos bien dentro
del presupuesto. Igual hay que medirlo (hito 0), pero es muy probable que pase.

---

## 2. Opciones evaluadas

### A. .NET 10 + Avalonia UI + Whisper.net — **elegida**

Una sola base de código .NET, con Avalonia para la UI y Whisper.net (binding de
`whisper.cpp`) para la transcripción.

- **+** Avalonia renderiza con **Skia**, que es lo que hace viable el personaje
  animado y una UI con identidad propia. WPF renderiza con DirectX a través de un
  modelo mucho más rígido para dibujo custom.
- **+** `TrayIcon` viene **en el framework**, con menú nativo. En WPF hay que sumar
  `Hardcodet.NotifyIcon.Wpf`, una dependencia de terceros más.
- **+** Ventanas sin decoración con fondo transparente son soporte de primera
  clase (`WindowDecorations="None"` + `TransparencyLevelHint`), que es exactamente
  la ventana del personaje.
- **+** Lottie está integrado (`Avalonia.Labs.Lottie`, sobre Skottie/Skia), así que
  la animación del personaje es un control, no un proyecto.
- **+** El sistema de estilos es mejor que el de WPF: selectores tipo CSS en vez de
  la maraña de `Style`/`Trigger`/`Template`.
- **+** Whisper.net entrega runtimes nativos precompilados con fallback automático
  `Cuda13 → Cuda12 → Vulkan → OpenVino → Cpu → CpuNoAvx`, validando el driver. El
  requisito del brief de "funcionar en máquinas sin GPU" se resuelve eligiendo
  paquetes, no escribiendo `if`s.
- **+** Trae Silero VAD en la misma librería (`WhisperVadFactory`), que es la
  ganancia de latencia más barata disponible.
- **+** P/Invoke de primera clase para las cinco primitivas nativas.
- **+** Efecto secundario gratis: deja Linux abierto para el futuro.
- **−** Menos respuestas en Stack Overflow que WPF, y sin diseñador visual en
  Visual Studio.
- **−** No se ve 100% nativo de Windows. Para una app con personalidad propia y un
  personaje animado en pantalla, es un costo menor — de hecho es lo buscado.

### B. .NET 10 + WPF — **la opción obvia si solo hay Windows, y aun así se rechaza**

Al recortar el alcance a Windows, WPF vuelve a estar sobre la mesa. Se evaluó en
serio y perdió:

- **+** Más maduro en Windows, más documentación, diseñador visual, y una
  integración visual nativa que Avalonia no iguala.
- **−** El personaje animado es el caso de uso que peor le sienta. Dibujo custom y
  animación vectorial en WPF son bastante más trabajo que en Skia, y no hay una
  historia de Lottie de primera clase.
- **−** Necesita `Hardcodet.NotifyIcon.Wpf` para algo que Avalonia trae de fábrica.
- **−** Cierra Linux para siempre. Volver atrás sería reescribir toda la UI.
- **Veredicto:** la ventaja de WPF es "más ejemplos en internet". La desventaja es
  "peor para las dos features visuales que definen el producto, y sin retorno".
  No compensa.

### C. Tauri v2 + Rust — **finalista legítimo**

- **+** Binario chico, tray y atajos globales cubiertos por plugins oficiales.
- **+** La UI de notas sería más rápida y linda de construir en web.
- **−** No hay toolchain de Rust instalado. La curva es real y el hito 0 se corre
  semanas.
- **−** `whisper-rs` con CUDA exige compilar contra un CUDA Toolkit instalado
  localmente, lo que convierte "compilar en CI" en un proyecto aparte. Whisper.net
  te da binarios ya compilados.
- **Veredicto:** es la alternativa seria. Se rechaza por tiempo hasta el hito 0 y
  por el costo de CI con CUDA, no por calidad técnica.

### D. Python + faster-whisper + PySide6

- **+** Iteración rapidísima y el motor de inferencia más rápido (CTranslate2).
- **−** **El empaquetado lo mata.** PyInstaller más los DLL de CUDA y cuDNN da un
  bundle de varios gigas, y los ejecutables one-file de PyInstaller tienen peor
  reputación ante SmartScreen y antivirus que un binario .NET — en una app que ya
  registra hotkeys globales y sintetiza teclas, eso se suma a un riesgo que el
  brief ya identifica en §8.
- **−** El intérprete instalado es Python 3.14. Los wheels de CTranslate2
  históricamente van atrás de las versiones nuevas de CPython, así que este camino
  arranca fijando un entorno 3.11/3.12 aparte. Fricción el día uno y una trampa
  para cualquiera que clone el repo.
- **−** El push-to-talk global en `pynput` se implementa con un hook de teclado de
  bajo nivel, o sea la opción de mayor riesgo ante antivirus, sin alternativa.
- **Veredicto:** rechazado como stack de producción. Se conserva como **banco de
  pruebas** opcional en el hito 0 si se quiere un segundo punto de comparación de
  precisión — eso es un script descartable, no una dependencia.

### E. Electron

Rechazado. Un runtime de Chromium de ~200 MB para hospedar un ícono de bandeja y
un formulario, envolviendo las mismas llamadas nativas a través de un FFI peor.
El consumo en reposo además choca de frente con un criterio de aceptación del
brief.

---

## 3. Decisión

| Componente | Elección | Nota |
|---|---|---|
| Plataforma | **Windows 10/11 x64** | Única soportada en v1 |
| Runtime | **.NET 10 (LTS)** | No .NET 8 — ver §5.1 |
| UI | **Avalonia UI 11** | `TrayIcon` nativo, ventanas transparentes, render Skia |
| Patrón de UI | MVVM con `CommunityToolkit.Mvvm` | Generadores de código, sin reflexión |
| STT | `Whisper.net` | Sobre `whisper.cpp` |
| Runtimes STT | `Whisper.net.Runtime` (CPU) + **`.Vulkan`** | CUDA descartado — medido, §5.4 |
| Modelo | `ggml-large-v3-turbo` | Confirmado midiendo. Fallback sin GPU: `base`, no `small` |
| VAD | Silero vía `WhisperVadFactory` | **Compuerta, no partidor.** Ver §5.7 |
| Captura de audio | `NAudio` (`WasapiCapture`) | Resampleo a 16 kHz mono float32 |
| Persistencia de notas | **SQLite** (`Microsoft.Data.Sqlite`) + FTS5 | Ver §4 |
| Config | JSON en `%APPDATA%\Otto\` | Como el brief |
| Personaje animado | Ventana Avalonia sin decoración + Lottie | Ver §5.7 |
| LLM opcional | Ollama por HTTP | Fuera del camino crítico — ver §5.3 |
| DI | `Microsoft.Extensions.DependencyInjection` | Como el brief |
| Tests | xUnit + NSubstitute | Solo servicios de `Otto.Core`; sin tests de UI en v1 |
| CI | GitHub Actions, `windows-latest` | Compila, testea y publica el ZIP |

### Estructura del repositorio

Los **puertos** (interfaces) viven en `Otto.Core`. Todo lo específico del sistema
operativo o de un proveedor externo es un **adaptador**.

```
otto/
├── docs/
│   ├── brief.md
│   ├── vision-producto.md
│   └── adr/0001-stack-tecnologico.md
├── src/
│   ├── Otto.Core/                 Puertos + orquestación del pipeline.
│   │                              Cero código nativo, cero dependencias de SO.
│   ├── Otto.Speech/               Adaptador de Whisper.net, descarga y caché del modelo
│   ├── Otto.Storage/              Adaptador de SQLite: notas, historial, búsqueda
│   ├── Otto.PostProcessing/       Adaptador de Ollama, mapeo contexto→formato, diccionario
│   ├── Otto.Platform.Windows/     P/Invoke Win32: hotkey, inyección, portapapeles, ventana en foco
│   └── Otto.App/                  Host Avalonia: tray, ventana principal, overlay, composition root
└── tests/
    └── Otto.Core.Tests/
```

`Otto.Platform.Windows` es el **único** proyecto con código nativo. Si alguna vez
aparece `Otto.Platform.Linux`, se suma al lado sin tocar nada más.

Los puertos que definen la frontera:

```csharp
public interface IHotkeyService     // registrar combinación, eventos de presión y liberación
public interface IAudioCapture      // PCM 16 kHz mono float32
public interface ITranscriber       // audio + prompt de contexto → texto
public interface ITextInjector      // escribir en la ventana con foco
public interface IForegroundWindow  // qué proceso tiene el foco
public interface IOverlayHost       // mostrar/ocultar el personaje, cambiar estado
public interface INoteRepository    // guardar, editar, listar, buscar
public interface IPostProcessor     // limpieza opcional con LLM
```

`Otto.Core` orquesta el pipeline hablando **solo** con estas interfaces. Se puede
testear el flujo entero sin micrófono, sin GPU y sin ventana en foco.

**Ojo con el orden de dependencias:** el [hito 0.5](../hito-0-5-resultados.md)
mostró que el `initial_prompt` tiene que elegirse según la aplicación en foco,
porque un prompt técnico mejora mucho el dictado con jerga y empeora un poco el
narrativo. O sea que `IForegroundWindow` alimenta a `ITranscriber` **antes** de
inferir, no solo a `IPostProcessor` después. Es la diferencia entre un pipeline
lineal y uno donde el contexto se resuelve primero.

---

## 4. Persistencia: componente nuevo

La sección de transcripciones editables con títulos no estaba en el brief y agrega
una pieza de stack que antes no existía.

**Elección: SQLite** vía `Microsoft.Data.Sqlite`.

- Un archivo, cero configuración, cero servidor. Encaja con la promesa de que todo
  vive en tu máquina.
- **FTS5** (búsqueda de texto completo, viene incluida) hace que buscar entre miles
  de notas sea instantáneo. Un JSON plano no escala y obliga a cargar todo en
  memoria para filtrar.
- Las escrituras son transaccionales: si la app se cierra mal, no perdés el
  historial ni te queda un archivo corrupto a medio escribir.

Se descartó JSON plano por lo anterior, y un ORM completo (EF Core) por peso: el
esquema son dos tablas. Migraciones como scripts SQL numerados, corridos al
arrancar. Si el esquema creciera bastante, EF Core es el reemplazo natural.

**Regla de oro de latencia:** guardar en la base **nunca** puede estar en el camino
crítico entre soltar la tecla y ver el texto. El orden es: inyectar el texto
primero, persistir después, en segundo plano. Si la escritura falla, se loguea y
se avisa — pero el dictado ya funcionó.

Ubicación: `%LOCALAPPDATA%\Otto\otto.db`.

---

## 5. Correcciones al brief

Estas son correcciones, no preferencias. Cada una cambia código que el brief
especificaría mal.

### 5.1 — .NET 10, no .NET 8

El soporte LTS de .NET 8 se cierra en noviembre de 2026. Arrancar un proyecto
nuevo sobre un runtime al que le queda alrededor de un año significa una migración
forzada antes de que el proyecto madure. .NET 10 es el LTS actual y los SDKs
10.0.300 y 10.0.301 ya están instalados. No hay ningún beneficio que compense
quedarse en 8.

### 5.2 — `RegisterHotKey` solo no puede implementar push-to-talk

**Esta es la corrección más importante.** El diagrama de arquitectura del brief
especifica `RegisterHotKey` con `KeyDown → start / KeyUp → stop`. `RegisterHotKey`
postea `WM_HOTKEY` cuando la combinación se **presiona** y **no manda ningún
mensaje al soltarla**. Tal como está escrito, el servicio puede arrancar una
grabación pero no puede terminarla nunca.

Dos implementaciones viables, las dos detrás de `IHotkeyService`:

- **`RegisterHotKey` + sondeo.** Se arma con `RegisterHotKey`; al recibir
  `WM_HOTKEY` se arranca la captura y se sondea `GetAsyncKeyState` cada ~15 ms
  hasta que la tecla se suelta. No instala ningún hook global, así que es el
  perfil más bajo ante antivirus. Limitación: la combinación tiene que incluir una
  tecla que no sea modificadora — no podés atar "mantener Ctrl derecho".
- **`SetWindowsHookEx(WH_KEYBOARD_LL)`.** Eventos reales de presión y liberación,
  y soporta combinaciones de solo modificadores, que es ergonómicamente mejor para
  push-to-talk. Costo: es un hook de teclado global, o sea estructuralmente
  idéntico a un keylogger, y se va a ganar la atención de antivirus y SmartScreen
  que el brief ya anticipa en §8.

**Decisión:** el sondeo va como implementación por defecto y el hook de bajo nivel
como opción activable, documentando el intercambio en el README. Además se ofrece
un **modo toggle** (una pulsación arranca, otra corta), que funciona solo con
`RegisterHotKey` y que para dictados largos es mejor experiencia igual.

### 5.3 — El objetivo de latencia y el timeout de Ollama se contradicen

El brief fija un objetivo de punta a punta de **< 1,5 s** (§7) y un timeout de
Ollama de **~2 s** (§5). El post-procesamiento no puede entrar en un presupuesto
que por sí solo tiene permitido exceder.

Resolución — dos SLOs separados, que se reportan por separado:

| Camino | Presupuesto |
|---|---|
| Captura → transcripción → inyección (crudo) | **< 1,5 s** |
| …con post-procesamiento de Ollama | **< 3,5 s** |

El post-procesamiento es opt-in por contexto (según el mapeo proceso→formato que
describe el brief), nunca un default global, y el camino crudo es el que se
publica en la tabla de benchmarks del README. El timeout sigue disparando a los
2 s y cayendo a la salida cruda de Whisper.

### 5.4 — NativeAOT no está disponible; elegir el runtime de GPU a conciencia

El brief justifica .NET en parte con "permite AOT". Avalonia tiene buena historia
de AOT, pero Whisper.net necesita que le fijes a mano `RuntimeOptions.LibraryPath`
bajo despliegue single-file o AOT, porque su búsqueda automática de la librería
nativa se rompe. Sumado a que las librerías nativas de `whisper.cpp` van como
archivos sueltos igual, **el single-file real no existe acá**. El objetivo
alcanzable es **self-contained + ReadyToRun**: una carpeta que se comprime en ZIP.
Autocontenido sí, AOT no.

**Runtime de GPU: Vulkan. Decidido midiendo, y no por velocidad.**

CUDA **no carga** en la máquina de desarrollo, con una RTX 4060 y driver al día.
Las DLL nativas están presentes en la salida del build, pero `ggml-cuda-whisper.dll`
depende de `cudart64` y `cublas64`, que vienen con el **CUDA Toolkit** — no con el
driver. El driver de NVIDIA solo aporta `nvcuda.dll`.

O sea que enviar CUDA obligaría a cada usuario a instalar un toolkit de varios
gigas, o a empaquetar los redistribuibles de cuBLAS dentro del ZIP portable.
Vulkan solo necesita `vulkan-1.dll`, que ya viene con cualquier driver de GPU, y
encima cubre AMD e Intel.

**Vulkan no es el runtime más chico: es el único realmente distribuible.**

Paquetes a incluir:

1. **`Whisper.net.Runtime.Vulkan`** — el camino de GPU.
2. **`Whisper.net.Runtime`** (CPU) — el piso, siempre incluido.

CUDA queda fuera del artefacto. Si alguna vez aparece una razón de rendimiento que
lo justifique, va como descarga opcional aparte y documentando la dependencia del
Toolkit.

**El fallback de CPU no puede ser `small`.** Medido: `large-v3-turbo` por CPU tarda
17 segundos para una frase de 5, y `small` 3,5 s. El fallback sin GPU es **`base`**.
La diferencia entre GPU y CPU es de 17× a 37×, así que la detección de hardware del
primer arranque no es un detalle de pulido — decide si la herramienta se puede usar.

### 5.5 — Inyección por portapapeles: hay que defenderse de los gestores de portapapeles

El brief pone bien como criterio de aceptación guardar y restaurar el portapapeles,
pero restaurar no alcanza. El Historial del Portapapeles de Windows, el
portapapeles en la nube y los gestores de terceros observan el portapapeles **en
el momento en que cambia** — van a capturar cada frase dictada, que para una
herramienta que se posiciona como offline y privada es exactamente el resultado
opuesto al buscado.

Antes de setear el portapapeles, hay que registrar y adjuntar los formatos de
exclusión (`ExcludeClipboardContentFromMonitorProcessing`,
`CanIncludeInClipboardHistory`, `CanUploadToCloudClipboard`). Son advertencias: los
gestores que se portan bien las respetan, otros no — así que también hay que
documentar la limitación residual. Verificar el comportamiento contra Win+V
durante el hito 1.

Aparte: guardar y restaurar es "lo mejor posible" para formatos que no son texto.
Un portapapeles con una imagen o contenido enriquecido no siempre se puede
devolver fiel. Restaurar texto de forma exacta y documentar el resto.

### 5.6 — El personaje animado es una ventana con requisitos raros

Técnicamente el personaje es una ventana sin decoración, con fondo transparente,
siempre encima, que no aparece en la barra de tareas ni en Alt+Tab, que nunca roba
el foco, y con los clics atravesándola.

- **Animación:** Lottie (vía `Avalonia.Labs.Lottie`, que usa Skottie sobre Skia).
  Es vectorial, los archivos pesan poco, hay assets disponibles y se puede cambiar
  de animación por estado sin recompilar. Una hoja de sprites es la alternativa
  simple si Lottie da problemas.
- **Click-through:** Avalonia no tiene una propiedad para esto. Es P/Invoke:
  `SetWindowLong` con `WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW`.
  `WS_EX_TOOLWINDOW` es lo que además lo saca de Alt+Tab. Por eso existe el puerto
  `IOverlayHost` en vez de resolverlo dentro de la ventana.
- **Cuidado con el foco:** la ventana del personaje **no puede** activarse nunca.
  Si roba el foco justo antes de inyectar texto, el texto termina en Otto en vez de
  en la app del usuario. Es el bug más probable de toda esta feature.

### 5.7 — Whisper alucina con el silencio, y push-to-talk está lleno de silencio

Modo de falla documentado y conocido de Whisper: alimentado con silencio, **no
devuelve vacío — inventa texto**. En español es célebre por emitir cosas como
"Subtítulos por la comunidad de Amara.org" o "¡Gracias por ver el video!", que son
residuos de los subtítulos de YouTube con los que se entrenó.

Para transcribir un podcast es una molestia. **Para dictado con push-to-talk es
letal**, porque el flujo está lleno de silencio: apretás la tecla, dudás medio
segundo, hablás, hacés una pausa para pensar, soltás. Cada uno de esos huecos es
una oportunidad de que Otto escriba una frase fantasma en el documento del
usuario.

**Confirmado midiendo:** 4 segundos de ruido de sala, sin VAD, dieron `[Música]`.
Texto que se le habría escrito al usuario en el medio del documento.

**Mitigación: el VAD de Silero — pero usado como compuerta, no como partidor.**

La implementación obvia es un error caro, y está medido. Detectar cada región de
voz y transcribir cada una por separado parece razonable, pero `whisper.cpp`
rellena **cada llamada** hasta una ventana fija de 30 segundos. N regiones cuestan
N inferencias completas:

| | Mediana | Peor caso | Factor tiempo real |
|---|---:|---:|---:|
| VAD como partidor | 0,94 s | 7,18 s | 0,230 |
| VAD como compuerta | **0,47 s** | **0,68 s** | **0,056** |

Diez veces peor. Y encima **transcribe peor**, porque partir el audio le saca a
Whisper el contexto que usa para desambiguar: los nombres propios pasaron de 100 %
de error a 62 % con solo dejar de partir.

El diseño correcto:

1. Preguntarle al VAD una sola cosa: **¿hay voz?** Si no hay, devolver vacío sin
   invocar el modelo. Eso es lo que frena la alucinación, y cuesta 0,01 s.
2. Si hay voz, recortar al tramo entre el inicio de la primera región y el fin de
   la última, y correr **una sola** inferencia sobre todo eso.
3. Cargar el procesador de VAD **una vez**, nunca por dictado.

**Casos de prueba de regresión** (ya cubiertos en `Otto.Bench`):

- Apretar la tecla, no decir nada, soltar. **Salida esperada: vacío.**
- Apretar, esperar dos segundos, después hablar, soltar.
- Hablar, hacer una pausa larga en el medio, seguir hablando.

### 5.8 — El hito 0 tiene que medir precisión, no solo latencia

El brief trata el primer hito como una compuerta de latencia. El criterio de
aceptación más difícil es "precisión aceptable en español rioplatense con términos
técnicos en inglés intercalados" — y es el que tiene más chances de fallar.
Ampliar el spike para grabar un conjunto fijo de ~10 frases representativas (jerga
real: nombres de frameworks, de repos, `kubectl`, `pnpm`, frases con
code-switching) y correr todos los modelos candidatos contra el mismo audio.

Salida: la tabla de latencia por modelo y por hardware que el brief quiere para el
README, con una columna de precisión al lado. Esa tabla es un activo de portfolio
por sí misma — la evidencia medida es lo que separa este repo de cualquier otro
wrapper de Whisper.

---

## 6. Por qué Linux no está en v1

Esta sección existe porque "solo Windows" tiene que ser una decisión defendible,
no un encogimiento de hombros. Y lo es: **Linux no se descartó por esfuerzo, se
descartó porque el modelo de seguridad de Wayland vuelve imposibles tres de las
cinco primitivas.**

Wayland bloquea a propósito casi todo lo que Otto necesita. No es un bug ni algo
que falte implementar. Un cliente de Wayland no puede espiar el teclado global, no
puede inyectar eventos en otro cliente y no puede saber qué aplicación tiene el
foco.

| Primitiva | X11 | Wayland |
|---|---|---|
| Hotkey global | `XGrabKey` — funciona | Solo vía el portal `org.freedesktop.portal.GlobalShortcuts`. Soporte desigual según entorno de escritorio |
| Inyección de teclas | `XTEST` — funciona | Bloqueado. Requiere `ydotool` con acceso a `/dev/uinput` (regla de udev), o el portal RemoteDesktop con permiso por sesión |
| Portapapeles en segundo plano | Selecciones X11 — funciona | Depende del protocolo de data-control que exponga el compositor |
| Ventana/proceso en foco | `_NET_ACTIVE_WINDOW` + `_NET_WM_PID` — funciona | **No disponible.** Ningún protocolo se lo expone a otros clientes |
| Overlay click-through | Extensión Shape — funciona | `wlr-layer-shell`, que existe solo en compositores wlroots (Sway, Hyprland). No en GNOME ni KDE bajo Wayland |

Un Otto en Linux hoy funcionaría completo bajo X11 y **degradado** bajo Wayland:
sin detección de contexto, sin personaje, y con inyección de teclas que exige que
el usuario configure permisos de `/dev/uinput` a mano. Y Wayland es cada vez más
el default en las distribuciones, así que esa sería la experiencia de la mayoría.

Publicar eso como "soporte de Linux" sería peor que no soportarlo. Una herramienta
de dictado que a veces no escribe donde tiene que escribir no es una herramienta
de dictado.

**Trampa adicional:** Flatpak sandboxea justamente el acceso a `/dev/uinput` y a la
entrada global. Si Otto se distribuyera como Flatpak, la inyección no andaría en
absoluto.

**Decisión:** Windows en v1. Si en algún momento se retoma Linux, se implementa
`Otto.Platform.Linux` contra X11 y se declara Wayland como no soportado, con esta
misma tabla en el README. La arquitectura ya está lista para eso; lo que no está
listo es la plataforma.

---

## 7. Consecuencias

**A favor**

- Una sola plataforma que probar, empaquetar y soportar. Se llega mucho antes a
  algo que se pueda usar todos los días.
- El fallback GPU→CPU no requiere código propio.
- La separación de puertos y adaptadores hace que `Otto.Core`, `Otto.Speech`,
  `Otto.Storage` y `Otto.PostProcessing` sean testeables sin hardware, lo que
  cumple el objetivo de tests del brief sin inventar ceremonia.
- Avalonia + Skia son mejores que WPF justo para las dos features más visibles del
  producto: el personaje animado y una UI con identidad.
- El VAD recorta el silencio del principio y del final antes de inferir: latencia
  gratis en absolutamente todos los dictados.
- Linux queda como una puerta entornada que no costó nada dejar así.

**En contra, aceptadas**

- Sin NativeAOT. El artefacto es una carpeta, no un archivo único.
- `whisper.cpp` deja algo de velocidad de inferencia sobre la mesa contra
  CTranslate2. El presupuesto de latencia lo absorbe.
- El modo de hotkey con hook de bajo nivel va a ser marcado por algunos antivirus.
  Se declara en el README, y el modo por defecto lo evita.
- Avalonia tiene menos ejemplos en internet que WPF, y no hay diseñador visual.
- Un usuario de Linux que encuentre el repo se va a ir con las manos vacías. Se
  mitiga documentando el porqué (§6) en vez de callarlo.

---

## 8. Plan de hitos

| Hito | Entregable | Criterio de corte |
|---|---|---|
| ~~0~~ | ~~Spike de latencia y precisión~~ | ✅ **Superado.** [Resultados](../hito-0-resultados.md): 0,53 s de mediana con `large-v3-turbo` en Vulkan |
| ~~0.5~~ | ~~Precisión de vocabulario~~ | ✅ **Superado.** [Resultados](../hito-0-5-resultados.md): `08-nombres` de 67 % a 17 %, sin costo de latencia |
| 1 | Pipeline mínimo | Hotkey → grabar → transcribir → escribir en Notepad |
| 2 | Persistencia y notas | SQLite + la sección de transcripciones editables con títulos y botón de copiar |
| 3 | Shell de la aplicación | Ventana principal en Avalonia, settings persistidos, bandeja, minimizar en vez de cerrar, arranque con el sistema |
| 4 | Post-procesamiento | Ollama + detección de contexto + diccionario personalizado + **regla de voseo** |
| 5 | Personaje animado | Overlay Lottie, click-through, estados visuales, sin robo de foco |
| 6 | Distribución | Asistente de primer arranque, detección de hardware, descarga reanudable, ZIP portable, GIF de demo, CI. Criterio de corte: la checklist de [Distribución y primer arranque](../distribucion-y-primer-arranque.md) |

El hito 6 **no** es "comprimir una carpeta". Ver el documento de distribución: hay
siete trampas concretas entre "compila en mi máquina" y "un desconocido lo usa",
entre ellas que Whisper.net depende del Visual C++ Redistributable.

---

## 9. Pendientes

Del hito 0 — cerrados:

- [x] CUDA vs Vulkan vs CPU. **CUDA no carga sin el Toolkit; Vulkan sí.** Ver §5.4.
- [x] Medir `large-v3-turbo`, `medium` y `small` sobre el mismo audio.
- [x] Grabar el set de frases rioplatenses con code-switching técnico.
- [x] Verificar la alucinación con silencio y su mitigación.

Abiertos:

- [ ] Confirmar tres referencias donde la evidencia sugiere que el guion está mal
      y el modelo bien ([resultados, cabos sueltos](../hito-0-resultados.md)).
- [ ] Probar las dos implementaciones de hotkey contra un push-to-talk real.
- [ ] Verificar que `otto-dictation` esté libre en GitHub (brief, §Nombre).
- [ ] Instalar Ollama — no está en la máquina — antes del hito 4.
- [ ] Elegir licencia.
