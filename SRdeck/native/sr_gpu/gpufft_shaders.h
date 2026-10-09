#pragma once

static const char* kShaderPackedToComplex = R"(
cbuffer FftParams : register(b0)
{
    uint fftSize;
    uint stage;
    uint batchCount;
    uint _pad;
}
StructuredBuffer<int> packed : register(t0);
StructuredBuffer<float> window : register(t1);
RWStructuredBuffer<float2> outputBuf : register(u0);
[numthreads(128,1,1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    uint x = tid.x;
    uint b = tid.y;
    if (b >= batchCount || x >= fftSize) return;
    uint idx = b * fftSize + x;
    int p = packed[idx];
    int i = (p << 16) >> 16;
    int q = p >> 16;
    float w = window[x];
    outputBuf[idx] = float2((float)i * w, (float)q * w);
}
)";

static const char* kShaderStockham = R"(
cbuffer FftParams : register(b0)
{
    uint fftSize;
    uint stage;
    uint batchCount;
    uint _pad;
}
StructuredBuffer<float2> inputBuf : register(t0);
RWStructuredBuffer<float2> outputBuf : register(u0);
[numthreads(64,1,1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    uint t = tid.x;
    uint batch = tid.y;
    if (batch >= batchCount || t >= fftSize / 2) return;
    uint L = 1u << stage;
    uint j = t % L;
    uint i = t / L;
    uint baseIdx = batch * fftSize;
    uint i0 = baseIdx + i * L + j;
    uint i1 = i0 + fftSize / 2;
    uint out0 = baseIdx + i * (2u * L) + j;
    uint out1 = out0 + L;
    float angle = -6.28318530718 * (float)j / (float)(2u * L);
    float s, c;
    sincos(angle, s, c);
    float2 w = float2(c, s);
    float2 u = inputBuf[i0];
    float2 v = inputBuf[i1];
    float2 vw = float2(v.x * w.x - v.y * w.y, v.x * w.y + v.y * w.x);
    outputBuf[out0] = u + vw;
    outputBuf[out1] = u - vw;
}
)";

// Two consecutive Stockham stages without a global intermediate write/read.
static const char* kShaderStockhamPair = R"(
cbuffer FftParams : register(b0)
{
    uint fftSize;
    uint stage;
    uint batchCount;
    uint _pad;
}
StructuredBuffer<float2> inputBuf : register(t0);
RWStructuredBuffer<float2> outputBuf : register(u0);

float2 multiply(float2 v, float2 w)
{
    return float2(v.x * w.x - v.y * w.y, v.x * w.y + v.y * w.x);
}

[numthreads(64,1,1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    uint t = tid.x;
    uint batch = tid.y;
    if (batch >= batchCount || t >= fftSize / 4) return;
    uint L = 1u << stage;
    uint j = t % L;
    uint i = t / L;
    uint baseIdx = batch * fftSize;
    float2 a = inputBuf[baseIdx + t];
    float2 b = inputBuf[baseIdx + t + fftSize / 2];
    float2 c = inputBuf[baseIdx + t + fftSize / 4];
    float2 d = inputBuf[baseIdx + t + 3 * (fftSize / 4)];
    float s, co;
    sincos(-6.28318530718 * (float)j / (float)(2u * L), s, co);
    float2 bw = multiply(b, float2(co, s));
    float2 dw = multiply(d, float2(co, s));
    float2 u = a + bw;
    float2 v = a - bw;
    float2 p = c + dw;
    float2 q = c - dw;
    // Keep the original angles and arithmetic, including the second branch.
    sincos(-6.28318530718 * (float)j / (float)(4u * L), s, co);
    float2 pw = multiply(p, float2(co, s));
    sincos(-6.28318530718 * (float)(j + L) / (float)(4u * L), s, co);
    float2 qw = multiply(q, float2(co, s));
    uint out0 = baseIdx + i * (4u * L) + j;
    outputBuf[out0] = u + pw;
    outputBuf[out0 + 2u * L] = u - pw;
    outputBuf[out0 + L] = v + qw;
    outputBuf[out0 + 3u * L] = v - qw;
}
)";

// Stockham stages keep their radix-sized intermediates in registers.
static const char* kShaderStockhamFused = R"(
#ifndef FFT_STAGES
#define FFT_STAGES 4
#endif
#ifndef FFT_THREADS
#define FFT_THREADS 64
#endif
static const uint radix = 1u << FFT_STAGES;
cbuffer FftParams : register(b0)
{
    uint fftSize;
    uint stage;
    uint batchCount;
    uint _pad;
}
#if FFT_PACKED_INPUT
StructuredBuffer<int> packedBuf : register(t0);
StructuredBuffer<float> windowBuf : register(t1);
#else
StructuredBuffer<float2> inputBuf : register(t0);
#endif
RWStructuredBuffer<float2> outputBuf : register(u0);

[numthreads(FFT_THREADS,1,1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    uint t = tid.x;
    uint batch = tid.y;
    if (batch >= batchCount || t >= fftSize / radix) return;
    uint L = 1u << stage;
    uint j = t % L;
    uint i = t / L;
    uint baseIdx = batch * fftSize;
    float2 values[radix];
    float2 next[radix];
    [unroll]
    for (uint k = 0; k < radix; ++k)
    {
        uint sample = t + k * (fftSize / radix);
#if FFT_PACKED_INPUT
        int p = packedBuf[baseIdx + sample];
        int real = (p << 16) >> 16;
        int imag = p >> 16;
        float w = windowBuf[sample];
        // Preserve the rounding of the separate window conversion shader.
        precise float2 windowed = float2((float)real * w, (float)imag * w);
        values[k] = windowed;
#else
        values[k] = inputBuf[baseIdx + sample];
#endif
    }
    [unroll]
    for (uint step = 0; step < FFT_STAGES; ++step)
    {
        uint width = 1u << step;
        [unroll]
        for (uint branch = 0; branch < width; ++branch)
        {
            float s, c;
            float angle = -6.28318530718 * (float)(j + branch * L) / (float)(2u * width * L);
            sincos(angle, s, c);
            [unroll]
            for (uint block = 0; block < (radix / 2) / width; ++block)
            {
                uint k = block * width + branch;
                float2 u = values[k];
                float2 v = values[k + radix / 2];
                float2 vw = float2(v.x * c - v.y * s, v.x * s + v.y * c);
                uint out0 = block * (2u * width) + branch;
                next[out0] = u + vw;
                next[out0 + width] = u - vw;
            }
        }
        [unroll]
        for (uint k = 0; k < radix; ++k) values[k] = next[k];
    }
    [unroll]
    for (uint k = 0; k < radix; ++k)
        outputBuf[baseIdx + i * (radix * L) + j + k * L] = values[k];
}
)";

static const char* kShaderDbConvert = R"(
cbuffer DbParams : register(b0)
{
    uint fftSize;
    uint batchCount;
    float offset;
    float _pad;
}
StructuredBuffer<float2> inputBuf : register(t0);
RWStructuredBuffer<float> outputBuf : register(u0);
[numthreads(128,1,1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    uint x = tid.x;
    uint batch = tid.y;
    if (batch >= batchCount || x >= fftSize) return;
    uint baseIdx = batch * fftSize;
    uint halfSize = fftSize / 2;
    uint target = baseIdx + ((x + halfSize) % fftSize);
    float2 v = inputBuf[baseIdx + x];
    float power = max(1e-20, v.x * v.x + v.y * v.y);
    outputBuf[target] = 10.0 * log10(power) + offset;
}
)";

static const char* kShaderSpectrumReduce = R"(
cbuffer SpectrumParams : register(b0)
{
    uint fftSize;
    uint spectrumWidth;
    uint noiseWidth;
    uint startBin;
    uint endBin;
    int centerBin;
    uint partialCount;
    float offset;
}
StructuredBuffer<float2> inputBuf : register(t0);
RWStructuredBuffer<float> outputBuf : register(u0);
groupshared float values[64];
StructuredBuffer<uint2> ranges : register(t1);

float powerAt(uint bin)
{
    float2 v = inputBuf[(bin + fftSize / 2) % fftSize];
    return max(1e-20, v.x * v.x + v.y * v.y);
}

[numthreads(64,1,1)]
void main(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    uint index = group.x + group.y * 65535;
    uint displayCount = spectrumWidth + noiseWidth;
    if (index > displayCount + partialCount) return;
    if (index == displayCount + partialCount)
    {
        if (lane == 0)
            outputBuf[index] = centerBin >= 0 && centerBin < (int)fftSize
                ? 10.0 * log10(powerAt((uint)centerBin)) + offset : 0.0;
        return;
    }

    bool sumPower = index >= displayCount;
    uint first, last;
    if (sumPower)
    {
        first = startBin + (index - displayCount) * 4096;
        last = min(first + 4096, endBin);
    }
    else
    {
        // CPU-built uint64 boundaries exactly match the existing aggregation,
        // including non-power-of-two widths at 1M--4M FFT sizes.
        first = ranges[index].x;
        last = ranges[index].y;
    }
    float value = 0.0;
    for (uint bin = first + lane; bin < last; bin += 64)
    {
        float power = powerAt(bin);
        value = sumPower ? value + power : max(value, power);
    }
    values[lane] = value;
    GroupMemoryBarrierWithGroupSync();
    for (uint stride = 32; stride > 0; stride >>= 1)
    {
        if (lane < stride)
            values[lane] = sumPower ? values[lane] + values[lane + stride]
                                   : max(values[lane], values[lane + stride]);
        GroupMemoryBarrierWithGroupSync();
    }
    if (lane == 0)
        outputBuf[index] = sumPower ? values[0] : 10.0 * log10(values[0]) + offset;
}
)";

static const char* kShaderChannelMix = R"(
cbuffer ChannelMixParams : register(b0)
{
    uint inputCount;
    float phaseStart;
    float phaseStep;
    float phaseStep256;
    float phaseStep65536;
    float3 _pad;
}
StructuredBuffer<float2> rawInput : register(t0);
RWStructuredBuffer<float2> mixedOutput : register(u0);
[numthreads(64,1,1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    uint index = tid.x;
    if (index >= inputCount) return;
    uint lowIndex = index & 255u;
    uint middleIndex = (index >> 8u) & 255u;
    uint highIndex = index >> 16u;
    float angle = phaseStart +
        phaseStep * (float)lowIndex +
        phaseStep256 * (float)middleIndex +
        phaseStep65536 * (float)highIndex;
    float s, c;
    sincos(angle, s, c);
    float2 v = rawInput[index];
    mixedOutput[index] = float2(v.x * c - v.y * s, v.x * s + v.y * c);
}
)";

static const char* kShaderChannelFilter = R"(
cbuffer ChannelFilterParams : register(b0)
{
    uint outputCount;
    uint tapCount;
    uint phaseCount;
    uint _pad;
}
StructuredBuffer<float2> mixedInput : register(t0);
StructuredBuffer<float> taps : register(t1);
StructuredBuffer<uint2> outputMap : register(t2);
RWStructuredBuffer<float2> outputIq : register(u0);
[numthreads(64,1,1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    uint outputIndex = tid.x;
    if (outputIndex >= outputCount) return;
    uint2 mapping = outputMap[outputIndex];
    uint anchor = mapping.x;
    uint phase = min(mapping.y, phaseCount - 1);
    uint tapBase = phase * tapCount;
    float2 sum = float2(0.0, 0.0);
    uint available = min(tapCount, anchor + 1);
    for (uint tap = 0; tap < available; ++tap)
        sum += mixedInput[anchor - tap] * taps[tapBase + tap];
    outputIq[outputIndex] = sum;
}
)";

static const char* kShaderChannelBatchMix = R"(
cbuffer ChannelBatchParams : register(b0)
{
    uint inputCount;
    uint outputCount;
    uint tapCount;
    uint phaseCount;
    uint channelCount;
    uint inputStride;
    uint outputStride;
    uint _pad;
}
StructuredBuffer<float2> rawInput : register(t0);
StructuredBuffer<float4> channelPhases : register(t1);
RWStructuredBuffer<float2> mixedOutput : register(u0);
[numthreads(64,1,1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    uint index = tid.x;
    uint channel = tid.y;
    if (index >= inputCount || channel >= channelCount) return;
    float4 phases = channelPhases[channel];
    uint lowIndex = index & 255u;
    uint middleIndex = (index >> 8u) & 255u;
    uint highIndex = index >> 16u;
    float angle = phases.x +
        phases.y * (float)lowIndex +
        phases.z * (float)middleIndex +
        phases.w * (float)highIndex;
    float s, c;
    sincos(angle, s, c);
    float2 v = rawInput[index];
    mixedOutput[channel * inputStride + index] =
        float2(v.x * c - v.y * s, v.x * s + v.y * c);
}
)";

static const char* kShaderChannelBatchFilter = R"(
cbuffer ChannelBatchParams : register(b0)
{
    uint inputCount;
    uint outputCount;
    uint tapCount;
    uint phaseCount;
    uint channelCount;
    uint inputStride;
    uint outputStride;
    uint _pad;
}
StructuredBuffer<float2> mixedInput : register(t0);
StructuredBuffer<float> taps : register(t1);
StructuredBuffer<uint2> outputMap : register(t2);
RWStructuredBuffer<float2> outputIq : register(u0);
groupshared float2 partialSums[64];
[numthreads(64,1,1)]
void main(uint3 groupId : SV_GroupID, uint groupIndex : SV_GroupIndex)
{
    uint outputIndex = groupId.x;
    uint channel = groupId.y;
    if (outputIndex >= outputCount || channel >= channelCount) return;
    uint2 mapping = outputMap[outputIndex];
    uint anchor = mapping.x;
    uint phase = min(mapping.y, phaseCount - 1);
    uint tapBase = phase * tapCount;
    uint inputBase = channel * inputStride;
    float2 sum = float2(0.0, 0.0);
    uint available = min(tapCount, anchor + 1);
    for (uint tap = groupIndex; tap < available; tap += 64)
        sum += mixedInput[inputBase + anchor - tap] * taps[tapBase + tap];
    partialSums[groupIndex] = sum;
    GroupMemoryBarrierWithGroupSync();
    for (uint width = 32; width > 0; width >>= 1)
    {
        if (groupIndex < width)
            partialSums[groupIndex] += partialSums[groupIndex + width];
        GroupMemoryBarrierWithGroupSync();
    }
    if (groupIndex == 0)
        outputIq[channel * outputStride + outputIndex] = partialSums[0];
}
)";

static const char* kShaderLineDraw = R"(
struct VSIn
{
    float2 pos : POSITION;
    uint color : COLOR0;
};

struct VSOut
{
    float4 pos : SV_POSITION;
    float4 color : COLOR0;
};

cbuffer SurfaceParams : register(b0)
{
    float width;
    float height;
    float2 _pad;
}

VSOut vs_main(VSIn input)
{
    VSOut output;
    float2 ndc;
    ndc.x = (input.pos.x / max(width, 1.0)) * 2.0 - 1.0;
    ndc.y = 1.0 - (input.pos.y / max(height, 1.0)) * 2.0;
    output.pos = float4(ndc, 0.0, 1.0);

    float a = ((input.color >> 24) & 255) / 255.0;
    float r = ((input.color >> 16) & 255) / 255.0;
    float g = ((input.color >> 8) & 255) / 255.0;
    float b = (input.color & 255) / 255.0;
    output.color = float4(r, g, b, a);
    return output;
}

float4 ps_main(VSOut input) : SV_TARGET
{
    return input.color;
}
)";
