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
  redistribuibles en el EULA del CUDA Toolkit) y desde dónde se descargan.
- Primer dictado tras arrancar el worker: ≈ 1,3 s extra (carga + calentamiento). Opciones para H1.3: precargar al
  iniciar la app o al pulsar el atajo; la liberación por inactividad la vuelve a pagar.
- `TextFlow.VoiceWorker` es x64 y con .NET 10, como el spike; la app no referencia Whisper.net.
- Volver a medir con el paquete real de H1 (audio de WASAPI en vez de WAV) para confirmar la latencia de punta a punta.
