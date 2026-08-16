# Hito 0 — Resultados medidos

> La compuerta de viabilidad del proyecto. Todo lo de acá está medido en hardware
> real con audio real, no estimado.
>
> Herramienta: [`tools/Otto.Bench`](../tools/Otto.Bench/README.md) · Decisiones que
> se derivan: [ADR 0001](adr/0001-stack-tecnologico.md)

**Veredicto: pasa.** El proyecto es viable como lo describe el brief. Queda un solo
problema abierto, y es estrecho.

---

## Cómo se midió

| | |
|---|---|
| GPU | NVIDIA GeForce RTX 4060 Laptop (driver 32.0.16.1062) |
| CPU | Intel Core i7-13620H, 10 núcleos / 16 hilos |
| Runtime | `Whisper.net.Runtime.Vulkan` |
| Audio | 13 clips grabados por el autor, 16 kHz mono, de 4 a 30 segundos |
| Idioma | Español rioplatense con términos técnicos en inglés intercalados |

Los clips se graban **una vez** y todos los modelos corren contra los mismos
archivos, así la única variable es el modelo. Las referencias de texto son lo que
el hablante **realmente dijo**, no el guion que se le pidió leer: medir contra el
guion le cobra al modelo el fraseo natural de la persona.

---

## Latencia

| Modelo | Carga | Mediana | Peor caso | Factor tiempo real |
|---|---:|---:|---:|---:|
| `large-v3-turbo` | 1,8 s | **0,53 s** | 0,76 s | 0,059 |
| `medium` | 1,4 s | 0,60 s | 1,15 s | 0,070 |
| `small` | 0,5 s | 0,29 s | 0,56 s | 0,033 |

Presupuesto: **< 1,5 s** de punta a punta. La transcripción es una parte; todavía
faltan captura, inyección y post-proceso. Con 0,53 s de mediana sobra margen.

### La latencia no depende de la duración del dictado

`large-v3-turbo`, mismo modelo, distintos largos de audio:

| Audio | Transcripción |
|---:|---:|
| 4,1 s | 0,45 s |
| 6,5 s | 0,46 s |
| 12,7 s | 0,53 s |
| 30,0 s | 0,56 s |

`whisper.cpp` procesa en una ventana fija de 30 segundos, así que el costo es
prácticamente constante mientras el dictado entre ahí. **Dictar una frase o dictar
un párrafo cuesta lo mismo.** Es un dato de producto, no solo de rendimiento: no
hay que empujar al usuario a dictar corto.

---

## Precisión

WER contra lo que el hablante realmente dijo. Más bajo es mejor.

| Clip | Categoría | `large-v3-turbo` | `medium` | `small` |
|---|---|---:|---:|---:|
| `04-frontend` | Jerga densa | **0 %** | 15 % | 15 % |
| `09-proyecto` | Vocabulario del proyecto | **0 %** | 0 % | 15 % |
| `13-pausa` | Pausa larga en el medio | **0 %** | 0 % | 0 % |
| `10-largo` | Dictado de 30 segundos | **2 %** | 7 % | 11 % |
| `06-tooling` | Comandos encadenados | 8 % | 8 % | 17 % |
| `01-control` | Español puro | 10 % | 20 % | 20 % |
| `05-git` | Jerga de git | 10 % | 10 % | 10 % |
| `07-numeros` | Números y códigos | 10 % | 10 % | 10 % |
| `02-anglicismos` | Anglicismos comunes | 11 % | 11 % | 11 % |
| `03-comandos` | Comandos de terminal | 17 % | 8 % | 25 % |
| `12-duda` | Duda inicial | 25 % | 25 % | 25 % |
| `08-nombres` | **Nombres propios** | **67 %** | 50 % | 67 % |

`large-v3-turbo` gana o empata en casi todo, a un costo de latencia
indistinguible de `small` en la práctica. **Queda confirmado como el modelo de v1.**

### El caso que mejor resume el resultado

Un dictado de 30 segundos, con un tropiezo real del hablante en el medio:

> Estuve pensando en cómo encarar el tema de la latencia y creo que lo mejor es
> dejar el modelo cargado en memoria desde que arranca la aplicación. Porque si no
> lo cargamos en cada pedido se nos va a… van var… años segundos. Y la herramienta
> deja de ser usable para dictar de forma cotidiana.

`large-v3-turbo` lo transcribió con **2 % de error**, reproduciendo el tropiezo
tal cual. Una sola palabra distinta en 30 segundos de habla espontánea.

---

## El problema que queda: nombres propios

Es el único resultado malo, y es nítido. Los tres modelos fallan en **las mismas
palabras**:

| Se dijo | `large-v3-turbo` | `medium` | `small` |
|---|---|---|---|
| Whisper.net | Whisper.net ✓ | Whisper.net ✓ | whisper.net ✓ |
| Ollama | Olama | Olama | olama |
| Hugging Face | Hugginsface | Hagins Face | haginsface |
| kubectl | Kubechtel | Qubechtel | kubectel |

El modelo **escucha bien** — las transcripciones son fonéticamente correctas. Lo
que no sabe es **cómo se escriben** esas palabras, porque nunca las vio en
contexto español. Eso no lo arregla un modelo más grande: `medium` no es mejor
que `turbo` acá de forma consistente.

Es exactamente el caso de uso del `initial_prompt` de Whisper, y el objetivo del
hito 0.5.

## Problema secundario: el voseo se neutraliza

Sistemático en todos los clips:

```
Corré    → Corre        Instalá → Instala
podés    → puedes       levantá → levanta
revisá   → Revisar      arrancá → arranca
```

Es una fracción chica de las palabras — por eso los WER quedan bajos igual — pero
para alguien que dicta en rioplatense es fricción constante. La hipótesis a probar
en el hito 0.5 es que anclar el `initial_prompt` con una frase en voseo corrige
esto y los nombres propios de una sola vez.

---

## Alucinación con silencio

Los tres modelos devuelven **vacío** con la compuerta de VAD activada. Sin VAD,
un clip de ruido de sala produjo `[Música]` — texto que se le habría escrito al
usuario en el documento.

Ver [ADR 0001 §5.7](adr/0001-stack-tecnologico.md) para el diseño correcto de esa
compuerta, que no es el obvio.

---

## Runtimes: qué carga y qué no

| Runtime | Carga | Motivo |
|---|---|---|
| Vulkan | **Sí** | Solo necesita `vulkan-1.dll`, que viene con el driver |
| CPU | Sí | Funciona, pero ver abajo |
| CUDA | **No** | Necesita el CUDA Toolkit (`cudart64`, `cublas64`). El driver de NVIDIA solo aporta `nvcuda.dll` |

Esto resuelve la pregunta abierta del ADR §5.4, y no por velocidad: **Vulkan es el
único runtime de GPU realmente distribuible.** CUDA le exigiría a cada usuario
instalar un toolkit de varios gigas.

### GPU contra CPU

`large-v3-turbo`, mismos clips:

| Clip | Vulkan | CPU | |
|---|---:|---:|---|
| 01-control | 0,67 s | 17,44 s | 26× |
| 04-frontend | 0,92 s | 33,82 s | 37× |
| 10-largo | 7,18 s | 122,67 s | 17× |

> Nota: estos números de Vulkan son de una corrida anterior con la compuerta de VAD
> mal implementada. La proporción contra CPU es lo válido acá.

**El fallback de CPU no puede ser `small`** — dio 3,47 s para un clip corto.
Tiene que ser `base`. `large-v3-turbo` por CPU es directamente inusable: 17
segundos para una frase de 5.

---

## Qué cambia en el plan

1. **Runtime de GPU: Vulkan.** Decisión cerrada, y por distribución, no por velocidad.
2. **Modelo de v1: `large-v3-turbo`.** Confirmado con datos.
3. **Fallback sin GPU: `base`**, no `small`.
4. **El VAD va como compuerta, no como partidor.** Ver ADR §5.7.
5. **Nuevo hito 0.5**, antes de tocar interfaz: `initial_prompt` con vocabulario
   técnico y anclaje de voseo, medido contra esta misma línea de base.

## Cabos sueltos

Tres referencias donde la evidencia sugiere que el guion está mal y el modelo
bien. Los tres modelos y las dos sesiones de grabación coinciden en la versión del
modelo:

- `03-comandos`: el guion dice "si tira", todos transcriben "tienen".
- `10-largo`: el guion dice "cada pedido", todos transcriben "periodo".
- `12-duda`: el guion dice "arrancá con esto", todos transcriben "arranca con eso".

No se ajustaron: corregir referencias para mejorar los propios números es
exactamente cómo se arruina un benchmark. Si se confirman, la precisión real es
mejor que la de las tablas de arriba.
