using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;
using SRdeck.Models;
using SRdeck.Renderers;
using SRdeck.ViewModels.Components;

namespace SRdeck.ViewModels
{
    public partial class SpectrumOverlayViewModel : ObservableObject
    {
        private double _spRawCsLeft;
        public double SpRawCsLeft { get => _spRawCsLeft; set => SetProperty(ref _spRawCsLeft, value); }
        [ObservableProperty] private double _spBandLeft;
        [ObservableProperty] private double _spBandWidth;
        [ObservableProperty] private Visibility _spBandVisible = Visibility.Hidden;

        [ObservableProperty] private double _spBand2Left;
        [ObservableProperty] private double _spBand2Width;
        [ObservableProperty] private Visibility _spBand2Visible = Visibility.Hidden;

        [ObservableProperty] private double _spCsLeft;
        [ObservableProperty] private double _spCsWidth;
        [ObservableProperty] private double _spCsLineX;
        [ObservableProperty] private double _spCsRightEdge;
        [ObservableProperty] private double _spCsLineWidth;
        [ObservableProperty] private Visibility _spCsVisible = Visibility.Hidden;
        [ObservableProperty] private double _spCsOpacity = 1.0;

        [ObservableProperty] private double _spCsDbY;
        [ObservableProperty] private Visibility _spCsDbVisible = Visibility.Hidden;
        [ObservableProperty] private string _spCsText = "";
        [ObservableProperty] private double _spCsTextX;
        [ObservableProperty] private double _spCsTextY;
        [ObservableProperty] private double _spCsHotspotX;
        [ObservableProperty] private double _spCsHotspotYMin;
        [ObservableProperty] private double _spCsHotspotYMax;

        [ObservableProperty] private double _spectrumWidth = 10;
        [ObservableProperty] private double _spectrumHeight = 10;
        [ObservableProperty] private float _gridTopDb = -40.0f;

        [ObservableProperty] private string _spCursorFreqText = "";
        [ObservableProperty] private Brush _waterfallColorScaleBrush = Brushes.Black;
        [ObservableProperty] private double _spColorBarLeft;
        [ObservableProperty] private double _spColorBarHeight;
        [ObservableProperty] private string _debugBiasText = "";
        [ObservableProperty] private string _debugPwrText = "";

        public ObservableCollection<SpectrumYLabel> SpectrumYLabels { get; } = new ObservableCollection<SpectrumYLabel>();
        public ObservableCollection<StationLabel> StationLabels { get; } = new ObservableCollection<StationLabel>();
        public ObservableCollection<BandPlanRendererItem> BandPlanRegions { get; } = new ObservableCollection<BandPlanRendererItem>();
        public ObservableCollection<ReceiverBandRendererItem> ReceiverBands { get; } = new ObservableCollection<ReceiverBandRendererItem>();

        public SpectrumOverlayViewModel()
        {
            for (int i = 0; i < 9; i++) SpectrumYLabels.Add(new SpectrumYLabel());
        }

        public void LoadBandPlans()
        {
        }

        public void LoadStationNames()
        {
        }

    }
}
