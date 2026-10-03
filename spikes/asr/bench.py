"""S5 ASR benchmark: whisper.cpp vs Parakeet (sherpa-onnx) on the user's own recordings.

Usage:  python spikes/asr/bench.py [--recordings models/recordings] [--runs 3]

Metrics per engine:
  - WER (lowercase, no punctuation) and WER without accents (diacritics folded)
  - cold latency: one process per phrase (model load + decode), p50/p95
  - warm latency: one process for all phrases; (batch time - cold load) / n
  - peak VRAM (nvidia-smi, sampled every 100 ms)
Transcripts are printed only as WER numbers; the recordings are the user's voice and stay in models/.
"""

import argparse
import json
import re
import statistics
import subprocess
import threading
import time
import unicodedata
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MODELS = ROOT / "models"
WHISPER_CUDA = MODELS / "whisper-cuda/Release/whisper-cli.exe"
WHISPER_CPU = MODELS / "whisper-cpu/Release/whisper-cli.exe"
SHERPA = MODELS / "sherpa-onnx-v1.13.8-win-x64-shared-MT-Release-no-tts/bin/sherpa-onnx-offline.exe"
PARAKEET = MODELS / "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8"
PROMPT = "Rider, pickup, dropoff, pedido, dashboard, ticket, Jira."


def whisper(exe, model, gpu=True):
    def command(wavs):
        args = [str(exe), "-m", str(MODELS / model), "-l", "es", "-nt", "-np", "--prompt", PROMPT, "-t", "6"]
        if not gpu:
            args.append("-ng")
        return args + [str(w) for w in wavs]

    def parse(stdout, _stderr, count):
        lines = [l.strip() for l in stdout.splitlines() if l.strip()]
        return lines[-count:] if len(lines) >= count else lines + [""] * (count - len(lines))

    return command, parse


def parakeet():
    def command(wavs):
        return [
            str(SHERPA),
            f"--encoder={PARAKEET / 'encoder.int8.onnx'}",
            f"--decoder={PARAKEET / 'decoder.int8.onnx'}",
            f"--joiner={PARAKEET / 'joiner.int8.onnx'}",
            f"--tokens={PARAKEET / 'tokens.txt'}",
            "--model-type=nemo_transducer",
            "--num-threads=6",
        ] + [str(w) for w in wavs]

    def parse(stdout, stderr, count):
        output = stdout + "\n" + stderr
        texts = [json.loads(m)["text"] for m in re.findall(r"^\{.*\}$", output, re.MULTILINE)]
        return texts + [""] * (count - len(texts))

    return command, parse


ENGINES = {
    "whisper-turbo-q5-cuda": whisper(WHISPER_CUDA, "ggml-large-v3-turbo-q5_0.bin"),
    "whisper-small-q5-cuda": whisper(WHISPER_CUDA, "ggml-small-q5_1.bin"),
    "whisper-small-q5-cpu": whisper(WHISPER_CPU, "ggml-small-q5_1.bin", gpu=False),
    "parakeet-v3-int8-cpu": parakeet(),
}


NUMBERS = {
    "0": "cero", "1": "uno", "2": "dos", "3": "tres", "4": "cuatro", "5": "cinco", "6": "seis", "7": "siete",
    "8": "ocho", "9": "nueve", "10": "diez", "15": "quince", "20": "veinte", "30": "treinta",
}


def normalize(text, fold_accents=False):
    """Lowercase, no punctuation; digits spelled out so "10 minutos" == "diez minutos" (both fine for dictation)."""
    text = text.lower()
    if fold_accents:
        text = "".join(c for c in unicodedata.normalize("NFD", text) if unicodedata.category(c) != "Mn")
    return [NUMBERS.get(w, w) for w in re.sub(r"[^\w\s]", " ", text).split()]


def wer(reference, hypothesis):
    r, h = reference, hypothesis
    d = list(range(len(h) + 1))
    for i in range(1, len(r) + 1):
        prev, d[0] = d[0], i
        for j in range(1, len(h) + 1):
            cur = min(d[j] + 1, d[j - 1] + 1, prev + (r[i - 1] != h[j - 1]))
            prev, d[j] = d[j], cur
    return d[len(h)], len(r)


class VramSampler:
    def __init__(self):
        self.peak = 0
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._run, daemon=True)

    def _run(self):
        while not self._stop.is_set():
            try:
                out = subprocess.run(["nvidia-smi", "--query-gpu=memory.used", "--format=csv,noheader,nounits"],
                                     capture_output=True, text=True, timeout=2).stdout
                self.peak = max(self.peak, int(out.strip().splitlines()[0]))
            except (OSError, ValueError, subprocess.SubprocessError):
                pass
            self._stop.wait(0.1)

    def __enter__(self):
        self._thread.start()
        return self

    def __exit__(self, *_):
        self._stop.set()
        self._thread.join()


def run(cmd):
    start = time.perf_counter()
    proc = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    elapsed = time.perf_counter() - start
    if proc.returncode != 0:
        raise RuntimeError(f"{Path(cmd[0]).name} failed ({proc.returncode}): {proc.stderr[-400:]}")
    return (proc.stdout, proc.stderr), elapsed


def percentile(values, p):
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, round(p / 100 * (len(ordered) - 1)))]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--recordings", default=str(MODELS / "recordings"))
    parser.add_argument("--corpus", default=str(Path(__file__).with_name("corpus.tsv")))
    parser.add_argument("--engines", default=",".join(ENGINES))
    args = parser.parse_args()

    rows = [line.split("\t", 1) for line in Path(args.corpus).read_text(encoding="utf-8").splitlines()[1:] if line.strip()]
    items = [(Path(args.recordings) / f"{rid}.wav", ref) for rid, ref in rows if (Path(args.recordings) / f"{rid}.wav").exists()]
    if not items:
        raise SystemExit(f"No recordings found in {args.recordings}. Run spikes/asr/record-corpus.ps1 first.")
    wavs = [w for w, _ in items]
    audio_seconds = sum((w.stat().st_size - 44) / 32000 for w in wavs)
    print(f"{len(items)} grabaciones, {audio_seconds:.1f} s de audio\n")

    results = []
    for name in args.engines.split(","):
        command, parse = ENGINES[name]
        cold = []
        with VramSampler() as vram:
            hyps = []
            for wav in wavs:
                out, elapsed = run(command([wav]))
                cold.append(elapsed)
                hyps.append(parse(*out, 1)[0])
            out, batch = run(command(wavs))
        # batch = load + Σ decode; min(cold) = load + one decode → the rest is n-1 warm decodes
        warm = max(0.0, (batch - min(cold)) / max(1, len(wavs) - 1))

        errors = total = errors_folded = 0
        for (wav, ref), hyp in zip(items, hyps):
            e, n = wer(normalize(ref), normalize(hyp))
            ef, _ = wer(normalize(ref, True), normalize(hyp, True))
            errors, total, errors_folded = errors + e, total + n, errors_folded + ef

        results.append((name, errors / total, errors_folded / total, statistics.median(cold), percentile(cold, 95), warm, vram.peak))

    print(f"{'motor':<24}{'WER':>7}{'WER*':>7}{'frío p50':>10}{'frío p95':>10}{'caliente':>10}{'VRAM pico':>11}")
    for name, w, wf, p50, p95, warm, peak in results:
        print(f"{name:<24}{w:>7.1%}{wf:>7.1%}{p50:>9.2f}s{p95:>9.2f}s{warm:>9.2f}s{peak:>8} MiB")
    print("\nWER* = sin tildes. frío = proceso nuevo por frase (carga + decodificación). caliente = por frase con el modelo ya cargado.")


if __name__ == "__main__":
    main()
