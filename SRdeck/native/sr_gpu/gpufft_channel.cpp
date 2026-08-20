#include "gpufft_common.h"

class GpuChannelContext
{
public:
    int inputSampleRate = 0;
    int outputSampleRate = 0;
    int coarseFactor = 1;
    int fineFactor = 1;
    int totalFactor = 1;
    int interpolationFactor = 1;
    int resamplerDecimationFactor = 1;
    int firTaps = 0;
    int cicStages = 0;
    int phaseCount = 0;
    int effectiveTapCount = 0;
    double frequencyOffsetHz = 0;
    int64_t totalInputSamples = 0;
    int64_t nextOutputNumerator = 0;
    double nextInputPhase = 0.0;
    std::vector<Float2> history;
    std::vector<Float2> combinedInput;
    std::vector<ChannelMapEntry> outputMap;
    std::vector<float> effectiveTaps;
    int inputCapacity = 0;
    int outputCapacity = 0;
    double lastUploadMs = 0;
    double lastDispatchMs = 0;
    double lastReadbackMs = 0;
    bool outputPending = false;
    int pendingOutputCount = 0;
    std::chrono::steady_clock::time_point dispatchStarted;

    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11ComputeShader> csMix;
    ComPtr<ID3D11ComputeShader> csFilter;
    ComPtr<ID3D11Buffer> cbMix;
    ComPtr<ID3D11Buffer> cbFilter;
    ComPtr<ID3D11Buffer> bufRaw;
    ComPtr<ID3D11ShaderResourceView> srvRaw;
    ComPtr<ID3D11Buffer> bufMixed;
    ComPtr<ID3D11ShaderResourceView> srvMixed;
    ComPtr<ID3D11UnorderedAccessView> uavMixed;
    ComPtr<ID3D11Buffer> bufMap;
    ComPtr<ID3D11ShaderResourceView> srvMap;
    ComPtr<ID3D11Buffer> bufTaps;
    ComPtr<ID3D11ShaderResourceView> srvTaps;
    ComPtr<ID3D11Buffer> bufOutput;
    ComPtr<ID3D11UnorderedAccessView> uavOutput;
    ComPtr<ID3D11Buffer> stagingOutput;
    ComPtr<ID3D11Query> outputQuery;
};

class SharedChannelDevice
{
public:
    std::mutex mutex;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11ComputeShader> csMix;
    ComPtr<ID3D11ComputeShader> csFilter;

    HRESULT EnsureCreated()
    {
        if (device != nullptr && context != nullptr &&
            csMix != nullptr && csFilter != nullptr)
        {
            if (SUCCEEDED(device->GetDeviceRemovedReason())) return S_OK;
            csFilter.Reset();
            csMix.Reset();
            context.Reset();
            device.Reset();
        }

        ComPtr<ID3D11Device> newDevice;
        ComPtr<ID3D11DeviceContext> newContext;
        ComPtr<ID3D11ComputeShader> newMix;
        ComPtr<ID3D11ComputeShader> newFilter;
        D3D_FEATURE_LEVEL requested[] = { D3D_FEATURE_LEVEL_11_0 };
        D3D_FEATURE_LEVEL obtained = D3D_FEATURE_LEVEL_11_0;
        HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, requested, ARRAYSIZE(requested),
            D3D11_SDK_VERSION, &newDevice, &obtained, &newContext);
        if (FAILED(hr) || obtained < D3D_FEATURE_LEVEL_11_0)
            return FAILED(hr) ? hr : E_NOINTERFACE;
        hr = CompileCs(newDevice.Get(), kShaderChannelMix, &newMix);
        if (FAILED(hr)) return hr;
        hr = CompileCs(newDevice.Get(), kShaderChannelFilter, &newFilter);
        if (FAILED(hr)) return hr;

        device = newDevice;
        context = newContext;
        csMix = newMix;
        csFilter = newFilter;
        return S_OK;
    }
};

static SharedChannelDevice g_channelDevice;

static std::vector<double> CreateCicImpulse(int factor, int stages)
{
    std::vector<double> impulse(1, 1.0);
    for (int stage = 0; stage < stages; ++stage)
    {
        std::vector<double> next(impulse.size() + factor - 1, 0.0);
        for (size_t source = 0; source < impulse.size(); ++source)
            for (int tap = 0; tap < factor; ++tap)
                next[source + tap] += impulse[source];
        impulse.swap(next);
    }
    double gain = std::pow(static_cast<double>(factor), stages);
    for (double& value : impulse) value /= gain;
    return impulse;
}

static std::vector<double> CreateCicCompensationImpulse(int factor, int stages)
{
    constexpr int TapCount = 17;
    constexpr int ResponsePoints = 1024;
    constexpr double MaximumGain = 2.0;
    constexpr double Pi = 3.1415926535897932384626433832795;
    if (factor <= 1) return { 1.0 };

    std::vector<double> response(ResponsePoints / 2 + 1);
    for (int bin = 0; bin <= ResponsePoints / 2; ++bin)
    {
        double normalized = bin / static_cast<double>(ResponsePoints);
        double numerator = std::sin(Pi * normalized);
        double denominator = factor * std::sin(Pi * normalized / factor);
        double cicMagnitude = bin == 0 ? 1.0 : std::pow(std::abs(numerator / denominator), stages);
        response[bin] = std::min(MaximumGain, 1.0 / std::max(cicMagnitude, 1e-9));
    }

    std::vector<double> impulse(TapCount);
    int center = (TapCount - 1) / 2;
    for (int tap = 0; tap < TapCount; ++tap)
    {
        int offset = tap - center;
        double coefficient = response[0] + response[ResponsePoints / 2] * std::cos(Pi * offset);
        for (int bin = 1; bin < ResponsePoints / 2; ++bin)
            coefficient += 2.0 * response[bin] * std::cos(
                2.0 * Pi * bin * offset / ResponsePoints);
        impulse[tap] = coefficient / ResponsePoints;
    }
    double dcGain = std::accumulate(impulse.begin(), impulse.end(), 0.0);
    for (double& value : impulse) value /= dcGain;
    return impulse;
}

static std::vector<double> ConvolveStrided(
    const std::vector<double>& source,
    const std::vector<double>& filter,
    int stride)
{
    std::vector<double> result(source.size() + static_cast<size_t>(stride) * (filter.size() - 1), 0.0);
    for (size_t filterTap = 0; filterTap < filter.size(); ++filterTap)
        for (size_t sourceTap = 0; sourceTap < source.size(); ++sourceTap)
            result[sourceTap + filterTap * stride] += source[sourceTap] * filter[filterTap];
    return result;
}

static bool BuildEffectiveChannelTaps(GpuChannelContext* c, double cutoffHz)
{
    auto coarse = CreateCicImpulse(c->coarseFactor, c->cicStages);
    auto fine = CreateCicImpulse(c->fineFactor, c->cicStages);
    auto coarseCompensation = CreateCicCompensationImpulse(c->coarseFactor, c->cicStages);
    auto fineCompensation = CreateCicCompensationImpulse(c->fineFactor, c->cicStages);
    std::vector<double> decimator(
        coarse.size() + static_cast<size_t>(c->coarseFactor) * (fine.size() - 1), 0.0);
    for (size_t fineTap = 0; fineTap < fine.size(); ++fineTap)
        for (size_t coarseTap = 0; coarseTap < coarse.size(); ++coarseTap)
            decimator[coarseTap + fineTap * c->coarseFactor] += coarse[coarseTap] * fine[fineTap];
    decimator = ConvolveStrided(decimator, coarseCompensation, c->coarseFactor);
    decimator = ConvolveStrided(decimator, fineCompensation, c->totalFactor);

    double intermediateRate = c->inputSampleRate / static_cast<double>(c->totalFactor);
    double normalizedCutoff = cutoffHz / intermediateRate;
    if (!(normalizedCutoff > 0.0 && normalizedCutoff < 0.5)) return false;
    c->effectiveTapCount = (c->firTaps - 1) * c->totalFactor +
        static_cast<int>(decimator.size());
    c->effectiveTaps.assign(
        static_cast<size_t>(c->phaseCount) * c->effectiveTapCount, 0.0f);
    double center = (c->firTaps - 1) * 0.5;
    constexpr double Pi = 3.1415926535897932384626433832795;
    for (int phase = 0; phase < c->phaseCount; ++phase)
    {
        double fraction = phase / static_cast<double>(c->phaseCount);
        std::vector<double> resampler(c->firTaps);
        double sum = 0.0;
        for (int tap = 0; tap < c->firTaps; ++tap)
        {
            double distance = tap + fraction - center;
            double sinc = distance == 0.0 ? 2.0 * normalizedCutoff :
                std::sin(2.0 * Pi * normalizedCutoff * distance) / (Pi * distance);
            double window = 0.42 - 0.5 * std::cos(2.0 * Pi * tap / (c->firTaps - 1)) +
                0.08 * std::cos(4.0 * Pi * tap / (c->firTaps - 1));
            resampler[tap] = sinc * window;
            sum += resampler[tap];
        }
        if (std::abs(sum) < 1e-20) return false;
        for (double& value : resampler) value /= sum;
        float* target = c->effectiveTaps.data() +
            static_cast<size_t>(phase) * c->effectiveTapCount;
        for (int resamplerTap = 0; resamplerTap < c->firTaps; ++resamplerTap)
            for (size_t decimatorTap = 0; decimatorTap < decimator.size(); ++decimatorTap)
                target[resamplerTap * c->totalFactor + decimatorTap] +=
                    static_cast<float>(resampler[resamplerTap] * decimator[decimatorTap]);
    }
    return true;
}

static void UnbindChannel(ID3D11DeviceContext* context)
{
    ID3D11ShaderResourceView* nullSrvs[3] = { nullptr, nullptr, nullptr };
    ID3D11UnorderedAccessView* nullUavs[1] = { nullptr };
    context->CSSetShaderResources(0, 3, nullSrvs);
    context->CSSetUnorderedAccessViews(0, 1, nullUavs, nullptr);
    context->CSSetShader(nullptr, nullptr, 0);
}

static HRESULT EnsureChannelBuffers(GpuChannelContext* c, int inputCount, int outputCount)
{
    if (inputCount > c->inputCapacity)
    {
        c->inputCapacity = std::max(inputCount, std::max(1024, c->inputCapacity * 2));
        c->srvRaw.Reset(); c->bufRaw.Reset();
        c->srvMixed.Reset(); c->uavMixed.Reset(); c->bufMixed.Reset();
        HRESULT hr = CreateStructuredBuffer<Float2>(c->device.Get(), c->inputCapacity,
            D3D11_BIND_SHADER_RESOURCE, &c->bufRaw);
        if (FAILED(hr)) return hr;
        hr = CreateSrv(c->device.Get(), c->bufRaw.Get(), c->inputCapacity, &c->srvRaw);
        if (FAILED(hr)) return hr;
        hr = CreateStructuredBuffer<Float2>(c->device.Get(), c->inputCapacity,
            D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS, &c->bufMixed);
        if (FAILED(hr)) return hr;
        hr = CreateSrv(c->device.Get(), c->bufMixed.Get(), c->inputCapacity, &c->srvMixed);
        if (FAILED(hr)) return hr;
        hr = CreateUav(c->device.Get(), c->bufMixed.Get(), c->inputCapacity, &c->uavMixed);
        if (FAILED(hr)) return hr;
    }
    if (outputCount > c->outputCapacity)
    {
        c->outputCapacity = std::max(outputCount, std::max(1024, c->outputCapacity * 2));
        c->srvMap.Reset(); c->bufMap.Reset();
        c->uavOutput.Reset(); c->bufOutput.Reset(); c->stagingOutput.Reset();
        HRESULT hr = CreateStructuredBuffer<ChannelMapEntry>(c->device.Get(), c->outputCapacity,
            D3D11_BIND_SHADER_RESOURCE, &c->bufMap);
        if (FAILED(hr)) return hr;
        hr = CreateSrv(c->device.Get(), c->bufMap.Get(), c->outputCapacity, &c->srvMap);
        if (FAILED(hr)) return hr;
        hr = CreateStructuredBuffer<Float2>(c->device.Get(), c->outputCapacity,
            D3D11_BIND_UNORDERED_ACCESS, &c->bufOutput);
        if (FAILED(hr)) return hr;
        hr = CreateUav(c->device.Get(), c->bufOutput.Get(), c->outputCapacity, &c->uavOutput);
        if (FAILED(hr)) return hr;
        D3D11_BUFFER_DESC staging = {};
        staging.ByteWidth = static_cast<UINT>(sizeof(Float2) * c->outputCapacity);
        staging.Usage = D3D11_USAGE_STAGING;
        staging.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        hr = c->device->CreateBuffer(&staging, nullptr, &c->stagingOutput);
        if (FAILED(hr)) return hr;
    }
    return S_OK;
}

extern "C" {

__declspec(dllexport) int gpuchannel_is_available()
{
    std::lock_guard<std::mutex> guard(g_channelDevice.mutex);
    return SUCCEEDED(g_channelDevice.EnsureCreated()) ? 1 : 0;
}

__declspec(dllexport) int gpuchannel_get_adapter_identity(
    unsigned int* vendorId,
    unsigned int* deviceId,
    unsigned int* subsystemId,
    unsigned int* revision,
    unsigned long long* adapterLuid,
    long long* driverVersion)
{
    if (!vendorId || !deviceId || !subsystemId || !revision || !adapterLuid || !driverVersion)
        return -220;
    std::lock_guard<std::mutex> guard(g_channelDevice.mutex);
    HRESULT hr = g_channelDevice.EnsureCreated();
    if (FAILED(hr)) return -221;
    ComPtr<IDXGIDevice> dxgiDevice;
    hr = g_channelDevice.device.As(&dxgiDevice);
    if (FAILED(hr)) return -222;
    ComPtr<IDXGIAdapter> adapter;
    hr = dxgiDevice->GetAdapter(&adapter);
    if (FAILED(hr)) return -223;
    DXGI_ADAPTER_DESC description = {};
    hr = adapter->GetDesc(&description);
    if (FAILED(hr)) return -224;
    LARGE_INTEGER version = {};
    if (FAILED(adapter->CheckInterfaceSupport(__uuidof(ID3D11Device), &version)))
        version.QuadPart = 0;
    *vendorId = description.VendorId;
    *deviceId = description.DeviceId;
    *subsystemId = description.SubSysId;
    *revision = description.Revision;
    *adapterLuid = (static_cast<unsigned long long>(
        static_cast<unsigned int>(description.AdapterLuid.HighPart)) << 32) |
        description.AdapterLuid.LowPart;
    *driverVersion = version.QuadPart;
    return 0;
}

__declspec(dllexport) int gpuchannel_create(
    int inputSampleRate,
    int outputSampleRate,
    double frequencyOffsetHz,
    int bandwidthHz,
    int coarseFactor,
    int fineFactor,
    int firTaps,
    int cicStages,
    void** handle)
{
    if (!handle || inputSampleRate <= 0 || outputSampleRate <= 0 || bandwidthHz <= 0 ||
        coarseFactor <= 0 || fineFactor <= 0 || firTaps < 2 || cicStages <= 0 ||
        !std::isfinite(frequencyOffsetHz) || std::abs(frequencyOffsetHz) > inputSampleRate * 0.5)
        return -201;
    *handle = nullptr;
    auto* c = new (std::nothrow) GpuChannelContext();
    if (!c) return -202;
    c->inputSampleRate = inputSampleRate;
    c->outputSampleRate = outputSampleRate;
    c->frequencyOffsetHz = frequencyOffsetHz;
    c->coarseFactor = coarseFactor;
    c->fineFactor = fineFactor;
    c->totalFactor = coarseFactor * fineFactor;
    c->firTaps = firTaps;
    c->cicStages = cicStages;
    int64_t numerator = static_cast<int64_t>(outputSampleRate) * c->totalFactor;
    int64_t divisor = std::gcd(numerator, static_cast<int64_t>(inputSampleRate));
    c->interpolationFactor = static_cast<int>(numerator / divisor);
    c->resamplerDecimationFactor = static_cast<int>(inputSampleRate / divisor);
    c->phaseCount = std::min(c->interpolationFactor, 256);
    double intermediateRate = inputSampleRate / static_cast<double>(c->totalFactor);
    double cutoffHz = bandwidthHz * 0.5;
    if (!(cutoffHz > 0.0 && cutoffHz < std::min(intermediateRate, static_cast<double>(outputSampleRate)) * 0.5) ||
        !BuildEffectiveChannelTaps(c, cutoffHz))
    {
        delete c;
        return -203;
    }

    std::lock_guard<std::mutex> guard(g_channelDevice.mutex);
    HRESULT hr = g_channelDevice.EnsureCreated();
    if (FAILED(hr)) { delete c; return -204; }
    c->device = g_channelDevice.device;
    c->context = g_channelDevice.context;
    c->csMix = g_channelDevice.csMix;
    c->csFilter = g_channelDevice.csFilter;
    D3D11_BUFFER_DESC constantBuffer = {};
    constantBuffer.ByteWidth = sizeof(ChannelMixParams);
    constantBuffer.Usage = D3D11_USAGE_DEFAULT;
    constantBuffer.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    hr = c->device->CreateBuffer(&constantBuffer, nullptr, &c->cbMix);
    if (FAILED(hr)) { delete c; return -207; }
    constantBuffer.ByteWidth = sizeof(ChannelFilterParams);
    hr = c->device->CreateBuffer(&constantBuffer, nullptr, &c->cbFilter);
    if (FAILED(hr)) { delete c; return -208; }
    D3D11_QUERY_DESC queryDesc = {};
    queryDesc.Query = D3D11_QUERY_EVENT;
    hr = c->device->CreateQuery(&queryDesc, &c->outputQuery);
    if (FAILED(hr)) { delete c; return -220; }
    int tapElementCount = static_cast<int>(c->effectiveTaps.size());
    hr = CreateStructuredBuffer<float>(c->device.Get(), tapElementCount,
        D3D11_BIND_SHADER_RESOURCE, &c->bufTaps);
    if (FAILED(hr)) { delete c; return -209; }
    c->context->UpdateSubresource(c->bufTaps.Get(), 0, nullptr, c->effectiveTaps.data(), 0, 0);
    hr = CreateSrv(c->device.Get(), c->bufTaps.Get(), tapElementCount, &c->srvTaps);
    if (FAILED(hr)) { delete c; return -210; }
    *handle = c;
    return 0;
}

__declspec(dllexport) void gpuchannel_destroy(void* handle)
{
    std::lock_guard<std::mutex> guard(g_channelDevice.mutex);
    delete reinterpret_cast<GpuChannelContext*>(handle);
}

__declspec(dllexport) int gpuchannel_reset(void* handle)
{
    auto* c = reinterpret_cast<GpuChannelContext*>(handle);
    if (!c) return -211;
    std::lock_guard<std::mutex> guard(g_channelDevice.mutex);
    c->totalInputSamples = 0;
    c->nextOutputNumerator = 0;
    c->nextInputPhase = 0.0;
    c->history.clear();
    c->outputPending = false;
    c->pendingOutputCount = 0;
    return 0;
}

__declspec(dllexport) int gpuchannel_get_output_capacity(void* handle, int inputCount)
{
    auto* c = reinterpret_cast<GpuChannelContext*>(handle);
    if (!c || inputCount < 0) return -1;
    std::lock_guard<std::mutex> guard(g_channelDevice.mutex);
    if (c->outputPending) return -2;
    int64_t newTotal = c->totalInputSamples + inputCount;
    int64_t maximumIntermediateIndex = newTotal / c->totalFactor - 1;
    int64_t numerator = c->nextOutputNumerator;
    int count = 0;
    while (numerator / c->interpolationFactor <= maximumIntermediateIndex)
    {
        ++count;
        numerator += c->resamplerDecimationFactor;
    }
    return count;
}

static int SubmitChannel(
    GpuChannelContext* c,
    const Float2* input,
    int inputCount,
    int outputCapacity,
    int* outputCount)
{
    if (c->outputPending) return -218;
    *outputCount = 0;
    int64_t historyStartGlobal = c->totalInputSamples - static_cast<int64_t>(c->history.size());
    c->combinedInput.clear();
    c->combinedInput.reserve(c->history.size() + inputCount);
    c->combinedInput.insert(c->combinedInput.end(), c->history.begin(), c->history.end());
    if (inputCount > 0) c->combinedInput.insert(c->combinedInput.end(), input, input + inputCount);

    int64_t newTotal = c->totalInputSamples + inputCount;
    int64_t maximumIntermediateIndex = newTotal / c->totalFactor - 1;
    int64_t localNextNumerator = c->nextOutputNumerator;
    c->outputMap.clear();
    while (localNextNumerator / c->interpolationFactor <= maximumIntermediateIndex)
    {
        int64_t sourceIndex = localNextNumerator / c->interpolationFactor;
        int64_t anchorGlobal = (sourceIndex + 1) * c->totalFactor - 1;
        int64_t anchor = anchorGlobal - historyStartGlobal;
        if (anchor < 0 || anchor >= static_cast<int64_t>(c->combinedInput.size())) return -213;
        int64_t remainder = localNextNumerator % c->interpolationFactor;
        int phase = static_cast<int>((remainder * c->phaseCount + c->interpolationFactor / 2LL) /
            c->interpolationFactor) % c->phaseCount;
        c->outputMap.push_back({ static_cast<uint32_t>(anchor), static_cast<uint32_t>(phase) });
        localNextNumerator += c->resamplerDecimationFactor;
    }
    *outputCount = static_cast<int>(c->outputMap.size());
    if (*outputCount > outputCapacity) return -214;

    constexpr double TwoPi = 6.283185307179586476925286766559;
    double step = -TwoPi * c->frequencyOffsetHz / c->inputSampleRate;
    auto commitState = [&]()
    {
        c->totalInputSamples = newTotal;
        c->nextOutputNumerator = localNextNumerator;
        c->nextInputPhase = std::fmod(
            c->nextInputPhase + step * static_cast<double>(inputCount), TwoPi);
        size_t keep = std::min(c->combinedInput.size(),
            static_cast<size_t>(std::max(0, c->effectiveTapCount - 1)));
        c->history.assign(c->combinedInput.end() - keep, c->combinedInput.end());
    };
    if (*outputCount == 0)
    {
        commitState();
        return 0;
    }

    HRESULT hr = EnsureChannelBuffers(c, static_cast<int>(c->combinedInput.size()), *outputCount);
    if (FAILED(hr)) return -215;
    auto started = std::chrono::steady_clock::now();
    D3D11_BOX rawRange = {
        0u, 0u, 0u,
        static_cast<UINT>(sizeof(Float2) * c->combinedInput.size()), 1u, 1u
    };
    D3D11_BOX mapRange = {
        0u, 0u, 0u,
        static_cast<UINT>(sizeof(ChannelMapEntry) * c->outputMap.size()), 1u, 1u
    };
    c->context->UpdateSubresource(c->bufRaw.Get(), 0, &rawRange, c->combinedInput.data(), 0, 0);
    c->context->UpdateSubresource(c->bufMap.Get(), 0, &mapRange, c->outputMap.data(), 0, 0);
    c->lastUploadMs = ElapsedMs(started);

    double startPhase = std::fmod(
        c->nextInputPhase - step * static_cast<double>(c->history.size()), TwoPi);
    ChannelMixParams mixParams = {
        static_cast<uint32_t>(c->combinedInput.size()),
        static_cast<float>(startPhase),
        static_cast<float>(step),
        0u
    };
    started = std::chrono::steady_clock::now();
    c->context->UpdateSubresource(c->cbMix.Get(), 0, nullptr, &mixParams, 0, 0);
    ID3D11Buffer* mixConstants[] = { c->cbMix.Get() };
    ID3D11ShaderResourceView* mixSrvs[] = { c->srvRaw.Get() };
    ID3D11UnorderedAccessView* mixUavs[] = { c->uavMixed.Get() };
    c->context->CSSetShader(c->csMix.Get(), nullptr, 0);
    c->context->CSSetConstantBuffers(0, 1, mixConstants);
    c->context->CSSetShaderResources(0, 1, mixSrvs);
    c->context->CSSetUnorderedAccessViews(0, 1, mixUavs, nullptr);
    c->context->Dispatch(CeilDiv(static_cast<UINT>(c->combinedInput.size()), 64), 1, 1);
    UnbindChannel(c->context.Get());

    ChannelFilterParams filterParams = {
        static_cast<uint32_t>(*outputCount),
        static_cast<uint32_t>(c->effectiveTapCount),
        static_cast<uint32_t>(c->phaseCount),
        0u
    };
    c->context->UpdateSubresource(c->cbFilter.Get(), 0, nullptr, &filterParams, 0, 0);
    ID3D11Buffer* filterConstants[] = { c->cbFilter.Get() };
    ID3D11ShaderResourceView* filterSrvs[] = {
        c->srvMixed.Get(), c->srvTaps.Get(), c->srvMap.Get()
    };
    ID3D11UnorderedAccessView* filterUavs[] = { c->uavOutput.Get() };
    c->context->CSSetShader(c->csFilter.Get(), nullptr, 0);
    c->context->CSSetConstantBuffers(0, 1, filterConstants);
    c->context->CSSetShaderResources(0, 3, filterSrvs);
    c->context->CSSetUnorderedAccessViews(0, 1, filterUavs, nullptr);
    c->context->Dispatch(CeilDiv(static_cast<UINT>(*outputCount), 64), 1, 1);
    UnbindChannel(c->context.Get());
    c->context->CopyResource(c->stagingOutput.Get(), c->bufOutput.Get());
    c->context->End(c->outputQuery.Get());
    c->context->Flush();
    c->dispatchStarted = started;
    c->pendingOutputCount = *outputCount;
    c->outputPending = true;
    commitState();
    return 0;
}

static int CollectChannel(
    GpuChannelContext* c,
    Float2* output,
    int outputCapacity,
    int* outputCount)
{
    if (!c->outputPending) return -219;
    if (outputCapacity < c->pendingOutputCount) return -214;
    constexpr auto MaximumGpuWait = std::chrono::milliseconds(50);
    auto waitStarted = std::chrono::steady_clock::now();
    for (;;)
    {
        HRESULT queryResult = c->context->GetData(
            c->outputQuery.Get(), nullptr, 0, D3D11_ASYNC_GETDATA_DONOTFLUSH);
        if (queryResult == S_OK) break;
        if (FAILED(queryResult) ||
            std::chrono::steady_clock::now() - waitStarted >= MaximumGpuWait)
        {
            c->outputPending = false;
            c->pendingOutputCount = 0;
            return -220;
        }
        SwitchToThread();
    }
    D3D11_MAPPED_SUBRESOURCE mapped = {};
    HRESULT hr = c->context->Map(c->stagingOutput.Get(), 0, D3D11_MAP_READ, 0, &mapped);
    if (FAILED(hr))
    {
        c->outputPending = false;
        c->pendingOutputCount = 0;
        return -216;
    }
    c->lastDispatchMs = ElapsedMs(c->dispatchStarted);
    auto started = std::chrono::steady_clock::now();
    memcpy(output, mapped.pData, sizeof(Float2) * c->pendingOutputCount);
    c->context->Unmap(c->stagingOutput.Get(), 0);
    c->lastReadbackMs = ElapsedMs(started);
    *outputCount = c->pendingOutputCount;
    c->outputPending = false;
    c->pendingOutputCount = 0;
    return 0;
}

__declspec(dllexport) int gpuchannel_submit(
    void* handle,
    const Float2* input,
    int inputCount,
    int outputCapacity,
    int* outputCount)
{
    auto* c = reinterpret_cast<GpuChannelContext*>(handle);
    if (!c || inputCount < 0 || (inputCount > 0 && !input) || !outputCount ||
        outputCapacity < 0) return -212;
    std::lock_guard<std::mutex> guard(g_channelDevice.mutex);
    return SubmitChannel(c, input, inputCount, outputCapacity, outputCount);
}

__declspec(dllexport) int gpuchannel_collect(
    void* handle,
    Float2* output,
    int outputCapacity,
    int* outputCount)
{
    auto* c = reinterpret_cast<GpuChannelContext*>(handle);
    if (!c || !outputCount || outputCapacity < 0 ||
        (outputCapacity > 0 && !output)) return -212;
    std::lock_guard<std::mutex> guard(g_channelDevice.mutex);
    return CollectChannel(c, output, outputCapacity, outputCount);
}

__declspec(dllexport) int gpuchannel_process(
    void* handle,
    const Float2* input,
    int inputCount,
    Float2* output,
    int outputCapacity,
    int* outputCount)
{
    auto* c = reinterpret_cast<GpuChannelContext*>(handle);
    if (!c || inputCount < 0 || (inputCount > 0 && !input) || !outputCount ||
        outputCapacity < 0 || (outputCapacity > 0 && !output)) return -212;
    std::lock_guard<std::mutex> guard(g_channelDevice.mutex);
    int result = SubmitChannel(c, input, inputCount, outputCapacity, outputCount);
    if (result != 0 || *outputCount == 0) return result;
    return CollectChannel(c, output, outputCapacity, outputCount);
}

__declspec(dllexport) int gpuchannel_get_last_timings(
    void* handle, double* uploadMs, double* dispatchMs, double* readbackMs)
{
    auto* c = reinterpret_cast<GpuChannelContext*>(handle);
    if (!c || !uploadMs || !dispatchMs || !readbackMs) return -217;
    std::lock_guard<std::mutex> guard(g_channelDevice.mutex);
    *uploadMs = c->lastUploadMs;
    *dispatchMs = c->lastDispatchMs;
    *readbackMs = c->lastReadbackMs;
    return 0;
}

} // extern "C"
