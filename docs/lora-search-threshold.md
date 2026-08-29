# LoRa Search threshold evaluation

The detector defaults to the original fixed 11 dB Search threshold. Tests and
diagnostic tools can explicitly select `AdaptiveTemporalQuantile`, which uses a
bounded sliding histogram of `PeakToAverageDb` observations in the time
direction.

The adaptive policy operates in dB, targets a configured upper quantile, adds a
margin, and clamps the result between configured minimum and maximum values.
The estimator updates while Search has no active candidate. After the first
accepted Search symbol it freezes, preventing a preamble candidate or an active
packet from raising its own threshold. Aligning, Preamble, Sync, SFD, Header,
and Payload continue to use their existing thresholds.

Run the Release evaluation with:

```powershell
dotnet run --project SRdeck.Tests\SRdeck.Tests.csproj -c Release -- `
  --benchmark-lora-thresholds artifacts\lora-thresholds
```

The JSON output records the complete estimator definition. JSON and CSV results
include fixed/adaptive peak distributions, false alarms per hour, detection
probability, and threshold ranges for noise, three weak-signal levels, high SNR,
an adjacent-like offset, and clipping. The adaptive mode is opt-in so deployments
can immediately return to the fixed reference behavior.

## Reference result

The deterministic Release run added with this change produced the following
decision points. These are regression fixtures, not universal RF performance
claims:

| Mode | Noise false alarms/hour | -26 dB detection | -20 dB detection | -14 dB detection | High-SNR detection |
| --- | ---: | ---: | ---: | ---: | ---: |
| Fixed 11 dB | 0 | 0% | 100% | 100% | 100% |
| Adaptive temporal quantile | 0 | 0% | 20% | 100% | 100% |

The tested adaptive configuration raised the average noise threshold to about
12.1 dB and materially regressed the boundary signal without improving the
already-zero deterministic noise false-alarm result. Consequently the fixed
policy remains the product default. The adaptive path is retained as an explicit
diagnostic option for longer real-capture evaluation and can be removed without
affecting normal reception.
