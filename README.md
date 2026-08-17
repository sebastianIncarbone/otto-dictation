# Otto

**Offline Transcription, Totally Open**

Dictado por voz para Windows. Mantenés una tecla, hablás, la soltás, y el texto
aparece donde estabas escribiendo — en cualquier programa. La transcripción es
100% local: tu voz no sale de tu computadora.

> **La prueba: desenchufá internet y Otto sigue funcionando.**

---

## Por qué existe

El dictado de Windows tiene precisión insuficiente en español, sobre todo con
terminología técnica y frases que mezclan español e inglés. Las alternativas
comerciales lo resuelven, pero con suscripción y mandando tu audio a servidores de
terceros.

Otto no manda nada a ningún lado. La única conexión de red en toda su vida es la
descarga del modelo la primera vez.

## Qué hace

- **Dicta en cualquier programa.** Editor, navegador, chat, terminal. Donde tengas
  el cursor.
- **Corrige al rioplatense.** Escribe `Instalá` y `corré`, no `Instala` y `corre`.
  Opcional, con un modelo local.
- **Guarda todo.** Cada dictado queda como nota editable, con título, búsqueda y
  copiado de un clic. Dictar y guardar son la misma acción.
- **Vive en la bandeja**, con un personaje animado que muestra qué está haciendo.

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

1. Bajá `Otto-windows-x64.zip` de la sección de releases (~75 MB).
2. Descomprimilo donde quieras. **No hace falta instalar .NET ni nada más.**
3. Ejecutá `Otto.App.exe`.

La primera vez baja el modelo de voz (~1,6 GB con GPU, ~150 MB sin ella) y abre la
ventana. Después arranca directo en la bandeja.

> **Windows te va a mostrar una advertencia azul de SmartScreen.** Otto no está
> firmado con un certificado de código, que cuesta varios cientos de dólares por
> año. Hacé clic en **Más información** → **Ejecutar de todas formas**.
>
> Es razonable que desconfíes. Por eso el código es público, cada release tiene su
> análisis de VirusTotal, y todo lo que hace se puede leer.

## Uso

Mantené **Ctrl+Alt+Espacio**, hablá, soltá. El texto aparece donde estaba el cursor.

Abrí la ventana desde la bandeja para ver tus notas, buscarlas, editarlas o
cambiar la configuración.

## Corrección al rioplatense (opcional)

Whisper neutraliza el voseo: donde decís *"instalá"* escribe *"instala"*. Si tenés
[Ollama](https://ollama.com) instalado, Otto lo corrige:

```bash
ollama pull qwen2.5:3b
```

Otto lo detecta solo al arrancar. **Si no lo tenés, funciona igual** con la salida
cruda de Whisper.

El [hito 4](docs/hito-4-resultados.md) cuenta por qué esto no puede ser una tabla
de reemplazos y por qué el prompt importó más que el modelo.

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
- **La calidad del micrófono importa más que el modelo.**

## Documentación

| | |
|---|---|
| [Visión de producto](docs/vision-producto.md) | Qué es, para qué sirve, qué no es |
| [ADR 0001 — Stack tecnológico](docs/adr/0001-stack-tecnologico.md) | Qué se eligió, qué se descartó y por qué |
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
Otto.PostProcessing    Modelo local por HTTP: corrección al rioplatense
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
dotnet build            # requiere .NET 10 SDK
dotnet test
.\build\publicar.ps1    # arma dist\Otto-windows-x64.zip
```

## Stack

.NET 10 · Avalonia UI · Whisper.net (`large-v3-turbo`, runtime Vulkan) · SQLite ·
Ollama (opcional)

## Licencia

Pendiente.
