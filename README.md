# Otto

**Offline Transcription, Totally Open**

Dictado por voz para Windows. Apretás una tecla, hablás, y el texto aparece donde
estabas escribiendo — en cualquier programa. La transcripción es 100% local: tu voz
no sale de tu computadora.

> **Estado: hitos 0 y 0.5 superados.** Todavía no hay aplicación, pero la
> viabilidad está medida: **0,53 s de mediana** para transcribir, un dictado de 30
> segundos con 2 % de error, y los nombres propios técnicos de 67 % a 17 % de error
> con `initial_prompt`. Siguiente: hito 1, el pipeline mínimo de punta a punta.
> [Hito 0](docs/hito-0-resultados.md) · [Hito 0.5](docs/hito-0-5-resultados.md)

## Por qué

El dictado de Windows tiene precisión insuficiente en español, sobre todo con
terminología técnica y frases que mezclan español e inglés. Las alternativas
comerciales lo resuelven, pero con suscripción y mandando tu audio a servidores de
terceros.

Otto no manda nada a ningún lado. La prueba: **desenchufá internet y sigue
funcionando.**

## Documentación

| | |
|---|---|
| [Visión de producto](docs/vision-producto.md) | Qué es, para qué sirve, qué no es |
| [ADR 0001 — Stack tecnológico](docs/adr/0001-stack-tecnologico.md) | Qué se eligió, qué se descartó y por qué |
| [Hito 0 — Resultados medidos](docs/hito-0-resultados.md) | Latencia y precisión en hardware real |
| [Hito 0.5 — Efecto del `initial_prompt`](docs/hito-0-5-resultados.md) | Cómo se arregló el vocabulario técnico |
| [Distribución y primer arranque](docs/distribucion-y-primer-arranque.md) | Qué hace falta para que lo use otra persona |
| [Brief técnico](docs/brief.md) | El documento de arranque original |
| [Otto.Bench](tools/Otto.Bench/README.md) | La herramienta de medición del hito 0 |

## Stack

.NET 10 · Avalonia UI · Whisper.net (`large-v3-turbo`, runtime Vulkan) · SQLite · Ollama (opcional)

Windows es la única plataforma soportada. Linux está fuera de la v1 por una razón
técnica concreta — Wayland bloquea por diseño tres de las cinco primitivas que Otto
necesita — que está documentada en el [ADR 0001 §6](docs/adr/0001-stack-tecnologico.md).
macOS está fuera de forma permanente.

## Licencia

Pendiente.
