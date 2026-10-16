"""V0.2 H0.3 spike: Whisper.net in-process (spikes/TextFlow.VoiceSpike) vs whisper-server, same turbo model and prompt.

Usage:  python spikes/asr/spike_h03.py [--recordings models/recordings-v2] [--cpu-limit 8] [--skip-server]

Per engine: WER (total / silencio / ruido), load time, first decode, warm latency p50/p95, peak VRAM (whole GPU),
process memory, and the CPU fallback (no GPU) on the first --cpu-limit phrases.
Build the spike first: dotnet build -c Release spikes/TextFlow.VoiceSpike
Only numbers are printed; transcripts stay in the JSON files under --work (the recordings are the user's voice).
"""

import argparse
import json
import os
import subprocess
import sys
import tempfile
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import bench  # noqa: E402  (same scoring, prompt and VRAM sampler as H0.2)

ROOT = bench.ROOT
SPIKE = ROOT / "spikes/TextFlow.VoiceSpike/bin/Release/net10.0/win-x64/TextFlow.VoiceSpike.exe"
MODEL = bench.MODELS / "ggml-large-v3-turbo-q5_0.bin"


def gpu_busy():
    """GPU utilisation before measuring: a game or another model on the GPU makes every latency meaningless."""
    out = subprocess.run(["nvidia-smi", "--query-gpu=utilization.gpu,memory.used", "--format=csv,noheader,nounits"],
                         capture_output=True, text=True).stdout.strip()
    util, used = (int(v) for v in out.split(","))
    return util, used


def run_spike(args, out, runtime, limit=0, env=None):
    cmd = [str(SPIKE), "--model", str(MODEL), "--corpus", args.corpus, "--recordings", args.recordings,
           "--out", str(out), "--runtime", runtime, "--limit", str(limit)]
    with bench.VramSampler() as vram:
        start = time.perf_counter()
        proc = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", env=env)
        wall = time.perf_counter() - start
    if proc.returncode != 0:
        raise RuntimeError(f"spike failed ({proc.returncode}): {proc.stderr[-600:]}")
    report = json.loads(Path(out).read_text(encoding="utf-8-sig"))  # .NET writes a BOM
    report["peakVramMb"] = vram.peak
    report["wall"] = wall
    return report


def latencies(values):
    return bench.percentile(values, 50), bench.percentile(values, 95)


def line(name, items, hyps, lat, extra):
    total, _folded, by_cond, _ = bench.score(items, hyps)
    p50, p95 = latencies(lat)
    conds = " ".join(f"{c} {v:.1%}" for c, v in sorted(by_cond.items()))
    print(f"{name:<34} WER {total:5.1%} ({conds})  caliente p50 {p50:.2f} s / p95 {p95:.2f} s  {extra}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--recordings", default=str(bench.MODELS / "recordings-v2"))
    parser.add_argument("--corpus", default=str(Path(__file__).with_name("corpus.tsv")))
    parser.add_argument("--work", default=tempfile.gettempdir())
    parser.add_argument("--cpu-limit", type=int, default=8, help="phrases for the no-GPU runs (turbo on CPU is slow)")
    parser.add_argument("--skip-server", action="store_true")
    args = parser.parse_args()

    if not SPIKE.exists():
        raise SystemExit(f"Build the spike first: {SPIKE}")
    util, used = gpu_busy()
    print(f"GPU antes de medir: {util} % de uso, {used} MiB ocupados")
    if util > 20:
        raise SystemExit("La GPU está ocupada (¿un juego u otro modelo?). Ciérralo y repite: las latencias no valdrían.")

    items = bench.load_items(args.corpus, args.recordings)
    wavs = [w for _, _, w, _ in items]
    print(f"{len(items)} grabaciones\n")
    work = Path(args.work)

    # 1. Whisper.net in-process, CUDA 12 runtime (what TextFlow.VoiceWorker would embed).
    gpu = run_spike(args, work / "h03-whispernet-cuda.json", "cuda12")
    by_id = {i["id"]: i for i in gpu["items"]}
    line("Whisper.net CUDA 12 (en proceso)", items, [by_id[i[0]]["text"] for i in items],
         [by_id[i[0]]["ms"] / 1000 for i in items],
         f"carga {gpu['loadMs'] / 1000:.2f} s, 1.ª {gpu['firstMs'] / 1000:.2f} s, listo {gpu['readyMs'] / 1000:.2f} s, "
         f"VRAM pico {gpu['peakVramMb']} MiB, RAM privada {gpu['privateMb']:.0f} MB")

    # 2. whisper-server, model loaded once (H0.2 reference: 0.39 s p50).
    if not args.skip_server:
        with bench.VramSampler() as vram:
            hyps, lat = bench.server_latencies(wavs)
        line("whisper-server CUDA (proceso aparte)", items, hyps, lat, f"VRAM pico {vram.peak} MiB")

    # 3. No GPU: CUDA hidden from the process. Whisper.net must fall back to its CPU runtime by itself.
    subset = items[: args.cpu_limit]
    no_gpu = dict(os.environ, CUDA_VISIBLE_DEVICES="-1")
    for runtime in ("auto", "cpu"):
        cpu = run_spike(args, work / f"h03-whispernet-nogpu-{runtime}.json", runtime, args.cpu_limit, no_gpu)
        by_id = {i["id"]: i for i in cpu["items"]}
        line(f"Whisper.net sin GPU (--runtime {runtime})", subset, [by_id[i[0]]["text"] for i in subset],
             [by_id[i[0]]["ms"] / 1000 for i in subset],
             f"runtime cargado {cpu['runtime']}, carga {cpu['loadMs'] / 1000:.2f} s, {len(subset)} frases")


if __name__ == "__main__":
    main()
