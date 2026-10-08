# ADR-0007 — Motor ASR: whisper.cpp large-v3-turbo q5_0 en CUDA

- Estado: Aceptado (2026-10-02)
- Resuelve: ADR-0005 (benchmark S5)

## Datos (S5, hardware de referencia: i5-12450H, RTX 2050 4 GB)
Corpus: 12 frases del usuario en español con términos de su trabajo y en inglés (`spikes/asr/corpus.tsv`),
grabadas con el micrófono del portátil. Válidas para la medición: 9 (03–11); 01, 02 y 12 salieron
desincronizadas y se descartaron. WER normalizado (minúsculas, sin puntuación, cifras = palabras).

| Motor | WER | WER sin tildes | Frío p50 | Caliente/frase | VRAM extra |
|---|---|---|---|---|---|
| **whisper.cpp large-v3-turbo q5_0, CUDA** | **0,9 %** | 0,9 % | 1,5 s | ≈ 0,55 s | ≈ 950 MiB |
| whisper.cpp small q5_1, CUDA | 5,5 % | 3,6 % | 1,1 s | 0,3 s | ≈ 620 MiB |
| whisper.cpp small q5_1, CPU | 6,4 % | 4,5 % | 4,7 s | 4,5 s | — |
| Parakeet TDT 0.6B v3 int8 (sherpa-onnx), CPU | 6,4 % | 6,4 % | 3,2 s | 0,6 s | — |

"Caliente" turbo: 0,52–0,59 s en dos rondas completas; una tercera dio 1,7 s por contención de GPU (a vigilar).

## Decisión
1. **Motor principal: whisper.cpp large-v3-turbo q5_0 en CUDA**, con prompt de vocabulario del usuario
   (rider, pickup, dropoff, Jira…). Mejor precisión con diferencia; Parakeet confunde justo ese vocabulario
   ("render" por rider, "Gira" por Jira, "de morada") y no admite prompt.
2. **Modelo residente**: cargarlo al iniciar (o al primer dictado) y mantenerlo en memoria; el arranque en frío
   (1,5 s) no puede pagarse en cada dictado.
3. **Fallback sin GPU / VRAM ocupada**: Parakeet v3 int8 en CPU (0,6 s caliente, 6 % WER). whisper small en CPU
   queda descartado por latencia (4,5 s).

## Consecuencias
- ~950 MiB de VRAM mientras TextFlow está activo; el LLM local de V0.3 no cabe a la vez en 4 GB (ver ADR-0005):
  CPU, modelo ≤ 3B cuantizado, o descargar el ASR antes del turno del LLM.
- Corpus pequeño (9 frases, 1 hablante): repetir el benchmark con más frases y ruido de oficina antes de V0.2.
- Grabación del corpus: ejecutar `spikes/asr/record-corpus.ps1` en una terminal propia, no vía `!` de Claude Code
  (sin salida en vivo el usuario pierde la sincronía con "HABLA AHORA").
- PC de trabajo (2026-10-08, H0.1 de V0.2): mismo hardware que el de referencia (i5-12450H, 16 GB, RTX 2050 4 GB), así que allí también va el motor principal en CUDA.
