using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SRdeck.ViewModels.Components;

public partial class SpectrumYLabel : ObservableObject
{
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _xRight;
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _textColor = "#FFD6D6D6";
}

public partial class WaterfallXLabel : ObservableObject
{
    [ObservableProperty] private double _x;
    [ObservableProperty] private string _text = "";
}

public partial class WaterfallYLabel : ObservableObject
{
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _yLine;
    [ObservableProperty] private double _xRight;
    [ObservableProperty] private double _xRightLine1;
    [ObservableProperty] private double _xRightLine2;
    [ObservableProperty] private double _xRightText;
    [ObservableProperty] private string _textLeft = "";
    [ObservableProperty] private string _textRight = "";
}

public partial class WaterfallAnnotationVisual : ObservableObject
{
    private string? label;

    public string Id { get; }
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _width;
    [ObservableProperty] private double _height;
    [ObservableProperty] private string _color = "#FFFFFFFF";
    [ObservableProperty] private string? _toolTip;
    [ObservableProperty] private double _rotationDegrees;

    public string? Label
    {
        get => label;
        set
        {
            if (!SetProperty(ref label, value)) return;
            OnPropertyChanged(nameof(HasLabel));
        }
    }

    public bool HasLabel => !string.IsNullOrEmpty(Label);

    public WaterfallAnnotationVisual(string id) => Id = id;
}
