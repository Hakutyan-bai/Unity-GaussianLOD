"""Prepare public numeric v0.2 evidence; never copy campus geometry, images or raw logs.

Run from the development project root, or provide --reports and --dataset explicitly.
The output can be copied into Validation/v0.2 in the public source repository.
"""
from __future__ import annotations

import argparse
import csv
import datetime as dt
import hashlib
import json
import re
import statistics
from pathlib import Path


def read(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def pick(source, keys):
    return {key: source[key] for key in keys if key in source}


def test_summary(source):
    # MCP's wrapper is intentionally not copied: it can contain machine paths and traces.
    result = source.get("structuredContent", {}).get("result", {})
    data = result.get("data", {})
    summary = data.get("result", {}).get("summary")
    if data.get("status") != "succeeded" or not summary or summary["failed"]:
        raise ValueError("Expected a completed successful development-project test report.")
    return pick(summary, ("total", "passed", "failed", "skipped", "durationSeconds", "resultState"))


def write_json(path, value):
    text = json.dumps(value, ensure_ascii=False, indent=2) + "\n"
    # A fail-closed publication check. No anonymized machine paths are needed by these metrics.
    if re.search(r"(?<![A-Za-z])[A-Za-z]:[\\/]", text) or re.search(r"(?:file|jar)://", text):
        raise ValueError("Public numeric evidence unexpectedly contains an absolute local path.")
    path.write_text(text, encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reports", type=Path, default=Path("Reports/PICO"))
    parser.add_argument("--dataset", type=Path,
                        default=Path("Assets/StreamingAssets/GaussianSplatLOD/SUIT-20261027/lod-meta.json"))
    parser.add_argument("--output", type=Path, default=Path("Distribution/v0.2/evidence"))
    args = parser.parse_args()
    reports, dataset, output = args.reports.resolve(), args.dataset.resolve(), args.output.resolve()
    inputs = {
        "comparison": reports / "comparison.json",
        "io": reports / "io-microbenchmark.json",
        "runtime": reports / "runtime-soak-analysis.json",
        "editorTests": reports / "regression-tests-78.json",
        "playTests": reports / "playmode-tests-final.json",
        "quality": reports / "visual-fixture/comparison.json",
    }
    data = {name: read(path) for name, path in inputs.items()}
    metadata = read(dataset)
    metadata_hash = hashlib.sha256(dataset.read_bytes()).hexdigest()
    if metadata_hash != data["runtime"]["datasetSHA256"]:
        raise ValueError("Current dataset does not match the recorded runtime measurement.")
    if data["io"]["status"] != "passed" or data["runtime"]["status"] != "passed":
        raise ValueError("Expected completed IO and normal Play Mode reports.")
    root = dataset.parent
    model = {
        "name": "SUIT-20261027", "sourcePoints": metadata["counts"],
        "spatialNodes": len(metadata["tree"]), "plyFiles": len(metadata["files"]),
        "lodLevels": metadata["levels"], "shDegree": metadata["shDegree"],
        "maxFinestRangePoints": max(node["lods"][0]["count"] for node in metadata["tree"]),
        "plyBytes": sum((root / name).stat().st_size for name in metadata["files"]),
        "losslessCacheBytes": sum((root / (name + ".gsbin")).stat().st_size for name in metadata["files"]),
        "gpuRecordStrideBytes": 256, "metadataSHA256": metadata_hash,
        "sourceGeometryOrScreenshotsIncluded": False,
    }
    short = {
        "scope": data["comparison"]["scope"],
        "settings": {"unity": "2022.3.62f3c1", "device": "NVIDIA GeForce RTX 4050 Laptop GPU",
                     "platform": "Windows Editor", "pipeline": "URP14", "api": "Direct3D12",
                     "width": 1280, "height": 720, "trialsPerPath": 3, "secondsPerTrial": 30,
                     "maxVisibleSplats": 1000000, "cpuCacheMiB": 384, "gpuBudgetMiB": 640,
                     "maxUploadSplatsPerFrame": 16384},
        "caveats": data["comparison"]["caveats"],
        "results": data["comparison"]["results"],
    }
    io = {
        "scope": "File/CPU warm-page-cache microbenchmark; not rendering, upload, startup or device FPS.",
        "unity": data["io"]["unity"], "repeats": data["io"]["repeats"],
        "conditions": data["io"]["notes"], "selectedRanges": data["io"]["selected"],
        "measurements": data["io"]["measurements"], "medians": [],
        "versionCaveat": ".gsbin already existed in v0.1. These compare PLY and cache paths, not v0.1-cache versus v0.2-cache throughput.",
    }
    for index, selected in enumerate(io["selectedRanges"], 1):
        row = pick(selected, ("filename", "offset", "count", "lod"))
        for algorithm in ("original-sync-ply", "optimized-async-ply", "optimized-async-gsbin"):
            row[algorithm + "MedianMs"] = statistics.median(
                value["elapsedMs"] for value in io["measurements"]
                if value["range"] == index and value["algorithm"] == algorithm)
        row["cacheVersusOriginalPLYRatio"] = row["original-sync-plyMedianMs"] / row["optimized-async-gsbinMedianMs"]
        io["medians"].append(row)
    runtime = dict(data["runtime"])
    runtime["controller"] = pick(runtime["controller"], (
        "status", "seconds", "firstCoverageSeconds", "minCoveredNodes", "sourcePoints",
        "visibleSplats", "cpuBytes", "gpuBytes", "diskReads", "cacheReads", "error", "api", "unity", "notes"))
    # Read only aggregate working-set counters, never publish the raw process log or machine paths.
    start = read(reports / "runtime-soak-start.json")["startedUtc"]
    began = dt.datetime.fromisoformat(start.replace("Z", "+00:00"))
    memory = [json.loads(line) for line in (reports / "runtime-process-memory.jsonl").read_text(encoding="utf-8-sig").splitlines()]
    memory = [row for row in memory if 0 <= (
        dt.datetime.fromisoformat(row["utc"].replace("Z", "+00:00")) - began).total_seconds() <= runtime["controller"]["seconds"]]
    runtime["maxProcessWorkingSetBytes"] = max(row["workingSetBytes"] for row in memory)
    runtime["privateMedianFirstToLastGrowthBytes"] = runtime["privateMedianLastMinuteBytes"] - runtime["privateMedianFirstMinuteBytes"]
    runtime["privateMedianWarmToLastGrowthBytes"] = runtime["privateMedianLastMinuteBytes"] - runtime["privateMedianFiveToSixMinutesBytes"]
    runtime["scopeCaveat"] = "Normal Play Mode desktop mono, not PICO. Post-run Game View reading was1470x728; resolution was not locked to the short replay. Memory growth remains unresolved."
    quality = pick(data["quality"], ("status", "fixture", "count", "shDegree", "lod", "width", "height",
                                      "distanceMeters", "api", "differences", "notes"))
    correctness = {
        "developmentProjectEditorTests": test_summary(data["editorTests"]),
        "developmentProjectPlayModeTests": test_summary(data["playTests"]),
        "scope": "Development project counts include host-only navigation and PICO manifest tests. Fresh standalone release/import counts are reported separately in release-validation.json; do not infer the same test count for the distributed package.",
        "fullRecordReadEqualityChecks": 27, "testedRecordStrideBytes": 256,
        "qualityComparison": quality,
    }
    limitations = [
        "No PICO/Android/iPhone device measurement, APK build, verified device90Hz, controller thumbstick binding or device memory stability result.",
        "Overall first coarse coverage remains approximately0.8s in the comparable short records; no demonstrated significant first-screen or same-image FPS improvement.",
        "CPU bytes are decoded arrays and in-flight reservations, not total process memory. GPU bytes estimate plugin buffers, not all VRAM.",
        "Normal Play Mode process private memory grew approximately820.13MiB from first to last minute and187.16MiB from minute5-6 to last minute. No leak-free claim.",
        "Earlier unfocused DX12 Editor offscreen soak crashed on upload scratch allocation; separate DX11 offscreen replay contained a48.4s MCP diagnostic pause. Both remain failed/qualified evidence, not erased by the later normal Play Mode result.",
        "The host-only SUIT-bound DemoPICO scene is not included in the generic UPM/resource package; shipped synthetic demo and core XR paths do not prove headset/runtime/controller compatibility.",
    ]
    summary = {
        "schemaVersion": 1, "release": "v0.2", "packageVersion": "0.2.0",
        "measurementDate": "2026-10-31", "dataset": model,
        "shortReplay": short, "warmReadMicrobenchmark": io,
        "normalPlayModeSoak": runtime, "correctness": correctness,
        "priorOffscreenSoaks": data["comparison"]["soakRuns"],
        "limitations": limitations,
        "sourceReportDigests": {name: {"file": path.relative_to(reports).as_posix(),
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest()} for name, path in inputs.items()},
        "publicationScope": "Numeric aggregates only. No campus geometry, Gaussian cache payload, screenshot, raw crash/process log, credentials or absolute machine path.",
    }
    output.mkdir(parents=True, exist_ok=True)
    write_json(output / "benchmark-summary.json", summary)
    write_json(output / "read-microbenchmark.json", io)
    write_json(output / "runtime-soak-summary.json", runtime)
    write_json(output / "correctness-summary.json", correctness)
    with (output / "read-microbenchmark.csv").open("w", newline="", encoding="utf-8") as stream:
        keys = list(io["measurements"][0])
        writer = csv.DictWriter(stream, fieldnames=keys)
        writer.writeheader(); writer.writerows(io["measurements"])
    (output / "README.md").write_text(
        "# v0.2 public numeric validation evidence\n\n"
        "Recorded2026-10-31 on Windows Unity2022.3.62f3c1 / RTX4050Laptop. "
        "These sanitized numeric aggregates do not contain campus geometry, cache payloads or screenshots.\n\n"
        "- benchmark-summary.json: dataset size, short comparisons, warm IO, normal20min Play Mode, earlier qualified/failed runs and limitations.\n"
        "- read-microbenchmark.json / .csv:27 timed reads, exact256-byte record equality, conditions and medians.\n"
        "- runtime-soak-summary.json: normal desktop Play Mode1200.689s,52/52coverage; unresolved process memory growth.\n"
        "- correctness-summary.json:78/2 development-project test counts and fixed-view image difference metrics; standalone release import tests are separate.\n\n"
        "PICO/device90Hz, Android cold startup, controllers and device memory were not measured. "
        "The24.6x figure compares original PLY read/decode with lossless cache read; .gsbin already existed in v0.1. "
        "It is not an overall version-to-version or FPS speedup. CPU/GPU plugin budgets are not total process/VRAM usage.\n",
        encoding="utf-8")
    for file in output.iterdir():
        if file.is_file():
            text = file.read_text(encoding="utf-8")
            if re.search(r"(?<![A-Za-z])[A-Za-z]:[\\/]", text) or "sakuraink" in text.lower():
                raise ValueError("Publication check failed: " + file.name)
    print(json.dumps({"output": str(output), "files": sorted(path.name for path in output.iterdir() if path.is_file()),
                      "sourcePoints": model["sourcePoints"], "testCountScope": "development project78Editor/2Play; standalone release counts separate",
                      "absoluteMachinePaths": 0, "campusPayloadsOrImages": 0}, ensure_ascii=True, indent=2))


if __name__ == "__main__":
    main()
