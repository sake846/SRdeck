using CommunityToolkit.Mvvm.ComponentModel;

namespace SRdeck.ViewModels.Components;

public enum DemodWaveCommandType
{
    Debug,
    Station,
    Color,
    Green,
    Amber
}

public partial class DemodWaveOverlayButton : ObservableObject
{
    public DemodWaveCommandType CommandType { get; init; }
    [ObservableProperty] private bool _isActive;
}
