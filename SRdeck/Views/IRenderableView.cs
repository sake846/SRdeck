using SRdeck.Models;
using SRdeck.Services;

namespace SRdeck.Views
{
    public interface IRenderableView
    {
        void RenderFrame(IRadioRenderContext engine, MainFftFrame frame);
        void DisposeRenderer();
    }
}
