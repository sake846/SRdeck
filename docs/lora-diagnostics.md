# LoRa diagnostic benchmark

The Meshtastic LoRa detector has an opt-in metrics path for repeatable DSP
comparisons. Normal receivers leave this path disabled. When enabled, it records:

- input samples, symbol analyses, FFT executions, and FFT skips;
- Search candidate rejects, preamble detections, and frame synchronizations;
- decoded and checksum-valid headers;
- decoded payloads and valid or invalid payload CRCs;
- analysis counts and `Stopwatch` ticks for Search, Aligning, Preamble,
  SyncLow, SFD, Header, and Payload states.

Run the deterministic benchmark in a Release build:

```powershell
dotnet run --project SRdeck.Tests\SRdeck.Tests.csproj -c Release -- `
  --benchmark-lora-diagnostics artifacts\lora
```

The command writes `lora-diagnostics.json` and `lora-diagnostics.csv`. The
scenario set covers zero input, noise, a below-noise preamble, CFO, STO, sample
clock error, an adjacent slot, a same-channel collision, clipping, input scale,
multiple spreading factors, 125/250 kHz bandwidths, and 0.5/1/2 MS/s inputs.

Each result includes the random seed and a SHA-256 hash of the generated IQ.
To compare two revisions, run the command from both worktrees and verify that
the scenario seed and IQ hash match before comparing the stage counts and CPU
columns. Timing comparisons should use the same Release runtime, CPU, power
policy, and idle-system conditions.

The allocation column covers processing after the scenario input, detector,
and channelizer have been constructed. One-time channelizer output-buffer
growth is therefore visible; steady-state allocation can be measured by replaying
the same IQ through an already-warmed instance when investigating allocation
changes.
