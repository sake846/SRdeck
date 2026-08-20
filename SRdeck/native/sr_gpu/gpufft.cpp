#include "gpufft_common.h"

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

    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11ComputeShader> csPackedToComplex;
    ComPtr<ID3D11ComputeShader> csStockham;
    ComPtr<ID3D11ComputeShader> csDbConvert;
    ComPtr<ID3D11Buffer> cbFft;
    ComPtr<ID3D11Buffer> cbDb;

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

    struct ReadbackSlot
    {
        ComPtr<ID3D11Buffer> stagingOut;
        ComPtr<ID3D11Query> query;
        bool pending = false;
        uint64_t sequence = 0;
        int batchCount = 0;
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

static int RunPipeline(GpuFftContext* c, int batchCount, float offset, bool usePackedInput)
{
    FftParams fftParams = {
        static_cast<uint32_t>(c->fftSize),
        0u,
        static_cast<uint32_t>(batchCount),
        0u
    };

    UINT dispatchX = CeilDiv(static_cast<UINT>(c->fftSize), 64);
    UINT dispatchY = static_cast<UINT>(batchCount);

    if (usePackedInput)
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
    for (int s = 0; s < c->logN; ++s)
    {
        fftParams.stage = static_cast<uint32_t>(s);
        c->context->UpdateSubresource(c->cbFft.Get(), 0, nullptr, &fftParams, 0, 0);
        ID3D11Buffer* cbs[] = { c->cbFft.Get() };
        ID3D11ShaderResourceView* srvs[] = { pingPong ? c->srvA.Get() : c->srvB.Get() };
        ID3D11UnorderedAccessView* uavs[] = { pingPong ? c->uavB.Get() : c->uavA.Get() };
        c->context->CSSetShader(c->csStockham.Get(), nullptr, 0);
        c->context->CSSetConstantBuffers(0, 1, cbs);
        c->context->CSSetShaderResources(0, 1, srvs);
        c->context->CSSetUnorderedAccessViews(0, 1, uavs, nullptr);

        UINT dispatchStockhamX = CeilDiv(static_cast<UINT>(c->fftSize / 2), 64);
        c->context->Dispatch(dispatchStockhamX, dispatchY, 1);
        UnbindFft(c->context.Get());
        pingPong = !pingPong;
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

__declspec(dllexport) int gpufft_process_packed(
    void* handle,
    const short* inputI,
    const short* inputQ,
    int inputLength,
    const int* offsets,
    int batchCount,
    float offset,
    float* outputDbFlat)
{
    auto* c = reinterpret_cast<GpuFftContext*>(handle);
    if (!c || !inputI || !inputQ || !offsets || !outputDbFlat) return -30;
    if (batchCount <= 0 || batchCount > c->maxBatchSize) return -31;
    if (inputLength <= 0) return -34;

    c->lastPackMs = 0.0;
    c->lastUploadMs = 0.0;
    c->lastDispatchMs = 0.0;
    c->lastReadbackMs = 0.0;

    auto t0 = std::chrono::steady_clock::now();
    bool hasOutput = false;
    uint64_t newestOutputSequence = 0;
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
        HRESULT hr = c->context->Map(slot.stagingOut.Get(), 0, D3D11_MAP_READ, 0, &mapped);
        if (FAILED(hr))
        {
            slot.pending = false;
            c->lastReadbackMs = ElapsedMs(t0);
            return -33;
        }
        if (slot.sequence >= newestOutputSequence)
        {
            int copyBatchCount = std::min(slot.batchCount, batchCount);
            memcpy(outputDbFlat, mapped.pData, sizeof(float) * c->fftSize * copyBatchCount);
            newestOutputSequence = slot.sequence;
            hasOutput = true;
        }
        c->context->Unmap(slot.stagingOut.Get(), 0);
        slot.pending = false;
    }
    c->lastReadbackMs = ElapsedMs(t0);

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

    t0 = std::chrono::steady_clock::now();
    const int packedCount = c->fftSize * batchCount;
    if (static_cast<int>(c->packedInput.size()) < packedCount)
    {
        c->packedInput.resize(packedCount);
    }

    for (int b = 0; b < batchCount; ++b)
    {
        int base = b * c->fftSize;
        int sourceIdx = offsets[b] % inputLength;
        if (sourceIdx < 0) sourceIdx += inputLength;
        for (int i = 0; i < c->fftSize; ++i)
        {
            int idx = sourceIdx + i;
            if (idx >= inputLength) idx -= inputLength;
            c->packedInput[base + i] = (static_cast<int32_t>(static_cast<uint16_t>(inputQ[idx])) << 16) | static_cast<uint16_t>(inputI[idx]);
        }
    }
    c->lastPackMs = ElapsedMs(t0);

    t0 = std::chrono::steady_clock::now();
    c->context->UpdateSubresource(c->bufPacked.Get(), 0, nullptr, c->packedInput.data(), 0, 0);
    c->lastUploadMs = ElapsedMs(t0);

    t0 = std::chrono::steady_clock::now();
    int rc = RunPipeline(c, batchCount, offset, true);
    c->lastDispatchMs = ElapsedMs(t0);
    if (rc != 0) return -32;

    t0 = std::chrono::steady_clock::now();
    c->context->CopyResource(freeSlot->stagingOut.Get(), c->bufOut.Get());
    c->context->End(freeSlot->query.Get());
    c->context->Flush();
    freeSlot->pending = true;
    freeSlot->sequence = c->nextReadbackSequence++;
    freeSlot->batchCount = batchCount;
    c->lastReadbackMs += ElapsedMs(t0);
    return hasOutput ? 0 : 1;
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

} // extern "C"
