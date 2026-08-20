using System;
using System.Threading;
using SRdeck.Models;

namespace SRdeck.Services;

public interface IRadioStateStore
{
    RadioState WorkingState { get; }
    RadioState PublishedState { get; }
    void Replace(RadioState state);
    void Publish();
    void PublishProcessingState(float frequencyErrorEmaAlpha);
    void SetZoomHighResolutionMode(int receiverIndex, bool isHighResolution);
}

public sealed class RadioStateStore : IRadioStateStore
{
    private readonly object _syncRoot = new();
    private RadioState _workingState = new();
    private RadioState _publishedState = new();
    private int _zoomHighResolutionMode1;

    public RadioState WorkingState => _workingState;
    public RadioState PublishedState => Volatile.Read(ref _publishedState);

    public void Replace(RadioState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_syncRoot)
        {
            _workingState = state;
            Volatile.Write(ref _zoomHighResolutionMode1, state.IsZoomHighResMode ? 1 : 0);
            Volatile.Write(ref _publishedState, state.CreateSnapshot());
        }
    }

    public void Publish()
    {
        lock (_syncRoot)
        {
            SyncSnapshot();
        }
    }

    public void PublishProcessingState(float frequencyErrorEmaAlpha)
    {
        lock (_syncRoot)
        {
            SyncSnapshot();
        }
    }

    private void SyncSnapshot()
    {
        RadioState snapshot = _workingState.CreateSnapshot();
        snapshot.IsZoomHighResMode = Volatile.Read(ref _zoomHighResolutionMode1) != 0;
        Volatile.Write(ref _publishedState, snapshot);
    }

    public void SetZoomHighResolutionMode(int receiverIndex, bool isHighResolution)
    {
        if (receiverIndex != 1) return;

        ref int target = ref _zoomHighResolutionMode1;
        int requestedValue = isHighResolution ? 1 : 0;
        if (Volatile.Read(ref target) == requestedValue)
        {
            return;
        }

        Volatile.Write(ref target, requestedValue);
        lock (_syncRoot)
        {
            RadioState snapshot = _publishedState.CreateSnapshot();
            snapshot.IsZoomHighResMode = isHighResolution;
            Volatile.Write(ref _publishedState, snapshot);
        }
    }
}
