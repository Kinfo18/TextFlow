"""ASR benchmark (S5, extended for V0.2 H0.2): whisper.cpp vs Parakeet (sherpa-onnx) on the user's own recordings.

Usage:  python spikes/asr/bench.py [--recordings models/recordings-v2] [--engines a,b] [--server]

Metrics per engine:
  - WER (lowercase, no punctuation) and WER without accents (diacritics folded)
  - cold latency: one process per phrase (model load + decode), p50/p95
  - warm latency: one process for all phrases; (batch time - cold load) / n
  - peak VRAM (nvidia-smi, sampled every 100 ms)
  - with --server: real warm latency per phrase through whisper-server (model loaded once), p50/p95
  - WER per recording condition (silencio / ruido) and the phrases with most errors (id only)
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
WHISPER_SERVER = MODELS / "whisper-cuda/Release/whisper-server.exe"
WHISPER_CPU = MODELS / "whisper-cpu/Release/whisper-cli.exe"
SHERPA = MODELS / "sherpa-onnx-v1.13.8-win-x64-shared-MT-Release-no-tts/bin/sherpa-onnx-offline.exe"
PARAKEET = MODELS / "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8"
# H0.2: the wider prompt took silent-room WER from 3.0 % to 2.4 % (S5 used "Rider, pickup, dropoff, pedido, dashboard, ticket, Jira.").
PROMPT = ("Soporte de pedidos a domicilio: rider, pickup, dropoff, pedido, orden, reembolso, cupón, stock, standup, "
          "dashboard, ticket, Jira, Slack.")


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


def server_latencies(wavs, model="ggml-large-v3-turbo-q5_0.bin", port=8178):
    """Warm latency per phrase as the app will see it: whisper-server keeps the model loaded between requests."""
    import urllib.request
    import uuid

    proc = subprocess.Popen([str(WHISPER_SERVER), "-m", str(MODELS / model), "-l", "es", "--prompt", PROMPT, "-t", "6",
                             "--port", str(port), "-nt"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        url = f"http://127.0.0.1:{port}/inference"
        deadline = time.time() + 60
        while time.time() < deadline:
            try:
                urllib.request.urlopen(f"http://127.0.0.1:{port}/", timeout=1)
                break
            except OSError:
                time.sleep(0.5)

        def post(wav):
            boundary = uuid.uuid4().hex
            body = (f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"a.wav\"\r\n"
                    f"Content-Type: audio/wav\r\n\r\n").encode() + wav.read_bytes() + (
                    f"\r\n--{boundary}\r\nContent-Disposition: form-data; name=\"response_format\"\r\n\r\njson"
                    f"\r\n--{boundary}--\r\n").encode()
            req = urllib.request.Request(url, data=body, headers={"Content-Type": f"multipart/form-data; boundary={boundary}"})
            start = time.perf_counter()
            text = json.loads(urllib.request.urlopen(req, timeout=60).read().decode("utf-8"))["text"]
            return text, time.perf_counter() - start

        post(wavs[0])  # first request warms CUDA kernels
        results = [post(w) for w in wavs]
        return [t for t, _ in results], [s for _, s in results]
    finally:
        proc.kill()


def score(items, hyps):
    """Total WER, WER without accents, WER per condition and the per-phrase error counts."""
    per_phrase, by_cond = [], {}
    errors = total = errors_folded = 0
    for (rid, cond, _wav, ref), hyp in zip(items, hyps):
        e, n = wer(normalize(ref), normalize(hyp))
        ef, _ = wer(normalize(ref, True), normalize(hyp, True))
        errors, total, errors_folded = errors + e, total + n, errors_folded + ef
        ce, cn = by_cond.get(cond, (0, 0))
        by_cond[cond] = (ce + e, cn + n)
        per_phrase.append((rid, e, n))
    return errors / total, errors_folded / total, {c: e / n for c, (e, n) in by_cond.items()}, per_phrase


def load_items(corpus, recordings):
    lines = Path(corpus).read_text(encoding="utf-8").splitlines()
    header = lines[0].split("\t")
    items = []
    for line in lines[1:]:
        if not line.strip():
            continue
        cols = dict(zip(header, line.split("\t")))
        wav = Path(recordings) / f"{cols['id']}.wav"
        if wav.exists():
            items.append((cols["id"], cols.get("cond", "silencio"), wav, cols["reference"]))
    return items


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--recordings", default=str(MODELS / "recordings-v2"))
    parser.add_argument("--corpus", default=str(Path(__file__).with_name("corpus.tsv")))
    parser.add_argument("--engines", default=",".join(ENGINES))
    parser.add_argument("--server", action="store_true", help="also measure turbo through whisper-server (real warm latency)")
    parser.add_argument("--prompt", help="vocabulary prompt for whisper (default: the S5 one)")
    parser.add_argument("--show", action="store_true", help="print reference vs transcript for phrases with errors")
    args = parser.parse_args()

    if args.prompt:
        global PROMPT
        PROMPT = args.prompt
    items = load_items(args.corpus, args.recordings)
    if not items:
        raise SystemExit(f"No recordings found in {args.recordings}. Run spikes/asr/record-corpus.ps1 first.")
    wavs = [w for _, _, w, _ in items]
    audio_seconds = sum((w.stat().st_size - 44) / 32000 for w in wavs)
    conds = sorted({c for _, c, _, _ in items})
    print(f"{len(items)} grabaciones ({', '.join(f'{c}: {sum(1 for i in items if i[1] == c)}' for c in conds)}), "
          f"{audio_seconds:.1f} s de audio\n")

    results, details = [], {}
    for name in [e for e in args.engines.split(",") if e]:
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
        w, wf, by_cond, per_phrase = score(items, hyps)
        results.append((name, w, wf, by_cond, statistics.median(cold), percentile(cold, 95), warm, warm, vram.peak))
        details[name] = (per_phrase, hyps)

    if args.server:
        with VramSampler() as vram:
            hyps, lat = server_latencies(wavs)
        w, wf, by_cond, per_phrase = score(items, hyps)
        results.append(("turbo-server (caliente)", w, wf, by_cond, float("nan"), float("nan"),
                        statistics.median(lat), percentile(lat, 95), vram.peak))
        details["turbo-server (caliente)"] = (per_phrase, hyps)

    cond_cols = "".join(f"{c[:8]:>9}" for c in conds)
    print(f"{'motor':<26}{'WER':>7}{'WER*':>7}{cond_cols}{'frío p50':>10}{'frío p95':>10}{'cal p50':>9}{'cal p95':>9}{'VRAM':>10}")
    for name, w, wf, by_cond, p50, p95, wp50, wp95, peak in results:
        cells = "".join(f"{by_cond.get(c, 0):>9.1%}" for c in conds)
        cold = f"{p50:>9.2f}s{p95:>9.2f}s" if p50 == p50 else f"{'—':>10}{'—':>10}"  # NaN: not measured
        print(f"{name:<26}{w:>7.1%}{wf:>7.1%}{cells}{cold}{wp50:>8.2f}s{wp95:>8.2f}s{peak:>6} MiB")
    print("\nWER* = sin tildes. frío = proceso nuevo por frase. cal = caliente (con --server, medido frase a frase; "
          "si no, estimado como media).")

    for name, (per_phrase, hyps) in details.items():
        worst = sorted((p for p in per_phrase if p[1]), key=lambda p: -p[1] / p[2])[:8]
        if worst:
            print(f"\n{name}: frases con errores → " + ", ".join(f"{rid} ({e}/{n})" for rid, e, n in worst))
        if args.show:
            refs = {rid: ref for rid, _, _, ref in items}
            for (rid, e, _), hyp in zip(per_phrase, hyps):
                if e:
                    print(f"  {rid}  ref: {refs[rid]}\n      asr: {hyp}")


if __name__ == "__main__":
    main()
