# ADR-0007 — Motor ASR: whisper.cpp large-v3-turbo q5_0 en CUDA

- Estado: Aceptado (2026-10-02)
- Resuelve: ADR-0005 (benchmark S5)
- Revisado: 2026-10-13 con el benchmark ampliado (V0.2 H0.2): **confirmado**

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
- Distribución del modelo (V0.2 H0.5): descarga desde Hugging Face con revisión fija y SHA-256, ver ADR-0009.
- PC de trabajo (2026-10-08, H0.1 de V0.2): mismo hardware que el de referencia (i5-12450H, 16 GB, RTX 2050 4 GB), así que allí también va el motor principal en CUDA.

## Benchmark ampliado (V0.2 H0.2, 2026-10-13)

Corpus: 55 grabaciones del usuario (`spikes/asr/corpus.tsv`, `models/recordings-v2`, fuera de git) con el micrófono
del portátil: 35 en silencio (12 de S5 + 23 frases nuevas de soporte, 3 de ellas comandos cortos tipo «Envía saludo»)
y 20 repetidas con ruido de fondo (TV / ambiente de oficina). 290 s de audio. Grabadora nueva con Enter para empezar
y terminar (las de S5 se cortaban por usar duración fija). `bench.py --server` mide la latencia caliente real frase a
frase con `whisper-server` (modelo cargado una vez, como lo usará la app).

| Motor | WER | Silencio | Ruido | Frío p50 | Caliente p50 / p95 | VRAM pico (total GPU) |
|---|---|---|---|---|---|---|
| **turbo q5_0 CUDA, prompt ampliado** | **4,2 %** | **2,4 %** | 7,3 % | 1,34 s | 0,39 s / 0,42 s (server) | ≈ 2,0 GiB |
| turbo q5_0 CUDA, prompt de S5 | 4,5 % | 3,0 % | 7,3 % | 1,34 s | 0,44 s | ≈ 2,0 GiB |
| small q5_1 CUDA | 15,1 % | 10,8 % | 22,8 % | 0,95 s | 0,25 s | ≈ 1,7 GiB |
| Parakeet v3 int8 CPU | 13,3 % | 10,5 % | 18,4 % | 2,68 s | 0,56 s | — |

Prompt ampliado: «Soporte de pedidos a domicilio: rider, pickup, dropoff, pedido, orden, reembolso, cupón, stock,
standup, dashboard, ticket, Jira, Slack.»

Conclusiones:
- **Se mantiene turbo en CUDA.** En silencio cumple el criterio de V0.2 (WER ≤ 3 %); con ruido llega a 7,3 %. Parte del
  error es de métrica («stand up» por standup, «3 y 4» por «tres y cuarto» sí es fallo real).
- **El prompt de vocabulario importa** (3,0 → 2,4 % en silencio): H2.4 (vocabulario editable) es necesario, no opcional.
- **No filtrar ruido antes del ASR:** un `afftdn` de ffmpeg mejoró el silencio (1,9 %) pero empeoró el ruido (9,2 %).
  Whisper es más robusto con el audio original.
- **Respaldo en CPU más débil de lo medido en S5:** Parakeet pasa de 6 % a 13 % con el corpus amplio; sigue siendo el
  respaldo (whisper small en CPU es peor y lento), pero hay que avisar en Diagnóstico de que la calidad baja.
- **Comandos cortos frágiles con ruido:** «Envía saludo» → «Te envía saludos», «Envía despedida» → «Vía despedida».
  Refuerza H6: atajo propio para comandos, coincidencia tolerante y nunca adivinar.
- **Alucinaciones:** en una grabación vieja mal cortada (S5 #12), turbo inventó un párrafo en inglés. VAD (H4.1) y
  descartar salidas en otro idioma o repetitivas son obligatorios.
- **Micrófono:** el usuario usa el del portátil; la app no debe depender de un micrófono concreto (elegir dispositivo,
  H1.2) y el benchmark ya está hecho con el peor caso realista.
