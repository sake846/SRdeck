#include "gpufft_common.h"
#include <emmintrin.h>
#include <dxgi.h>
#include <string>

class GpuFftContext
{
public:
    static constexpr int ReadbackSlotCount = 3;

    int fftSize = 0;
    int logN = 0;
    int maxBatchSize = 0;
    int capacity = 0;
    std::vector<Float2> hostComplex;
    std::vector<float> windowCopy;
    std::vector<int32_t> packedInput;
    double lastPackMs = 0.0;
    double lastUploadMs = 0.0;
    double lastDispatchMs = 0.0;
    double lastReadbackMs = 0.0;
    double lastCollectMs = 0.0;
    double lastCopyQueueMs = 0.0;
    double lastFlushMs = 0.0;

    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11ComputeShader> csPackedToComplex;
    ComPtr<ID3D11ComputeShader> csStockham;
    ComPtr<ID3D11ComputeShader> csStockhamPair;
    ComPtr<ID3D11ComputeShader> csStockhamFour;
    ComPtr<ID3D11ComputeShader> csStockhamSix;
    struct FftPass
    {
        int stages;
        UINT threads;
        ComPtr<ID3D11ComputeShader> shader;
        ComPtr<ID3D11ComputeShader> packedShader;
    };
    std::vector<FftPass> fftPlan;
    ComPtr<ID3D11ComputeShader> csDbConvert;
    ComPtr<ID3D11ComputeShader> csSpectrumReduce;
    ComPtr<ID3D11Buffer> cbFft;
    ComPtr<ID3D11Buffer> cbDb;
    ComPtr<ID3D11Buffer> cbSpectrum;

    ComPtr<ID3D11Buffer> bufPacked;
    ComPtr<ID3D11ShaderResourceView> srvPacked;
    ComPtr<ID3D11Buffer> bufWindow;
    ComPtr<ID3D11ShaderResourceView> srvWindow;

    ComPtr<ID3D11Buffer> bufA;
    ComPtr<ID3D11Buffer> bufB;
    ComPtr<ID3D11ShaderResourceView> srvA;
    ComPtr<ID3D11ShaderResourceView> srvB;
    ComPtr<ID3D11UnorderedAccessView> uavA;
    ComPtr<ID3D11UnorderedAccessView> uavB;

    ComPtr<ID3D11Buffer> bufOut;
    ComPtr<ID3D11UnorderedAccessView> uavOut;

    struct BinRange { uint32_t first; uint32_t last; };
    std::vector<BinRange> ranges;
    int spectrumWidth = 0;
    int noiseWidth = 0;
    int spectrumCapacity = 0;
    ComPtr<ID3D11Buffer> bufRanges;
    ComPtr<ID3D11ShaderResourceView> srvRanges;
    ComPtr<ID3D11Buffer> bufSpectrum;
    ComPtr<ID3D11UnorderedAccessView> uavSpectrum;

    struct ReadbackSlot
    {
        ComPtr<ID3D11Buffer> stagingOut;
        ComPtr<ID3D11Buffer> stagingSpectrum;
        int spectrumCapacity = 0;
        ComPtr<ID3D11Query> query;
        bool pending = false;
        uint64_t sequence = 0;
        int64_t submissionTag = 0;
        int batchCount = 0;
        bool aggregated = false;
        int outputCount = 0;
        SpectrumRequest spectrumRequest = {};
    };

    ReadbackSlot readbackSlots[ReadbackSlotCount];
    uint64_t nextReadbackSequence = 1;
};

static void UnbindFft(ID3D11DeviceContext* ctx)
{
    ID3D11ShaderResourceView* nullSrv[2] = { nullptr, nullptr };
    ID3D11UnorderedAccessView* nullUav[1] = { nullptr };
    UINT counts[1] = { 0 };
    ctx->CSSetShaderResources(0, 2, nullSrv);
    ctx->CSSetUnorderedAccessViews(0, 1, nullUav, counts);
}

// Profiles are compiled on initialization/calibration only; normal frames allocate nothing.
static HRESULT BuildFftProfile(GpuFftContext* c, int profile, std::vector<GpuFftContext::FftPass>& plan,
    std::vector<GpuFftContext::FftPass>* kernels = nullptr)
{
    if (profile < 0 || profile > 8 || c->logN < 2 || c->logN > 22) return E_INVALIDARG;
    plan.clear();
    if (profile == 0) return S_OK; // Original single-stage reference and safe fallback.
    const UINT bodyThreads = profile == 3 || profile == 5 ? 32 : profile == 7 ? 128 : profile == 8 ? 256 : 64;
    for (int remaining = c->logN; remaining > 0;)
    {
        bool first = plan.empty();
        int stages = profile <= 2 ?
            (profile == 2 && remaining >= 6 ? 6 : remaining >= 4 ? 4 : remaining >= 2 ? 2 : 1) :
            profile <= 4 ? std::min(5, remaining) : first ? (remaining % 6 == 0 ? 6 : remaining % 6) : 6;
        UINT threads = profile >= 5 && first ? 32 : bodyThreads;
        GpuFftContext::FftPass pass = { stages, threads, {} };
        const bool packedInput = first && profile >= 3;
        if (kernels)
            for (const auto& kernel : *kernels)
                if (kernel.stages == stages && kernel.threads == threads)
                {
                    pass.shader = kernel.shader;
                    if (packedInput) pass.packedShader = kernel.packedShader;
                }
        for (const auto& previous : plan)
            if (previous.stages == stages && previous.threads == threads) pass.shader = previous.shader;
        if (!pass.shader && threads == 64)
            pass.shader = stages == 1 ? c->csStockham : stages == 2 ? c->csStockhamPair :
                stages == 4 ? c->csStockhamFour : stages == 6 ? c->csStockhamSix : nullptr;
        std::string s = std::to_string(stages), t = std::to_string(threads);
        const D3D_SHADER_MACRO defines[] = { { "FFT_STAGES", s.c_str() }, { "FFT_THREADS", t.c_str() }, { nullptr, nullptr } };
        if (!pass.shader)
        {
            HRESULT hr = CompileCs(c->device.Get(), kShaderStockhamFused, &pass.shader, defines);
            if (FAILED(hr)) return hr;
        }
        if (packedInput && !pass.packedShader)
        {
            const D3D_SHADER_MACRO packed[] = { { "FFT_STAGES", s.c_str() }, { "FFT_THREADS", t.c_str() },
                { "FFT_PACKED_INPUT", "1" }, { nullptr, nullptr } };
            HRESULT hr = CompileCs(c->device.Get(), kShaderStockhamFused, &pass.packedShader, packed);
            if (FAILED(hr)) return hr;
        }
        if (kernels)
        {
            auto found = std::find_if(kernels->begin(), kernels->end(), [&](const auto& kernel)
                { return kernel.stages == stages && kernel.threads == threads; });
            if (found == kernels->end()) kernels->push_back(pass);
            else if (pass.packedShader) found->packedShader = pass.packedShader;
        }
        plan.push_back(pass);
        remaining -= stages;
    }
    return S_OK;
}

static int RunPipeline(GpuFftContext* c, int batchCount, float offset, bool usePackedInput,
    const SpectrumRequest* spectrumRequest = nullptr, int stagesPerPass = 0,
    ID3D11ComputeShader* wideShader = nullptr)
{
    const bool usePlan = stagesPerPass == 0 && !c->fftPlan.empty();
    const bool fusedInput = usePlan && usePackedInput && c->fftPlan.front().packedShader;
    if (stagesPerPass == 0) stagesPerPass = 1;
    if (stagesPerPass == 6 && !wideShader) wideShader = c->csStockhamSix.Get();
    FftParams fftParams = {
        static_cast<uint32_t>(c->fftSize),
        0u,
        static_cast<uint32_t>(batchCount),
        0u
    };

    // 128 threads keep a 4M FFT within D3D11's 65535-group dispatch limit.
    UINT dispatchX = CeilDiv(static_cast<UINT>(c->fftSize), 128);
    UINT dispatchY = static_cast<UINT>(batchCount);

    if (usePackedInput && !fusedInput)
    {
        c->context->UpdateSubresource(c->cbFft.Get(), 0, nullptr, &fftParams, 0, 0);
        ID3D11Buffer* cbs[] = { c->cbFft.Get() };
        ID3D11ShaderResourceView* srvs[] = { c->srvPacked.Get(), c->srvWindow.Get() };
        ID3D11UnorderedAccessView* uavs[] = { c->uavA.Get() };
        c->context->CSSetShader(c->csPackedToComplex.Get(), nullptr, 0);
        c->context->CSSetConstantBuffers(0, 1, cbs);
        c->context->CSSetShaderResources(0, 2, srvs);
        c->context->CSSetUnorderedAccessViews(0, 1, uavs, nullptr);
        c->context->Dispatch(dispatchX, dispatchY, 1);
        UnbindFft(c->context.Get());
    }

    bool pingPong = true;
    size_t passIndex = 0;
    for (int s = 0; s < c->logN;)
    {
        const auto* pass = usePlan ? &c->fftPlan[passIndex++] : nullptr;
        const int stages = pass ? pass->stages : stagesPerPass > 4 && wideShader && s + stagesPerPass <= c->logN ? stagesPerPass :
            stagesPerPass >= 4 && s + 3 < c->logN ? 4 :
            stagesPerPass >= 2 && s + 1 < c->logN ? 2 : 1;
        fftParams.stage = static_cast<uint32_t>(s);
        c->context->UpdateSubresource(c->cbFft.Get(), 0, nullptr, &fftParams, 0, 0);
        ID3D11Buffer* cbs[] = { c->cbFft.Get() };
        const bool packedPass = fusedInput && s == 0;
        ID3D11ShaderResourceView* srvs[] = {
            packedPass ? c->srvPacked.Get() : pingPong ? c->srvA.Get() : c->srvB.Get(),
            packedPass ? c->srvWindow.Get() : nullptr };
        ID3D11UnorderedAccessView* uavs[] = { pingPong ? c->uavB.Get() : c->uavA.Get() };
        c->context->CSSetShader(packedPass ? pass->packedShader.Get() : pass ? pass->shader.Get() : stages > 4 ? wideShader : stages == 4 ? c->csStockhamFour.Get() :
            stages == 2 ? c->csStockhamPair.Get() : c->csStockham.Get(), nullptr, 0);
        c->context->CSSetConstantBuffers(0, 1, cbs);
        c->context->CSSetShaderResources(0, 2, srvs);
        c->context->CSSetUnorderedAccessViews(0, 1, uavs, nullptr);

        UINT dispatchStockhamX = CeilDiv(static_cast<UINT>(c->fftSize >> stages), pass ? pass->threads : 64);
        c->context->Dispatch(dispatchStockhamX, dispatchY, 1);
        UnbindFft(c->context.Get());
        pingPong = !pingPong;
        s += stages;
    }

    if (spectrumRequest != nullptr)
    {
        const auto& r = *spectrumRequest;
        const UINT partialCount = CeilDiv(static_cast<UINT>(r.endBin - r.startBin), 4096);
        SpectrumParams params = { static_cast<uint32_t>(c->fftSize),
            static_cast<uint32_t>(r.spectrumWidth), static_cast<uint32_t>(r.noiseWidth),
            static_cast<uint32_t>(r.startBin), static_cast<uint32_t>(r.endBin),
            r.centerBin, partialCount, offset };
        c->context->UpdateSubresource(c->cbSpectrum.Get(), 0, nullptr, &params, 0, 0);
        ID3D11Buffer* cbs[] = { c->cbSpectrum.Get() };
        ID3D11ShaderResourceView* srvs[] = {
            pingPong ? c->srvA.Get() : c->srvB.Get(), c->srvRanges.Get() };
        ID3D11UnorderedAccessView* uavs[] = { c->uavSpectrum.Get() };
        c->context->CSSetShader(c->csSpectrumReduce.Get(), nullptr, 0);
        c->context->CSSetConstantBuffers(0, 1, cbs);
        c->context->CSSetShaderResources(0, 2, srvs);
        c->context->CSSetUnorderedAccessViews(0, 1, uavs, nullptr);
        const UINT groups = r.spectrumWidth + r.noiseWidth + partialCount + 1;
        c->context->Dispatch(std::min(groups, 65535u), CeilDiv(groups, 65535), 1);
        UnbindFft(c->context.Get());
        return 0;
    }

    DbParams dbParams = {
        static_cast<uint32_t>(c->fftSize),
        static_cast<uint32_t>(batchCount),
        offset,
        0.0f
    };
    c->context->UpdateSubresource(c->cbDb.Get(), 0, nullptr, &dbParams, 0, 0);
    ID3D11Buffer* dbCbs[] = { c->cbDb.Get() };
    ID3D11ShaderResourceView* dbSrvs[] = { pingPong ? c->srvA.Get() : c->srvB.Get() };
    ID3D11UnorderedAccessView* dbUavs[] = { c->uavOut.Get() };
    c->context->CSSetShader(c->csDbConvert.Get(), nullptr, 0);
    c->context->CSSetConstantBuffers(0, 1, dbCbs);
    c->context->CSSetShaderResources(0, 1, dbSrvs);
    c->context->CSSetUnorderedAccessViews(0, 1, dbUavs, nullptr);
    c->context->Dispatch(dispatchX, dispatchY, 1);
    UnbindFft(c->context.Get());

    return 0;
}

static HRESULT PrepareSpectrum(GpuFftContext* c, const SpectrumRequest& r, int count)
{
    if (!c->csSpectrumReduce || !c->cbSpectrum)
    {
        HRESULT hr = CompileCs(c->device.Get(), kShaderSpectrumReduce, &c->csSpectrumReduce);
        if (FAILED(hr)) return hr;
        D3D11_BUFFER_DESC desc = {};
        desc.ByteWidth = sizeof(SpectrumParams);
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        hr = c->device->CreateBuffer(&desc, nullptr, &c->cbSpectrum);
        if (FAILED(hr)) return hr;
    }
    if (count > c->spectrumCapacity)
    {
        c->spectrumCapacity = 0;
        c->uavSpectrum.Reset();
        c->bufSpectrum.Reset();
        HRESULT hr = CreateStructuredBuffer<float>(c->device.Get(), count,
            D3D11_BIND_UNORDERED_ACCESS, &c->bufSpectrum);
        if (FAILED(hr)) return hr;
        hr = CreateUav(c->device.Get(), c->bufSpectrum.Get(), count, &c->uavSpectrum);
        if (FAILED(hr)) return hr;
        c->spectrumCapacity = count;
    }
    if (c->spectrumWidth != r.spectrumWidth || c->noiseWidth != r.noiseWidth)
    {
        c->spectrumWidth = c->noiseWidth = 0;
        // Build exact bucket boundaries once per layout, not once per frame.
        const int rangeCount = r.spectrumWidth + r.noiseWidth;
        c->ranges.resize(rangeCount);
        int offset = 0;
        for (int width : { r.spectrumWidth, r.noiseWidth })
        {
            for (int i = 0; i < width; ++i)
                c->ranges[offset + i] = {
                    static_cast<uint32_t>(static_cast<int64_t>(i) * c->fftSize / width),
                    static_cast<uint32_t>(static_cast<int64_t>(i + 1) * c->fftSize / width) };
            offset += width;
        }
        c->srvRanges.Reset();
        c->bufRanges.Reset();
        HRESULT hr = CreateStructuredBuffer<GpuFftContext::BinRange>(c->device.Get(), rangeCount,
            D3D11_BIND_SHADER_RESOURCE, &c->bufRanges);
        if (FAILED(hr)) return hr;
        hr = CreateSrv(c->device.Get(), c->bufRanges.Get(), rangeCount, &c->srvRanges);
        if (FAILED(hr)) return hr;
        c->context->UpdateSubresource(c->bufRanges.Get(), 0, nullptr, c->ranges.data(), 0, 0);
        c->spectrumWidth = r.spectrumWidth;
        c->noiseWidth = r.noiseWidth;
    }
    return S_OK;
}

extern "C" {

__declspec(dllexport) int gpufft_create(int fftSize, int logN, int maxBatchSize, const float* window, void** handle)
{
    if (handle == nullptr || fftSize <= 0 || logN <= 0 || maxBatchSize <= 0) return -1;
    *handle = nullptr;

    auto* c = new (std::nothrow) GpuFftContext();
    if (c == nullptr) return -2;
    c->fftSize = fftSize;
    c->logN = logN;
    c->maxBatchSize = maxBatchSize;
    c->capacity = fftSize * maxBatchSize;
    c->hostComplex.resize(c->capacity);
    c->windowCopy.resize(fftSize);
    c->packedInput.resize(c->capacity);
    if (window != nullptr)
    {
        std::copy(window, window + fftSize, c->windowCopy.begin());
    }
    else
    {
        std::fill(c->windowCopy.begin(), c->windowCopy.end(), 1.0f);
    }

    D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0 };
    D3D_FEATURE_LEVEL got = D3D_FEATURE_LEVEL_11_0;
    HRESULT hr = D3D11CreateDevice(
        nullptr,
        D3D_DRIVER_TYPE_HARDWARE,
        nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        levels,
        ARRAYSIZE(levels),
        D3D11_SDK_VERSION,
        &c->device,
        &got,
        &c->context);
    if (FAILED(hr)) { delete c; return -3; }

    hr = CompileCs(c->device.Get(), kShaderPackedToComplex, &c->csPackedToComplex);
    if (FAILED(hr)) { delete c; return -4; }
    hr = CompileCs(c->device.Get(), kShaderStockham, &c->csStockham);
    if (FAILED(hr)) { delete c; return -5; }
    hr = CompileCs(c->device.Get(), kShaderStockhamPair, &c->csStockhamPair);
    if (FAILED(hr)) { delete c; return -5; }
    hr = CompileCs(c->device.Get(), kShaderStockhamFused, &c->csStockhamFour);
    if (FAILED(hr)) { delete c; return -5; }
    if (fftSize >= (1 << 20))
    {
        const D3D_SHADER_MACRO defines[] = { { "FFT_STAGES", "6" }, { nullptr, nullptr } };
        // Keep the 4-stage path available if this larger shader cannot be created.
        hr = CompileCs(c->device.Get(), kShaderStockhamFused, &c->csStockhamSix, defines);
        if (FAILED(hr)) c->csStockhamSix.Reset();
    }
    hr = CompileCs(c->device.Get(), kShaderDbConvert, &c->csDbConvert);
    if (FAILED(hr)) { delete c; return -6; }

    D3D11_BUFFER_DESC cbDesc = {};
    cbDesc.ByteWidth = sizeof(FftParams);
    cbDesc.Usage = D3D11_USAGE_DEFAULT;
    cbDesc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    hr = c->device->CreateBuffer(&cbDesc, nullptr, &c->cbFft);
    if (FAILED(hr)) { delete c; return -7; }
    cbDesc.ByteWidth = sizeof(DbParams);
    hr = c->device->CreateBuffer(&cbDesc, nullptr, &c->cbDb);
    if (FAILED(hr)) { delete c; return -8; }

    hr = CreateStructuredBuffer<int32_t>(c->device.Get(), c->capacity, D3D11_BIND_SHADER_RESOURCE, &c->bufPacked);
    if (FAILED(hr)) { delete c; return -9; }
    hr = CreateSrv(c->device.Get(), c->bufPacked.Get(), c->capacity, &c->srvPacked);
    if (FAILED(hr)) { delete c; return -10; }

    hr = CreateStructuredBuffer<float>(c->device.Get(), c->fftSize, D3D11_BIND_SHADER_RESOURCE, &c->bufWindow);
    if (FAILED(hr)) { delete c; return -11; }
    c->context->UpdateSubresource(c->bufWindow.Get(), 0, nullptr, c->windowCopy.data(), 0, 0);
    hr = CreateSrv(c->device.Get(), c->bufWindow.Get(), c->fftSize, &c->srvWindow);
    if (FAILED(hr)) { delete c; return -12; }

    hr = CreateStructuredBuffer<Float2>(c->device.Get(), c->capacity, D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS, &c->bufA);
    if (FAILED(hr)) { delete c; return -13; }
    hr = CreateStructuredBuffer<Float2>(c->device.Get(), c->capacity, D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS, &c->bufB);
    if (FAILED(hr)) { delete c; return -14; }
    hr = CreateSrv(c->device.Get(), c->bufA.Get(), c->capacity, &c->srvA);
    if (FAILED(hr)) { delete c; return -15; }
    hr = CreateSrv(c->device.Get(), c->bufB.Get(), c->capacity, &c->srvB);
    if (FAILED(hr)) { delete c; return -16; }
    hr = CreateUav(c->device.Get(), c->bufA.Get(), c->capacity, &c->uavA);
    if (FAILED(hr)) { delete c; return -17; }
    hr = CreateUav(c->device.Get(), c->bufB.Get(), c->capacity, &c->uavB);
    if (FAILED(hr)) { delete c; return -18; }

    hr = CreateStructuredBuffer<float>(c->device.Get(), c->capacity, D3D11_BIND_UNORDERED_ACCESS, &c->bufOut);
    if (FAILED(hr)) { delete c; return -19; }
    hr = CreateUav(c->device.Get(), c->bufOut.Get(), c->capacity, &c->uavOut);
    if (FAILED(hr)) { delete c; return -20; }

    D3D11_BUFFER_DESC stDesc = {};
    stDesc.ByteWidth = static_cast<UINT>(sizeof(float) * c->capacity);
    stDesc.Usage = D3D11_USAGE_STAGING;
    stDesc.BindFlags = 0;
    stDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    stDesc.MiscFlags = 0;
    stDesc.StructureByteStride = 0;

    D3D11_QUERY_DESC queryDesc = {};
    queryDesc.Query = D3D11_QUERY_EVENT;
    queryDesc.MiscFlags = 0;
    for (int i = 0; i < GpuFftContext::ReadbackSlotCount; ++i)
    {
        hr = c->device->CreateBuffer(&stDesc, nullptr, &c->readbackSlots[i].stagingOut);
        if (FAILED(hr)) { delete c; return -21; }
        hr = c->device->CreateQuery(&queryDesc, &c->readbackSlots[i].query);
        if (FAILED(hr)) { delete c; return -22; }
    }

    *handle = c;
    return 0;
}

__declspec(dllexport) void gpufft_destroy(void* handle)
{
    auto* c = reinterpret_cast<GpuFftContext*>(handle);
    delete c;
}

} // extern "C"

static void PackIq(const short* real, const short* imag, int32_t* output, int count)
{
    int i = 0;
    // SSE2 is available on every Windows x64 target. Unaligned loads handle ring offsets.
    for (; i + 8 <= count; i += 8)
    {
        const __m128i r = _mm_loadu_si128(reinterpret_cast<const __m128i*>(real + i));
        const __m128i q = _mm_loadu_si128(reinterpret_cast<const __m128i*>(imag + i));
        _mm_storeu_si128(reinterpret_cast<__m128i*>(output + i), _mm_unpacklo_epi16(r, q));
        _mm_storeu_si128(reinterpret_cast<__m128i*>(output + i + 4), _mm_unpackhi_epi16(r, q));
    }
    for (; i < count; ++i)
        output[i] = static_cast<uint16_t>(real[i]) |
            (static_cast<uint32_t>(static_cast<uint16_t>(imag[i])) << 16);
}

static void PackRingIq(const short* real, const short* imag, int inputLength,
    int sourceOffset, int32_t* output, int count)
{
    int source = sourceOffset % inputLength;
    if (source < 0) source += inputLength;
    while (count > 0)
    {
        const int chunk = std::min(count, inputLength - source);
        PackIq(real + source, imag + source, output, chunk);
        output += chunk;
        count -= chunk;
        source = 0;
    }
}

static int ProcessPacked(
    void* handle,
    const short* inputI,
    const short* inputQ,
    int inputLength,
    const int* offsets,
    int batchCount,
    float offset,
    int64_t submissionTag,
    int64_t* completedTag,
    int* inputAccepted,
    float* outputDbFlat,
    int outputCapacity,
    const SpectrumRequest* spectrumRequest,
    SpectrumRequest* completedSpectrumRequest)
{
    auto* c = reinterpret_cast<GpuFftContext*>(handle);
    if (!c || !inputI || !inputQ || !offsets || !completedTag || !inputAccepted || !outputDbFlat) return -30;
    if (batchCount <= 0 || batchCount > c->maxBatchSize) return -31;
    if (inputLength <= 0) return -34;
    if (spectrumRequest != nullptr && (batchCount != 1 || !completedSpectrumRequest ||
        spectrumRequest->spectrumWidth <= 0 || spectrumRequest->spectrumWidth > c->fftSize ||
        spectrumRequest->noiseWidth <= 0 || spectrumRequest->noiseWidth > c->fftSize ||
        spectrumRequest->startBin < 0 || spectrumRequest->endBin < spectrumRequest->startBin ||
        spectrumRequest->endBin > c->fftSize)) return -35;

    *completedTag = 0;
    *inputAccepted = 0;

    c->lastPackMs = 0.0;
    c->lastUploadMs = 0.0;
    c->lastDispatchMs = 0.0;
    c->lastReadbackMs = 0.0;
    c->lastCollectMs = c->lastCopyQueueMs = c->lastFlushMs = 0.0;

    auto t0 = std::chrono::steady_clock::now();
    bool hasOutput = false;
    uint64_t newestOutputSequence = 0;
    int64_t newestOutputTag = 0;
    for (int i = 0; i < GpuFftContext::ReadbackSlotCount; ++i)
    {
        auto& slot = c->readbackSlots[i];
        if (!slot.pending) continue;

        HRESULT qhr = c->context->GetData(slot.query.Get(), nullptr, 0, D3D11_ASYNC_GETDATA_DONOTFLUSH);
        if (qhr == S_FALSE) continue;
        if (FAILED(qhr))
        {
            slot.pending = false;
            c->lastReadbackMs = ElapsedMs(t0);
            return -33;
        }

        D3D11_MAPPED_SUBRESOURCE mapped = {};
        ID3D11Buffer* staging = slot.aggregated ? slot.stagingSpectrum.Get() : slot.stagingOut.Get();
        HRESULT hr = c->context->Map(staging, 0, D3D11_MAP_READ, 0, &mapped);
        if (FAILED(hr))
        {
            slot.pending = false;
            c->lastReadbackMs = ElapsedMs(t0);
            return -33;
        }
        if (slot.sequence >= newestOutputSequence && slot.aggregated == (spectrumRequest != nullptr))
        {
            int count = slot.aggregated ? slot.outputCount : c->fftSize * std::min(slot.batchCount, batchCount);
            if (count > outputCapacity)
            {
                c->context->Unmap(staging, 0);
                return -36;
            }
            memcpy(outputDbFlat, mapped.pData, sizeof(float) * count);
            if (slot.aggregated) *completedSpectrumRequest = slot.spectrumRequest;
            newestOutputSequence = slot.sequence;
            newestOutputTag = slot.submissionTag;
            hasOutput = true;
        }
        c->context->Unmap(staging, 0);
        slot.pending = false;
    }
    c->lastReadbackMs = ElapsedMs(t0);
    c->lastCollectMs = c->lastReadbackMs;
    if (hasOutput) *completedTag = newestOutputTag;

    GpuFftContext::ReadbackSlot* freeSlot = nullptr;
    for (int i = 0; i < GpuFftContext::ReadbackSlotCount; ++i)
    {
        if (!c->readbackSlots[i].pending)
        {
            freeSlot = &c->readbackSlots[i];
            break;
        }
    }
    if (freeSlot == nullptr)
    {
        return hasOutput ? 0 : 1;
    }

    int outputCount = c->fftSize * batchCount;
    if (spectrumRequest != nullptr)
    {
        outputCount = spectrumRequest->spectrumWidth + spectrumRequest->noiseWidth +
            CeilDiv(spectrumRequest->endBin - spectrumRequest->startBin, 4096) + 1;
        if (FAILED(PrepareSpectrum(c, *spectrumRequest, outputCount))) return -37;
        if (outputCount > freeSlot->spectrumCapacity)
        {
            freeSlot->stagingSpectrum.Reset();
            freeSlot->spectrumCapacity = 0;
            D3D11_BUFFER_DESC desc = {};
            desc.ByteWidth = sizeof(float) * outputCount;
            desc.Usage = D3D11_USAGE_STAGING;
            desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            if (FAILED(c->device->CreateBuffer(&desc, nullptr, &freeSlot->stagingSpectrum))) return -37;
            freeSlot->spectrumCapacity = outputCount;
        }
    }

    t0 = std::chrono::steady_clock::now();
    const int packedCount = c->fftSize * batchCount;
    if (static_cast<int>(c->packedInput.size()) < packedCount)
    {
        c->packedInput.resize(packedCount);
    }

    for (int b = 0; b < batchCount; ++b)
    {
        PackRingIq(inputI, inputQ, inputLength, offsets[b],
            c->packedInput.data() + b * c->fftSize, c->fftSize);
    }
    c->lastPackMs = ElapsedMs(t0);

    t0 = std::chrono::steady_clock::now();
    c->context->UpdateSubresource(c->bufPacked.Get(), 0, nullptr, c->packedInput.data(), 0, 0);
    c->lastUploadMs = ElapsedMs(t0);

    t0 = std::chrono::steady_clock::now();
    int rc = RunPipeline(c, batchCount, offset, true, spectrumRequest);
    c->lastDispatchMs = ElapsedMs(t0);
    if (rc != 0) return -32;

    t0 = std::chrono::steady_clock::now();
    if (spectrumRequest != nullptr)
    {
        D3D11_BOX box = { 0, 0, 0, static_cast<UINT>(sizeof(float) * outputCount), 1, 1 };
        c->context->CopySubresourceRegion(freeSlot->stagingSpectrum.Get(), 0, 0, 0, 0,
            c->bufSpectrum.Get(), 0, &box);
    }
    else
        c->context->CopyResource(freeSlot->stagingOut.Get(), c->bufOut.Get());
    c->context->End(freeSlot->query.Get());
    c->lastCopyQueueMs = ElapsedMs(t0);
    auto flushStart = std::chrono::steady_clock::now();
    c->context->Flush();
    c->lastFlushMs = ElapsedMs(flushStart);
    freeSlot->pending = true;
    freeSlot->sequence = c->nextReadbackSequence++;
    freeSlot->submissionTag = submissionTag;
    freeSlot->batchCount = batchCount;
    freeSlot->aggregated = spectrumRequest != nullptr;
    freeSlot->outputCount = outputCount;
    if (spectrumRequest != nullptr) freeSlot->spectrumRequest = *spectrumRequest;
    *inputAccepted = 1;
    c->lastReadbackMs += ElapsedMs(t0);
    return hasOutput ? 0 : 1;
}

extern "C" {

__declspec(dllexport) int gpufft_process_packed(
    void* handle, const short* inputI, const short* inputQ, int inputLength,
    const int* offsets, int batchCount, float offset, int64_t submissionTag,
    int64_t* completedTag, int* inputAccepted, float* outputDbFlat)
{
    auto* c = reinterpret_cast<GpuFftContext*>(handle);
    return ProcessPacked(handle, inputI, inputQ, inputLength, offsets, batchCount, offset,
        submissionTag, completedTag, inputAccepted, outputDbFlat,
        c ? c->capacity : 0, nullptr, nullptr);
}

__declspec(dllexport) int gpufft_process_spectrum(
    void* handle, const short* inputI, const short* inputQ, int inputLength,
    const int* offsets, float offset, int64_t submissionTag,
    int64_t* completedTag, int* inputAccepted, float* output, int outputCapacity,
    const SpectrumRequest* request, SpectrumRequest* completedRequest)
{
    if (!request) return -35;
    return ProcessPacked(handle, inputI, inputQ, inputLength, offsets, 1, offset,
        submissionTag, completedTag, inputAccepted, output, outputCapacity, request, completedRequest);
}

__declspec(dllexport) int gpufft_process_float(
    void* handle,
    const float* inputIFlat,
    const float* inputQFlat,
    int batchCount,
    float offset,
    float* outputDbFlat)
{
    auto* c = reinterpret_cast<GpuFftContext*>(handle);
    if (!c || !inputIFlat || !inputQFlat || !outputDbFlat) return -40;
    if (batchCount <= 0 || batchCount > c->maxBatchSize) return -41;

    const int count = c->fftSize * batchCount;
    for (int i = 0; i < count; ++i)
    {
        c->hostComplex[i].x = inputIFlat[i];
        c->hostComplex[i].y = inputQFlat[i];
    }

    c->context->UpdateSubresource(c->bufA.Get(), 0, nullptr, c->hostComplex.data(), 0, 0);
    int rc = RunPipeline(c, batchCount, offset, false);
    if (rc != 0) return -42;

    auto& slot = c->readbackSlots[0];
    c->context->CopyResource(slot.stagingOut.Get(), c->bufOut.Get());
    D3D11_MAPPED_SUBRESOURCE mapped = {};
    HRESULT hr = c->context->Map(slot.stagingOut.Get(), 0, D3D11_MAP_READ, 0, &mapped);
    if (FAILED(hr)) return -43;
    memcpy(outputDbFlat, mapped.pData, sizeof(float) * count);
    c->context->Unmap(slot.stagingOut.Get(), 0);
    return 0;
}

__declspec(dllexport) int gpufft_get_last_timings(
    void* handle,
    double* packMs,
    double* uploadMs,
    double* dispatchMs,
    double* readbackMs)
{
    auto* c = reinterpret_cast<GpuFftContext*>(handle);
    if (!c || !packMs || !uploadMs || !dispatchMs || !readbackMs) return -50;
    *packMs = c->lastPackMs;
    *uploadMs = c->lastUploadMs;
    *dispatchMs = c->lastDispatchMs;
    *readbackMs = c->lastReadbackMs;
    return 0;
}

__declspec(dllexport) int gpufft_get_last_readback_timings(
    void* handle, double* collectMs, double* copyQueueMs, double* flushMs)
{
    auto* c = reinterpret_cast<GpuFftContext*>(handle);
    if (!c || !collectMs || !copyQueueMs || !flushMs) return -50;
    *collectMs = c->lastCollectMs;
    *copyQueueMs = c->lastCopyQueueMs;
    *flushMs = c->lastFlushMs;
    return 0;
}

} // extern "C"

#include "gpufft_calibration.h"
