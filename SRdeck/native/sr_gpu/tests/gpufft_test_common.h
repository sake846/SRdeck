#pragma once
// Include the implementation to compare kernels and time GPU work without a public API.
#include "../gpufft.cpp"
#include <iostream>
#include <iomanip>
#include <random>
#include <stdexcept>
#include <limits>

static void Require(bool condition, const char* message)
{
    if (!condition) throw std::runtime_error(message);
}

struct Timings { double gpuMs; double enqueueMs; };

static Timings Run(GpuFftContext* c, int batch, const SpectrumRequest* request,
    int stagesPerPass, std::vector<float>& output, ID3D11ComputeShader* wideShader = nullptr,
    bool usePackedInput = true)
{
    ComPtr<ID3D11Query> disjoint, start, end;
    D3D11_QUERY_DESC desc = { D3D11_QUERY_TIMESTAMP_DISJOINT, 0 };
    Require(SUCCEEDED(c->device->CreateQuery(&desc, &disjoint)), "disjoint query");
    desc.Query = D3D11_QUERY_TIMESTAMP;
    Require(SUCCEEDED(c->device->CreateQuery(&desc, &start)), "start query");
    Require(SUCCEEDED(c->device->CreateQuery(&desc, &end)), "end query");
    const int count = request ? request->spectrumWidth + request->noiseWidth +
        (request->endBin - request->startBin + 4095) / 4096 + 1 : c->fftSize * batch;
    if (request) Require(SUCCEEDED(PrepareSpectrum(c, *request, count)), "prepare spectrum");
    output.resize(count);
    c->context->Begin(disjoint.Get());
    c->context->End(start.Get());
    auto begun = std::chrono::steady_clock::now();
    Require(RunPipeline(c, batch, 0.0f, usePackedInput, request, stagesPerPass, wideShader) == 0, "FFT pipeline");
    const double enqueueMs = ElapsedMs(begun);
    c->context->End(end.Get());
    c->context->End(disjoint.Get());
    D3D11_BOX box = { 0, 0, 0, static_cast<UINT>(count * sizeof(float)), 1, 1 };
    auto* staging = c->readbackSlots[0].stagingOut.Get();
    c->context->CopySubresourceRegion(staging, 0, 0, 0, 0,
        request ? c->bufSpectrum.Get() : c->bufOut.Get(), 0, &box);
    D3D11_MAPPED_SUBRESOURCE mapped = {};
    Require(SUCCEEDED(c->context->Map(staging, 0, D3D11_MAP_READ, 0, &mapped)), "result map");
    std::memcpy(output.data(), mapped.pData, count * sizeof(float));
    c->context->Unmap(staging, 0);
    D3D11_QUERY_DATA_TIMESTAMP_DISJOINT frequency = {};
    UINT64 first = 0, last = 0;
    auto deadline = std::chrono::steady_clock::now();
    for (;;)
    {
        const HRESULT frequencyResult = c->context->GetData(disjoint.Get(), &frequency, sizeof(frequency), 0);
        const HRESULT startResult = c->context->GetData(start.Get(), &first, sizeof(first), 0);
        const HRESULT endResult = c->context->GetData(end.Get(), &last, sizeof(last), 0);
        Require(SUCCEEDED(frequencyResult) && SUCCEEDED(startResult) && SUCCEEDED(endResult), "timestamp readback");
        if (frequencyResult == S_OK && startResult == S_OK && endResult == S_OK) break;
        Require(ElapsedMs(deadline) < 5000, "GPU timestamps timed out");
        Sleep(1);
    }
    Require(!frequency.Disjoint && frequency.Frequency > 0 && last >= first, "invalid GPU timestamps");
    return { (last - first) * 1000.0 / frequency.Frequency, enqueueMs };
}

static void Seed(GpuFftContext* c, int batch, int noiseAmplitude = 200,
    int carrierAmplitude = 2000, int weakAmplitude = 20)
{
    std::mt19937 random(420);
    std::uniform_int_distribution<int> noise(-noiseAmplitude, noiseAmplitude);
    for (int b = 0; b < batch; ++b)
    for (int i = 0; i < c->fftSize; ++i)
    {
        const double weak = 6.283185307179586 * (17.3 + b) * i / c->fftSize;
        const double strong = 6.283185307179586 * (c->fftSize / 3 + 0.25) * i / c->fftSize;
        const auto real = static_cast<int16_t>(noise(random) + weakAmplitude * std::cos(weak) + carrierAmplitude * std::cos(strong));
        const auto imag = static_cast<int16_t>(noise(random) + weakAmplitude * std::sin(weak) + carrierAmplitude * std::sin(strong));
        c->packedInput[b * c->fftSize + i] =
            static_cast<uint16_t>(real) | (static_cast<uint32_t>(static_cast<uint16_t>(imag)) << 16);
    }
    c->context->UpdateSubresource(c->bufPacked.Get(), 0, nullptr, c->packedInput.data(), 0, 0);
}

static void Check(GpuFftContext* c, int batch, int stagesPerPass,
    ID3D11ComputeShader* wideShader = nullptr)
{
    std::vector<float> reference, actual;
    Run(c, batch, nullptr, 1, reference);
    Run(c, batch, nullptr, stagesPerPass, actual, wideShader);
    double maximum = 0, mean = 0;
    for (size_t i = 0; i < actual.size(); ++i)
    {
        Require(std::isfinite(actual[i]), "non-finite FFT bin");
        const double delta = actual[i] - reference[i];
        maximum = std::max(maximum, std::abs(delta));
        mean += delta / actual.size();
    }
    if (maximum >= 0.001)
        std::cout << "FAILED bin delta=" << maximum << " dB, mean=" << mean << " dB\n";
    Require(maximum == 0, "fused stages changed FFT bins");
    std::cout << "PASS FFT " << c->fftSize << " batch " << batch
        << " stages/pass " << stagesPerPass
        << ": maximum bin delta=" << maximum << " dB, mean=" << mean << " dB\n";
    if (batch != 1) return;
    SpectrumRequest r = { std::min(c->fftSize, 2048), std::min(c->fftSize, 1920),
        c->fftSize / 2 - c->fftSize / 320, c->fftSize / 2 + c->fftSize / 320,
        c->fftSize / 2, 32000000, 0, 200000 };
    Run(c, 1, &r, stagesPerPass, actual, wideShader);
    int at = 0;
    double aggregationError = 0;
    float referenceFloor = std::numeric_limits<float>::infinity(), actualFloor = referenceFloor;
    for (int width : { r.spectrumWidth, r.noiseWidth })
    for (int bucket = 0; bucket < width; ++bucket)
    {
        const int first = static_cast<int>(static_cast<int64_t>(bucket) * c->fftSize / width);
        const int last = static_cast<int>(static_cast<int64_t>(bucket + 1) * c->fftSize / width);
        const float expected = *std::max_element(reference.begin() + first, reference.begin() + last);
        aggregationError = std::max(aggregationError, static_cast<double>(std::abs(actual[at] - expected)));
        if (at >= r.spectrumWidth)
        {
            referenceFloor = std::min(referenceFloor, expected);
            actualFloor = std::min(actualFloor, actual[at]);
        }
        ++at;
    }
    std::cout << "AGG," << c->fftSize << ",max_delta=" << aggregationError
        << ",floor_delta=" << actualFloor - referenceFloor << " dB\n";
    Require(aggregationError == 0 && actualFloor == referenceFloor, "fused spectrum changed floor/peaks");
    double expectedPower = 0, actualPower = 0;
    for (int i = r.startBin; i < r.endBin; ++i) expectedPower += std::pow(10.0, reference[i] * 0.1);
    for (int i = at; i < static_cast<int>(actual.size()) - 1; ++i) actualPower += actual[i];
    Require(std::abs(10 * std::log10(actualPower / expectedPower)) < 0.001, "fused RF band power changed");
    Require(std::abs(actual.back() - reference[r.centerBin]) < 0.001f, "fused tuned power changed");
}

