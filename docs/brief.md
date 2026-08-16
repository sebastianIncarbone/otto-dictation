# Otto — Brief técnico

**Otto** · *Offline Transcription, Totally Open*
Herramienta de dictado por voz local para Windows.

> Documento de arranque para pasarle a Claude Code. Contiene contexto, decisiones tomadas, arquitectura objetivo y criterios de aceptación. No es un tutorial: asume que quien lo lee escribe el código.

---

## Nombre e identidad

| | |
|---|---|
| Nombre de producto | **Otto** |
| Expansión | Offline Transcription, Totally Open |
| Ejecutable | `otto.exe` |
| Repositorio sugerido | `otto-dictation` |
| Config | `%APPDATA%\Otto\config.json` |
| Modelos | `%LOCALAPPDATA%\Otto\models\` |

La expansión no es decorativa: nombra las dos propiedades que diferencian el proyecto de las alternativas comerciales. Todo el messaging del README debería apoyarse en esas dos palabras — *offline* y *open* — antes que en precisión o velocidad, que son terreno donde cualquier wrapper de Whisper dice lo mismo.

**Verificar antes de crear el repo:** que `otto-dictation` esté libre en GitHub y que no haya un proyecto activo con ese nombre en el espacio de STT. Existe cercanía fonética con Otter.ai (producto grande de transcripción), así que el nombre corto **Otto** se usa en la UI y el tray, pero el repo lleva el sufijo para ser buscable y no leerse como derivativo.

Nota de tono: Otto se pronuncia igual en español y en inglés, es palíndromo y se escribe sin deletrear. Vale mantener esa simplicidad en la marca — sin estilizaciones raras de mayúsculas ni caracteres especiales.

---

## 1. Contexto y objetivo

Construir una aplicación de escritorio para Windows que permita dictar por voz en **cualquier** aplicación del sistema (VS Code, navegador, Slack, Word, terminal) mediante un atajo global de teclado, con transcripción **100% local** — sin nube, sin API keys, sin costo por uso.

Motivación: el dictado integrado de Windows (Win+H) tiene precisión insuficiente, especialmente en español con terminología técnica y code-switching español/inglés. Las alternativas comerciales (Wispr Flow ~USD 15/mes) resuelven el problema pero con suscripción y enviando audio a servidores de terceros.

Objetivo secundario: que el repositorio funcione como pieza de portfolio. Esto implica que el código, el README y la experiencia de instalación importan tanto como la funcionalidad.

## 2. Alcance de la v1

**Dentro:**

- Captura de audio por push-to-talk con hotkey global configurable.
- Transcripción local con modelo Whisper.
- Inserción del texto transcripto en la ventana activa.
- Post-procesamiento opcional del texto con un LLM local vía Ollama.
- Icono en bandeja del sistema con estado visible (idle / grabando / transcribiendo).
- Ventana de configuración: hotkey, modelo, idioma, micrófono, modos de post-procesamiento.

**Fuera (explícitamente no-objetivos de la v1):**

- Comandos de voz para controlar la app ("borrar eso", "nuevo párrafo").
- Wake word / escucha continua.
- Transcripción de archivos de audio pre-grabados.
- Multiplataforma (macOS / Linux).
- Instalador MSI firmado. Un ZIP portable con ejecutable alcanza.

## 3. Stack técnico

> **Decisión pendiente de confirmar:** este brief asume el camino .NET. Si se prefiere prototipar en Python primero, se reemplaza la sección de UI/hotkey por `pynput` + `sounddevice` y `Whisper.net` por `faster-whisper`; el resto de la arquitectura y los criterios de aceptación se mantienen igual.

| Componente | Elección | Racional |
|---|---|---|
| Runtime | .NET 8 | Stack propio; permite AOT y binario autocontenido |
| UI | WPF con `Hardcodet.NotifyIcon.Wpf` | Tray app liviana; WinUI 3 agrega fricción de empaquetado sin beneficio acá |
| STT | `Whisper.net` (binding de whisper.cpp) | NuGet con runtimes CPU / CUDA / Vulkan; sin dependencia de Python |
| Modelo | `ggml-large-v3-turbo` | Mejor relación velocidad/precisión y multilingüe real. **Evitar los `distil-*`: son solo inglés.** Permitir fallback a `small` / `medium` en máquinas sin GPU |
| Captura de audio | `NAudio` (`WasapiCapture`) | Acceso a WASAPI, resampleo a 16 kHz mono |
| Hotkey global | `RegisterHotKey` / `UnregisterHotKey` vía P/Invoke | API nativa de Win32; evita dependencias de hooks de bajo nivel |
| Inserción de texto | `SendInput` con fallback a portapapeles + Ctrl+V | Ver sección 5 |
| LLM opcional | Ollama vía HTTP (`http://localhost:11434`) | Detección en runtime; la app debe funcionar sin Ollama instalado |
| Config | JSON en `%APPDATA%` | Sin registro de Windows |

## 4. Arquitectura

```
┌─────────────────────────────────────────────────────────┐
│  HotkeyService (P/Invoke: RegisterHotKey)               │
│      ↓ KeyDown → start / KeyUp → stop                   │
├─────────────────────────────────────────────────────────┤
│  AudioCaptureService (NAudio / WASAPI)                  │
│      → PCM 16 kHz mono float32, buffer en memoria       │
├─────────────────────────────────────────────────────────┤
│  TranscriptionService (Whisper.net)                     │
│      → modelo cargado UNA vez al inicio, residente      │
├─────────────────────────────────────────────────────────┤
│  PostProcessingService (Ollama, opcional)               │
│      → limpieza, puntuación, formato según contexto     │
├─────────────────────────────────────────────────────────┤
│  TextInjectionService (SendInput / clipboard)           │
│      → escribe en la ventana con foco                   │
└─────────────────────────────────────────────────────────┘
         ↕
  TrayIconViewModel (estado) + SettingsWindow (config)
```

Separar cada servicio detrás de una interfaz e inyectarlos con `Microsoft.Extensions.DependencyInjection`. Esto no es sobre-ingeniería: permite mockear el pipeline en tests y hace que el código sea legible para quien revise el repo.

## 5. Detalles de implementación críticos

**Carga del modelo.** El modelo se carga al arrancar la app y queda residente en memoria. Cargarlo por request agrega segundos de latencia y mata la usabilidad. Mostrar estado "cargando modelo" en el tray hasta que esté listo.

**Latencia percibida.** El objetivo es que el texto aparezca en menos de ~1,5 s desde que se suelta la tecla, para dictados de una o dos frases. Si no se llega, la herramienta no se usa. Instrumentar y loguear el tiempo de cada etapa (captura → transcripción → post-proceso → inserción) desde el primer día.

**Descarga del modelo.** No versionar el `.bin` en Git (large-v3-turbo pesa ~1,5 GB). Descargarlo en el primer arranque desde Hugging Face con barra de progreso, y cachearlo en `%LOCALAPPDATA%`.

**Inserción de texto.** `SendInput` es lo más compatible pero es lento para textos largos y algunas apps (terminales, Electron) lo manejan mal. Estrategia: usar portapapeles + Ctrl+V como default por velocidad, preservando y restaurando el contenido previo del portapapeles; dejar `SendInput` como opción configurable para apps problemáticas.

**Detección de contexto para el post-procesamiento.** Obtener el proceso de la ventana en foco con `GetForegroundWindow` + `GetWindowThreadProcessId`. Mapear proceso → modo de formato en la config (ej. `Code.exe` → comentario de código; `chrome.exe` → texto plano). Esto es lo que diferencia la herramienta de un demo.

**Ollama es opcional.** Si no responde el health check al arranque, la app deshabilita el post-procesamiento y sigue funcionando con la salida cruda de Whisper. Nunca debe bloquear el flujo principal. Timeout agresivo (~2 s) — si el LLM tarda, se devuelve el texto sin procesar.

**Diccionario personalizado.** Lista de términos en la config (nombres de frameworks, repos, jerga propia) que se inyecta en el prompt del post-procesador para corregir transcripciones. Alternativa complementaria: el parámetro `initial_prompt` de Whisper.

## 6. Plan por hitos

| Hito | Entregable | Criterio de corte |
|---|---|---|
| 1 | Spike de latencia | Consola que graba 5 s, transcribe con Whisper.net y mide tiempos en la máquina objetivo. **Si la latencia es inaceptable, revisar modelo/hardware antes de seguir.** |
| 2 | Pipeline mínimo end-to-end | Hotkey → grabar → transcribir → escribir en Notepad |
| 3 | Tray app | Icono, estados visuales, arranque con Windows, salida limpia |
| 4 | Configuración | Ventana de settings persistida en JSON |
| 5 | Post-procesamiento | Integración con Ollama + detección de contexto + diccionario |
| 6 | Empaquetado y README | ZIP portable, GIF de demo, instrucciones de instalación |

El hito 1 es un gate real: la viabilidad del proyecto depende de que el hardware disponible dé latencias usables.

## 7. Criterios de aceptación

- El texto dictado aparece correctamente en VS Code, Chrome, Slack y Notepad.
- Latencia < 1,5 s para dictados de hasta 15 segundos de audio (con GPU).
- Precisión aceptable en español rioplatense con términos técnicos en inglés intercalados.
- La app funciona sin conexión a internet después de la descarga inicial del modelo.
- La app funciona sin Ollama instalado, degradando funcionalidad sin romperse.
- Consumo en reposo razonable y sin fugas de memoria tras varias horas de uso.
- El portapapeles del usuario queda intacto después de cada inserción.

## 8. Riesgos conocidos

- **Latencia en CPU sin GPU.** Mitigación: permitir modelos más chicos y documentar los tiempos esperados por hardware en el README.
- **Antivirus / SmartScreen.** Una app sin firmar que registra hotkeys globales e inyecta teclas puede ser marcada como sospechosa. Documentarlo en el README.
- **Apps que rechazan la inserción.** Terminales elevadas y algunas apps con protección de entrada. Documentar limitaciones conocidas.
- **Calidad del micrófono.** Es la variable que más afecta la precisión, por encima del tamaño del modelo. Vale la pena una nota en el README.

## 9. Notas de portfolio

El repo tiene que poder evaluarse en dos minutos. Prioridades:

- Título del README: **Otto** con la expansión como subtítulo. El posicionamiento arranca por *offline* y *open*, no por precisión.
- README con GIF de demo arriba de todo, mostrando el dictado en acción en VS Code.
- Sección de arquitectura con el diagrama de componentes.
- Tabla de benchmarks de latencia por modelo y por hardware — evidencia medida, no afirmaciones.
- Tests unitarios sobre los servicios mockeables (post-procesamiento, config, mapeo de contexto).
- CI en GitHub Actions que compile y publique el ZIP como release artifact.
