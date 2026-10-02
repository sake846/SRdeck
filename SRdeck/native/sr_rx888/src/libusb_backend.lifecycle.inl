#include "libusb_backend.h"
#include <cmath>

namespace
{
constexpr float kHighGainRatio = 0.409f;
constexpr float kLowGainRatio = 0.059f;
} // namespace

LibUsbBackend::LibUsbBackend()
{
    hfIfSteps_.reserve(127);
    for (int i = 0; i < 127; ++i)
    {
        if (i > kGainSweetPoint)
        {
            hfIfSteps_.push_back(20.0f * std::log10(kHighGainRatio * static_cast<float>(i - kGainSweetPoint + 3)));
        }
        else
        {
            hfIfSteps_.push_back(20.0f * std::log10(kLowGainRatio * static_cast<float>(i + 1)));
        }
    }
}

LibUsbBackend::~LibUsbBackend()
{
    Close();
}

bool LibUsbBackend::Open(int index, const std::string& imageFile)
{
    api_ = LibUsbApi::Create();
    if (!api_ || !api_->Available())
    {
        return false;
    }

    if (api_->init_(&context_) != 0)
    {
        context_ = nullptr;
        return false;
    }

    libusb_device** devices = nullptr;
    const libusb_ssize_t count = api_->getDeviceList_(context_, &devices);
    if (count < 0 || devices == nullptr)
    {
        return false;
    }

    auto tryOpenStreamer = [&](libusb_device** devices, libusb_ssize_t deviceCount) -> bool
    {
        int matchedIndex = 0;
        for (libusb_ssize_t i = 0; i < deviceCount; ++i)
        {
            libusb_device* device = devices[i];
            libusb_device_descriptor descriptor{};
            if (device == nullptr || api_->getDeviceDescriptor_(device, &descriptor) != 0)
            {
                continue;
            }

            if (descriptor.idVendor != kVendorId || descriptor.idProduct != kStreamerProductId)
            {
                continue;
            }

            if (matchedIndex++ != index)
            {
                continue;
            }

            if (api_->open_(device, &handle_) != 0 || handle_ == nullptr)
            {
                return false;
            }

            if (api_->claimInterface_(handle_, kInterfaceNumber) != 0)
            {
                api_->close_(handle_);
                handle_ = nullptr;
                return false;
            }

            uint32_t hardwareInfo = 0;
            if (!ReadHardwareInfo(hardwareInfo))
            {
                api_->releaseInterface_(handle_, kInterfaceNumber);
                api_->close_(handle_);
                handle_ = nullptr;
                return false;
            }

            hardwareInfo_ = hardwareInfo;
            radioModel_ = static_cast<uint8_t>(hardwareInfo & 0xffu);
            open_ = true;
            return true;
        }

        return false;
    };

    if (tryOpenStreamer(devices, count))
    {
        api_->freeDeviceList_(devices, 1);
        return true;
    }

    int bootloaderIndex = 0;
    for (libusb_ssize_t i = 0; i < count; ++i)
    {
        libusb_device* device = devices[i];
        libusb_device_descriptor descriptor{};
        if (device == nullptr || api_->getDeviceDescriptor_(device, &descriptor) != 0)
        {
            continue;
        }

        if (descriptor.idVendor != kVendorId || descriptor.idProduct != kBootloaderProductId)
        {
            continue;
        }

        if (bootloaderIndex++ != index)
        {
            continue;
        }

        libusb_device_handle* bootHandle = nullptr;
        if (api_->open_(device, &bootHandle) != 0 || bootHandle == nullptr)
        {
            api_->freeDeviceList_(devices, 1);
            Close();
            return false;
        }

        const bool downloaded = DownloadFx3Firmware(bootHandle, imageFile);
        api_->close_(bootHandle);
        api_->freeDeviceList_(devices, 1);
        if (!downloaded)
        {
            Close();
            return false;
        }

        Sleep(kFx3FirmwareReenumerationDelayMs);
        for (int poll = 0; poll < kFx3FirmwareReenumerationPollCount; ++poll)
        {
            libusb_device** retryDevices = nullptr;
            const libusb_ssize_t retryCount = api_->getDeviceList_(context_, &retryDevices);
            if (retryCount >= 0 && retryDevices != nullptr)
            {
                const bool opened = tryOpenStreamer(retryDevices, retryCount);
                api_->freeDeviceList_(retryDevices, 1);
                if (opened)
                {
                    return true;
                }
            }

            Sleep(kFx3FirmwareReenumerationPollDelayMs);
        }

        Close();
        return false;
    }

    api_->freeDeviceList_(devices, 1);
    Close();
    return false;
}

void LibUsbBackend::Close()
{
    if (api_ && handle_)
    {
        api_->releaseInterface_(handle_, kInterfaceNumber);
        api_->close_(handle_);
    }

    handle_ = nullptr;

    if (api_ && context_)
    {
        api_->exit_(context_);
    }

    context_ = nullptr;
    api_.reset();
    open_ = false;
    streaming_ = false;
    hardwareInfo_ = 0;
    radioModel_ = 0;
    rfMode_ = NO_RF_MODE;
    gpioState_ = 0;
    rawTransferBuffer_.clear();
    ResetCallbackAggregationState();
    callback_ = nullptr;
    callbackContext_ = nullptr;
    frameSize_ = 0;
    numFrames_ = 0;
    adcFrequencyHz_ = 64000000.0;
    sampleRateHz_ = 32000000.0;
    tunerFrequencyHz_ = 0.0;
    ddc_.Reset();
}

int LibUsbBackend::ConfigureAsync(
    uint32_t frameSize,
    uint32_t numFrames,
    rx888_read_async_cb_t callback,
    void* callbackContext)
{
    frameSize_ = frameSize;
    numFrames_ = numFrames;
    callback_ = callback;
    callbackContext_ = callbackContext;
    return 0;
}

int LibUsbBackend::SetSampleRate(double sampleRate)
{
    if (sampleRate <= 0.0)
    {
        return -1;
    }

    sampleRateHz_ = sampleRate;
    ResetStreamingState();
    return 0;
}

int LibUsbBackend::SetAdcFrequency(double adcFrequency)
{
    adcFrequencyHz_ = adcFrequency;
    ResetStreamingState();
    return SendU32(Fx3Command::StartAdc, static_cast<uint32_t>(adcFrequency));
}

int LibUsbBackend::SetRfMode(RFMode rfMode)
{
    return SetRfModeImpl(rfMode);
}

int LibUsbBackend::SetTunerFrequency(double frequency)
{
    if (rfMode_ != VHF_MODE)
    {
        tunerFrequencyHz_ = frequency;
        ResetStreamingState();
        return 0;
    }

    tunerFrequencyHz_ = frequency;
    ResetStreamingState();
    return SendU64(Fx3Command::TunerTune, static_cast<uint64_t>(frequency));
}

int LibUsbBackend::SetTunerRfAttenuation(double attenuation)
{
    if (rfMode_ == VHF_MODE)
    {
        const int gainIndex = FindNearestIndex(kVhfRfSteps, attenuation);
        return SetArgument(ArgumentId::R82xxAttenuator, static_cast<uint16_t>(gainIndex));
    }

    const int halfDbSteps = ClampInt(static_cast<int>(std::lround(attenuation * 2.0)), 0, 63);
    return SetArgument(ArgumentId::Dat31Att, static_cast<uint16_t>(halfDbSteps));
}

int LibUsbBackend::SetTunerIfAttenuation(double attenuation)
{
    if (rfMode_ == VHF_MODE)
    {
        const int gainIndex = FindNearestIndex(kVhfIfSteps, attenuation);
        return SetArgument(ArgumentId::R82xxVga, static_cast<uint16_t>(gainIndex));
    }

    const int gainIndex = FindNearestIndex(hfIfSteps_, attenuation);
    const uint16_t gainValue = EncodeHfIfGain(gainIndex);
    return SetArgument(ArgumentId::Ad8340Vga, gainValue);
}

int LibUsbBackend::SetAdcDither(int dither)
{
    return UpdateGpioBit(GpioPin::Dith, dither != 0);
}

int LibUsbBackend::SetAdcRandom(int random)
{
    ddc_.SetAdcRandom(random != 0);
    return UpdateGpioBit(GpioPin::Rando, random != 0);
}

int LibUsbBackend::SetVhfBias(int bias)
{
    return UpdateGpioBit(GpioPin::BiasVhf, bias != 0);
}

int LibUsbBackend::SetHfBias(int bias)
{
    return UpdateGpioBit(GpioPin::BiasHf, bias != 0);
}

int LibUsbBackend::SetHfAttenuation(double attenuation)
{
    const int halfDbSteps = ClampInt(static_cast<int>(std::lround(attenuation * 2.0)), 0, 63);
    return SetArgument(ArgumentId::Dat31Att, static_cast<uint16_t>(halfDbSteps));
}
