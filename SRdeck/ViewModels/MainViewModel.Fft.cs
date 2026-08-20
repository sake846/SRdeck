using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Models;
using SRdeck.Messages;

namespace SRdeck.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public class FftBatchOption : ObservableObject
    {
        public int Count { get; set; }
        public string Label { get; set; } = "";
        private bool _isEnabled = true;
        public bool IsEnabled 
        { 
            get => _isEnabled; 
            set => SetProperty(ref _isEnabled, value); 
        }
    }

    public class FftResolutionOption : ObservableObject
    {
        public int Mode { get; set; }
        public string Label { get; set; } = "";
        private bool _isEnabled = true;
        public bool IsEnabled 
        { 
            get => _isEnabled; 
            set => SetProperty(ref _isEnabled, value); 
        }
    }

    private static readonly int[] _fftResolutionSizes = { 4096, 8192, 16384, 32768, 65536, 131072, 262144, 524288, 1048576, 2097152, 4194304 };

    public ObservableCollection<FftResolutionOption> FftResolutionOptions { get; } = new()
    {
        new() { Mode = 0, Label = "4K" },
        new() { Mode = 1, Label = "8K" },
        new() { Mode = 2, Label = "16K" },
        new() { Mode = 3, Label = "32K" },
        new() { Mode = 4, Label = "64K" },
        new() { Mode = 5, Label = "128K" },
        new() { Mode = 6, Label = "256K" },
        new() { Mode = 7, Label = "512K" },
        new() { Mode = 8, Label = "1M" },
        new() { Mode = 9, Label = "2M" },
        new() { Mode = 10, Label = "4M" }
    };

    public ObservableCollection<FftBatchOption> FftBatchOptions { get; } = new()
    {
        new() { Count = 1, Label = "1回" },
        new() { Count = 2, Label = "2回" },
        new() { Count = 4, Label = "4回" },
        new() { Count = 8, Label = "8回" },
        new() { Count = 16, Label = "16回" },
        new() { Count = 32, Label = "32回" }
    };

    internal void ApplyFftAveragingLimit()
    {
        if (_engine == null) return;
        
        RadioControl radioControl = _engine.Control;
        radioControl.FftBatchCount = 1;
        WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));

        FftBatchMode = 0;

        if (_lastState != null)
        {
            _lastState.FftBatchMode = 0;
            PersistFftBatchState(0);
            _lastStateService.SaveLastState(_lastState);
        }
    }

    internal void ApplyFftResolutionLimit()
    {
        if (_engine == null) return;

        int[] batchMapping = { 1, 2, 4, 8, 16, 32 };
        int selectedBatchCount = FftBatchMode >= 0 && FftBatchMode < batchMapping.Length
            ? batchMapping[FftBatchMode]
            : 1;
        RadioControl radioControl = _engine.Control;
        int sampleRateHz = radioControl.FsHz > 0
            ? radioControl.FsHz
            : (int)AppConstants.FULL_BW;
        int maxAllowedMode = GetMaximumFftResolutionMode(
            sampleRateHz, selectedBatchCount, IsGpuFftEnabled);

        if (FftResolutionMode > maxAllowedMode)
        {
            FftResolutionMode = maxAllowedMode;
        }

        foreach (var option in FftResolutionOptions)
        {
            bool gpuAllowed = IsGpuFftEnabled || option.Mode <= 3;
            bool device300msAllowed = option.Mode <= maxAllowedMode;
            option.IsEnabled = gpuAllowed && device300msAllowed;
        }

        ApplyFftAveragingLimit();
    }

    internal static int GetMaximumFftResolutionMode(
        int sampleRateHz,
        int batchCount,
        bool isGpuEnabled)
    {
        int safeSampleRateHz = Math.Max(1, sampleRateHz);
        int safeBatchCount = Math.Clamp(batchCount, 1, 32);
        double maximumFftPoints = 0.3 * safeSampleRateHz / safeBatchCount;
        int maximumMode = 0;
        for (int index = 0; index < _fftResolutionSizes.Length; index++)
            if (_fftResolutionSizes[index] <= maximumFftPoints)
                maximumMode = index;

        // CPU FFTs above 32K can monopolize the processing worker even when
        // the sample-rate window would otherwise permit them.
        return isGpuEnabled ? maximumMode : Math.Min(maximumMode, 3);
    }
}
