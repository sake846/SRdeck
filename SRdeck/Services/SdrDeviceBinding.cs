using System;
using SRdeck.Models;

namespace SRdeck.Services;

public interface ISdrDeviceBinding : IDisposable
{
    ISdrDevice? Device { get; }
    void SetDevice(ISdrDevice? device);
}

public interface ISdrDeviceBindingFactory
{
    ISdrDeviceBinding Create(
        Action<short[], short[], uint> samplesReceived,
        Action<double, int> gainHardwareChanged,
        Action deviceRemoved,
        Action streamStalled,
        Action<SdrSampleBlock>? sampleBlockReceived = null);
}

public sealed class SdrDeviceBindingFactory : ISdrDeviceBindingFactory
{
    public ISdrDeviceBinding Create(
        Action<short[], short[], uint> samplesReceived,
        Action<double, int> gainHardwareChanged,
        Action deviceRemoved,
        Action streamStalled,
        Action<SdrSampleBlock>? sampleBlockReceived = null) =>
        new SdrDeviceBinding(samplesReceived, gainHardwareChanged, deviceRemoved, streamStalled, sampleBlockReceived);
}

internal sealed class SdrDeviceBinding : ISdrDeviceBinding
{
    private readonly Action<short[], short[], uint> _samplesReceived;
    private readonly Action<double, int> _gainHardwareChanged;
    private readonly Action _deviceRemoved;
    private readonly Action _streamStalled;
    private readonly Action<SdrSampleBlock>? _sampleBlockReceived;

    public SdrDeviceBinding(
        Action<short[], short[], uint> samplesReceived,
        Action<double, int> gainHardwareChanged,
        Action deviceRemoved,
        Action streamStalled,
        Action<SdrSampleBlock>? sampleBlockReceived = null)
    {
        _samplesReceived = samplesReceived;
        _gainHardwareChanged = gainHardwareChanged;
        _deviceRemoved = deviceRemoved;
        _streamStalled = streamStalled;
        _sampleBlockReceived = sampleBlockReceived;
    }

    public ISdrDevice? Device { get; private set; }

    public void SetDevice(ISdrDevice? device)
    {
        DetachCurrentDevice();
        Device = device;
        if (Device == null)
        {
            return;
        }

        if (Device is ISdrSampleBlockSource source && _sampleBlockReceived is not null)
            source.SampleBlockReceived += _sampleBlockReceived;
        else
            Device.SamplesReceived += _samplesReceived;
        Device.GainHardwareChanged += _gainHardwareChanged;
        Device.DeviceRemoved += _deviceRemoved;
        Device.StreamStalled += _streamStalled;
    }

    public void Dispose()
    {
        DetachCurrentDevice();
        Device = null;
    }

    private void DetachCurrentDevice()
    {
        if (Device == null)
        {
            return;
        }

        if (Device is ISdrSampleBlockSource source && _sampleBlockReceived is not null)
            source.SampleBlockReceived -= _sampleBlockReceived;
        else
            Device.SamplesReceived -= _samplesReceived;
        Device.GainHardwareChanged -= _gainHardwareChanged;
        Device.DeviceRemoved -= _deviceRemoved;
        Device.StreamStalled -= _streamStalled;
    }
}
