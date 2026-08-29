# LoRa ring-buffer benchmark

Issue #17 compares the detector's current mirrored ring with three single-write
alternatives. Each access pattern has scalar and AVX/FMA dechirp variants:

- mirrored ring and contiguous read (current production design);
- two ring segments copied to contiguous scratch;
- a wrap branch inside the dechirp loop;
- two contiguous dechirp loops split at the ring boundary.

The benchmark covers SF7 and SF11, Search-like noise and Payload-like chirps,
precomputed 0 Hz and 20 kHz carrier-correction phasors, and one or twelve
detectors. Every row uses the same input hash, five repetitions, and the median
Release-build time. It reports both the ring-write plus dechirp micro-kernel and
the complete dechirp/FFT symbol path.

Run:

```text
dotnet run --project SRdeck.Tests/SRdeck.Tests.csproj -c Release -- \
  --benchmark-lora-ring-buffer <output-directory>
```

`LoRa ring-buffer strategy parity` also compares every FFT-input complex value
and the final peak bin/power for SF7, SF9, and SF11, with and without carrier
correction. Scalar and AVX results must remain within `2e-6` at the FFT input and
select the same final peak.

## 2026-08-25 reference result

The Windows x64 reference machine supports AVX/FMA. Median changes relative to
the current scalar mirror, paired by SF/state/CFO/detector count, were:

| Strategy | Dechirp-only delta | Full symbol/FFT delta |
| --- | ---: | ---: |
| copy segments, scalar | -28.07% | +0.12% |
| wrapped branch, scalar | -7.17% | -0.41% |
| split loops, scalar | -32.52% | -1.30% |
| mirror, SIMD | -24.49% | -1.16% |
| copy segments, SIMD | -50.71% | -1.39% |
| wrapped branch, SIMD | +23.23% | -0.12% |
| split loops, SIMD | -56.11% | -1.68% |

The fastest micro-kernel result did not translate into a stable whole-symbol
gain: split-loop SIMD's paired full-path range was -7.23% to +2.55%, crossing
zero, and its median improvement was only 1.68%. FFT cost dominates the measured
symbol path. The current mirror also avoids scratch copying and wrap control,
with zero measured steady-state allocation.

The JSON/CSV reports exact samples/second, nanoseconds/sample,
nanoseconds/symbol, `Stopwatch` ticks/sample, allocation, and estimated memory
writes. Mirroring writes 16 ring bytes/sample; single-copy alternatives write 8.
CPU-cycle, cache-miss, and branch-miss hardware counters are `null` because this
managed Windows environment did not expose a trustworthy process counter API;
the benchmark does not substitute estimates for unavailable counters.

Conclusion: no production ring-buffer change is made in this PR. The whole-path
measurement does not establish a significant, repeatable improvement over the
current mirrored implementation.
