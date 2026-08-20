using System;
using SRdeck.Renderers.Compat.Direct2D1;
using SRdeck.Renderers.Compat.DirectWrite;

namespace SRdeck.Renderers;

internal sealed class DirectXManager : IDisposable
{
    private static readonly Lazy<DirectXManager> _instance = new(() => new DirectXManager());
    public static DirectXManager Instance => _instance.Value;

    public ID2D1Factory1 D2DFactory { get; } = new();
    public IDWriteFactory DWriteFactory { get; } = new();

    public ID2D1RenderTarget CreateRenderTarget(int width, int height)
    {
        return D2DFactory.CreateRenderTarget(width, height);
    }

    private DirectXManager()
    {
    }

    public void Dispose()
    {
    }
}
