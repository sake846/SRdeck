#include "gpufft_common.h"

class GpuWpfSurfaceContext
{
public:
    int width = 0;
    int height = 0;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11Texture2D> sharedTexture;
    ComPtr<ID3D11Texture2D> scrollScratchTexture;
    HANDLE sharedHandle = nullptr;
    std::vector<uint32_t> cpuBuffer;

    ComPtr<ID3D11VertexShader> lineVs;
    ComPtr<ID3D11PixelShader> linePs;
    ComPtr<ID3D11InputLayout> lineLayout;
    ComPtr<ID3D11Buffer> lineParamsBuffer;
    ComPtr<ID3D11Buffer> lineVertexBuffer;
    ComPtr<ID3D11BlendState> alphaBlend;
    int lineVertexCapacity = 0;
};

class SharedWpfDrawDevice
{
public:
    std::mutex mutex;
    ID3D11Device* device = nullptr;
    ID3D11DeviceContext* context = nullptr;

    HRESULT EnsureCreated()
    {
        if (device != nullptr && context != nullptr) return S_OK;

        D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0 };
        D3D_FEATURE_LEVEL got = D3D_FEATURE_LEVEL_11_0;
        return D3D11CreateDevice(
            nullptr,
            D3D_DRIVER_TYPE_HARDWARE,
            nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            levels,
            ARRAYSIZE(levels),
            D3D11_SDK_VERSION,
            &device,
            &got,
            &context);
    }

    void Release()
    {
        if (context != nullptr) { context->Release(); context = nullptr; }
        if (device != nullptr) { device->Release(); device = nullptr; }
    }
};

static SharedWpfDrawDevice g_wpfDrawDevice;

static HRESULT CreateSharedTextureWithFallback(ID3D11Device* dev, int width, int height, ID3D11Texture2D** outTex)
{
    if (outTex == nullptr) return E_INVALIDARG;
    *outTex = nullptr;

    D3D11_TEXTURE2D_DESC desc = {};
    desc.Width = static_cast<UINT>(width);
    desc.Height = static_cast<UINT>(height);
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.CPUAccessFlags = 0;
    desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED;

    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
    HRESULT hr = dev->CreateTexture2D(&desc, nullptr, outTex);
    if (SUCCEEDED(hr) && *outTex != nullptr) return S_OK;

    desc.BindFlags = 0;
    hr = dev->CreateTexture2D(&desc, nullptr, outTex);
    return hr;
}

static HRESULT CreateScratchTexture(ID3D11Device* dev, int width, int height, ID3D11Texture2D** outTex)
{
    if (outTex == nullptr) return E_INVALIDARG;
    *outTex = nullptr;

    D3D11_TEXTURE2D_DESC desc = {};
    desc.Width = static_cast<UINT>(width);
    desc.Height = static_cast<UINT>(height);
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = 0;
    desc.CPUAccessFlags = 0;
    desc.MiscFlags = 0;
    return dev->CreateTexture2D(&desc, nullptr, outTex);
}

static HRESULT EnsureLinePipeline(GpuWpfSurfaceContext* c, int vertexCount)
{
    if (c == nullptr || c->device == nullptr || c->context == nullptr) return E_INVALIDARG;

    if (c->lineVs == nullptr || c->linePs == nullptr || c->lineLayout == nullptr || c->alphaBlend == nullptr)
    {
        ComPtr<ID3DBlob> vsCode;
        ComPtr<ID3DBlob> psCode;
        ComPtr<ID3DBlob> err;
        HRESULT hr = D3DCompile(kShaderLineDraw, strlen(kShaderLineDraw), nullptr, nullptr, nullptr, "vs_main", "vs_4_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &vsCode, &err);
        if (FAILED(hr)) return hr;
        hr = D3DCompile(kShaderLineDraw, strlen(kShaderLineDraw), nullptr, nullptr, nullptr, "ps_main", "ps_4_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &psCode, &err);
        if (FAILED(hr)) return hr;
        hr = c->device->CreateVertexShader(vsCode->GetBufferPointer(), vsCode->GetBufferSize(), nullptr, &c->lineVs);
        if (FAILED(hr)) return hr;
        hr = c->device->CreatePixelShader(psCode->GetBufferPointer(), psCode->GetBufferSize(), nullptr, &c->linePs);
        if (FAILED(hr)) return hr;

        D3D11_INPUT_ELEMENT_DESC elems[] = {
            { "POSITION", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 0, D3D11_INPUT_PER_VERTEX_DATA, 0 },
            { "COLOR", 0, DXGI_FORMAT_R32_UINT, 0, 8, D3D11_INPUT_PER_VERTEX_DATA, 0 },
        };
        hr = c->device->CreateInputLayout(elems, ARRAYSIZE(elems), vsCode->GetBufferPointer(), vsCode->GetBufferSize(), &c->lineLayout);
        if (FAILED(hr)) return hr;

        D3D11_BLEND_DESC blend = {};
        blend.RenderTarget[0].BlendEnable = TRUE;
        blend.RenderTarget[0].SrcBlend = D3D11_BLEND_SRC_ALPHA;
        blend.RenderTarget[0].DestBlend = D3D11_BLEND_INV_SRC_ALPHA;
        blend.RenderTarget[0].BlendOp = D3D11_BLEND_OP_ADD;
        blend.RenderTarget[0].SrcBlendAlpha = D3D11_BLEND_ONE;
        blend.RenderTarget[0].DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA;
        blend.RenderTarget[0].BlendOpAlpha = D3D11_BLEND_OP_ADD;
        blend.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
        hr = c->device->CreateBlendState(&blend, &c->alphaBlend);
        if (FAILED(hr)) return hr;

        D3D11_BUFFER_DESC cbDesc = {};
        cbDesc.ByteWidth = sizeof(SurfaceParams);
        cbDesc.Usage = D3D11_USAGE_DEFAULT;
        cbDesc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        hr = c->device->CreateBuffer(&cbDesc, nullptr, &c->lineParamsBuffer);
        if (FAILED(hr)) return hr;
    }

    if (vertexCount > c->lineVertexCapacity || c->lineVertexBuffer == nullptr)
    {
        int capacity = std::max(vertexCount, 1024);
        D3D11_BUFFER_DESC desc = {};
        desc.ByteWidth = static_cast<UINT>(sizeof(LineVertex) * capacity);
        desc.Usage = D3D11_USAGE_DYNAMIC;
        desc.BindFlags = D3D11_BIND_VERTEX_BUFFER;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        HRESULT hr = c->device->CreateBuffer(&desc, nullptr, &c->lineVertexBuffer);
        if (FAILED(hr)) return hr;
        c->lineVertexCapacity = capacity;
    }

    return S_OK;
}

static int DrawSurfaceVertices(
    GpuWpfSurfaceContext* c,
    const LineVertex* vertices,
    int vertexCount,
    D3D11_PRIMITIVE_TOPOLOGY topology,
    int clearFirst,
    unsigned int clearBgra,
    int flushAfter)
{
    if (c == nullptr || c->sharedTexture == nullptr || c->context == nullptr || vertices == nullptr) return -130;
    if (vertexCount <= 0) return -131;

    std::lock_guard<std::mutex> guard(g_wpfDrawDevice.mutex);

    HRESULT hr = EnsureLinePipeline(c, vertexCount);
    if (FAILED(hr)) return static_cast<int>(hr);

    D3D11_MAPPED_SUBRESOURCE mapped = {};
    hr = c->context->Map(c->lineVertexBuffer.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);
    if (FAILED(hr)) return static_cast<int>(hr);
    memcpy(mapped.pData, vertices, sizeof(LineVertex) * static_cast<size_t>(vertexCount));
    c->context->Unmap(c->lineVertexBuffer.Get(), 0);

    ComPtr<ID3D11RenderTargetView> rtv;
    hr = c->device->CreateRenderTargetView(c->sharedTexture.Get(), nullptr, &rtv);
    if (FAILED(hr) || rtv == nullptr) return -132;

    if (clearFirst)
    {
        float color[4] = {
            ((clearBgra >> 16) & 0xFF) / 255.0f,
            ((clearBgra >> 8) & 0xFF) / 255.0f,
            ((clearBgra >> 0) & 0xFF) / 255.0f,
            ((clearBgra >> 24) & 0xFF) / 255.0f
        };
        c->context->ClearRenderTargetView(rtv.Get(), color);
    }

    SurfaceParams sp = { static_cast<float>(c->width), static_cast<float>(c->height), 0.0f, 0.0f };
    c->context->UpdateSubresource(c->lineParamsBuffer.Get(), 0, nullptr, &sp, 0, 0);

    D3D11_VIEWPORT vp = {};
    vp.Width = static_cast<float>(c->width);
    vp.Height = static_cast<float>(c->height);
    vp.MinDepth = 0.0f;
    vp.MaxDepth = 1.0f;
    c->context->RSSetViewports(1, &vp);

    ID3D11RenderTargetView* rtvs[1] = { rtv.Get() };
    c->context->OMSetRenderTargets(1, rtvs, nullptr);
    float blendFactor[4] = { 0, 0, 0, 0 };
    c->context->OMSetBlendState(c->alphaBlend.Get(), blendFactor, 0xffffffffu);

    UINT stride = sizeof(LineVertex);
    UINT offset = 0;
    ID3D11Buffer* vb = c->lineVertexBuffer.Get();
    ID3D11Buffer* cb = c->lineParamsBuffer.Get();
    c->context->IASetInputLayout(c->lineLayout.Get());
    c->context->IASetPrimitiveTopology(topology);
    c->context->IASetVertexBuffers(0, 1, &vb, &stride, &offset);
    c->context->VSSetShader(c->lineVs.Get(), nullptr, 0);
    c->context->VSSetConstantBuffers(0, 1, &cb);
    c->context->PSSetShader(c->linePs.Get(), nullptr, 0);
    c->context->Draw(static_cast<UINT>(vertexCount), 0);

    ID3D11RenderTargetView* nullRtv[1] = { nullptr };
    c->context->OMSetRenderTargets(1, nullRtv, nullptr);
    if (flushAfter) c->context->Flush();
    return 0;
}

extern "C" {

__declspec(dllexport) void gpudraw_shutdown()
{
    std::lock_guard<std::mutex> guard(g_wpfDrawDevice.mutex);
    g_wpfDrawDevice.Release();
}

__declspec(dllexport) int gpudraw_create_surface(int width, int height, void** handle, void** sharedHandleOut)
{
    if (handle == nullptr || sharedHandleOut == nullptr || width <= 0 || height <= 0) return -100;
    *handle = nullptr;
    *sharedHandleOut = nullptr;

    auto* c = new (std::nothrow) GpuWpfSurfaceContext();
    if (c == nullptr) return -101;
    c->width = width;
    c->height = height;
    c->cpuBuffer.resize(static_cast<size_t>(width) * static_cast<size_t>(height), 0xFF000000u);

    std::lock_guard<std::mutex> guard(g_wpfDrawDevice.mutex);
    HRESULT hr = g_wpfDrawDevice.EnsureCreated();
    if (FAILED(hr)) { delete c; return -102; }

    c->device = g_wpfDrawDevice.device;
    c->context = g_wpfDrawDevice.context;

    hr = CreateSharedTextureWithFallback(c->device.Get(), width, height, &c->sharedTexture);
    if (FAILED(hr) || c->sharedTexture == nullptr) { delete c; return static_cast<int>(hr); }
    hr = CreateScratchTexture(c->device.Get(), width, height, &c->scrollScratchTexture);
    if (FAILED(hr) || c->scrollScratchTexture == nullptr) { delete c; return static_cast<int>(hr); }

    ComPtr<IDXGIResource> dxgiRes;
    hr = c->sharedTexture.As(&dxgiRes);
    if (FAILED(hr)) { delete c; return static_cast<int>(hr); }
    hr = dxgiRes->GetSharedHandle(&c->sharedHandle);
    if (FAILED(hr) || c->sharedHandle == nullptr) { delete c; return static_cast<int>(FAILED(hr) ? hr : E_FAIL); }

    *handle = c;
    *sharedHandleOut = c->sharedHandle;
    return 0;
}

__declspec(dllexport) void gpudraw_destroy_surface(void* handle)
{
    std::lock_guard<std::mutex> guard(g_wpfDrawDevice.mutex);
    auto* c = reinterpret_cast<GpuWpfSurfaceContext*>(handle);
    delete c;
}

__declspec(dllexport) int gpudraw_clear_surface(void* handle, unsigned int bgra)
{
    auto* c = reinterpret_cast<GpuWpfSurfaceContext*>(handle);
    if (c == nullptr || c->sharedTexture == nullptr || c->context == nullptr) return -110;
    std::lock_guard<std::mutex> guard(g_wpfDrawDevice.mutex);
    float color[4] = {
        ((bgra >> 16) & 0xFF) / 255.0f,
        ((bgra >> 8) & 0xFF) / 255.0f,
        ((bgra >> 0) & 0xFF) / 255.0f,
        ((bgra >> 24) & 0xFF) / 255.0f
    };
    ComPtr<ID3D11RenderTargetView> rtv;
    HRESULT hr = c->device->CreateRenderTargetView(c->sharedTexture.Get(), nullptr, &rtv);
    if (FAILED(hr) || rtv == nullptr) return -111;

    c->context->ClearRenderTargetView(rtv.Get(), color);
    c->context->Flush();
    return 0;
}

__declspec(dllexport) int gpudraw_upload_bgra_surface(void* handle, const unsigned int* pixels, int width, int height)
{
    auto* c = reinterpret_cast<GpuWpfSurfaceContext*>(handle);
    if (c == nullptr || c->sharedTexture == nullptr || c->context == nullptr || pixels == nullptr) return -120;
    if (width != c->width || height != c->height) return -121;
    std::lock_guard<std::mutex> guard(g_wpfDrawDevice.mutex);

    D3D11_BOX box = {};
    box.left = 0;
    box.top = 0;
    box.front = 0;
    box.right = static_cast<UINT>(width);
    box.bottom = static_cast<UINT>(height);
    box.back = 1;

    c->context->UpdateSubresource(c->sharedTexture.Get(), 0, &box, pixels, static_cast<UINT>(width * 4), 0);
    c->context->Flush();
    return 0;
}

__declspec(dllexport) int gpudraw_scroll_upload_top_row(void* handle, const unsigned int* rowPixels, int width)
{
    auto* c = reinterpret_cast<GpuWpfSurfaceContext*>(handle);
    if (c == nullptr || c->sharedTexture == nullptr || c->scrollScratchTexture == nullptr || c->context == nullptr || rowPixels == nullptr) return -170;
    if (width != c->width || c->height <= 0) return -171;
    std::lock_guard<std::mutex> guard(g_wpfDrawDevice.mutex);

    if (c->height > 1)
    {
        c->context->CopyResource(c->scrollScratchTexture.Get(), c->sharedTexture.Get());

        D3D11_BOX srcBox = {};
        srcBox.left = 0;
        srcBox.top = 0;
        srcBox.front = 0;
        srcBox.right = static_cast<UINT>(c->width);
        srcBox.bottom = static_cast<UINT>(c->height - 1);
        srcBox.back = 1;
        c->context->CopySubresourceRegion(c->sharedTexture.Get(), 0, 0, 1, 0, c->scrollScratchTexture.Get(), 0, &srcBox);
    }

    D3D11_BOX rowBox = {};
    rowBox.left = 0;
    rowBox.top = 0;
    rowBox.front = 0;
    rowBox.right = static_cast<UINT>(c->width);
    rowBox.bottom = 1;
    rowBox.back = 1;
    c->context->UpdateSubresource(c->sharedTexture.Get(), 0, &rowBox, rowPixels, static_cast<UINT>(c->width * 4), 0);
    c->context->Flush();
    return 0;
}

__declspec(dllexport) int gpudraw_scroll_upload_row_region(void* handle, const unsigned int* rowPixels, int width, int top, int height, int flushAfter)
{
    auto* c = reinterpret_cast<GpuWpfSurfaceContext*>(handle);
    if (c == nullptr || c->sharedTexture == nullptr || c->scrollScratchTexture == nullptr || c->context == nullptr || rowPixels == nullptr) return -180;
    if (width != c->width || top < 0 || height <= 0 || top + height > c->height) return -181;
    std::lock_guard<std::mutex> guard(g_wpfDrawDevice.mutex);

    if (height > 1)
    {
        c->context->CopyResource(c->scrollScratchTexture.Get(), c->sharedTexture.Get());

        D3D11_BOX srcBox = {};
        srcBox.left = 0;
        srcBox.top = static_cast<UINT>(top);
        srcBox.front = 0;
        srcBox.right = static_cast<UINT>(c->width);
        srcBox.bottom = static_cast<UINT>(top + height - 1);
        srcBox.back = 1;
        c->context->CopySubresourceRegion(c->sharedTexture.Get(), 0, 0, static_cast<UINT>(top + 1), 0, c->scrollScratchTexture.Get(), 0, &srcBox);
    }

    D3D11_BOX rowBox = {};
    rowBox.left = 0;
    rowBox.top = static_cast<UINT>(top);
    rowBox.front = 0;
    rowBox.right = static_cast<UINT>(c->width);
    rowBox.bottom = static_cast<UINT>(top + 1);
    rowBox.back = 1;
    c->context->UpdateSubresource(c->sharedTexture.Get(), 0, &rowBox, rowPixels, static_cast<UINT>(c->width * 4), 0);
    if (flushAfter) c->context->Flush();
    return 0;
}

__declspec(dllexport) int gpudraw_draw_lines(void* handle, const LineVertex* vertices, int vertexCount, int clearFirst, unsigned int clearBgra)
{
    auto* c = reinterpret_cast<GpuWpfSurfaceContext*>(handle);
    if (vertexCount <= 0 || (vertexCount & 1) != 0) return -131;
    return DrawSurfaceVertices(c, vertices, vertexCount, D3D11_PRIMITIVE_TOPOLOGY_LINELIST, clearFirst, clearBgra, 1);
}

__declspec(dllexport) int gpudraw_draw_triangles(void* handle, const LineVertex* vertices, int vertexCount, int clearFirst, unsigned int clearBgra)
{
    auto* c = reinterpret_cast<GpuWpfSurfaceContext*>(handle);
    if (vertexCount <= 0 || (vertexCount % 3) != 0) return -141;
    return DrawSurfaceVertices(c, vertices, vertexCount, D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST, clearFirst, clearBgra, 1);
}

__declspec(dllexport) int gpudraw_draw_lines_ex(void* handle, const LineVertex* vertices, int vertexCount, int clearFirst, unsigned int clearBgra, int flushAfter)
{
    auto* c = reinterpret_cast<GpuWpfSurfaceContext*>(handle);
    if (vertexCount <= 0 || (vertexCount & 1) != 0) return -151;
    return DrawSurfaceVertices(c, vertices, vertexCount, D3D11_PRIMITIVE_TOPOLOGY_LINELIST, clearFirst, clearBgra, flushAfter);
}

__declspec(dllexport) int gpudraw_draw_triangles_ex(void* handle, const LineVertex* vertices, int vertexCount, int clearFirst, unsigned int clearBgra, int flushAfter)
{
    auto* c = reinterpret_cast<GpuWpfSurfaceContext*>(handle);
    if (vertexCount <= 0 || (vertexCount % 3) != 0) return -161;
    return DrawSurfaceVertices(c, vertices, vertexCount, D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST, clearFirst, clearBgra, flushAfter);
}

} // extern "C"
