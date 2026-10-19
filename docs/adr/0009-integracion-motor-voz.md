# ADR-0009 — Integración del motor de voz: Whisper.net dentro de `TextFlow.VoiceWorker`

- Estado: Aceptado (2026-10-16)
- Resuelve: H0.3 de [`v0.2-plan.md`](../v0.2-plan.md)
- Relacionado: ADR-0007 (motor ASR: whisper.cpp large-v3-turbo q5_0 en CUDA)

## Contexto
ADR-0007 eligió el motor (whisper.cpp turbo en CUDA). Faltaba decidir cómo lo usa la app:
- **A. Whisper.net** (bindings .NET de whisper.cpp, runtimes nativos en NuGet) cargado dentro de un proceso propio
  `TextFlow.VoiceWorker`;
- **B. `whisper-server`** (binario de whisper.cpp) como proceso externo al que se envía el audio por HTTP.

## Spike (2026-10-16)
`spikes/TextFlow.VoiceSpike` (consola .NET 10, fuera de `TextFlow.sln`: copia ~1 GB de runtime a `bin/`) carga turbo con
Whisper.net 1.9.1 y transcribe las 55 grabaciones de H0.2 con las muestras ya en memoria, como lo hará el worker.
`spikes/asr/spike_h03.py` lo compara con `whisper-server` (mismo modelo y prompt) y puntúa con las funciones de `bench.py`.
Hardware de referencia (i5-12450H, RTX 2050 4 GB), GPU libre antes de medir (una primera medición con un juego abierto
dio 5–12 s por frase y se descartó; el script ahora se niega a medir con la GPU por encima del 20 %).

| | Whisper.net CUDA 12 (en proceso) | `whisper-server` CUDA |
|---|---|---|
| WER total / silencio / ruido | 4,3 % / 2,4 % / 7,8 % | 4,2 % / 2,4 % / 7,3 % |
| Caliente p50 / p95 | 0,42 s / 0,49 s (2.ª ronda 0,42 / 0,44) | 0,41 s / 0,44 s |
| Carga del modelo | 0,70–0,75 s | (al arrancar el servidor) |
| Primera frase (calentar CUDA) | 0,53–0,56 s | — |
| Listo desde que arranca el proceso | 1,3–1,4 s | — |
| VRAM pico (GPU entera, base ≈ 0,6 GiB) | 1 462 MiB | 1 414 MiB |
| RAM privada del proceso | ≈ 1,2 GB | — |
| Runtime GPU (sin comprimir / zip) | 1 087 MB / 624 MB | ≈ 1,1 GB (mismas DLL de CUDA) |

La diferencia de WER en ruido (7,8 frente a 7,3 %) son unas pocas palabras en 20 frases: mismo motor y mismo modelo.

Sin GPU (CUDA oculto con `CUDA_VISIBLE_DEVICES=-1`): Whisper.net no falla, carga solo su runtime de CPU y transcribe,
pero turbo en CPU tarda **23–27 s por frase** (4 frases). Inutilizable, como ya preveía ADR-0007.

## Hallazgos
1. **Flash attention es obligatoria.** `whisper-cli` y `whisper-server` la activan por defecto; Whisper.net no
   (`WhisperFactoryOptions.UseFlashAttention`). Sin ella, turbo llenó los 4 GB de VRAM y tardó 5–12 s por frase.
2. **Runtime CUDA 12, no CUDA 13.** `Whisper.net.Runtime.Cuda` (CUDA 13) importa `cublas64_13.dll`, que solo trae el
   CUDA Toolkit 13. `Whisper.net.Runtime.Cuda12.Windows` importa `cudart64_12.dll` y `cublas64_12.dll` (más
   `cublasLt64_12.dll`), las mismas que distribuye el zip de whisper.cpp: no hace falta instalar el Toolkit, basta el
   driver (aquí 617.42, CUDA 13.4, compatible hacia atrás).
3. **El tamaño no decide.** Las dos opciones necesitan la misma DLL de CUDA de whisper.cpp (~540 MB) y cuBLAS (~550 MB).
4. **El cargador de Whisper.net cae solo a CPU** si CUDA no está disponible; la app no se cae.

## Decisión
1. **Whisper.net 1.9.1** (`Whisper.net`, `Whisper.net.Runtime`, `Whisper.net.Runtime.Cuda12.Windows`) **dentro de
   `TextFlow.VoiceWorker`**, proceso aparte con named pipe hacia la app (H1.1). Se descarta `whisper-server`:
   - misma precisión y latencia;
   - sin puerto HTTP local (cortafuegos, puerto ocupado, otra app hablando con él) ni multipart por cada frase;
   - API tipada con cancelación; prompt y parámetros por dictado; VAD de whisper.cpp disponible (`WhisperVadFactory`, H4.1);
   - no hay que distribuir el `whisper-server.exe` ni la treintena de binarios que lo acompañan.
   El aislamiento que daba el servidor lo da el worker: un fallo nativo de CUDA no tira la app, los ~1,2 GB de RAM del
   modelo no van en `TextFlow.App`, y terminar el worker libera la VRAM con seguridad (H1.3; el V0.3 con LLM local la
   necesita).
2. **`UseFlashAttention = true`** siempre. Prompt de vocabulario igual que en H0.2 (editable en H2.4).
3. **Orden de runtimes** `[Cuda12, Cpu]` explícito en `RuntimeOptions.RuntimeLibraryOrder` (sin probar CUDA 13 ni
   Vulkan). El worker comunica a la app qué runtime cargó (`RuntimeOptions.LoadedLibrary`) para Diagnóstico.
4. **Sin GPU usable** (runtime cargado `Cpu`): no se usa turbo. El respaldo sigue siendo Parakeet en CPU (ADR-0007);
   su integración se decide en H4. Hasta entonces el dictado se desactiva con un aviso claro.
5. **Distribución** (concreta H0.5): el runtime de CPU (~1 MB zip) va en el instalador; el **paquete GPU** (DLL de
   CUDA de whisper.cpp + `cudart`/`cublas`/`cublasLt` 12: 624 MB zip) y el modelo (547 MB) **no**: se descargan la
   primera vez que se activa el dictado, solo en equipos con GPU NVIDIA, con reanudación y SHA-256 (H1.4).

## Consecuencias
- Descarga total la primera vez ≈ 1,2 GB (paquete GPU + modelo). Con la red de este PC, la reanudación no es opcional.
- Confirmar en H0.5 que la licencia de NVIDIA permite redistribuir `cudart`, `cublas` y `cublasLt` (figuran como
  redistribuibles en el EULA del CUDA Toolkit) y desde dónde se descargan. **Confirmado**, ver la sección H0.5.
- Primer dictado tras arrancar el worker: ≈ 1,3 s extra (carga + calentamiento). Opciones para H1.3: precargar al
  iniciar la app o al pulsar el atajo; la liberación por inactividad la vuelve a pagar.
- `TextFlow.VoiceWorker` es x64 y con .NET 10, como el spike; la app no referencia Whisper.net.
- Volver a medir con el paquete real de H1 (audio de WASAPI en vez de WAV) para confirmar la latencia de punta a punta.

## Distribución del modelo y del paquete GPU (V0.2 H0.5, 2026-10-17)

### Datos
| Pieza | Origen | Tamaño | Verificación | Licencia |
|---|---|---|---|---|
| Modelo `ggml-large-v3-turbo-q5_0.bin` | Hugging Face `ggerganov/whisper.cpp`, revisión fija `5359861c739e955e79d9a303bcbc70fb988958b1` | 574 041 195 B (547 MiB) | SHA-256 `394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2` (= LFS oid de HF = copia local de `models/`) | MIT (pesos de OpenAI Whisper) |
| DLL de whisper.cpp con CUDA (`ggml-cuda-whisper.dll` 564 MB y 4 DLL pequeñas) | NuGet `Whisper.net.Runtime.Cuda12.Windows` 1.9.1 (nupkg 250 MB) | ≈ 566 MB | hash del nupkg en el catálogo de NuGet | MIT |
| `cudart64_12.dll` | NVIDIA, `cuda_cudart` 12.4.127 (redist oficial, zip 2,5 MB) | 0,55 MB | SHA-256 en `redistrib_12.4.1.json` | EULA del CUDA Toolkit |
| `cublas64_12.dll`, `cublasLt64_12.dll` | NVIDIA, `libcublas` 12.4.5.8 (redist oficial, zip 392 MB) | 574 MB | SHA-256 en `redistrib_12.4.1.json` | EULA del CUDA Toolkit |

- **Licencia de NVIDIA:** el Anexo A del EULA del CUDA Toolkit lista como redistribuibles en Windows `cudart.dll`
  (CUDA Runtime) y `cublas.dll`, `cublasLt.dll` (cuBLAS y cuBLASLt). Condiciones (§1.1.2): ir dentro de una
  aplicación con "funcionalidad adicional material" (TextFlow lo es), que solo la use esa aplicación, y términos de
  distribución coherentes con el EULA. **Se pueden redistribuir**; el paquete lleva el aviso de licencia de NVIDIA.
- **Hugging Face** responde con un 302 a su CDN con URL **firmada y con caducidad**; admite `Range`. Para reanudar hay
  que volver a pedir la URL de HF (no guardar la del CDN).
- `release.yml` solo se dispara con tags `v*.*.*`: un tag de otra forma no genera instalador.

### Decisión
1. **Nada de voz en el instalador** salvo el runtime de CPU de Whisper.net (~1 MB) y el código del worker. Quien no
   usa el dictado no descarga nada.
2. **Modelo:** se descarga de Hugging Face con la **revisión fija** (no `main`) y el SHA-256 de arriba escritos en el
   código, a `%LOCALAPPDATA%\TextFlow\models\ggml-large-v3-turbo-q5_0.bin`.
3. **Paquete GPU:** **un zip propio** (`textflow-voz-cuda12-1.zip`, ≈ 620 MB) con las DLL de whisper.cpp CUDA de
   Whisper.net 1.9.1 + `cudart64_12`, `cublas64_12`, `cublasLt64_12` (12.4) + los avisos de licencia (MIT de
   Whisper.net/whisper.cpp y EULA de NVIDIA). Se publica como asset de un release propio del repo con tag
   **`voz-gpu-1`** (prerelease, sin instalador), y lo construye **GitHub Actions** (workflow manual) bajando el nupkg
   de NuGet y los zips oficiales de NVIDIA y verificando sus hashes: la red de este PC corta las subidas grandes.
   Se instala en `%LOCALAPPDATA%\TextFlow\voz\cuda12-1\`, fuera de la carpeta de Velopack (cada actualización de la
   app la sustituye entera y obligaría a bajarlo otra vez). El worker la pasa como `RuntimeOptions.LibraryPath`.
   - Se descarta que la app baje las tres piezas de NuGet y NVIDIA directamente: tres orígenes, tres formatos, 650 MB
     de archivos de los que se tiran cabeceras y `.lib`, y dependencia de URLs ajenas en cada instalación.
   - El número `1` es la versión del paquete, independiente de la de la app: las actualizaciones de TextFlow no lo
     vuelven a bajar mientras no cambie Whisper.net o CUDA.
4. **Cuándo:** al activar el dictado por primera vez (Ajustes → Voz), no al arrancar. Solo se ofrece el paquete GPU si
   hay una GPU NVIDIA con driver compatible con CUDA 12 (consulta en H1.4); sin ella, el dictado queda desactivado
   hasta el respaldo en CPU de H4 (ADR-0007; Parakeet, ≈ 490 MB, se distribuiría igual que el modelo).
5. **Descarga (H1.4):** a un `.part` con reanudación por `Range` y reintentos, verificación SHA-256 al terminar
   (incorrecto → se borra y se avisa), y cambio de nombre atómico al final. Comprobar antes ≈ 2 GB libres (zip +
   extracción + modelo). El paquete se extrae a una carpeta temporal y se renombra cuando todas las DLL están.
6. **Desinstalar** no borra `%LOCALAPPDATA%\TextFlow` (ADR-0004), así que los ≈ 1,7 GB de voz se quedan:
   Diagnóstico muestra el espacio usado y ofrece "Borrar archivos de voz".

### Pendiente de verificar en H1
- Que Velopack (`GithubSource`) ignora el release `voz-gpu-1` al buscar actualizaciones (no tiene `releases.*.json`
  y es prerelease). Si no, mover el paquete a un repo aparte (`TextFlow-assets`).
- Que `ggml-cuda-whisper.dll` encuentra `cudart`/`cublas` en su propia carpeta: si no, el worker llama a
  `AddDllDirectory` con la carpeta del paquete antes de cargar el runtime. **Resuelto en H1.3:** `cublas` sí se
  encuentra; `cudart` no (ver la sección H1.3).

## Implementación del worker (V0.2 H1.1, 2026-10-17)

- **Proyectos:** mensajes y framing en `TextFlow.Contracts/Voice` (4 bytes de longitud + JSON UTF-8, tope 1 MB);
  `VoiceWorkerSupervisor` en `TextFlow.Core/Voice`; lanzador y Job Object en `TextFlow.Infrastructure/Voice`;
  `TextFlow.VoiceWorker.exe` (WinExe x64, solo referencia Contracts) junto a `TextFlow.exe`.
- **Protocolo v1:** `hello` → `ready{versión, pid}`, `ping`/`pong`, `shutdown`. Solo control y texto: el audio se
  captura dentro del worker (H1.2).
- **Pipe:** lo crea la app, nombre `TextFlow.Voice.<pid>.<guid>` nuevo por cada worker y `PipeOptions.CurrentUserOnly`
  en los dos lados. El worker se cierra si el pipe se cierra o si el proceso de TextFlow (`--parent`) desaparece.
- **Job Object** con `KILL_ON_JOB_CLOSE`: si TextFlow muere de golpe, Windows mata el worker (y libera la VRAM).
  Windows da código de salida 0 a los procesos que mata así.
- **Vigilancia:** ping cada 10 s, sin respuesta en 3 s → colgado, se mata. Reinicio tras 0,5 s, doblando con cada caída
  en 2 min hasta 30 s; 5 caídas en 2 min → `Faulted` hasta un nuevo `Start()`. Una petición en curso cuando el worker
  cae falla con `VoiceWorkerUnavailableException`, nunca se queda colgada.
- **Diagnóstico:** `VoiceWorkerStarted`, `VoiceWorkerExited` (motivo, código, tiempo arriba) y `VoiceWorkerFaulted`.
- **Medido** (`TextFlow.Spikes voice-worker 3`, PC de desarrollo): arranque hasta `ready` 93–146 ms, ping 1–3 ms,
  3 muertes → 3 reinicios.
- **Distribución:** la app referencia el proyecto (lo copia junto a `TextFlow.exe` en cada build);
  `publish-release.ps1` lo publica además self-contained en la misma carpeta, con el runtime del paquete
  (el PC de trabajo no tiene .NET instalado).
- **Apagado por defecto:** solo arranca con `"VoiceEnabled": true` en `settings.json` hasta que el dictado sirva (H2).

## Captura del micrófono (V0.2 H1.2, 2026-10-17)

- **NAudio.Wasapi 2.4.0** (MIT) en el worker: `WasapiCapture` en modo compartido pidiendo 16 kHz mono PCM16. Windows
  remuestrea y mezcla a mono por su cuenta (`AUTOCONVERTPCM`): no hay resampler propio. Se descartaron NAudio 3.x
  (reescritura de ago-2026; la 3.2.0 tenía 2 días) y WASAPI a mano por COM (≈ 400 líneas de interop propio).
- **Protocolo v2:** peticiones con `id` y respuesta con el mismo `id` (`ErrorReply` con un código si falla).
  `ListMicrophones`, `StartCapture{micId?}`, `StopCapture` → duración, nivel pico, recortado. Sin `micId` se usa el
  micrófono predeterminado de Windows en ese momento.
- **El audio no sale del worker** ni toca el disco: buffer en memoria de 60 s como máximo (≈ 1,9 MB). Se pone a cero al
  parar mientras nadie lo consuma; en H2 se transcribe primero (ADR-0006).
- **Errores:** sin micrófono, micrófono no encontrado, acceso denegado por la privacidad de Windows, no grabando.
  Cualquier otra excepción de NAudio o del driver responde `Unknown` a esa petición: nunca tumba el worker.
  Si el micrófono se pierde a mitad (desenchufado), `StopCapture` devuelve el audio hasta ese momento con
  `MicrophoneLost`. Un `StartCapture` con otra captura abierta (un arranque que la app dio por perdido) la cierra y
  descarta: el micrófono nunca queda abierto por una respuesta perdida. Las tramas se escriben siempre enteras (sin
  cancelar a mitad), para no desincronizar el pipe.
- **Medido** (PC de desarrollo, micrófono del portátil Intel Smart Sound, `TextFlow.Spikes voice-capture 5`):
  5 s → 5008 ms y 4998 ms capturados (la diferencia con el reloj, −26 a −41 ms, es el arranque del stream). Abrir el
  micrófono: **≈ 110 ms**; la **primera vez** en cada worker **400–570 ms** (Windows inicializa su motor de audio).
- **Para H2:** el micro se abre al empezar el dictado (Windows solo muestra el icono de micrófono mientras graba). Con
  el atajo empezando a 150 ms, el audio arranca unos 260 ms después de pulsar la tecla, y la primera vez casi 0,7 s.
  Opciones: abrir el micro al pulsar la tecla (antes de los 150 ms) y descartarlo si fue un toque, y calentar el motor
  de audio cuando arranca el worker sin abrir el micrófono. Medirlo con la tecla real.
- La elección de micrófono en la configuración llega con la pantalla de voz (H2); el protocolo ya la admite.

## Carga y liberación del modelo (V0.2 H1.3, 2026-10-18)

- **Protocolo v3:** `LoadModel{modelPath, gpuPackPath?}` → `ModelLoaded{runtime, loadMs, warmupMs, wasLoaded}`.
  Errores nuevos: `ModelNotFound`, `ModelLoadFailed`, `NoGpu` (solo cargó el runtime de CPU: turbo en CPU tarda
  23–27 s por frase, así que se rechaza). La carga incluye un **calentamiento** (1 s de silencio) para que el primer
  dictado no pague el arranque de los kernels de CUDA.
- **El worker sigue respondiendo mientras carga:** la carga va en el pool de hilos y responde al terminar; los pings
  y la captura siguen. Dos peticiones a la vez esperan la misma carga. Las tramas se escriben de una en una (lock).
- **Cuándo se carga:** al primer dictado (`VoiceWorkerSupervisor.EnsureModelAsync`), no al arrancar la app. H2 puede
  pedirla al pulsar la tecla, en paralelo con la apertura del micrófono.
- **Liberar = cambiar de worker.** Medido (RTX 2050, base 682 MiB): con el modelo cargado 1 303 MiB; liberando
  `WhisperFactory` dentro del proceso quedan **755 MiB** (el contexto de CUDA, ~70 MiB) y ~710 MB de RAM privada.
  No vuelve a la base, así que tras **15 min sin uso** (`ModelIdleTimeout`) el supervisor cierra el worker con
  `shutdown` y arranca otro limpio al momento, sin modelo. No cuenta como caída (`VoiceWorkerExited` con motivo
  `ModelIdle`). El tiempo cuenta desde la última petición terminada y nunca corre con una petición en curso.
- **Recarga transparente:** una petición que llega mientras el worker se está cambiando (o justo tras `Start()`)
  espera al nuevo en lugar de fallar; el siguiente `EnsureModelAsync` vuelve a cargar.
- **Carga colgada:** sin respuesta en 60 s (`ModelLoadTimeout`) se mata el worker como colgado y se reinicia.
- **Diagnóstico:** `VoiceModelLoaded` (runtime, carga, calentamiento) y `VoiceModelLoadFailed` (código o timeout).
- **Cómo encuentra Whisper.net el paquete GPU** (pendiente de H0.5, probado): `RuntimeOptions.LibraryPath` se toma
  como un *archivo*: Whisper.net busca `runtimes\cuda12\win-x64\` en la carpeta de ese archivo. Y comprueba CUDA
  cargando `cudart64_12` **por nombre**, que Windows no busca en esa carpeta: sin más, cae a CPU en silencio. El worker
  carga antes `cudart64_12.dll` por ruta completa y pone `LibraryPath = <paquete>\whisper.dll`. `cublas64_12` y
  `cublasLt64_12` sí se encuentran junto a `ggml-cuda-whisper.dll`. Por tanto el **zip `voz-gpu-1` lleva todas las
  DLL en `runtimes\cuda12\win-x64\`** (las 5 de Whisper.net + `cudart`, `cublas`, `cublasLt`), más los avisos de
  licencia en la raíz.
- **El runtime se elige una vez por proceso** (la primera carga). Si se instala el paquete GPU con el worker ya
  arrancado (H1.4), hay que cambiar de worker antes de cargar.
- **Medido** (`TextFlow.Spikes voice-model 20`, PC de desarrollo, GPU libre): primera carga **2,6 s** desde el arranque
  del worker (carga 1,6 s con el disco frío + calentamiento 0,6 s); segunda petición 0 ms; liberación a los 21 s de
  inactividad con la VRAM **exactamente en la base** (690 → 1 310 → 690 MiB); recarga **1,2 s** (0,7 + 0,5).
- En el instalador solo va el runtime de CPU de Whisper.net (~2 MB por arquitectura en `runtimes\win-*`).
