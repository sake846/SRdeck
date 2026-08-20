using System;
using SRdeck.Models;

namespace SRdeck.Services;

public readonly record struct SdrDeviceInitialization(int CurrentGainDb);

public interface ISdrDeviceManager
{
    ISdrDevice? Device { get; set; }
    SdrDeviceCapabilities Capabilities { get; }
    int ActiveCenterFrequencyHz { get; }

    bool TryInitialize(out SdrDeviceInitialization initialization);

    void Synchronize(
        RadioControl control,
        SdrDevicePropertyValues values,
        long frequencySwitchDelaySamples);

    void AdvanceFrequencyTransition(long sampleCount);
    ISdrDevice? DetachDevice();
}

public interface ISdrDeviceManagerFactory
{
    ISdrDeviceManager Create(
        Action<short[], short[], uint> samplesReceived,
        Action<double, int> gainHardwareChanged,
        Action deviceRemoved,
        Action streamStalled);
}

public sealed class SdrDeviceManagerFactory : ISdrDeviceManagerFactory
{
    private readonly ISdrDeviceBindingFactory _bindingFactory;
    private readonly ISdrDevicePropertySynchronizer _propertySynchronizer;
    private readonly ISdrFrequencyTransitionTracker _frequencyTransitionTracker;

    public SdrDeviceManagerFactory(
        ISdrDeviceBindingFactory bindingFactory,
        ISdrDevicePropertySynchronizer propertySynchronizer,
        ISdrFrequencyTransitionTracker frequencyTransitionTracker)
    {
        _bindingFactory = bindingFactory;
        _propertySynchronizer = propertySynchronizer;
        _frequencyTransitionTracker = frequencyTransitionTracker;
    }

    public ISdrDeviceManager Create(
        Action<short[], short[], uint> samplesReceived,
        Action<double, int> gainHardwareChanged,
        Action deviceRemoved,
        Action streamStalled) =>
        new SdrDeviceManager(
            _bindingFactory.Create(samplesReceived, gainHardwareChanged, deviceRemoved, streamStalled),
            _propertySynchronizer,
            _frequencyTransitionTracker);
}

internal sealed class SdrDeviceManager : ISdrDeviceManager
{
    private readonly ISdrDeviceBinding _binding;
    private readonly ISdrDevicePropertySynchronizer _propertySynchronizer;
    private readonly ISdrFrequencyTransitionTracker _frequencyTransitionTracker;
    private ISdrDevice? _initializedDevice;

    public SdrDeviceManager(
        ISdrDeviceBinding binding,
        ISdrDevicePropertySynchronizer propertySynchronizer,
        ISdrFrequencyTransitionTracker frequencyTransitionTracker)
    {
        _binding = binding;
        _propertySynchronizer = propertySynchronizer;
        _frequencyTransitionTracker = frequencyTransitionTracker;
    }

    public ISdrDevice? Device
    {
        get => _binding.Device;
        set
        {
            if (!ReferenceEquals(_binding.Device, value))
            {
                _initializedDevice = null;
            }
            _binding.SetDevice(value);
        }
    }

    public SdrDeviceCapabilities Capabilities =>
        Device?.Capabilities ?? new SdrDeviceCapabilities(SdrDeviceKind.SdrPlay);

    public int ActiveCenterFrequencyHz => _frequencyTransitionTracker.ActiveCenterFrequencyHz;

    public bool TryInitialize(out SdrDeviceInitialization initialization)
    {
        ISdrDevice? device = Device;
        if (device == null || ReferenceEquals(device, _initializedDevice))
        {
            initialization = default;
            return false;
        }

        _initializedDevice = device;
        initialization = new SdrDeviceInitialization(device.MaxGainReduction);
        return true;
    }

    public void Synchronize(
        RadioControl control,
        SdrDevicePropertyValues values,
        long frequencySwitchDelaySamples)
    {
        ISdrDevice? device = Device;
        if (device == null)
        {
            return;
        }

        long centerFrequencyHz = _propertySynchronizer.Synchronize(device, control, values);
        _frequencyTransitionTracker.TrackRequestedFrequency(
            centerFrequencyHz,
            frequencySwitchDelaySamples);
    }

    public void AdvanceFrequencyTransition(long sampleCount) =>
        _frequencyTransitionTracker.Advance(sampleCount);

    public ISdrDevice? DetachDevice()
    {
        ISdrDevice? device = Device;
        _initializedDevice = null;
        _binding.Dispose();
        return device;
    }
}
