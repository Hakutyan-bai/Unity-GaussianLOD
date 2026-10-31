"""Summarize recorded Editor replay evidence; never infer device FPS or a speedup.

Usage: python Tools/GaussianSplatLOD/summarize_pico_reports.py
Only completed, named reference reports are compared. Rejected/interrupted runs stay
on disk as evidence and are not promoted to successful results by this script.
"""
from __future__ import annotations

import csv
import json
import math
import statistics
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
REPORTS = ROOT / "Reports" / "PICO"
RUNS = ("baseline-original-ply", "baseline-controlled-ply", "optimized-ply", "optimized-cache")


def read_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def distribution(values):
    values = sorted(float(v) for v in values if v is not None and float(v) >= 0)
    if not values:
        return None
    return {"median": statistics.median(values), "p95": values[max(0, math.ceil(len(values) * .95) - 1)], "max": values[-1]}


def main():
    result = {
        "platform": "Windows Editor / RTX 4050 Laptop / per-run DX11 or DX12 / URP14 / 1280x720",
        "scope": "CPU submission and streaming evidence; not device frame rate or same-image GPU performance",
        "caveats": [
            "Editor update cadence changes from approximately 13ms to 100ms across the controlled baseline. Do not derive an FPS or first-screen speedup ratio.",
            "The operating-system page cache was not flushed. These are not cold-storage measurements.",
            "The route and hard budgets are shared, but scheduling may select different LOD cuts. Visible counts and fixed-view quality must be assessed separately.",
            "GPU timing -1 is unavailable. Render submission Stopwatch time is CPU time, not GPU time.",
            "CPU bytes count decoded data plus in-flight reservations, not total process memory; Unity allocated bytes are not Android PSS.",
            "DiskReads counts started range requests and can include cancellation; PLYReads/CacheReads count completed reads. Prediction can increase requests.",
            "Short-test status passed checks coverage/refinement, not 20-minute stability or PICO readiness.",
        ],
        "results": [],
    }
    for run in RUNS:
        summary = read_json(REPORTS / (run + ".json"))
        if summary.get("status") != "passed":
            result["results"].append({"run": run, "status": summary.get("status"), "excluded": True})
            continue
        samples = []
        for trial in summary["trials"]:
            samples.extend(read_json(REPORTS / f"{run}-trial{trial['trial']}.samples.json"))
        focus = [sample["editorFocused"] for sample in samples if "editorFocused" in sample]
        item = {
            "run": run, "status": summary["status"],
            "api": summary.get("api"), "device": summary.get("device"),
            "coverageSeconds": [t["firstCoverageSeconds"] for t in summary["trials"]],
            "coverageMedianSeconds": statistics.median(t["firstCoverageSeconds"] for t in summary["trials"]),
            "minimumCoveredNodes": min(t["minCoveredNodes"] for t in summary["trials"]),
            "editorIterationMs": distribution(sample["frameMs"] for sample in samples),
            "tickMs": distribution(sample["tickMs"] for sample in samples),
            "uploadSubmitMs": distribution(sample.get("uploadMs") for sample in samples),
            "renderSubmitMs": distribution(sample["renderSubmitMs"] for sample in samples),
            "gpuFrameMs": distribution(sample.get("gpuFrameMs") for sample in samples),
            "visibleSplats": distribution(sample["visible"] for sample in samples),
            "reads": [t["diskReads"] for t in summary["trials"]],
            "completedPLYReads": [t["plyReads"] for t in summary["trials"]],
            "completedCacheReads": [t["cacheReads"] for t in summary["trials"]],
            "peakCPUBytes": max(t["maxCPUBytes"] for t in summary["trials"]),
            "allocatedGPUBytes": max(t["maxGPUBytes"] for t in summary["trials"]),
            "editorFocusedFraction": sum(focus) / len(focus) if focus else None,
        }
        result["results"].append(item)
    soak_path = REPORTS / "soak-cache.json"
    if soak_path.exists():
        soak = read_json(soak_path)
        result["soakRecordedStatus"] = soak.get("status")
        progress = REPORTS / "soak-cache.progress.json"
        if progress.exists():
            result["soakLastProgress"] = read_json(progress)
        result["soakCaveat"] = "A stale running file does not prove the Editor is alive or the soak passed. See validation report and crash evidence."
    result["soakRuns"] = []
    for path in sorted(REPORTS.glob("soak*.json")):
        if ".progress." in path.name or ".samples." in path.name or "-trial" in path.name:
            continue
        report = read_json(path)
        if not isinstance(report, dict):
            continue
        item = {"file": path.name, "status": report.get("status"), "api": report.get("api"), "trialSeconds": report.get("trialSeconds", report.get("seconds")), "trials": report.get("trials"), "error": report.get("error", report.get("reason"))}
        sample_path = path.with_name(path.stem + "-trial1.samples.json")
        if report.get("status") == "passed" and sample_path.exists():
            samples = read_json(sample_path)
            start = [s["privateBytes"] for s in samples if s["seconds"] < 60 and s.get("privateBytes", -1) > 0]
            end = [s["privateBytes"] for s in samples if s["seconds"] >= 1140 and s.get("privateBytes", -1) > 0]
            item.update({
                "actualElapsedSeconds": samples[-1]["seconds"], "sampleCount": len(samples),
                "editorIterationMs": distribution(s["frameMs"] for s in samples),
                "tickMs": distribution(s["tickMs"] for s in samples),
                "uploadSubmitMs": distribution(s.get("uploadMs") for s in samples),
                "renderSubmitMs": distribution(s["renderSubmitMs"] for s in samples),
                "peakCPUBytes": max(s["cpuBytes"] for s in samples),
                "allocatedGPUBytes": max(s["gpuBytes"] for s in samples),
                "peakProcessPrivateBytes": max(s.get("privateBytes", -1) for s in samples),
                "peakGraphicsDriverBytes": max(s.get("graphicsDriverBytes", -1) for s in samples),
                "firstMinutePrivateMedianBytes": statistics.median(start) if start else None,
                "lastMinutePrivateMedianBytes": statistics.median(end) if end else None,
                "privateMedianGrowthBytes": statistics.median(end) - statistics.median(start) if start and end else None,
                "largestIterationSample": max(samples, key=lambda s: s["frameMs"]),
                "caveat": "The DX11 run completed its wall-clock route and coverage checks, but an MCP diagnostic pause caused a 48.4s Editor iteration at 404.2s. Process private memory grew about 668MiB. This is not uninterrupted roaming, flat process memory or a leak-free result; investigate native/managed allocations on device and in Player.",
            })
        result["soakRuns"].append(item)
    (REPORTS / "comparison.json").write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    with (REPORTS / "comparison.csv").open("w", newline="", encoding="utf-8-sig") as handle:
        writer = csv.writer(handle)
        writer.writerow(("run", "status", "coverageMedianSeconds", "minimumCoveredNodes", "tickP95Ms", "uploadSubmitP95Ms", "renderSubmitP95Ms", "peakCPUBytes", "allocatedGPUBytes"))
        for item in result["results"]:
            if item.get("excluded"):
                writer.writerow((item["run"], item["status"]))
                continue
            writer.writerow((item["run"], item["status"], item["coverageMedianSeconds"], item["minimumCoveredNodes"], item["tickMs"]["p95"], item["uploadSubmitMs"]["p95"] if item["uploadSubmitMs"] else "N/A", item["renderSubmitMs"]["p95"], item["peakCPUBytes"], item["allocatedGPUBytes"]))
    with (REPORTS / "soak-comparison.csv").open("w", newline="", encoding="utf-8-sig") as handle:
        writer = csv.writer(handle)
        writer.writerow(("run", "status", "api", "actualElapsedSeconds", "sampleCount", "tickP95Ms", "uploadP95Ms", "maxEditorIterationMs", "peakCPUBytes", "allocatedGPUBytes", "peakGraphicsDriverBytes", "peakProcessPrivateBytes", "firstMinutePrivateMedianBytes", "lastMinutePrivateMedianBytes", "privateMedianGrowthBytes", "caveat"))
        for item in result["soakRuns"]:
            writer.writerow((item["file"], item["status"], item.get("api"), item.get("actualElapsedSeconds"), item.get("sampleCount"), item.get("tickMs", {}).get("p95"), item.get("uploadSubmitMs", {}).get("p95"), item.get("editorIterationMs", {}).get("max"), item.get("peakCPUBytes"), item.get("allocatedGPUBytes"), item.get("peakGraphicsDriverBytes"), item.get("peakProcessPrivateBytes"), item.get("firstMinutePrivateMedianBytes"), item.get("lastMinutePrivateMedianBytes"), item.get("privateMedianGrowthBytes"), item.get("caveat", item.get("error"))))
    print(json.dumps(result, ensure_ascii=True, indent=2))


if __name__ == "__main__":
    main()
