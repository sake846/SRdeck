# LoRa CRC-aided list decoding

Issue #16 adds an opt-in, bounded retry stage on top of the soft-information
pipeline from #14. It is deliberately not enabled by default.

```csharp
var detector = new LoRaPreambleDetector(
    sampleRateHz,
    bandwidthHz,
    spreadingFactor,
    enableMetrics: true,
    softDecoding: new LoRaSoftDecodingOptions(),
    crcListDecoding: new LoRaCrcListDecodingOptions(
        maxCandidateSymbols: 12,
        maxCandidateFrames: 128,
        maxRetries: 64,
        maxCpuMilliseconds: 10));
```

The constructor rejects list decoding unless max-log soft decoding is also
enabled. A frame enters the retry stage only when it declares a payload CRC and
the normal decode has failed that CRC. A normally successful frame performs no
queue creation or list search.

## Search and acceptance policy

1. Header and payload symbols with more than one retained hypothesis are sorted
   by the top-two relative-cost gap. Only the configured number of least
   confident symbols enter the list.
2. A priority queue enumerates combinations by summed candidate cost. Duplicate
   rank vectors are suppressed.
3. Every candidate is hard decoded through the real header, diagonal
   deinterleaver, Hamming decoder, dewhitening, and payload CRC implementation.
4. A changed header must retain a valid header checksum and exactly the original
   payload length, coding rate, and CRC-presence fields.
5. CRC-valid hypotheses are grouped by decoded payload and received CRC. The
   frame is accepted only when exactly one decoded frame remains. Multiple
   distinct CRC-valid frames are reported as ambiguous and rejected.
6. Candidate-symbol count, queued candidate-frame count, retry count, and wall
   CPU time are all bounded. Hitting the CPU-time limit suppresses recovery.

Recovered frames set `WasRecoveredByListDecoding`, `CrcListMetric`, and
`CrcListRetries`. `CrcListDecodingCompleted` publishes retry, match, ambiguity,
selected-metric, time-limit, and elapsed-tick diagnostics. The opt-in detector
metrics snapshot also records eligible frames, retries, recoveries, ambiguous
frames, and list-decoder ticks.

## Reproducible evaluation

Run:

```text
dotnet run --project SRdeck.Tests/SRdeck.Tests.csproj -c Release -- \
  --benchmark-lora-crc-list <output-directory>
```

The benchmark uses seed `160016`, SF7, CR 4/8, four-byte payloads with a real
LoRa payload CRC, top-4 symbol observations, and 1,000 packets per point. The
normal and list paths consume the same cached complex-AWGN matched-filter
observations. The 2026-08-25 reference run produced:

| Es/N0 (dB) | Normal PRR | CRC-list PRR | Total retries | List CPU |
| ---: | ---: | ---: | ---: | ---: |
| 8 | 0.1890 | 0.3470 | 51,841 | 196.36 ms |
| 10 | 0.8490 | 0.9640 | 9,664 | 11.57 ms |
| 12 | 0.9960 | 1.0000 | 256 | 0.34 ms |
| 30 | 1.0000 | 1.0000 | 0 | 0 ms |

The initially valid 30 dB set caused zero list invocations. In a deliberately
conservative false-accept stress test that supplies an already-valid header but
random payload observations, results were:

| Input | Normal false accepts | Additional list false accepts |
| --- | ---: | ---: |
| no signal | 0/1,000 | 1/1,000 |
| coherent mis-synchronized symbols | 0/1,000 | 0/1,000 |

The no-signal stress case conditions on a valid header, so it is not an
end-to-end false-alarm probability. It does show that a 16-bit CRC plus a bounded
list is not proof against false acceptance. For that reason the feature remains
opt-in, has strict resource limits, exposes ambiguity/retry telemetry, and should
be enabled in production only after representative captured-IQ validation.
