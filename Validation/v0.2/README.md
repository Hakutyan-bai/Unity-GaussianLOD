# v0.2 public numeric validation evidence

Recorded2026-10-31 on Windows Unity2022.3.62f3c1 / RTX4050Laptop. These sanitized numeric aggregates do not contain campus geometry, cache payloads or screenshots.

- benchmark-summary.json: dataset size, short comparisons, warm IO, normal20min Play Mode, earlier qualified/failed runs and limitations.
- read-microbenchmark.json / .csv:27 timed reads, exact256-byte record equality, conditions and medians.
- runtime-soak-summary.json: normal desktop Play Mode1200.689s,52/52coverage; unresolved process memory growth.
- correctness-summary.json:78/2 development-project test counts and fixed-view image difference metrics; standalone release import tests are separate.

PICO/device90Hz, Android cold startup, controllers and device memory were not measured. The24.6x figure compares original PLY read/decode with lossless cache read; .gsbin already existed in v0.1. It is not an overall version-to-version or FPS speedup. CPU/GPU plugin budgets are not total process/VRAM usage.
