using System;
using SRdeck.Models;

namespace SRdeck.Services;

public interface IRadioControlStore
{
    RadioControl Snapshot { get; }
    event EventHandler<RadioControlChangedEventArgs>? Changed;
    RadioControl CreateProcessingSnapshot(
        int playbackSampleRateHz,
        int maxHistorySeconds);
    void CommitProcessingValues(RadioControl source);
    RadioControl Update(Func<RadioControl, RadioControl> update);
    void UpdateAndNotify(
        Func<RadioControl, RadioControl> update,
        Action<RadioControl> notify);
}

public sealed class RadioControlChangedEventArgs(
    RadioControl previous,
    RadioControl current) : EventArgs
{
    public RadioControl Previous { get; } = previous;
    public RadioControl Current { get; } = current;
}

public sealed class RadioControlStore : IRadioControlStore
{
    private readonly object _syncRoot = new();
    private RadioControl _control;
    public event EventHandler<RadioControlChangedEventArgs>? Changed;

    public RadioControl Snapshot
    {
        get
        {
            lock (_syncRoot)
            {
                return _control;
            }
        }
    }

    public RadioControl CreateProcessingSnapshot(
        int playbackSampleRateHz,
        int maxHistorySeconds)
    {
        lock (_syncRoot)
        {
            RadioControl snapshot = _control;
            if (playbackSampleRateHz > 0)
            {
                snapshot.FsHz = playbackSampleRateHz;
            }

            int maximum = Math.Max(0, maxHistorySeconds);
            snapshot.HistorySec = Math.Clamp(snapshot.HistorySec, 0, maximum);
            return snapshot;
        }
    }

    public void CommitProcessingValues(RadioControl source)
    {
        lock (_syncRoot)
        {
            _control.SystemDb = source.SystemDb;
            _control.AdjustmentPpm = source.AdjustmentPpm;
            _control.HistorySec = source.HistorySec;
        }
    }

    public RadioControl Update(Func<RadioControl, RadioControl> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        RadioControl previous;
        RadioControl current;
        lock (_syncRoot)
        {
            previous = _control;
            _control = update(_control);
            current = _control;
        }
        NotifyChanged(previous, current);
        return current;
    }

    public void UpdateAndNotify(
        Func<RadioControl, RadioControl> update,
        Action<RadioControl> notify)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(notify);
        RadioControl previous;
        RadioControl current;
        lock (_syncRoot)
        {
            previous = _control;
            _control = update(_control);
            notify(_control);
            current = _control;
        }
        NotifyChanged(previous, current);
    }

    private void NotifyChanged(RadioControl previous, RadioControl current)
    {
        EventHandler<RadioControlChangedEventArgs>? handlers = Changed;
        if (handlers is null) return;
        var args = new RadioControlChangedEventArgs(previous, current);
        foreach (EventHandler<RadioControlChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, args); }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[Warning] [radio-control.changed.failed] {exception}");
            }
        }
    }
}
