#pragma once
#include <d3d10.h>

struct FftCalibrationResult
{
    int profile;
    int samples;
    double medianMs;
    double p95Ms;
    double cpuMedianMs;
    double gpuMedianMs;
};

static double CalibrationPercentile(std::vector<double> values, double percentile)
{
    std::sort(values.begin(), values.end());
    return values[static_cast<size_t>(std::ceil((values.size() - 1) * percentile))];
}

class FftCalibrationTimer
{
public:
    GpuFftContext* c;
    std::chrono::steady_clock::time_point begun = std::chrono::steady_clock::now();
    double limitMs;
    float dbOffset;
    int error = -68;
    HANDLE waitTimer = CreateWaitableTimerExW(nullptr, nullptr,
        CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_MODIFY_STATE | SYNCHRONIZE);
    ComPtr<ID3D11Query> disjoint, start, end, ready;

    FftCalibrationTimer(GpuFftContext* context, double limit, float offset) : c(context), limitMs(limit), dbOffset(offset) { }
    ~FftCalibrationTimer() { if (waitTimer) CloseHandle(waitTimer); }
    bool Expired() const { return ElapsedMs(begun) >= limitMs; }
    bool CreateQueries()
    {
        D3D11_QUERY_DESC desc = { D3D11_QUERY_EVENT, 0 };
        if (FAILED(c->device->CreateQuery(&desc, &ready))) return false;
        desc.Query = D3D11_QUERY_TIMESTAMP_DISJOINT;
        if (FAILED(c->device->CreateQuery(&desc, &disjoint))) return false;
        desc.Query = D3D11_QUERY_TIMESTAMP;
        return SUCCEEDED(c->device->CreateQuery(&desc, &start)) && SUCCEEDED(c->device->CreateQuery(&desc, &end));
    }
    void Pause(double milliseconds)
    {
        if (milliseconds <= 0) return;
        milliseconds = std::min(milliseconds, std::max(0.0, limitMs - ElapsedMs(begun)));
        LARGE_INTEGER due;
        due.QuadPart = -std::max<LONGLONG>(1, static_cast<LONGLONG>(std::ceil(milliseconds * 10000)));
        if (waitTimer && SetWaitableTimer(waitTimer, &due, 0, nullptr, nullptr, FALSE))
            WaitForSingleObject(waitTimer, static_cast<DWORD>(std::ceil(milliseconds)) + 100);
        else Sleep(static_cast<DWORD>(std::ceil(milliseconds)));
    }

    bool Frame(const std::vector<short>& real, const std::vector<short>& imag,
        const std::vector<int>& offsets, int batch, const SpectrumRequest* request,
        FftCalibrationResult& timing, std::vector<float>* output = nullptr)
    {
        if (Expired()) return false;
        const int count = request ? request->spectrumWidth + request->noiseWidth +
            CeilDiv(request->endBin - request->startBin, 4096) + 1 : c->fftSize * batch;
        if (request && FAILED(PrepareSpectrum(c, *request, count))) return false;
        auto& slot = c->readbackSlots[0];
        if (request && count > slot.spectrumCapacity)
        {
            slot.stagingSpectrum.Reset();
            slot.spectrumCapacity = 0;
            D3D11_BUFFER_DESC desc = {};
            desc.ByteWidth = count * sizeof(float);
            desc.Usage = D3D11_USAGE_STAGING;
            desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            if (FAILED(c->device->CreateBuffer(&desc, nullptr, &slot.stagingSpectrum))) return false;
            slot.spectrumCapacity = count;
        }
        auto submitted = std::chrono::steady_clock::now();
        for (int b = 0; b < batch; ++b)
            PackRingIq(real.data(), imag.data(), static_cast<int>(real.size()), offsets[b],
                c->packedInput.data() + b * c->fftSize, c->fftSize);
        D3D11_BOX inputBox = { 0, 0, 0, static_cast<UINT>(c->fftSize * batch * sizeof(int32_t)), 1, 1 };
        c->context->UpdateSubresource(c->bufPacked.Get(), 0, &inputBox, c->packedInput.data(), 0, 0);
        c->context->Begin(disjoint.Get());
        c->context->End(start.Get());
        if (RunPipeline(c, batch, dbOffset, true, request) != 0) return false;
        c->context->End(end.Get());
        c->context->End(disjoint.Get());
        D3D11_BOX box = { 0, 0, 0, static_cast<UINT>(count * sizeof(float)), 1, 1 };
        auto* staging = request ? slot.stagingSpectrum.Get() : slot.stagingOut.Get();
        c->context->CopySubresourceRegion(staging, 0, 0, 0, 0,
            request ? c->bufSpectrum.Get() : c->bufOut.Get(), 0, &box);
        c->context->End(ready.Get());
        c->context->Flush();
        timing.cpuMedianMs = ElapsedMs(submitted);
        for (;;)
        {
            HRESULT hr = c->context->GetData(ready.Get(), nullptr, 0, D3D11_ASYNC_GETDATA_DONOTFLUSH);
            if (FAILED(hr) || Expired()) return false;
            if (hr == S_OK) break;
            Pause(0.25);
        }
        auto collect = std::chrono::steady_clock::now();
        if (output) output->resize(count);
        D3D11_MAPPED_SUBRESOURCE mapped = {};
        if (FAILED(c->context->Map(staging, 0, D3D11_MAP_READ, 0, &mapped))) return false;
        // Reuse the allocated host storage when benchmarking; no extra full-batch array.
        std::memcpy(output ? output->data() : static_cast<void*>(c->hostComplex.data()), mapped.pData, count * sizeof(float));
        c->context->Unmap(staging, 0);
        timing.cpuMedianMs += ElapsedMs(collect);
        timing.medianMs = ElapsedMs(submitted);
        D3D11_QUERY_DATA_TIMESTAMP_DISJOINT frequency = {};
        UINT64 first = 0, last = 0;
        if (c->context->GetData(disjoint.Get(), &frequency, sizeof(frequency), D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK ||
            c->context->GetData(start.Get(), &first, sizeof(first), D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK ||
            c->context->GetData(end.Get(), &last, sizeof(last), D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK ||
            frequency.Disjoint || frequency.Frequency == 0 || last < first) { error = -80; return false; }
        timing.gpuMedianMs = (last - first) * 1000.0 / frequency.Frequency;
        return true;
    }
};

static void SeedCalibration(std::vector<short>& real, std::vector<short>& imag, int fftSize, bool strong)
{
    const double angle = 6.283185307179586 * (fftSize / 3 + 0.25) / fftSize;
    const double weakAngle = 6.283185307179586 * 17.3 / fftSize;
    double cr = 1, ci = 0, wr = 1, wi = 0;
    const double ca = std::cos(angle), sa = std::sin(angle), cw = std::cos(weakAngle), sw = std::sin(weakAngle);
    const int noise = strong ? 1 : 200, carrier = strong ? 30000 : 2000, weak = strong ? 1 : 20;
    uint32_t random = 420;
    auto sampleNoise = [&]()
    {
        random ^= random << 13; random ^= random >> 17; random ^= random << 5;
        return static_cast<int>(random % (2 * noise + 1)) - noise;
    };
    for (size_t i = 0; i < real.size(); ++i)
    {
        real[i] = static_cast<short>(sampleNoise() + carrier * cr + weak * wr);
        imag[i] = static_cast<short>(sampleNoise() + carrier * ci + weak * wi);
        double next = cr * ca - ci * sa;
        ci = cr * sa + ci * ca; cr = next;
        next = wr * cw - wi * sw;
        wi = wr * sw + wi * cw; wr = next;
    }
}

static bool CalibrationMatches(const std::vector<float>& reference, const std::vector<float>& actual, int dbCount)
{
    if (reference.size() != actual.size()) return false;
    for (size_t i = 0; i < actual.size(); ++i)
    {
        if (!std::isfinite(actual[i]) || !std::isfinite(reference[i])) return false;
        // Require identical dB bins/buckets: a faster profile must not raise the floor.
        if (static_cast<int>(i) < dbCount || i + 1 == actual.size())
        {
            if (actual[i] != reference[i]) return false;
        }
        else if (std::abs(actual[i] - reference[i]) > 0.0001f * std::max(1.0f, std::abs(reference[i]))) return false;
    }
    return true;
}

extern "C" {

__declspec(dllexport) int gpufft_set_profile(void* handle, int profile)
{
    auto* c = static_cast<GpuFftContext*>(handle);
    if (!c) return -60;
    try
    {
        std::vector<GpuFftContext::FftPass> plan;
        if (FAILED(BuildFftProfile(c, profile, plan))) return -61;
        c->fftPlan = std::move(plan);
        return 0;
    }
    catch (...) { return -61; }
}

__declspec(dllexport) int gpufft_get_adapter_identity(void* handle,
    unsigned int* vendor, unsigned int* device, unsigned int* subsystem, unsigned int* revision, long long* driver)
{
    auto* c = static_cast<GpuFftContext*>(handle);
    if (!c || !vendor || !device || !subsystem || !revision || !driver) return -62;
    ComPtr<IDXGIDevice> dxgi;
    ComPtr<IDXGIAdapter> adapter;
    DXGI_ADAPTER_DESC identity = {};
    LARGE_INTEGER version = {};
    if (FAILED(c->device.As(&dxgi)) || FAILED(dxgi->GetAdapter(&adapter)) || FAILED(adapter->GetDesc(&identity))) return -63;
    if (FAILED(adapter->CheckInterfaceSupport(__uuidof(ID3D11Device), &version)) &&
        FAILED(adapter->CheckInterfaceSupport(__uuidof(ID3D10Device), &version))) return -64;
    *vendor = identity.VendorId; *device = identity.DeviceId; *subsystem = identity.SubSysId;
    *revision = identity.Revision; *driver = version.QuadPart;
    return 0;
}

// Call only before reception. Calibration never creates tagged readback submissions.
__declspec(dllexport) int gpufft_calibrate(void* handle, int batch, int step,
    float dbOffset, const SpectrumRequest* request, int timeLimitMs, FftCalibrationResult* result)
{
    auto* c = static_cast<GpuFftContext*>(handle);
    if (!c || !result || batch < 1 || batch > c->maxBatchSize || step < 0 ||
        !std::isfinite(dbOffset) || timeLimitMs < 1 || timeLimitMs > 60000 || (request && (batch != 1 ||
        request->spectrumWidth < 1 || request->spectrumWidth > c->fftSize ||
        request->noiseWidth < 1 || request->noiseWidth > c->fftSize || request->startBin < 0 ||
        request->endBin < request->startBin || request->endBin > c->fftSize))) return -65;
    for (auto& slot : c->readbackSlots) if (slot.pending) return -66;
    *result = {};
    c->fftPlan.clear();
    struct RestoreReference
    {
        GpuFftContext* c;
        bool committed = false;
        ~RestoreReference() { if (!committed) c->fftPlan.clear(); }
    } restore = { c };
    try
    {
        FftCalibrationTimer timer(c, timeLimitMs, dbOffset);
        if (!timer.CreateQueries()) return -67;
        const int64_t inputCount = static_cast<int64_t>(c->fftSize) + static_cast<int64_t>(batch - 1) * step;
        if (inputCount > INT_MAX) return -65;
        std::vector<short> real(static_cast<size_t>(inputCount)), imag(real.size());
        std::vector<int> offsets(batch);
        for (int b = 0; b < batch; ++b) offsets[b] = (batch - 1 - b) * step;
        struct Candidate
        {
            int id;
            std::vector<GpuFftContext::FftPass> plan;
            bool valid = true;
            std::vector<double> elapsed, cpu, gpu;
        };
        std::vector<Candidate> candidates;
        std::vector<GpuFftContext::FftPass> kernels;
        const bool heavy = c->capacity > (1 << 23);
        for (int id = 0; id <= 8; ++id)
        {
            // Keep startup bounded: small transforms avoid costly six-stage compilation.
            // Large transforms compare the reference, classic 4/6, uniform 5 and mixed 6.
            if (c->logN < 20 && id != 0 && id != 1 && id != 3 && id != 4) continue;
            if (c->logN >= 20 && (id == 5 || id == 6)) continue;
            if (heavy && id != 0 && id != 1 && id != 2 && id != 7) continue;
            Candidate candidate = { id };
            if (SUCCEEDED(BuildFftProfile(c, id, candidate.plan, &kernels))) candidates.push_back(std::move(candidate));
            if (timer.Expired()) return -71;
        }
        timer.error = -72;
        std::vector<float> reference, actual, referenceSpectrum;
        FftCalibrationResult sample = {};
        for (bool strong : { false, true })
        {
            SeedCalibration(real, imag, c->fftSize, strong);
            c->fftPlan.clear();
            if (!timer.Frame(real, imag, offsets, 1, nullptr, sample, &reference)) return timer.error;
            if (request && !timer.Frame(real, imag, offsets, 1, request, sample, &referenceSpectrum)) return timer.error;
            for (auto& candidate : candidates)
            {
                if (!candidate.valid) continue;
                c->fftPlan = candidate.plan;
                if (!timer.Frame(real, imag, offsets, 1, nullptr, sample, &actual)) return timer.error;
                candidate.valid = CalibrationMatches(reference, actual, c->fftSize);
                if (candidate.valid && request)
                {
                    if (!timer.Frame(real, imag, offsets, 1, request, sample, &actual)) return timer.error;
                    candidate.valid = CalibrationMatches(referenceSpectrum, actual, request->spectrumWidth + request->noiseWidth);
                }
            }
        }
        candidates.erase(std::remove_if(candidates.begin(), candidates.end(), [](const Candidate& c) { return !c.valid; }), candidates.end());
        c->fftPlan.clear();
        if (candidates.empty()) return -69;
        SeedCalibration(real, imag, c->fftSize, false);
        timer.error = -73;
        auto previous = std::chrono::steady_clock::now();
        // The two qualification inputs already warm each candidate; one paced warm-up
        // precedes five measurements at the normal 10 Hz display cadence.
        constexpr int warm = 1, measured = 5;
        for (int frame = 0; frame < warm + measured; ++frame)
        for (size_t order = 0; order < candidates.size(); ++order)
        {
            auto& candidate = candidates[(frame + order) % candidates.size()];
            timer.Pause(100 - ElapsedMs(previous));
            previous = std::chrono::steady_clock::now();
            c->fftPlan = candidate.plan;
            if (!timer.Frame(real, imag, offsets, batch, request, sample)) { c->fftPlan.clear(); return timer.error; }
            if (frame >= warm)
            {
                candidate.elapsed.push_back(sample.medianMs);
                candidate.cpu.push_back(sample.cpuMedianMs);
                candidate.gpu.push_back(sample.gpuMedianMs);
            }
        }
        auto best = std::min_element(candidates.begin(), candidates.end(), [](const Candidate& a, const Candidate& b)
        {
            return CalibrationPercentile(a.elapsed, 0.5) < CalibrationPercentile(b.elapsed, 0.5);
        });
        c->fftPlan = best->plan;
        *result = { best->id, measured, CalibrationPercentile(best->elapsed, 0.5),
            CalibrationPercentile(best->elapsed, 0.95), CalibrationPercentile(best->cpu, 0.5), CalibrationPercentile(best->gpu, 0.5) };
        restore.committed = true;
        return 0;
    }
    catch (...) { c->fftPlan.clear(); return -70; }
}

} // extern "C"
