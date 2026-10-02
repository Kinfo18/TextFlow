# ADR-0005 — Candidatos ASR para benchmark

- Estado: Aceptado (2026-10-01)
- Amplía: spec V2 §6, §13

## Decisión
Comparar en hardware de referencia (i5-12450H, 16 GB, RTX 2050 4 GB):
- whisper.cpp `large-v3-turbo` q5_0 y `small` (CUDA).
- NVIDIA Parakeet TDT 0.6B v3 (multilingüe, incluye español) vía sherpa-onnx.

Idioma: español con términos en inglés (prompt/vocabulario inicial).

## Motivo
whisper.cpp no hace streaming nativo: los parciales exigen re-decodificar una ventana deslizante, costoso con 4 GB de VRAM. Parakeet es mucho más rápido; decidir con datos (WER en español, latencias cold/warm p50/p95, VRAM).

## Riesgo
ASR + LLM local no caben juntos cómodamente en 4 GB: el LLM de V0.3 deberá correr en CPU, en un modelo ≤ 3B cuantizado, o en un turno separado tras liberar el ASR.
