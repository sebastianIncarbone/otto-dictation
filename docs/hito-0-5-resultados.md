# Hito 0.5 — Efecto del `initial_prompt`

> Objetivo: atacar los dos problemas que dejó abiertos el
> [hito 0](hito-0-resultados.md) — nombres propios destrozados y voseo
> neutralizado — sin tocar todavía la interfaz.
>
> Método: mismos clips, mismo modelo (`large-v3-turbo`), mismo runtime (Vulkan).
> La única variable es el prompt.

**Resultado: el vocabulario se resuelve. El voseo, a medias. Y aparece un
requisito de arquitectura que no estaba previsto.**

---

## Una nota de método: la métrica estaba rota

La primera corrida decía que el prompt de voseo no servía. Era mentira: el WER
normalizaba quitando acentos, y **el voseo rioplatense se marca justo con el
acento**. `Corré` y `Corre` le resultaban la misma palabra a la métrica que
supuestamente medía el problema.

Ahora se reportan las dos. La tolerante ordena modelos; la estricta ve el registro.

| `03-comandos` | tolerante | estricta |
|---|---:|---:|
| sin prompt | 17 % | **33 %** |
| vocabulario + voseo | 8 % | **8 %** |

La mejora real era el doble de lo que mostraba la primera tabla.

---

## Los cuatro prompts

| Clave | Qué aísla |
|---|---|
| `none` | Línea de base |
| `vocab` | Solo la lista de términos técnicos |
| `voseo` | Solo el registro rioplatense, sin jerga |
| `ambos` | Los dos, como una sola frase natural |

`whisper.cpp` condiciona el decodificador con este texto **como si fuera la
transcripción inmediatamente anterior**. Por eso funciona mejor como prosa que como
lista: está cebando una continuación, no cargando un diccionario.

> **Límite duro:** el prompt se trunca alrededor de los 224 tokens, y whisper.cpp
> **corta y sigue, sin error**. Un diccionario que se pasa pierde sus últimos
> términos en silencio, y el usuario concluye que la función no sirve.
>
> El prompt es gratis en **tiempo** — está medido, no cuesta latencia. No es
> gratis en **capacidad**. Ver [§ El diccionario no se le muestra al
> usuario](#el-diccionario-no-se-le-muestra-al-usuario-en-tokens).

---

## Resultados

WER estricto, `large-v3-turbo` sobre Vulkan:

| Clip | sin prompt | vocab | voseo | **ambos** |
|---|---:|---:|---:|---:|
| `03-comandos` | 33 % | 25 % | 17 % | **8 %** |
| `08-nombres` | 67 % | **17 %** | 67 % | 33 % |
| `01-control` | 10 % | 10 % | 10 % | 10 % |
| `06-tooling` | 25 % | 25 % | 25 % | 25 % |
| `10-largo` | **2 %** | 7 % | 7 % | 7 % |
| `04-frontend` | **0 %** | 15 % | 15 % | 15 % |
| **Promedio** | 18 % | **15 %** | 19 % | **15 %** |

Latencia: entre 0,52 s y 0,54 s de mediana en las cuatro variantes. **El prompt no
cuesta tiempo.**

### Lo que sí se arregló: los nombres propios

```
Se dijo     : Whisper.net  Ollama       Hugging Face   kubectl
sin prompt  : Whisper.net  Olama        Hugginsface    Kubechtel     (67 %)
con vocab   : Whisper.net  Ollama ✓     Hugging Face ✓ kubechtl      (17 %)
```

Tres de cuatro corregidos. `kubectl` sigue mal pero quedó fonéticamente al lado, y
ese último tramo es trabajo del diccionario de post-proceso, no del prompt.

### Lo que se arregló a medias: el voseo

Funcionó donde la forma exacta estaba en el prompt **y** el modelo dudaba:

```
sin prompt  → Corre npm run build y fíjate ...
con voseo   → Corré npm run build y fijate ...   ✓
```

No funcionó en las conjugaciones donde el modelo tiene una preferencia fuerte por
el español neutro, ni siquiera teniendo la frase exacta en el prompt:

```
Se dijo     : ¿me podés revisar?      Instalá las dependencias
todas       : ¿me puedes revisar?     Instala las credenciales
```

`Instalá las dependencias con pnpm` estaba **literalmente** en el prompt `ambos` y
aun así salió `Instala las credenciales`. El prior del modelo hacia el neutro le
gana al condicionamiento.

**Conclusión:** el `initial_prompt` es una herramienta de vocabulario, no de
registro. El voseo que queda hay que atacarlo en el post-procesamiento (hito 4),
donde un LLM local puede reescribir a voseo de forma explícita.

### Lo que empeoró

Cualquier prompt degrada un poco el habla espontánea sin jerga:

- `10-largo` (dictado de 30 s con un tropiezo real): 2 % → 7 %.
- `04-frontend`: 0 % → 15 %, aunque acá el prompt enseñó `re-render` y la
  referencia dice `rerender` — probablemente el modelo tenga razón y la
  referencia esté mal.

---

## El hallazgo de arquitectura

Los prompts **ayudan al dictado técnico y molestan al dictado común**. No hay un
prompt único que gane en todo.

La respuesta ya estaba en el brief, pero como intuición. Ahora está medida: **el
prompt tiene que ser contextual, no global.**

| Ventana en foco | Prompt |
|---|---|
| VS Code, terminal, cliente de git | `ambos` — jerga técnica y voseo |
| Chat, correo, documento | ninguno, o uno de lenguaje llano |

Esto es exactamente el mapeo proceso→formato que describe el brief en su §5, pero
ahora se sabe **por qué** existe y cuánto vale: entre 25 y 50 puntos de WER en los
clips técnicos, a cambio de 5 puntos en los clips narrativos.

`IPostProcessor` deja de ser el único consumidor de la detección de contexto. El
transcriptor también la necesita, **antes** de inferir.

### El diccionario no se le muestra al usuario en tokens

El límite de 224 tokens es real, pero un contador de tokens en la pantalla de
configuración sería filtrarle al usuario un detalle de implementación. Nadie sabe
qué es un token, y nadie debería tener que saberlo para usar un dictado.

**El prompt contextual ya resuelve el problema por su cuenta.** Si a cada dictado
se le inyectan solo los términos del contexto en foco, el prompt se mantiene chico
sin que el usuario administre nada: el diccionario completo puede tener trescientos
términos mientras el de "terminal" tenga veinte.

O sea que el diccionario **no se limita**. Se organiza por contexto, que es como el
usuario ya piensa sus términos.

Solo si un contexto en particular se pasa de largo hay que avisar, y en su idioma:

> *"En 'Terminal' tenés más términos de los que entran. Los últimos no se van a
> usar — probá moverlos a otro contexto o sacar los que menos uses."*

Nunca *"excediste el presupuesto de 224 tokens"*. El límite es nuestro problema, no
suyo; lo que sí es suyo es saber que algo que agregó no va a tener efecto.

---

## Qué cambia en el plan

1. **Se adopta `ambos` como prompt técnico por defecto.**
2. **El prompt se selecciona por contexto**, junto al modo de formato. La
   detección de ventana en foco sube de prioridad: ahora la necesita el pipeline
   de transcripción, no solo el post-proceso.
3. **El diccionario personalizado alimenta el prompt, organizado por contexto.**
   No se limita ni se le muestran tokens al usuario: el filtro por contexto
   mantiene el prompt corto solo. Se avisa únicamente si un contexto se pasa, y en
   lenguaje de persona.
4. **El voseo pasa al hito 4**, como regla explícita de post-procesamiento. El
   `initial_prompt` no lo resuelve.
5. **La métrica de precisión reporta acentos.** Sin eso no se puede medir voseo.
