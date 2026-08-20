using CommunityToolkit.Mvvm.ComponentModel;

namespace SRdeck.ViewModels
{
    public class BandPlanRendererItem
    {
        public double Left { get; set; }
        public double Width { get; set; }
        public string Label { get; set; } = "";
        public string Color { get; set; } = "";
    }

    public partial class StationLabel : ObservableObject
    {
        [ObservableProperty] private string _name = "";
        [ObservableProperty] private double _x;
        [ObservableProperty] private double _y;
        [ObservableProperty] private double _lineX;
        [ObservableProperty] private string _color = "";
        [ObservableProperty] private float _frequencyHz;
    }

    public class ReceiverBandRendererItem
    {
        public double Left { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public string Label { get; set; } = "";
        public string Fill { get; set; } = "#3000A0FF";
        public string Stroke { get; set; } = "#A080C8FF";
        public string LabelColor { get; set; } = "#FFFFFFFF";
        public double Top { get; set; }
    }

    public partial class SpectrumYLabel : ObservableObject
    {
        [ObservableProperty] private string _text = "";
        [ObservableProperty] private double _xRight;
        [ObservableProperty] private double _y;
        [ObservableProperty] private string _textColor = "#FFD6D6D6";
    }
}
