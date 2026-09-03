<p align="center">
  <img src="docs/banner.png" alt="Otto — Grumpy Imp" width="100%">
</p>

<p align="center">
  <b>Offline Transcription, Totally Open</b><br>
  Mantenés una tecla, hablás, la soltás, y el texto aparece donde estabas escribiendo — en cualquier programa.
</p>

<p align="center">
  <a href="https://github.com/sebastianIncarbone/otto-dictation/actions/workflows/build.yml"><img src="https://github.com/sebastianIncarbone/otto-dictation/actions/workflows/build.yml/badge.svg" alt="build"></a>
  <a href="https://github.com/sebastianIncarbone/otto-dictation/releases/latest"><img src="https://img.shields.io/github/v/release/sebastianIncarbone/otto-dictation" alt="release"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue" alt="MIT"></a>
  <img src="https://img.shields.io/badge/plataforma-Windows%2010%2B-lightgrey" alt="Windows 10+">
</p>

<p align="center">
  <a href="README.md">🇬🇧 Read in English</a>
</p>

---

La transcripción es 100% local: tu voz no sale de tu computadora.

> **La prueba: desenchufá internet y Otto sigue funcionando.**

> **Sobre este archivo.** Es una traducción de cortesía del
> [README en inglés](README.md), que es la fuente de verdad. Si alguna vez dicen
> cosas distintas, el que vale es el otro. La documentación técnica en `docs/` y las
> issues también están en inglés; la interfaz de Otto sí está en español, y es a
> propósito.

## Por qué existe

El dictado de Windows tiene precisión insuficiente en español, sobre todo con
terminología técnica y frases que mezclan español e inglés. Las alternativas
comerciales lo resuelven, pero con suscripción y mandando tu audio a servidores de
terceros.

Otto no manda nada a ningún lado. Las únicas conexiones de red en toda su vida son las
descargas de los modelos con los que funciona: el de voz la primera vez, el de corrección
al lado si la máquina tiene GPU, y una voz de lectura si prendés la lectura y apretás el
botón que la baja. Después de eso, nunca más, salvo que vos mismo busques actualizaciones
— y ahí solo baja lo que le pediste, si además le decís que instale. Todas y cada una las
arrancaste vos.

## Qué hace

- **Dicta en cualquier programa.** Editor, navegador, chat, terminal. Donde tengas
  el cursor.
- **Corrige al rioplatense.** Escribe `Instalá` y `corré`, no `Instala` y `corre`.
  Opcional, con un modelo local.
- **Te lee lo que tengas seleccionado.** Seleccionás texto en cualquier lado, apretás
  una tecla y lo escuchás en voz argentina. Opcional, y lo único opcional que no
  necesita placa de video.
- **Guarda todo.** Cada dictado queda como nota editable, con título, búsqueda y
  copiado de un clic. Dictar y guardar son la misma acción.
- **Vive en la bandeja**, con un personaje animado que muestra qué está haciendo.

<p align="center">
  <img src="docs/poses.png" alt="Los estados de Otto" width="100%">
</p>

## Rendimiento medido

En una RTX 4060 Laptop, con `large-v3-turbo` sobre Vulkan:

| | |
|---|---:|
| Transcripción | 0,57 s |
| Corrección al rioplatense | 0,33 s |
| Inserción del texto | 0,13 s |
| **Total, de soltar la tecla a ver el texto** | **1,02 s** |

**La latencia no depende de cuánto dictes.** `whisper.cpp` procesa en una ventana
fija de 30 segundos, así que una frase y un párrafo cuestan lo mismo:

| Audio dictado | Transcripción |
|---:|---:|
| 4 s | 0,45 s |
| 12 s | 0,53 s |
| 30 s | 0,56 s |

Sin GPU la historia es otra — entre 17 y 37 veces más lento — por eso Otto detecta
el hardware al arrancar y baja un modelo más chico si hace falta.

Todos los números y cómo se obtuvieron: [hito 0](docs/hito-0-resultados.md),
[hito 0.5](docs/hito-0-5-resultados.md), [hito 4](docs/hito-4-resultados.md).

## Instalación

Bajá **`Otto-Setup.exe`** de [la última release](https://github.com/sebastianIncarbone/otto-dictation/releases/latest)
(~50 MB) y ejecutalo.

No pide administrador: Otto se instala para tu usuario, en
`%LOCALAPPDATA%\Programs\Otto`. El instalador te deja elegir si querés el acceso
directo en el menú Inicio y en el escritorio, y deja la entrada correspondiente en
*Agregar o quitar programas*. **No hace falta instalar .NET ni nada más.**

La primera vez baja el modelo de voz (~1,6 GB con GPU, ~150 MB sin ella) y —en una máquina
con GPU, porque la corrección al rioplatense viene activada por defecto— también el modelo de
corrección (~2 GB), o sea **~3,6 GB en total**. Las dos descargas son reanudables y muestran
progreso. Una máquina sin GPU no baja el modelo de corrección: un modelo de 3B no puede
responder dentro del presupuesto de dictado en CPU, así que Otto ni lo ofrece. Después de
bajar los modelos, Otto abre la ventana una vez y de ahí en más arranca directo en la bandeja.

La voz de lectura **no** entra en esa cuenta: la lectura en voz alta viene apagada, y su voz
de ~110 MB la baja un botón en la configuración cuando la prendés. Que tildar un casillero
dispare una transferencia que no pediste es justo lo que Otto evita.

<details>
<summary><b>¿Preferís no instalar nada?</b></summary>

`Otto-windows-x64.zip` (~130 MB) es la misma aplicación en versión portable:
descomprimila donde quieras y ejecutá `Otto.App.exe`. Sirve para un pendrive o para
una máquina donde no podés instalar programas.

La diferencia está en cómo se saca: la versión instalada se desinstala desde
*Agregar o quitar programas*; la portable la limpia Otto desde su propia
configuración, y la carpeta la borrás vos.
</details>

Al desinstalar, **tus notas y el modelo descargado no se borran salvo que digas que
sí.** Reinstalar te devuelve todo tal cual, sin volver a bajar 1,6 GB.

> **Windows te va a mostrar una advertencia azul de SmartScreen.** Otto no está
> firmado con un certificado de código, que cuesta varios cientos de dólares por
> año. Hacé clic en **Más información** → **Ejecutar de todas formas**.
>
> Es razonable que desconfíes. Por eso el código es público, todo lo que hace se
> puede leer, y **cada release publica su análisis de VirusTotal junto al SHA-256
> del archivo** — así podés comprobar que el análisis corresponde exactamente al
> binario que bajaste, y no a otro. Están en las notas de cada release.
>
> Algunos motores lo van a marcar, y no es un error: Otto registra un atajo global
> y sintetiza pulsaciones de teclado, que es funcionalmente la descripción de un
> keylogger. Preferimos publicar el análisis con esas marcas a la vista antes que
> pedirte que confíes.

## Uso

Mantené **Ctrl+Alt+Espacio**, hablá, soltá. El texto aparece donde estaba el cursor.

Seleccioná texto en cualquier lado y apretá **Ctrl+Alt+L** para que te lo lea; apretalo
de nuevo para cortar. Las dos combinaciones se pueden cambiar desde la configuración.

Abrí la ventana desde la bandeja para ver tus notas, buscarlas, editarlas o
cambiar la configuración. Al personaje lo podés arrastrar a donde quieras y se queda
donde lo dejaste.

## Corrección al rioplatense (opcional)

Whisper neutraliza el voseo: donde decís *"instalá"* escribe *"instala"*. Otto lo corrige
solo, en el mismo proceso — sin ningún servicio aparte que instalar ni mantener corriendo. En
la primera ejecución, si tu máquina tiene GPU, Otto baja un modelo local chico
(Qwen2.5-3B-Instruct, ~2 GB) junto con el de voz, y corrige cada dictado con él.

**La corrección necesita GPU.** Un modelo de 3B no llega a responder dentro del presupuesto de
~2 s por dictado en CPU, así que en una máquina sin GPU el modelo nunca se descarga, nunca se
carga, y la opción no hace nada — Otto simplemente usa la salida cruda de Whisper. También
podés apagar la corrección vos mismo, desde la configuración o desde la bandeja, y ahí mismo
se descarga el modelo y te devuelve la VRAM.

**Y se corre solo cuando no lo usás.** Tener un modelo de 3B residente cuesta unos 2 GB de
VRAM, que es bastante para estar ocupando mientras no dictás — así que a los 15 minutos sin
uso Otto lo descarga. El dictado siguiente sale sin corregir y dispara la recarga en segundo
plano; el que viene después ya sale corregido. Ese intercambio es configurable desde la
configuración, incluso apagarlo para que el modelo se quede cargado.

Una corrección que sale mal nunca es peor que no corregir: `EditGuard` descarta cualquier
resultado que reescriba demasiado la frase, y va la transcripción cruda en su lugar — ver
[ADR 0002](docs/adr/0002-in-process-correction-llamasharp.md) para cómo se mide eso.

El [hito 4](docs/hito-4-resultados.md) cuenta por qué esto no puede ser una tabla de
reemplazos y por qué el prompt importó más que el modelo — medido cuando la corrección corría
sobre Ollama; el mecanismo se movió al mismo proceso desde entonces
([ADR 0002](docs/adr/0002-in-process-correction-llamasharp.md)), el razonamiento no cambió.

## Lectura en voz alta (opcional)

Seleccionás texto en cualquier aplicación, apretás **Ctrl+Alt+L** y Otto te lo lee.
Apretalo de nuevo y corta. Existe por accesibilidad antes que nada: alguien que no puede
leer cómodo la pantalla la escucha, en su propio acento, sin que el texto salga de la
máquina.

**Es lo único opcional que no se cae sin GPU**, y esa es la razón entera del motor que
usa. Otto usa Piper, medido en **×4,6** más rápido que el tiempo real en la misma máquina
donde la alternativa que suena mejor, Qwen3-TTS, llega a **×0,69** — más lento que el
habla, así que se atrasa más cuanto más lee. Un lector que no llega no es una versión
premium, es una rota. Las mediciones están en el
[ADR 0003](docs/adr/0003-read-aloud-piper.md).

El motor viaja adentro del paquete. **La voz no**, y bajarla es un botón en la
configuración y no algo que hace tildar un casillero: `es_AR/daniela` pesa ~110 MB, y la
única regla que Otto tiene sobre la red es que decide la persona, no la aplicación. Hay
cinco voces más en español —mexicanas y peninsulares— en la misma lista.

Mientras está leyendo aparece una tarjeta flotando sobre la pantalla con pausa, repetir y
velocidad (×1, ×1,5, ×2), y el personaje pone otra cara. La velocidad es un estiramiento de
audio ya generado, no un parámetro de síntesis, así que aplica sobre la oración que estás
escuchando y no sobre la siguiente — y el tono no se mueve con ella. La *Entonación* es la
otra perilla, y a propósito no se llama *Calidad*: Piper publica sus voces en cuatro
escalones de calidad, pero `daniela` existe sólo en el más alto, así que bajar un escalón
costaría el acento. Lo que queda cambia cómo se samplea un mismo modelo, que no cuesta
ninguna de las dos cosas.

## Limitaciones conocidas

- **Solo Windows.** Linux está fuera de la v1 por una razón concreta: Wayland
  bloquea por diseño tres de las cinco primitivas que Otto necesita.
  [El detalle](docs/adr/0001-stack-tecnologico.md). macOS está fuera de forma
  permanente — no hay hardware para mantenerlo.
- **Algunos antivirus lo van a marcar.** Otto registra un atajo global y sintetiza
  pulsaciones de teclado, que es funcionalmente la descripción de un keylogger. El
  modo por defecto no instala ningún hook de sistema, justamente por eso.
- **Terminales elevadas rechazan la inserción.** Un proceso sin privilegios no
  puede escribir en una ventana que sí los tiene.
- **Leer la selección pasa por el portapapeles.** Otto le manda Ctrl+C a la aplicación
  donde estabas, lee lo que vuelve y te deja el portapapeles tal cual estaba — pero esa
  copia la hace la otra aplicación, así que las marcas de exclusión que Otto le pone a sus
  propios dictados no se pueden aplicar ahí. Un gestor de portapapeles puede llegar a
  registrar la selección.
- **La calidad del micrófono importa más que el modelo.**

## Documentación

La documentación técnica está en inglés.

| | |
|---|---|
| [Visión de producto](docs/vision-producto.md) | Qué es, para qué sirve, qué no es |
| [ADR 0001 — Stack tecnológico](docs/adr/0001-stack-tecnologico.md) | Qué se eligió, qué se descartó y por qué |
| [ADR 0002 — Corrección en el mismo proceso](docs/adr/0002-in-process-correction-llamasharp.md) | Por qué se sacó Ollama por un modelo en el mismo proceso, y qué cambió |
| [ADR 0003 — Lectura en voz alta con Piper](docs/adr/0003-read-aloud-piper.md) | Por qué ganó el motor más rápido y no el que suena mejor |
| [Hito 0 — Latencia y precisión](docs/hito-0-resultados.md) | La compuerta de viabilidad |
| [Hito 0.5 — `initial_prompt`](docs/hito-0-5-resultados.md) | Vocabulario técnico |
| [Hito 4 — Corrección de voseo](docs/hito-4-resultados.md) | Por qué el prompt importó más que el modelo |
| [Distribución y primer arranque](docs/distribucion-y-primer-arranque.md) | Las siete trampas para que lo use otra persona |
| [Otto.Bench](tools/Otto.Bench/README.md) | La herramienta de medición |

## Arquitectura

```
Otto.Core              Puertos y orquestación. Sin código de sistema operativo.
Otto.Speech            Whisper.net: transcripción, VAD, prompt por contexto
Otto.Storage           SQLite con FTS5: notas y búsqueda
Otto.PostProcessing    LLamaSharp + Vulkan, en el mismo proceso: corrección al rioplatense
Otto.Tts               Piper como proceso hijo: voces, descarga, lectura en voz alta
Otto.Platform.Windows  P/Invoke: atajo global, inyección, portapapeles, overlay
Otto.App               Avalonia: bandeja, ventana de notas, personaje
```

`Otto.Core` no conoce Windows. Esa frontera **la hace cumplir el compilador**: los
proyectos portables apuntan a `net10.0` y los que tocan Win32 a `net10.0-windows`,
así que un P/Invoke en el núcleo no compila.

El pipeline completo se testea sin micrófono, sin GPU y sin ventana en foco. Si eso
no fuera posible, la separación sería decoración.

## Compilar

```bash
dotnet build                        # requiere .NET 10 SDK
dotnet test
.\build\publicar.ps1                # arma dist\Otto-Setup.exe y dist\Otto-windows-x64.zip
.\build\publicar.ps1 -NoInstaller   # solo el ZIP portable
```

El instalador necesita Inno Setup (`winget install JRSoftware.InnoSetup`). Si falta,
`publicar.ps1` corta con un error en vez de saltearlo: una release que sale sin
instalador porque el paso se salteó en silencio no la nota nadie hasta que alguien
pregunta dónde está el archivo.

### Publicar una versión

```bash
git tag v0.2.0
git push origin v0.2.0
```

El CI compila, testea, arma los dos artefactos y crea la release. **La versión sale
de la etiqueta**, no del código: si la aplicación dijera una versión distinta a la
publicada, el chequeo de actualizaciones mentiría en silencio para siempre.

## Stack

.NET 10 · Avalonia UI · Whisper.net (`large-v3-turbo`, runtime Vulkan) · SQLite ·
LLamaSharp (Qwen2.5-3B-Instruct, Vulkan, opcional, requiere GPU) · Piper (VITS, opcional) ·
NAudio + SoundTouch.Net para la reproducción

## Licencia

[MIT](LICENSE). Usalo, copialo, modificalo, vendelo — solo mantené el aviso de
copyright.

Casi todas las dependencias son MIT también: Whisper.net, Avalonia, NAudio,
Microsoft.Data.Sqlite, SkiaSharp, CommunityToolkit.Mvvm y el propio Piper. El modelo de
Whisper no viaja en el ZIP; se descarga aparte y OpenAI lo liberó bajo MIT.

Dos de los componentes que Otto **redistribuye como archivos** tienen otras condiciones, y
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) —que se copia al directorio de
instalación, no sólo vive en el repo— las dice completas. `SoundTouch.Net`, que hace el
control de velocidad de la lectura, es LGPL-2.1-or-later: queda en el directorio del
programa como ensamblado suelto justamente para que se pueda reemplazar, y por eso Otto se
publica self-contained pero ni single-file ni trimmed. `espeak-ng`, GPL-3.0-or-later, viene
adentro de la release de Piper y corre como parte de `piper.exe`, un proceso aparte con el
que Otto habla por entrada estándar en lugar de enlazarlo. Las voces de lectura son
CC-BY-4.0 o MIT según la voz, y se descargan en vez de viajar en el paquete.
