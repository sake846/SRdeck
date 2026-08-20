using SRdeck.Models;

namespace SRdeck.Views
{
    public interface IRenderableView
    {
        void RenderFrame(IRadioRenderContext engine);
        void DisposeRenderer();
    }
}
