# Otto.Bench — hito 0

Mide **latencia y precisión** de los modelos de Whisper antes de que se escriba una
sola línea de interfaz. Es la compuerta del proyecto: si los números no dan, hay
que cambiar el modelo, el hardware o el alcance — y es mejor saberlo ahora.

Contexto: [ADR 0001 §5.8](../../docs/adr/0001-stack-tecnologico.md)

## Por qué son dos comandos y no uno

`record` graba los clips **una sola vez**. `bench` corre todos los modelos contra
**esos mismos archivos**.

Si se grabara en cada corrida, la comparación no mediría el modelo: mediría cómo
hablaste ese día. Con audio fijo, la única variable es el modelo.

## Uso

```bash
# 0. Ver qué runtimes cargan realmente en esta máquina
dotnet run -- probe

# 1. Traer los modelos (el turbo pesa ~1,6 GB)
dotnet run -- models --models large-v3-turbo,medium,small,base

# 2. Grabar los 13 clips con tu voz y tu micrófono. Una vez.
dotnet run -- record          # --force para regrabar sin preguntar

# 3. Corregir las referencias (ver abajo, importa más de lo que parece)
dotnet run -- review

# 4. Medir. Un runtime por corrida.
dotnet run -- bench --runtime vulkan
dotnet run -- bench --runtime cpu

# 5. Ver qué pasa sin VAD — este es el que muestra las alucinaciones
dotnet run -- bench --runtime vulkan --no-vad
```

## `review`: por qué el guion no sirve como referencia

La referencia incorporada es el **guion** que se pidió leer. El habla natural se
desvía: donde el guion dice "¿podés revisar?" la persona dice "¿me podés revisar?",
porque en rioplatense sale así. Medir contra el guion le cobra ese desvío al
modelo, y el WER queda inflado por errores que no existen.

`review` transcribe los clips y escribe `clips/referencias.json` **precargado con lo
que el modelo escuchó**. Así el trabajo no es reescribir de memoria sino corregir
las pocas palabras que el modelo erró de verdad. Ese archivo pisa las referencias
incorporadas.

**Regenerá las referencias después de cada regrabación.** Un `referencias.json`
viejo contra clips nuevos mide el modelo contra sus propios errores anteriores y da
un 0 % falso.

Cada corrida escribe un `resultados-<runtime>.md` con tres tablas: latencia,
precisión (WER) y alucinación con silencio, más todas las transcripciones para
leer a ojo. El WER ordena; leer el texto decide.

## Un runtime por proceso

No se puede alternar de runtime dentro de la misma corrida. Whisper.net carga la
librería nativa una vez y queda cargada para toda la vida del proceso. Comparar
CUDA contra CPU son **dos ejecuciones**, no un bucle.

## Qué estamos buscando

| Métrica | Objetivo | Si no da |
|---|---|---|
| Factor de tiempo real | < 0,1 | Bajar a `medium` o `small` y volver a medir |
| WER en clips con inglés intercalado | Legible sin corregir | El diccionario y el post-proceso pasan a ser críticos de v1 |
| Alucinación con silencio | Cero palabras | El VAD tiene que arreglarlo antes de seguir |

El **factor de tiempo real** es tiempo de inferencia dividido duración del audio.
Con 0,1, un clip de 15 segundos transcribe en 1,5.

## Sobre los clips 11, 12 y 13

Whisper, alimentado con silencio, no devuelve vacío: **inventa texto**. Los tres
últimos clips son la prueba de regresión de eso, y el VAD de Silero es la defensa.

Reproducido: con 4 segundos de ruido de sala, sin VAD, la salida fue `[Música]`.
Texto que se le habría escrito al usuario en el medio del documento.

### El VAD va como compuerta, no como partidor

La implementación obvia — detectar todas las regiones de voz y transcribir cada
una — cuesta **diez veces más**, porque `whisper.cpp` rellena cada llamada hasta
una ventana fija de 30 segundos. N regiones son N inferencias completas.

| | Mediana | Peor caso |
|---|---:|---:|
| Partidor | 0,94 s | 7,18 s |
| Compuerta | **0,47 s** | **0,68 s** |

Y además transcribe **peor**: partir el audio le saca a Whisper el contexto que usa
para desambiguar. Los nombres propios pasaron de 100 % de error a 62 % con solo
dejar de partir.

Lo correcto: preguntarle al VAD si hay voz. Si no hay, devolver vacío sin invocar
el modelo. Si hay, recortar al tramo entre la primera y la última región y correr
**una sola** inferencia. Ver `Benchmark.TranscribeAsync`.

## Notas

- Los clips y los modelos están en `.gitignore`. Los modelos pesan demasiado y los
  clips son la voz del autor.
- El primer paso de cada modelo es un calentamiento que se descarta: la primera
  inferencia paga la inicialización nativa y ensuciaría la medición.
- La salida de consola está en español porque es una herramienta de uso propio;
  el código y los comentarios en inglés, como el resto del repositorio.
