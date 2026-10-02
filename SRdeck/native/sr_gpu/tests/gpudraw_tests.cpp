// Include the implementation so readback needs no production API or dependency.
#include "../gpufft_draw.cpp"
#include <iostream>
#include <stdexcept>

static void Require(bool condition, const char* message)
{
    if (!condition) throw std::runtime_error(message);
}

static void CheckPixels(GpuWpfSurfaceContext* c, const std::vector<uint32_t>& expected)
{
    D3D11_TEXTURE2D_DESC desc = {};
    c->sharedTexture->GetDesc(&desc);
    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = 0;
    desc.MiscFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> staging;
    Require(SUCCEEDED(c->device->CreateTexture2D(&desc, nullptr, &staging)), "readback texture");
    c->context->CopyResource(staging.Get(), c->sharedTexture.Get());
    D3D11_MAPPED_SUBRESOURCE mapped = {};
    Require(SUCCEEDED(c->context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped)), "readback map");
    bool equal = true;
    for (int y = 0; y < c->height; y++)
        equal &= std::memcmp(static_cast<const char*>(mapped.pData) + y * mapped.RowPitch,
            expected.data() + y * c->width, c->width * sizeof(uint32_t)) == 0;
    c->context->Unmap(staging.Get(), 0);
    Require(equal, "GPU pixels differ from newest-to-oldest CPU reference");
}

int main()
{
    try
    {
        for (auto size : { std::pair{ 7, 1 }, std::pair{ 17, 9 }, std::pair{ 1280, 600 } })
        {
            const auto [width, height] = size;
            void* handle = nullptr;
            void* shared = nullptr;
            Require(gpudraw_create_surface(width, height, &handle, &shared) == 0, "GPU surface creation");
            auto* c = static_cast<GpuWpfSurfaceContext*>(handle);
            std::vector<uint32_t> expected(width * height);
            std::iota(expected.begin(), expected.end(), 0xFF000000u);
            Require(gpudraw_upload_bgra_surface(handle, expected.data(), width, height) == 0, "seed upload");
            for (int rows : { 1, std::min(2, height), std::max(1, height - 1), height, 1 })
            {
                std::vector<uint32_t> incoming(width * rows);
                // Distinct rows also verify the top-to-bottom order of the upload.
                std::iota(incoming.begin(), incoming.end(), 0xFF801020u + rows);
                std::move_backward(expected.begin(), expected.end() - width * rows, expected.end());
                std::copy(incoming.begin(), incoming.end(), expected.begin());
                Require(gpudraw_scroll_upload_top_rows(handle, incoming.data(), width, rows) == 0, "batch upload");
                CheckPixels(c, expected);
            }
            Require(gpudraw_scroll_upload_top_rows(handle, expected.data(), width, 0) != 0, "zero rows accepted");
            Require(gpudraw_scroll_upload_top_rows(handle, expected.data(), width, -1) != 0, "negative rows accepted");
            Require(gpudraw_scroll_upload_top_rows(handle, expected.data(), width, height + 1) != 0, "too many rows accepted");
            Require(gpudraw_scroll_upload_top_rows(handle, expected.data(), width + 1, 1) != 0, "wrong width accepted");
            Require(gpudraw_scroll_upload_top_rows(handle, nullptr, width, 1) != 0, "null pixels accepted");
            CheckPixels(c, expected);
            std::vector<uint32_t> row(width, 0xFF112233u);
            std::move_backward(expected.begin(), expected.end() - width, expected.end());
            std::copy(row.begin(), row.end(), expected.begin());
            Require(gpudraw_scroll_upload_top_row(handle, row.data(), width) == 0, "legacy entry point");
            CheckPixels(c, expected);
            gpudraw_destroy_surface(handle);
            std::cout << "PASS GPU batch pixels and validation " << width << 'x' << height << '\n';
        }
        gpudraw_shutdown();
        return 0;
    }
    catch (const std::exception& e)
    {
        std::cerr << e.what() << '\n';
        return 1;
    }
}
