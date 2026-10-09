#include "gpufft_test_common.h"
#include <dxgi.h>
#include <limits>
#include <string>

using Pass = GpuFftContext::FftPass;

static void Pause(double milliseconds)
{
    struct HighResolutionTimer
    {
        HANDLE handle = CreateWaitableTimerExW(nullptr, nullptr,
            CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_MODIFY_STATE | SYNCHRONIZE);
        ~HighResolutionTimer() { if (handle) CloseHandle(handle); }
    };
    static HighResolutionTimer timer;
    Require(timer.handle != nullptr, "high-resolution timer");
    if (milliseconds <= 0) return;
    LARGE_INTEGER due;
    due.QuadPart = -static_cast<LONGLONG>(std::ceil(milliseconds * 10000));
    Require(SetWaitableTimer(timer.handle, &due, 0, nullptr, nullptr, FALSE) != FALSE, "set timer");
    Require(WaitForSingleObject(timer.handle, INFINITE) == WAIT_OBJECT_0, "wait timer");
}

static std::string Label(const std::vector<Pass>& plan)
{
    std::string result;
    for (const auto& p : plan)
    {
        if (!result.empty()) result += '+';
        result += std::to_string(p.stages) + "x" + std::to_string(p.threads) + (p.packedShader ? "P" : "");
    }
    return result;
}

static const Pass& Find(const std::vector<Pass>& kernels, int stages, UINT threads)
{
    for (const auto& k : kernels)
        if (k.stages == stages && k.threads == threads) return k;
    throw std::runtime_error("kernel missing");
}

static std::vector<Pass> Uniform(int logN, const Pass& kernel, const std::vector<Pass>& kernels)
{
    std::vector<Pass> plan;
    while (logN > 0)
    {
        int stages = std::min(logN, kernel.stages);
        plan.push_back(Find(kernels, stages, stages == 1 ? 64 : kernel.threads));
        if (plan.size() > 1) plan.back().packedShader.Reset();
        logN -= stages;
    }
    return plan;
}

static std::vector<Pass> Baseline(GpuFftContext* c)
{
    std::vector<Pass> plan;
    for (int remaining = c->logN; remaining > 0;)
    {
        int stages = remaining >= 6 ? 6 : remaining >= 4 ? 4 : remaining >= 2 ? 2 : 1;
        auto shader = stages == 6 ? c->csStockhamSix : stages == 4 ? c->csStockhamFour :
            stages == 2 ? c->csStockhamPair : c->csStockham;
        plan.push_back({ stages, 64, shader });
        remaining -= stages;
    }
    return plan;
}

struct Timer
{
    GpuFftContext* c;
    ComPtr<ID3D11Query> disjoint, start, end;
    explicit Timer(GpuFftContext* value) : c(value)
    {
        D3D11_QUERY_DESC desc = { D3D11_QUERY_TIMESTAMP_DISJOINT, 0 };
        Require(SUCCEEDED(c->device->CreateQuery(&desc, &disjoint)), "timer disjoint");
        desc.Query = D3D11_QUERY_TIMESTAMP;
        Require(SUCCEEDED(c->device->CreateQuery(&desc, &start)), "timer start");
        Require(SUCCEEDED(c->device->CreateQuery(&desc, &end)), "timer end");
    }
    void Begin()
    {
        c->context->Begin(disjoint.Get());
        c->context->End(start.Get());
    }
    double Finish()
    {
        c->context->End(end.Get());
        c->context->End(disjoint.Get());
        c->context->Flush();
        D3D11_QUERY_DATA_TIMESTAMP_DISJOINT frequency = {};
        UINT64 first = 0, last = 0;
        auto waiting = std::chrono::steady_clock::now();
        for (;;)
        {
            HRESULT a = c->context->GetData(disjoint.Get(), &frequency, sizeof(frequency), 0);
            HRESULT b = c->context->GetData(start.Get(), &first, sizeof(first), 0);
            HRESULT d = c->context->GetData(end.Get(), &last, sizeof(last), 0);
            Require(SUCCEEDED(a) && SUCCEEDED(b) && SUCCEEDED(d), "timer readback");
            if (a == S_OK && b == S_OK && d == S_OK) break;
            Require(ElapsedMs(waiting) < 5000, "timer timeout");
            Sleep(0);
        }
        Require(!frequency.Disjoint && frequency.Frequency && last >= first, "invalid timer");
        return (last - first) * 1000.0 / frequency.Frequency;
    }
};

static double StageCost(GpuFftContext* c, Timer& timer, int stage, const Pass& kernel)
{
    FftParams params = { static_cast<UINT>(c->fftSize), static_cast<UINT>(stage), 1, 0 };
    double samples[5] = {};
    for (int run = 0; run < 7; ++run)
    {
        timer.Begin();
        c->context->UpdateSubresource(c->cbFft.Get(), 0, nullptr, &params, 0, 0);
        ID3D11Buffer* cb[] = { c->cbFft.Get() };
        bool packed = stage == 0 && kernel.packedShader;
        ID3D11ShaderResourceView* srv[] = { packed ? c->srvPacked.Get() : c->srvA.Get(),
            packed ? c->srvWindow.Get() : nullptr };
        ID3D11UnorderedAccessView* uav[] = { c->uavB.Get() };
        c->context->CSSetConstantBuffers(0, 1, cb);
        c->context->CSSetShaderResources(0, 2, srv);
        c->context->CSSetUnorderedAccessViews(0, 1, uav, nullptr);
        c->context->CSSetShader(packed ? kernel.packedShader.Get() : kernel.shader.Get(), nullptr, 0);
        c->context->Dispatch(CeilDiv(c->fftSize >> kernel.stages, kernel.threads), 1, 1);
        UnbindFft(c->context.Get());
        double ms = timer.Finish();
        if (run >= 2) samples[run - 2] = ms;
    }
    std::sort(std::begin(samples), std::end(samples));
    return samples[2];
}

static std::vector<Pass> BestPlan(GpuFftContext* c, const std::vector<Pass>& kernels)
{
    Timer timer(c);
    std::vector<double> costs(c->logN + 1, std::numeric_limits<double>::infinity());
    std::vector<int> previous(c->logN + 1, -1), choice(c->logN + 1, -1);
    costs[0] = 0;
    for (int stage = 0; stage < c->logN; ++stage)
    {
        for (size_t i = 0; i < kernels.size(); ++i)
        {
            const auto& k = kernels[i];
            int next = stage + k.stages;
            if (next > c->logN) continue;
            double ms = StageCost(c, timer, stage, k);
            std::cout << "STAGE," << c->fftSize << ',' << stage << ',' << k.stages << ',' << k.threads << ',' << ms << '\n';
            if (costs[stage] + ms < costs[next])
            {
                costs[next] = costs[stage] + ms;
                previous[next] = stage;
                choice[next] = static_cast<int>(i);
            }
        }
    }
    std::vector<Pass> plan;
    for (int end = c->logN; end > 0; end = previous[end])
    {
        Require(choice[end] >= 0, "no FFT plan");
        plan.push_back(kernels[choice[end]]);
    }
    std::reverse(plan.begin(), plan.end());
    for (size_t i = 1; i < plan.size(); ++i) plan[i].packedShader.Reset();
    return plan;
}

struct Trial { std::vector<Pass> plan; double gpu = 0; std::string name; };

static void PackBenchmark(const std::vector<short>& real, const std::vector<short>& imag)
{
    int size = static_cast<int>(real.size()), source = size / 3;
    std::vector<int32_t> output(size);
    double scalar = 0, simd = 0;
    uint32_t checksum = 0;
    for (int frame = 0; frame < 25; ++frame)
    for (int order = 0; order < 2; ++order)
    {
        bool vector = (frame + order) % 2 != 0;
        auto begun = std::chrono::steady_clock::now();
        if (vector) PackRingIq(real.data(), imag.data(), size, source, output.data(), size);
        else for (int i = 0; i < size; ++i)
        {
            int at = source + i;
            if (at >= size) at -= size;
            output[i] = static_cast<uint16_t>(real[at]) |
                (static_cast<uint32_t>(static_cast<uint16_t>(imag[at])) << 16);
        }
        double ms = ElapsedMs(begun);
        if (frame >= 5) (vector ? simd : scalar) += ms / 20;
        checksum ^= output[frame % size];
    }
    std::cout << "PACKING," << size << ",scalar_ms=" << scalar << ",sse2_ms=" << simd << ",checksum=" << checksum << '\n';
}

static void Race(GpuFftContext* c, std::vector<Trial>& trials, int interval, int warm, int count)
{
    SpectrumRequest request = { 2048, 1920, c->fftSize / 2 - c->fftSize / 320,
        c->fftSize / 2 + c->fftSize / 320, c->fftSize / 2, 32000000, 0, 200000 };
    std::vector<float> output;
    auto previous = std::chrono::steady_clock::now();
    for (auto& trial : trials) trial.gpu = 0;
    for (int frame = 0; frame < warm + count; ++frame)
    for (size_t order = 0; order < trials.size(); ++order)
    {
        auto& trial = trials[(frame + order) % trials.size()];
        double remaining = interval - ElapsedMs(previous);
        Pause(remaining);
        previous = std::chrono::steady_clock::now();
        c->fftPlan = trial.plan;
        double ms = Run(c, 1, &request, 0, output).gpuMs;
        if (frame >= warm) trial.gpu += ms / count;
    }
    for (const auto& trial : trials)
        std::cout << "RACE," << c->fftSize << ',' << interval << ',' << trial.name << ','
            << Label(trial.plan) << ',' << trial.gpu << '\n';
}

static void PackedRace(int logN, const std::vector<Trial>& proposals, int warm = 5, int count = 20)
{
    // Fresh correctly-sized buffers include the real short packing and upload costs.
    void* handle = nullptr;
    int size = 1 << logN;
    std::vector<float> window(size);
    for (int i = 0; i < size; ++i)
        window[i] = static_cast<float>(0.5 - 0.5 * std::cos(6.283185307179586 * i / (size - 1)));
    Require(gpufft_create(size, logN, 1, window.data(), &handle) == 0, "packed context");
    auto* c = static_cast<GpuFftContext*>(handle);
    Seed(c, 1);
    std::vector<short> real(size), imag(size);
    for (int i = 0; i < size; ++i)
    {
        real[i] = static_cast<int16_t>(c->packedInput[i] & 65535);
        imag[i] = static_cast<int16_t>(static_cast<uint32_t>(c->packedInput[i]) >> 16);
    }
    PackBenchmark(real, imag);
    std::vector<Trial> trials = proposals;
    for (auto& trial : trials)
    for (auto& p : trial.plan)
    {
        if (trial.name == "baseline6" || trial.name == "baseline4")
        {
            p.shader = p.stages == 6 ? c->csStockhamSix : p.stages == 4 ? c->csStockhamFour :
                p.stages == 2 ? c->csStockhamPair : c->csStockham;
            continue;
        }
        std::string stages = std::to_string(p.stages), threads = std::to_string(p.threads);
        const D3D_SHADER_MACRO defines[] = { { "FFT_STAGES", stages.c_str() },
            { "FFT_THREADS", threads.c_str() }, { nullptr, nullptr } };
        p.shader.Reset();
        Require(SUCCEEDED(CompileCs(c->device.Get(), kShaderStockhamFused, &p.shader, defines)), "packed shader");
        if (p.packedShader)
        {
            const D3D_SHADER_MACRO packedDefines[] = { { "FFT_STAGES", stages.c_str() },
                { "FFT_THREADS", threads.c_str() }, { "FFT_PACKED_INPUT", "1" }, { nullptr, nullptr } };
            p.packedShader.Reset();
            Require(SUCCEEDED(CompileCs(c->device.Get(), kShaderStockhamFused, &p.packedShader, packedDefines)), "fused packed shader");
        }
    }
    SpectrumRequest request = { 2048, 1920, size / 2 - size / 320,
        size / 2 + size / 320, size / 2, 32000000, 0, 200000 }, completed = {};
    std::vector<float> output(2048 + 1920 + (request.endBin - request.startBin + 4095) / 4096 + 1);
    std::vector<double> cpu(trials.size()), latency(trials.size());
    std::vector<double> pack(trials.size()), upload(trials.size()), dispatch(trials.size()), flush(trials.size());
    auto previous = std::chrono::steady_clock::now();
    int sourceOffset = 0;
    int64_t tag = 1, completedTag = 0;
    int accepted = 0;
    for (int frame = 0; frame < warm + count; ++frame)
    for (size_t order = 0; order < trials.size(); ++order)
    {
        size_t index = (frame + order) % trials.size();
        c->fftPlan = trials[index].plan;
        double remaining = 100 - ElapsedMs(previous);
        Pause(remaining);
        previous = std::chrono::steady_clock::now();
        int result = ProcessPacked(c, real.data(), imag.data(), size, &sourceOffset, 1, 0,
            tag++, &completedTag, &accepted, output.data(), static_cast<int>(output.size()), &request, &completed);
        Require(result >= 0 && accepted, "packed submission");
        double cpuMs = ElapsedMs(previous);
        GpuFftContext::ReadbackSlot* submitted = nullptr;
        for (auto& slot : c->readbackSlots)
            if (slot.pending && slot.sequence + 1 == c->nextReadbackSequence) submitted = &slot;
        Require(submitted != nullptr, "packed query");
        for (;;)
        {
            HRESULT hr = c->context->GetData(submitted->query.Get(), nullptr, 0, D3D11_ASYNC_GETDATA_DONOTFLUSH);
            Require(SUCCEEDED(hr), "packed completion");
            if (hr == S_OK) break;
            Require(ElapsedMs(previous) < 5000, "packed timeout");
            Pause(0.25);
        }
        if (frame >= warm)
        {
            cpu[index] += cpuMs / count;
            latency[index] += ElapsedMs(previous) / count;
            pack[index] += c->lastPackMs / count;
            upload[index] += c->lastUploadMs / count;
            dispatch[index] += c->lastDispatchMs / count;
            flush[index] += c->lastFlushMs / count;
        }
    }
    for (size_t i = 0; i < trials.size(); ++i)
        std::cout << "PACKED," << size << ',' << trials[i].name << ',' << Label(trials[i].plan)
            << ",cpu_ms=" << cpu[i] << ",ready_ms=" << latency[i]
            << ",pack=" << pack[i] << ",upload=" << upload[i] << ",dispatch=" << dispatch[i] << ",flush=" << flush[i] << '\n';
    gpufft_destroy(handle);
}

int main(int argc, char** argv)
{
    try
    {
        std::cout << std::fixed << std::setprecision(6) << std::unitbuf;
        void* handle = nullptr;
        Require(gpufft_create(1 << 22, 22, 1, nullptr, &handle) == 0, "tuning context");
        auto* c = static_cast<GpuFftContext*>(handle);
        ComPtr<IDXGIDevice> dxgi;
        ComPtr<IDXGIAdapter> adapter;
        DXGI_ADAPTER_DESC description = {};
        Require(SUCCEEDED(c->device.As(&dxgi)) && SUCCEEDED(dxgi->GetAdapter(&adapter)) &&
            SUCCEEDED(adapter->GetDesc(&description)), "adapter identity");
        std::wcout << L"GPU: " << description.Description << L"\n";
        std::cout << "ADAPTER," << description.VendorId << ',' << description.DeviceId << '\n';
        bool confirm = argc > 1 && std::string(argv[1]) == "--confirm";
        bool finalistsOnly = confirm || (argc > 1 && std::string(argv[1]) == "--finalists");
        std::vector<Pass> kernels = { { 1, 64, c->csStockham } };
        for (int stages = 2; stages <= 6; ++stages)
        for (UINT threads : { 32u, 64u, 128u, 256u })
        {
            if (finalistsOnly && stages == 2 && threads != 32 && threads != 64) continue;
            Pass k = { stages, threads, {} };
            std::string s = std::to_string(stages), t = std::to_string(threads);
            const D3D_SHADER_MACRO defines[] = { { "FFT_STAGES", s.c_str() },
                { "FFT_THREADS", t.c_str() }, { nullptr, nullptr } };
            HRESULT hr = CompileCs(c->device.Get(), kShaderStockhamFused, &k.shader, defines);
            if (FAILED(hr))
            {
                std::cout << "SKIP," << stages << ',' << threads << ',' << hr << '\n';
                continue;
            }
            kernels.push_back(k);
            std::cout << "COMPILED," << stages << ',' << threads << '\n';
        }
        for (auto& k : kernels)
        {
            std::string s = std::to_string(k.stages), t = std::to_string(k.threads);
            const D3D_SHADER_MACRO defines[] = { { "FFT_STAGES", s.c_str() },
                { "FFT_THREADS", t.c_str() }, { "FFT_PACKED_INPUT", "1" }, { nullptr, nullptr } };
            Require(SUCCEEDED(CompileCs(c->device.Get(), kShaderStockhamFused, &k.packedShader, defines)), "input fusion shader");
        }
        for (int logN : { 20, 21, 22 })
        {
            c->fftSize = 1 << logN;
            c->logN = logN;
            c->spectrumWidth = c->noiseWidth = 0;
            for (int i = 0; i < c->fftSize; ++i)
                c->windowCopy[i] = static_cast<float>(0.5 - 0.5 * std::cos(6.283185307179586 * i / (c->fftSize - 1)));
            c->context->UpdateSubresource(c->bufWindow.Get(), 0, nullptr, c->windowCopy.data(), 0, 0);
            Seed(c, 1);
            std::vector<Trial> trials;
            if (finalistsOnly)
            {
                if (logN == 20)
                {
                    for (UINT threads : { 32u, 64u })
                        trials.push_back({ Uniform(logN, Find(kernels, 5, threads), kernels), 0,
                            "5x" + std::to_string(threads) });
                }
                else
                {
                    int first = logN == 21 ? 3 : 4;
                    for (UINT inputThreads : { 32u, 256u })
                    for (UINT threads : { 32u, 64u, 128u, 256u })
                    {
                        if (logN == 22 && inputThreads == 256) continue;
                        if (confirm && (inputThreads != 32 || (logN == 21 && threads != 128 && threads != 256) ||
                            (logN == 22 && threads != 32 && threads != 128))) continue;
                        std::vector<Pass> plan = { Find(kernels, first, inputThreads),
                            Find(kernels, 6, threads), Find(kernels, 6, threads), Find(kernels, 6, threads) };
                        for (size_t i = 1; i < plan.size(); ++i) plan[i].packedShader.Reset();
                        trials.push_back({ plan, 0, "first" + std::to_string(inputThreads) + "-body" + std::to_string(threads) });
                    }
                    if (logN == 21 && !confirm)
                    {
                        auto plan = std::vector<Pass> { Find(kernels, 3, 256), Find(kernels, 6, 256),
                            Find(kernels, 6, 256), Find(kernels, 6, 64) };
                        for (size_t i = 1; i < plan.size(); ++i) plan[i].packedShader.Reset();
                        trials.push_back({ plan, 0, "DP" });
                    }
                }
                auto baseline = Baseline(c);
                auto four = Uniform(logN, Find(kernels, 4, 64), kernels);
                four.front().packedShader.Reset();
                trials.push_back({ baseline, 0, "baseline6" });
                trials.push_back({ four, 0, "baseline4" });
                for (const auto& trial : trials)
                {
                    c->fftPlan = trial.plan;
                    Check(c, 1, 0);
                    Seed(c, 1, 1, 30000, 1);
                    Check(c, 1, 0);
                    Seed(c, 1);
                }
                Race(c, trials, 100, confirm ? 10 : 5, confirm ? 40 : 20);
                std::sort(trials.begin(), trials.end(), [](const Trial& a, const Trial& b) { return a.gpu < b.gpu; });
                std::vector<Trial> selected;
                for (const auto& trial : trials)
                    if (trial.name != "baseline6" && trial.name != "baseline4" && selected.size() < 3) selected.push_back(trial);
                selected.push_back({ baseline, 0, "baseline6" });
                selected.push_back({ four, 0, "baseline4" });
                PackedRace(logN, selected, confirm ? 10 : 5, confirm ? 40 : 20);
                std::cout << "FINISHED," << c->fftSize << '\n';
                continue;
            }
            for (const auto& k : kernels)
            {
                if (k.stages == 1) continue;
                Trial trial = { Uniform(logN, k, kernels), 0, std::to_string(k.stages) + "x" + std::to_string(k.threads) };
                c->fftPlan = trial.plan;
                Check(c, 1, 0);
                trials.push_back(trial);
            }
            Race(c, trials, 0, 2, 6);
            std::sort(trials.begin(), trials.end(), [](const Trial& a, const Trial& b) { return a.gpu < b.gpu; });
            trials.resize(std::min<size_t>(6, trials.size()));
            c->fftPlan.clear();
            auto plan = BestPlan(c, kernels);
            std::cout << "DP," << c->fftSize << ',' << Label(plan) << '\n';
            c->fftPlan = plan;
            Check(c, 1, 0);
            trials.push_back({ plan, 0, "DP" });
            auto baseline = Baseline(c);
            trials.push_back({ baseline, 0, "baseline6" });
            Race(c, trials, 100, 5, 15);
            std::sort(trials.begin(), trials.end(), [](const Trial& a, const Trial& b) { return a.gpu < b.gpu; });
            std::vector<Trial> finalists = { trials[0], trials[1], { baseline, 0, "baseline6" } };
            PackedRace(logN, finalists);
            std::cout << "FINISHED," << c->fftSize << '\n';
        }
        gpufft_destroy(handle);
        return 0;
    }
    catch (const std::exception& e)
    {
        std::cerr << e.what() << '\n';
        return 1;
    }
}
