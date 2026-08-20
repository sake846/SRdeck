using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SRdeck.ViewModels.Components;

public partial class SignalMeterSegment : ObservableObject
{
    [ObservableProperty] private bool _isActive;
    public double Height { get; init; }
    public SolidColorBrush ActiveColor { get; init; } = Brushes.Transparent;
}
