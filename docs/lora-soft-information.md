# LoRa soft-information pipeline

Issue #14 adds an opt-in max-log path while retaining the existing hard-decision
path as the product default. The option is selected when constructing a detector:

```csharp
var detector = new LoRaPreambleDetector(
    sampleRateHz,
    bandwidthHz,
    spreadingFactor,
    softDecoding: new LoRaSoftDecodingOptions(
        LoRaFecDecisionMode.MaxLog,
        candidateCount: 4));
```

Passing no option, or explicitly selecting `LoRaFecDecisionMode.Hard`, uses the
original maximum-bin, Gray mapping, deinterleaving, and nearest-Hamming-codeword
implementation. Candidate arrays are not allocated in that mode.

## Metric flow

For header and payload FFTs, the demodulator retains the strongest `K` distinct
bins. Adjacent FFT bins within one sample-per-chip interval are treated as the
same tone so spectral leakage is not presented as a second LoRa symbol. Each
candidate records linear power, log power, cost relative to the strongest bin,
and the interpolated bin position. `LoRaSoftSymbol` also carries the applied CFO
and timing corrections and has a clipping-quality field for later front-end use.

Every candidate bin is independently converted to its relative binary symbol and
then Gray mapped. Duplicate Gray values are removed without copying a metric from
one candidate to another.

For Gray bit `b`, the max-log costs are:

```
C(b=0) = min(CandidateCost(s) | bit_b(s) = 0)
C(b=1) = min(CandidateCost(s) | bit_b(s) = 1)
```

If top-K truncation omits one bit value, its cost is the best opposite-bit cost
plus `MissingCandidatePenalty` (24 dB by default). These two costs, rather than a
single peak-ratio value copied to every bit, follow the existing diagonal
deinterleaver into the corresponding codeword-bit position.

For each Hamming candidate codeword `w`, the decoder computes:

```
Score(w) = sum(C(bit_i = w_i))
```

and selects the minimum score. The same propagator is used for the explicit
header's Hamming(8,4) block and payload CR 4/5 through 4/8 blocks. The header and
payload candidate sequences are retained in `LoRaPayloadFrame.SoftInformation`
through CRC evaluation, which is the input required by CRC-aided list decoding.

## Reproducible measurement

Run:

```text
dotnet run --project SRdeck.Tests/SRdeck.Tests.csproj -c Release -- \
  --benchmark-lora-soft-information <output-directory>
```

The benchmark uses seed `140014`, SF7, CR 4/8, top-4 candidates, 2,000 blocks at
each point, and an orthogonal-symbol complex-AWGN model. It writes JSON and CSV.
The 2026-08-25 reference run produced:

| Es/N0 (dB) | Hard PER | Max-log PER |
| ---: | ---: | ---: |
| 8 | 0.7460 | 0.6000 |
| 10 | 0.1675 | 0.0895 |
| 12 | 0.0050 | 0.0050 |
| 14 | 0 | 0 |
| 16 | 0 | 0 |
| 18 | 0 | 0 |
| 30 | 0 | 0 |

On the same machine, decoder-only median-path cost was 340 ns and 64 allocated
bytes per block for hard decoding, versus 2,028 ns and 2,968 allocated bytes per
block for max-log decoding. The opt-in path therefore costs about 6.0x CPU and
2.9 KiB additional allocation per SF7/CR4/8 block in this implementation. The
30 dB point and an IQ-level regression test both produced the same decision as
the hard path, guarding high-SNR behavior. These measurements describe this
model and implementation; they do not claim a fixed coding gain in dB.
