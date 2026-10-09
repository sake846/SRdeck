#include "gpufft_test_common.h"

static void CheckPrecompiledReference()
{
#ifdef SR_GPU_PRECOMPILED
    for (const char* source : { kShaderPackedToComplex, kShaderStockham })
    {
        auto saved = FindPrecompiledCs(source, nullptr);
        ComPtr<ID3DBlob> live;
        Require(saved.data && SUCCEEDED(D3DCompile(source, strlen(source), nullptr, nullptr, nullptr,
            "main", "cs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &live, nullptr)), "precompiled reference shader");
        Require(saved.size == live->GetBufferSize() && memcmp(saved.data, live->GetBufferPointer(), saved.size) == 0,
            "precompiled reference differs from runtime compilation");
    }
    const D3D_SHADER_MACRO unsupported[] = { { "FFT_THREADS", "32+32" }, { nullptr, nullptr } };
    Require(!FindPrecompiledCs(kShaderStockhamFused, unsupported).data, "unsupported macros selected different bytecode");
    std::cout << "PASS precompiled reference bytecode matches runtime compilation; unknown variants retain fallback\n";
#endif
}

static void CheckPacking()
{
    short real[21], imag[21];
    for (int i = 0; i < 21; ++i)
    {
        real[i] = static_cast<short>(i * 5111 - 32768);
        imag[i] = static_cast<short>(32767 - i * 7123);
    }
    for (int count = 0; count <= 79; ++count)
    for (int offset = -38; offset <= 38; ++offset)
    {
        std::vector<int32_t> output(count + 2, 123456789);
        PackRingIq(real + 1, imag + 1, 19, offset, output.data() + 1, count);
        Require(output.front() == 123456789 && output.back() == 123456789, "packing overrun");
        for (int i = 0; i < count; ++i)
        {
            int source = (offset + i) % 19;
            if (source < 0) source += 19;
            uint32_t expected = static_cast<uint16_t>(real[source + 1]) |
                (static_cast<uint32_t>(static_cast<uint16_t>(imag[source + 1])) << 16);
            Require(static_cast<uint32_t>(output[i + 1]) == expected, "IQ packing changed sample bits");
        }
    }
    std::cout << "PASS IQ packing: unaligned arrays, signed shorts, tails, negative offsets and multiple ring wraps\n";
}

static void CheckFloatInput(GpuFftContext* c, int batch)
{
    std::vector<float> reference, actual;
    Run(c, batch, nullptr, 1, reference);
    for (int b = 0; b < batch; ++b)
    for (int i = 0; i < c->fftSize; ++i)
    {
        const int at = b * c->fftSize + i;
        const uint32_t packed = c->packedInput[at];
        c->hostComplex[at] = { static_cast<int16_t>(packed & 65535) * c->windowCopy[i],
            static_cast<int16_t>(packed >> 16) * c->windowCopy[i] };
    }
    c->context->UpdateSubresource(c->bufA.Get(), 0, nullptr, c->hostComplex.data(), 0, 0);
    Run(c, batch, nullptr, 0, actual, nullptr, false);
    double maximum = 0;
    for (size_t i = 0; i < actual.size(); ++i)
    {
        Require(std::isfinite(actual[i]), "non-finite float input FFT bin");
        maximum = std::max(maximum, static_cast<double>(std::abs(actual[i] - reference[i])));
    }
    Require(maximum < 0.001, "float input FFT changed bins");
    std::cout << "PASS float input " << c->fftSize << " batch " << batch << ": max_delta=" << maximum << " dB\n";
}

static void Benchmark(GpuFftContext* c, ID3D11ComputeShader* five, ID3D11ComputeShader* six)
{
    SpectrumRequest request = { 2048, 1920, c->fftSize / 2 - c->fftSize / 320,
        c->fftSize / 2 + c->fftSize / 320, c->fftSize / 2, 32000000, 0, 200000 };
    std::vector<float> output;
    for (int interval : { 0, 100 })
    {
        double gpu[3] = {}, cpu[3] = {};
        const int stages[] = { 4, 5, 6 };
        ID3D11ComputeShader* shaders[] = { nullptr, five, six };
        auto previousSubmission = std::chrono::steady_clock::now();
        for (int frame = 0; frame < 15; ++frame)
        for (int order = 0; order < 3; ++order)
        {
            // Alternate order to reduce clock/temperature bias.
            int path = (frame + order) % 3;
            const double remaining = interval - ElapsedMs(previousSubmission);
            if (remaining > 0) Sleep(static_cast<DWORD>(std::ceil(remaining)));
            previousSubmission = std::chrono::steady_clock::now();
            const auto timings = Run(c, 1, &request, stages[path], output, shaders[path]);
            if (frame >= 3) { gpu[path] += timings.gpuMs / 12; cpu[path] += timings.enqueueMs / 12; }
        }
        std::cout << "BENCH FFT " << c->fftSize << " interval " << interval
            << " ms: GPU packed+FFT+aggregation (4/5/6 stages) " << gpu[0] << " / " << gpu[1]
            << " / " << gpu[2] << " ms, CPU enqueue " << cpu[0] << " / " << cpu[1] << " / " << cpu[2] << " ms\n";
    }
}

int main(int argc, char** argv)
{
    try
    {
        std::cout << std::fixed << std::setprecision(6);
        CheckPrecompiledReference();
        CheckPacking();
        std::cout << "GPU timestamp timings: packed input conversion + FFT + aggregation; "
            "mean of 12 runs after 3 warm-up runs, rotating 4/5/6-stage kernels. "
            "Interval is measured between submission starts.\n";
        for (int logN = 12; logN <= 22; ++logN)
        {
            const int size = 1 << logN;
            const int batch = logN == 13 || logN == 15 || logN == 20 ? 3 : 1;
            std::vector<float> window(size);
            for (int i = 0; i < size; ++i)
                window[i] = static_cast<float>(0.5 - 0.5 * std::cos(6.283185307179586 * i / (size - 1)));
            void* handle = nullptr;
            Require(gpufft_create(size, logN, batch, window.data(), &handle) == 0, "GPU FFT creation");
            auto* c = static_cast<GpuFftContext*>(handle);
            ComPtr<ID3D11ComputeShader> five, six;
            const D3D_SHADER_MACRO fiveDefines[] = { { "FFT_STAGES", "5" }, { nullptr, nullptr } };
            const D3D_SHADER_MACRO sixDefines[] = { { "FFT_STAGES", "6" }, { nullptr, nullptr } };
            Require(SUCCEEDED(CompileCs(c->device.Get(), kShaderStockhamFused, &five, fiveDefines)), "5-stage shader");
            Require(SUCCEEDED(CompileCs(c->device.Get(), kShaderStockhamFused, &six, sixDefines)), "6-stage shader");
            Seed(c, batch);
            CheckFloatInput(c, batch);
            Check(c, batch, 2);
            Check(c, batch, 4);
            Check(c, batch, 5, five.Get());
            Check(c, batch, 6, six.Get());
            Check(c, batch, 0);
            std::vector<GpuFftContext::FftPass> kernels;
            for (int profile = 0; profile <= 8; ++profile)
            {
                Require(SUCCEEDED(BuildFftProfile(c, profile, c->fftPlan, &kernels)), "calibration profile compilation");
                std::cout << "PROFILE " << profile << '\n';
                Seed(c, batch);
                Check(c, batch, 0);
                CheckFloatInput(c, batch);
                Seed(c, batch, 1, 30000, 1);
                Check(c, batch, 0);
                if (profile >= 3)
                {
                    c->fftPlan.front().packedShader.Reset();
                    Check(c, batch, 0); // Separate input conversion remains equivalent.
                }
            }
            Require(gpufft_set_profile(handle, 9) != 0, "invalid profile accepted");
            Require(gpufft_set_profile(handle, 0) == 0 && c->fftPlan.empty(), "reference profile reset");
            Seed(c, batch);
            bool checksOnly = argc > 1 && std::string(argv[1]) == "--checks-only";
            if (!checksOnly && (logN == 20 || logN == 22)) Benchmark(c, five.Get(), six.Get());
            gpufft_destroy(handle);
        }
        return 0;
    }
    catch (const std::exception& e)
    {
        std::cerr << e.what() << '\n';
        return 1;
    }
}
