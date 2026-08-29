# LoRa decoder samples-per-chip benchmark

Issue #18 evaluates a 4 samples/chip path before changing the 2 samples/chip
production decoder. `LoRaChannelizer.ConfigureForTest` is the only channelizer
change: normal `Configure` still selects 500 kS/s. The test path selects 1 MS/s
for BW 250 kHz, and constructs `LoRaPreambleDetector` with the matching rate, so
the reference chirp and FFT grow coherently from 4,096 to 8,192 points at SF11.

Run the Release benchmark with:

```text
dotnet run --project SRdeck.Tests/SRdeck.Tests.csproj -c Release -- \
  --benchmark-lora-samples-per-chip <output-directory>
```

It creates the same 2 MS/s IQ frame for both paths, including preamble, sync,
SFD, explicit header, CR 4/8 payload, whitening, and a valid payload CRC. Thirty-
two frames per condition cover all eight input sample phases with four noise
seeds. The JSON contains per-frame results and aggregates; a separate CSV
contains the clipped-signal channelizer-output spectrum.

STO errors are reported in chips after subtracting the rate-specific zero-shift
FIR/group-delay baseline for each repetition. Thus the distribution compares
sample-phase resolution rather than a fixed channelizer delay.

## 2026-08-25 reference result

| Condition | samples/chip | Sync | Valid header | PRR | STO RMS | STO p95 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| boundary | 2 | 1.000 | 1.000 | 0.9375 | 0.4677 chip | 0.875 chip |
| boundary | 4 | 1.000 | 1.000 | 1.0000 | 0.2615 chip | 0.375 chip |
| clipped | 2 | 1.000 | 1.000 | 1.0000 | 0.5229 chip | 0.875 chip |
| clipped | 4 | 1.000 | 1.000 | 1.0000 | 0.2795 chip | 0.500 chip |
| high SNR | 2 | 1.000 | 1.000 | 1.0000 | 0.5229 chip | 0.875 chip |
| high SNR | 4 | 1.000 | 1.000 | 1.0000 | 0.2795 chip | 0.500 chip |

The synthetic boundary point recovered two additional packets out of 32 at
4 samples/chip. Synchronization and header success did not change.

## Frequency resolution and interpolation

| samples/chip | decoder rate | detector FFT | Fs/N | noiseless interpolation RMS | max error |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 2 | 500 kS/s | 4,096 | 122.0703125 Hz | 26.75 Hz | 33.80 Hz |
| 4 | 1 MS/s | 8,192 | 122.0703125 Hz | 26.75 Hz | 33.80 Hz |

Doubling both `Fs` and `N` does not change FFT-bin spacing. The detector's
parabolic interpolation bias is also identical in this controlled fractional-
bin test. The observed STO improvement comes from time-domain sample resolution
changing from 0.5 chip to 0.25 chip, not from narrower frequency bins.

## FIR output spectrum

The clipped-frame spectrum uses the same 61.035 Hz analysis-bin spacing for both
rates. The first bin outside the 125 kHz occupied-band edge is about -11 dB in
both paths because it lies in the existing FIR transition. At or beyond
187.5 kHz, the worst component is -77.08 dB for 2 samples/chip and -80.80 dB for
4 samples/chip. No material clipped harmonic or folded component was observed,
so this measurement does not justify an additional complex-baseband LPF.

## Cost and decision

- whole-path CPU/input sample increased by 23.2% to 29.2% across the three
  conditions;
- channelizer time increased by 44.7% to 49.2%, because the FIR emits twice as
  often;
- detector time increased by 10.5% to 18.9%;
- isolated FFT cost increased from 249.9 us (4,096) to 283.8 us (8,192);
- estimated detector working storage doubled from 147,456 to 294,912 bytes;
- measured per-frame-path allocation increased by about 98%.

The internal test shows better STO quantization and a small synthetic boundary
PRR result, but the result is only two packets, uses generated rather than field
captured IQ, and costs roughly 25-30% more whole-path CPU plus twice the memory.
That is not sufficient evidence to change the product path. Production remains
at 2 samples/chip; 4 samples/chip should be reconsidered only with a larger,
representative captured-IQ corpus showing repeatable PER/PRR benefit.
