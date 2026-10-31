"""Summarize the opt-in desktop Play Mode soak, never a device FPS claim."""
from __future__ import annotations

import csv
import datetime as dt
import hashlib
import json
import math
import shutil
import statistics
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
REPORTS = ROOT / "Reports/PICO"


def distribution(values):
    values = sorted(float(value) for value in values if float(value) >= 0)
    if not values:
        return None
    return {"median": statistics.median(values),
            "p95": values[max(0, math.ceil(len(values) * .95) - 1)], "max": values[-1]}


def main():
    control = json.loads((REPORTS / "runtime-soak.json").read_text(encoding="utf-8-sig"))
    if control["status"] == "running":
        raise SystemExit("Runtime soak is still running; no final report was generated.")
    source = Path(control["logPath"])
    rows = list(csv.DictReader(source.open(encoding="utf-8-sig")))
    if not rows:
        raise SystemExit("No runtime samples.")
    shutil.copy2(source, REPORTS / "runtime-soak.samples.csv")
    metadata = Path(str(source) + ".metadata.json")
    if metadata.exists():
        shutil.copy2(metadata, REPORTS / "runtime-soak.settings.json")
    steady = [row for row in rows if float(row["seconds"]) >= 30]
    first = next((float(row["seconds"]) for row in rows if int(row["covered"]) == 52), None)
    coverage_losses = sum(int(row["covered"]) != 52 for row in rows
                          if first is not None and float(row["seconds"]) >= first)
    intervals = [float(b["seconds"]) - float(a["seconds"]) for a, b in zip(rows, rows[1:])]
    start = json.loads((REPORTS / "runtime-soak-start.json").read_text(encoding="utf-8-sig"))
    began = dt.datetime.fromisoformat(start["startedUtc"].replace("Z", "+00:00"))
    memory = []
    for line in (REPORTS / "runtime-process-memory.jsonl").read_text(encoding="utf-8-sig").splitlines():
        record = json.loads(line)
        record["seconds"] = (dt.datetime.fromisoformat(record["utc"].replace("Z", "+00:00")) - began).total_seconds()
        if 0 <= record["seconds"] <= float(control["seconds"]) + 15:
            memory.append(record)

    def private_median(low, high):
        data = [r["privateBytes"] for r in memory if low <= r["seconds"] <= high]
        return statistics.median(data) if data else None

    result = {
        "status": control["status"], "platform": "Windows Editor / normal Play Mode / desktop mono",
        "api": control["api"], "unity": control["unity"], "controller": control,
        "datasetSHA256": hashlib.sha256((ROOT / "Assets/StreamingAssets/GaussianSplatLOD/SUIT-20261027/lod-meta.json").read_bytes()).hexdigest(),
        "sampleCount": len(rows), "lastSampleSeconds": float(rows[-1]["seconds"]),
        "firstLoggedCoverageSeconds": first, "coverageLossSamplesAfterFirstCoverage": coverage_losses,
        "sampleSpacingSeconds": distribution(intervals),
        "steadySampledCPUFrameMs": distribution(r["cpuFrameMs"] for r in steady),
        "steadySampledGPUFrameMs": distribution(r["gpuFrameMs"] for r in steady),
        "steadySampledUploadSubmitMs": distribution(r["uploadSubmitMs"] for r in steady),
        "maxCPUDecodedReservedBytes": max(int(r["cpuCacheBytes"]) for r in rows),
        "maxGPUBufferEstimateBytes": max(int(r["gpuEstimatedBytes"]) for r in rows),
        "maxUnityAllocatedBytes": max(int(r["unityAllocatedBytes"]) for r in rows),
        "maxProcessPrivateBytes": max((r["privateBytes"] for r in memory), default=None),
        "privateMedianFirstMinuteBytes": private_median(0, 60),
        "privateMedianFiveToSixMinutesBytes": private_median(300, 360),
        "privateMedianLastMinuteBytes": private_median(1140, 1215),
        "finalMeanLoadMs": float(rows[-1]["meanLoadMs"]),
        "finalP95LoadMs": float(rows[-1]["p95LoadMs"]),
        "finalCacheHit": float(rows[-1]["cacheHit"]), "finalPreloadHit": float(rows[-1]["preloadHit"]),
        "finalDiskReads": int(rows[-1]["diskReads"]),
        "notes": "Timestamp route in the actual application player loop; XR initialization disabled. "
                 "CSV takes 10Hz snapshots, not every frame; reported maxima/P95 can miss spikes. "
                 "FrameTimingManager values are application-frame samples, not per-pass GPU measurements. "
                 "Unavailable values are -1 and excluded. OS page cache was not flushed. "
                 "External process memory is sampled every10s. No PICO/Android/device90Hz or leak-free claim. "
                 "Unity total allocation, plugin cache budgets, driver memory and process private bytes are different measures."
    }
    (REPORTS / "runtime-soak-analysis.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
