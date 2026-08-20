#pragma once

#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <wrl/client.h>

#include <cstdint>
#include <cstring>
#include <new>
#include <vector>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <mutex>
#include <numeric>

#include "gpufft_shaders.h"

using Microsoft::WRL::ComPtr;

struct Float2
{
    float x;
    float y;
};

struct FftParams
{
    uint32_t fftSize;
    uint32_t stage;
    uint32_t batchCount;
    uint32_t pad;
};

struct DbParams
{
    uint32_t fftSize;
    uint32_t batchCount;
    float offset;
    float pad;
};

struct ChannelMapEntry
{
    uint32_t anchor;
    uint32_t phase;
};

struct ChannelMixParams
{
    uint32_t inputCount;
    float phaseStart;
    float phaseStep;
    uint32_t pad;
};

struct ChannelFilterParams
{
    uint32_t outputCount;
    uint32_t tapCount;
    uint32_t phaseCount;
    uint32_t pad;
};

struct SurfaceParams
{
    float width;
    float height;
    float pad0;
    float pad1;
};

struct LineVertex
{
    float x;
    float y;
    uint32_t bgra;
};

static inline UINT CeilDiv(UINT x, UINT y)
{
    return (x + y - 1u) / y;
}

static inline double ElapsedMs(std::chrono::steady_clock::time_point start)
{
    return std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
}

static inline HRESULT CompileCs(ID3D11Device* dev, const char* src, ID3D11ComputeShader** outCs)
{
    ComPtr<ID3DBlob> code;
    ComPtr<ID3DBlob> err;
    HRESULT hr = D3DCompile(src, strlen(src), nullptr, nullptr, nullptr, "main", "cs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &err);
    if (FAILED(hr))
    {
        return hr;
    }
    return dev->CreateComputeShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, outCs);
}

template<typename T>
static inline HRESULT CreateStructuredBuffer(ID3D11Device* dev, int elementCount, UINT bindFlags, ID3D11Buffer** outBuf)
{
    if (outBuf == nullptr || elementCount <= 0) return E_INVALIDARG;
    D3D11_BUFFER_DESC desc = {};
    desc.ByteWidth = static_cast<UINT>(sizeof(T) * elementCount);
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = bindFlags;
    desc.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;
    desc.StructureByteStride = sizeof(T);
    return dev->CreateBuffer(&desc, nullptr, outBuf);
}

static inline HRESULT CreateSrv(ID3D11Device* dev, ID3D11Buffer* buf, int elementCount, ID3D11ShaderResourceView** outSrv)
{
    if (outSrv == nullptr || buf == nullptr || elementCount <= 0) return E_INVALIDARG;
    D3D11_SHADER_RESOURCE_VIEW_DESC desc = {};
    desc.Format = DXGI_FORMAT_UNKNOWN;
    desc.ViewDimension = D3D11_SRV_DIMENSION_BUFFER;
    desc.Buffer.FirstElement = 0;
    desc.Buffer.NumElements = static_cast<UINT>(elementCount);
    return dev->CreateShaderResourceView(buf, &desc, outSrv);
}

static inline HRESULT CreateUav(ID3D11Device* dev, ID3D11Buffer* buf, int elementCount, ID3D11UnorderedAccessView** outUav)
{
    if (outUav == nullptr || buf == nullptr || elementCount <= 0) return E_INVALIDARG;
    D3D11_UNORDERED_ACCESS_VIEW_DESC desc = {};
    desc.Format = DXGI_FORMAT_UNKNOWN;
    desc.ViewDimension = D3D11_UAV_DIMENSION_BUFFER;
    desc.Buffer.FirstElement = 0;
    desc.Buffer.NumElements = static_cast<UINT>(elementCount);
    return dev->CreateUnorderedAccessView(buf, &desc, outUav);
}
