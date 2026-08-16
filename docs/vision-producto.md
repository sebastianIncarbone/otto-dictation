# Otto — Visión de producto

> Este documento captura la visión del producto tal como la definió el autor.
> El [brief técnico](brief.md) original cubría solo el pipeline de dictado; acá se
> agregan las piezas de producto que lo convierten en una aplicación completa.
> Las decisiones de stack que se derivan de esto viven en
> [ADR 0001](adr/0001-stack-tecnologico.md).

---

## Qué es Otto

Una aplicación de escritorio que corre en segundo plano y te deja dictar por voz
en cualquier programa del sistema, con transcripción 100% local. Además guarda
todo lo que dictaste, así que funciona también como block de notas por voz.

Dos propiedades no negociables, que son las que le dan sentido al nombre
(*Offline Transcription, Totally Open*):

- **Offline** — el audio nunca sale de la máquina. Sin nube, sin API keys, sin
  costo por uso.
- **Open** — código abierto, auditable, sin telemetría.

## Plataformas

| Plataforma | Estado |
|---|---|
| Windows | **Única plataforma soportada en v1** |
| Linux | Fuera de v1. No por esfuerzo: Wayland bloquea por diseño tres de las cinco primitivas que Otto necesita. El detalle, con tabla, en [ADR 0001 §6](adr/0001-stack-tecnologico.md#6-por-qué-linux-no-está-en-v1) |
| macOS | **Fuera de alcance, permanente.** No hay hardware para mantenerlo, y soportar algo que no podés probar es peor que no soportarlo |

El código se estructura igual con puertos y adaptadores, así que la decisión de
alcance no se cocina dentro de la lógica. Si Linux vuelve a la mesa, es sumar un
proyecto de adaptadores, no reescribir la app.

## Los tres modos de la aplicación

### 1. En segundo plano (el modo por defecto)

Otto arranca minimizado y se queda esperando. No ocupa lugar en la barra de
tareas. Presionás la hotkey, te escucha, procesa, y escribe el texto **en el
cursor donde estás parado** — sea VS Code, el navegador, Slack o una terminal.

El texto que se inyecta queda además **registrado dentro de Otto**. Dictar y
guardar son la misma acción: no hay un paso extra de "guardar esto".

### 2. El personaje animado

Mientras corre en segundo plano, aparece un **mini personaje animado en pantalla**.
Cumple dos funciones:

- Señal de vida: sabés que Otto está abierto sin ir a buscar el ícono de la bandeja.
- Estado: el personaje refleja en qué está — quieto, escuchando, procesando.

Es una ventana flotante, chiquita, siempre visible, que no interfiere con el
trabajo (los clics la atraviesan). Es deliberadamente una pieza de personalidad,
no un requisito funcional — pero es lo que hace que la herramienta se sienta un
producto y no un script.

### 3. La ventana principal

Si abrís Otto, tenés una interfaz chica con dos secciones:

**Configuración**
- Hotkey
- Modelo de Whisper
- Idioma
- Micrófono
- Modos de post-procesamiento
- Diccionario personalizado

**Transcripciones**
- Historial de todo lo dictado.
- Cada entrada es **editable** — corregís lo que Whisper entendió mal.
- Cada entrada puede llevar **título**, para usarla como nota.
- **Botón de copiar por entrada.** Un solo clic copia la nota entera. Nada de
  seleccionar texto a mano: ese es exactamente el tipo de fricción que la
  herramienta existe para eliminar.

## El flujo central, de punta a punta

```
  Presionás y mantenés la hotkey
            ↓
  Otto escucha (el personaje lo muestra)
            ↓
  Soltás la hotkey
            ↓
  Transcripción local con Whisper
            ↓
  Post-procesamiento opcional (LLM local, según en qué app estés)
            ↓
  ┌──────────────────┬─────────────────────────┐
  ↓                  ↓
  Se escribe en      Se guarda en el historial
  el cursor          de Otto (editable, con
  donde estabas      título, con botón copiar)
```

## Qué NO es

- No es un asistente por voz. No hay comandos ("borrar eso", "nuevo párrafo") en v1.
- No hay wake word ni escucha continua. Siempre push-to-talk explícito.
- No transcribe archivos de audio pregrabados.
- No sincroniza nada con ningún lado. Las notas viven en tu máquina.

## Criterios de éxito

Los del brief se mantienen, con estos agregados:

- La latencia de dictado se mantiene por debajo de ~1,5 s aunque ahora haya
  persistencia de por medio. Guardar en la base **nunca** puede estar en el camino
  crítico entre soltar la tecla y ver el texto.
- La ventana principal abre rápido y con el historial cargado, incluso con miles
  de notas acumuladas.
- El personaje no puede robar foco, ni aparecer en Alt+Tab, ni bloquear clics.
- Cerrar la ventana principal minimiza a la bandeja; no termina el proceso.
