# Hito 4 — Corrección de voseo con modelo local

> El [hito 0.5](hito-0-5-resultados.md) dejó abierto el registro rioplatense: el
> `initial_prompt` de Whisper arregla vocabulario pero no conjugaciones. Esto lo
> cierra con un modelo local de post-proceso.

**Resultado: funciona.** Error de palabra de 18 % a 13 %, y el dictado completo
sigue por debajo de 1,5 s.

---

## Por qué no puede ser una tabla

La respuesta obvia es un reemplazo determinístico: `instala`→`instalá`,
`corre`→`corré`. No sirve, y el motivo es una propiedad del idioma:

```
"Instalá las dependencias"            →  imperativo, hay que convertir
"el script instala las dependencias"  →  3ª persona, NO hay que tocar
```

**El imperativo de tú y el indicativo de tercera persona son la misma cadena de
caracteres.** Una tabla ciega corrompe la segunda frase. La diferencia acústica sí
existe —el rioplatense acentúa la última sílaba— pero Whisper la descarta al
neutralizar, así que del texto ya no se recupera.

Distinguirlas necesita sintaxis. Por eso hay un modelo.

Lo único que **sí** es seguro en una tabla son las formas de *tú* inequívocas
(`puedes`, `tienes`, `quieres`): no son homógrafas con nada. Cubren cerca de una
quinta parte de los casos.

## Qué modelo

Tres tamaños, mismos 12 clips, misma entrada:

| Modelo | Peso | WER final | Latencia mediana |
|---|---:|---:|---:|
| `qwen2.5:0.5b` | 0,37 GB | 17 % | 0,19 s |
| `qwen2.5:1.5b` | 0,92 GB | 18 % | 0,25 s |
| **`qwen2.5:3b`** | 1,80 GB | **13 %** | 0,32 s |

Sin tocar nada: 18 %.

**La latencia nunca fue la restricción** — los tres entran holgados en el
presupuesto de 2 s. Lo que se rompe por debajo de 3B es el **seguimiento de
instrucciones**: los modelos chicos contestan un comentario sobre el texto en vez
de devolver el texto corregido.

## El prompt importó más que el modelo

Cuatro versiones, mismo `qwen2.5:3b`:

| Versión | WER |
|---|---:|
| Reglas en prosa, en rioplatense, en el mensaje de usuario | 31 % ← **peor que no hacer nada** |
| + prohibición explícita del portugués | 26 % |
| Rol `system` + español neutro + ejemplos | 17 % |
| **+ ejemplos de primera persona** | **13 %** |

Tres cambios explican la diferencia:

**1. Las instrucciones van en español neutro, no en rioplatense.** La primera
versión decía *"Sos un corrector"*, *"Cambiá"*, *"devolvé"*. Esas formas con acento
final son justamente lo que el español comparte con el portugués, y el modelo
cruzaba la frontera:

```
Hay que fazer o deploy depois do margem.
El componente de React não está fazendo o rerender quando o estado muda.
```

Describir el objetivo en neutro, en vez de hablarlo, eliminó la contaminación por
completo.

**2. Van en el rol `system`.** El mismo texto en el mensaje de usuario se respeta
bastante menos.

**3. Ejemplos, no reglas.** Un modelo de 3B imita un patrón demostrado mucho mejor
de lo que sigue una descripción. Los ejemplos tienen que cubrir cada decisión: uno
que sí cambia, y cuatro que no.

El último es el más instructivo. Sin ejemplos de **primera persona**, el modelo
asume que todo verbo conjugado es del interlocutor:

```
Necesito hacer un rebase    →  Necesitás hacer un rebase   ✗
Primero revisamos… vemos…   →  Primero revisá… veré…       ✗
```

Dos ejemplos más y bajó de 17 % a 13 %.

## La reja

La corrección se descarta si el modelo cambió más de lo que una corrección de
conjugaciones puede justificar, y en ese caso se inserta el texto crudo.

- Rechaza si el largo varía más de ±25 %.
- Rechaza si tocó más del 20 % de las palabras (mínimo 2, para frases cortas).
- Solo mira entrada y salida: **en producción no existe la referencia**, porque
  nadie sabe qué dijo realmente el usuario.

Equivocarse con el voseo es una molestia. Equivocarse con lo que dijiste es una
herramienta rota, así que la reja se inclina a rechazar.

Tiene un límite conocido: **no puede distinguir un cambio de una palabra correcto
de uno corrupto.** Con el prompt final esos casos desaparecieron, pero la reja
queda como red por si el modelo se desvía.

## Todo modelo necesita calentarse

Tercera vez en este proyecto. Con el modelo frío, la corrección tarda **más que su
propio presupuesto de 2 s** y se descarta siempre: la función parece rota estando
perfectamente sana.

```
en frío   →  corrección 2,01 s  (descartada)
caliente  →  corrección 0,33 s
```

Se resuelve igual que con Whisper: una petición descartable al arrancar, más
`keep_alive` para que Ollama no lo descargue entre dictados.

**Regla general para este proyecto: todo lo que cargue un modelo se calienta al
arrancar, mientras la interfaz todavía dice "cargando".** Vulkan compila sus
pipelines la primera vez, Ollama carga pesos la primera vez. El usuario no puede
pagar ninguno de los dos.

## Resultado final

```
transcripción 0,57 s · corrección 0,33 s · inyección 0,13 s · total 1,02 s
```

Y en el documento:

```
Che, ¿me podés revisar el pull request que subí recién?
```

Aciertos: `me podés`, `Corré`, `Instalá…levantá`, `Revisá`, `arrancá`. Preserva
correctamente primera persona, tercera persona, infinitivos y los errores de
transcripción que no son asunto suyo. Quedan dos fallas menores en doce clips.

**Otto sigue funcionando sin Ollama.** Si no hay nada escuchando en `localhost`,
la función se desactiva al arrancar y se inserta la salida cruda de Whisper.
