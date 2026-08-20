using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SRdeck.ViewModels;

public partial class MainViewModel : ObservableObject
{
    // --- Drum/Drag Transient State Fields (Private) ---
    private bool _isSpectrumDragging;
    private Point _spectrumDragStartPoint;
    private int _spectrumDragStartCenterFreq;

    private bool _isWaterfallDraggingCenter;
    private bool _isWaterfallDraggingFrame;
    private Point _waterfallDragStartPoint;
    private int _waterfallDragStartCenterFreq;

    private bool _isZoomDragging;
    private Point _zoomDragStartAbsolute;
    private int _zoomDragStartFreqOffset;
    private int _zoomDragStartHistorySec;


    public bool MainGridAnchorEnabled { get; private set; }
    public double MainGridAnchorFrequencyHz { get; private set; }
    public double MainGridAnchorRatio { get; private set; } = 0.5;
}
