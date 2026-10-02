#include "cyusb_backend.h"
#include <cmath>

namespace
{
constexpr uint16_t kStreamerProductId = 0x00F1;
constexpr uint16_t kBootloaderProductId = 0x00F3;
constexpr uint8_t kBulkInEndpoint = 0x81;
constexpr uint16_t kControlTimeoutMs = 1000;
constexpr uint32_t kBulkTransferTimeoutMs = 80;
constexpr size_t kDefaultTransferBytes = 131072u;
constexpr int kHandleEventsBurstCount = 16;
constexpr size_t kUsbReadConcurrent = 24;
constexpr size_t kCallbackBufferCount = 8;
constexpr size_t kRawDdcBufferCount = kUsbReadConcurrent;
constexpr uint32_t kRx888Mk2Model = 0x04;
constexpr uint32_t kR828dReferenceFrequency = 16000000;
constexpr double kR828dIfCarrier = 4570000.0;
constexpr float kRx888Mk2GainFactor = 1.08e-8f;
constexpr int kGainSweetPoint = 18;
constexpr float kHighGainRatio = 0.409f;
constexpr float kLowGainRatio = 0.059f;
constexpr uint8_t kHighModeFlag = 0x80;

constexpr uint8_t kFx3BootloaderVendorRequest = 0xA0;
constexpr size_t kFx3FirmwareChunkBytes = 2048;
constexpr DWORD kFx3FirmwareReenumerationDelayMs = 800;
constexpr int kFx3FirmwareReenumerationPollCount = 20;
constexpr DWORD kFx3FirmwareReenumerationPollDelayMs = 100;

inline size_t GetUsbReadConcurrentCount()
{
    return kUsbReadConcurrent;
}

inline int GetHandleEventsBurstCount()
{
    return kHandleEventsBurstCount;
}
} // namespace

CyUsbDriverBackend::CyUsbDriverBackend()
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

CyUsbDriverBackend::~CyUsbDriverBackend()
{
    Close();
}

bool CyUsbDriverBackend::Open(int index, const std::string& imageFile)
{
    auto tryOpenStreamer = [&](const std::vector<DeviceInfoSnapshot>& devices) -> bool
    {
        int matchedIndex = 0;
        for (const DeviceInfoSnapshot& device : devices)
        {
            if (device.productId != kStreamerProductId)
            {
                continue;
            }

            if (matchedIndex++ != index)
            {
                continue;
            }

            NativeLog("CyUsbDriverBackend::Open candidate path=" + device.devicePath + " pid=" + std::to_string(device.productId));
            handle_ = CyOpenDevicePath(device.devicePath);
            if (handle_ == INVALID_HANDLE_VALUE)
            {
                return false;
            }

            hardwareInfo_ = 0;
            if (ReadHardwareInfo(hardwareInfo_))
            {
                NativeLog("ReadHardwareInfo ok value=" + std::to_string(hardwareInfo_));
                radioModel_ = static_cast<uint8_t>(hardwareInfo_ & 0xffu);
            }
            else
            {
                NativeLog("ReadHardwareInfo failed err=" + std::to_string(GetLastError()));
                hardwareInfo_ = kRx888Mk2Model;
                radioModel_ = static_cast<uint8_t>(kRx888Mk2Model);
            }
            open_ = true;
            CySetTransferSize(handle_, kBulkInEndpoint, static_cast<uint32_t>(GetTransferBytes()));
            NativeLog("CyUsbDriverBackend::Open success");
            return true;
        }

        return false;
    };

    std::vector<DeviceInfoSnapshot> devices = EnumerateMatchingCyDevices();
    if (tryOpenStreamer(devices))
    {
        return true;
    }

    int bootloaderIndex = 0;
    for (const DeviceInfoSnapshot& device : devices)
    {
        if (device.productId != kBootloaderProductId)
        {
            continue;
        }

        if (bootloaderIndex++ != index)
        {
            continue;
        }

        NativeLog("CyUsbDriverBackend::Open bootloader path=" + device.devicePath);
        HANDLE bootHandle = CyOpenDevicePath(device.devicePath);
        if (bootHandle == INVALID_HANDLE_VALUE)
        {
            return false;
        }

        const bool downloaded = DownloadFx3Firmware(bootHandle, imageFile);
        CloseHandle(bootHandle);
        if (!downloaded)
        {
            return false;
        }

        Sleep(kFx3FirmwareReenumerationDelayMs);
        for (int poll = 0; poll < kFx3FirmwareReenumerationPollCount; ++poll)
        {
            devices = EnumerateMatchingCyDevices();
            if (tryOpenStreamer(devices))
            {
                NativeLog("CyUsbDriverBackend::Open recovered from bootloader");
                return true;
            }
            Sleep(kFx3FirmwareReenumerationPollDelayMs);
        }

        NativeLog("CyUsbDriverBackend::Open bootloader download completed but streamer not found");
        return false;
    }

    NativeLog("CyUsbDriverBackend::Open no matching device");
    return false;
}

void CyUsbDriverBackend::Close()
{
    std::lock_guard<std::mutex> streamLock(streamMutex_);
    StopUsbWorker();
    CleanupUsbTransfers();
    StopDdcWorker();
    StopCallbackWorker();
    if (handle_ != INVALID_HANDLE_VALUE)
    {
        CloseHandle(handle_);
    }

    handle_ = INVALID_HANDLE_VALUE;
    open_ = false;
    streaming_ = false;
    hardwareInfo_ = 0;
    radioModel_ = 0;
    rfMode_ = NO_RF_MODE;
    gpioState_ = 0;
    callback_ = nullptr;
    callbackContext_ = nullptr;
    frameSize_ = 0;
    numFrames_ = 0;
    adcFrequencyHz_ = 64000000.0;
    sampleRateHz_ = 32000000.0;
    tunerFrequencyHz_ = 0.0;
    ddc_.Reset();
}

int CyUsbDriverBackend::ConfigureAsync(uint32_t frameSize, uint32_t numFrames, rx888_read_async_cb_t callback, void* callbackContext)
{
    frameSize_ = frameSize;
    numFrames_ = numFrames;
    callback_ = callback;
    callbackContext_ = callbackContext;
    if (handle_ != INVALID_HANDLE_VALUE)
    {
        CySetTransferSize(handle_, kBulkInEndpoint, static_cast<uint32_t>(GetTransferBytes()));
    }
    return 0;
}

int CyUsbDriverBackend::SetSampleRate(double sampleRate)
{
    if (sampleRate <= 0.0)
    {
        return -1;
    }

    sampleRateHz_ = sampleRate;
    ResetStreamingState();
    return 0;
}

int CyUsbDriverBackend::SetAdcFrequency(double adcFrequency)
{
    adcFrequencyHz_ = adcFrequency;
    ResetStreamingState();
    return SendU32(Fx3Command::StartAdc, static_cast<uint32_t>(adcFrequency));
}

int CyUsbDriverBackend::SetRfMode(RFMode rfMode)
{
    return SetRfModeImpl(rfMode);
}

int CyUsbDriverBackend::SetTunerFrequency(double frequency)
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

int CyUsbDriverBackend::SetTunerRfAttenuation(double attenuation)
{
    if (rfMode_ == VHF_MODE)
    {
        const int gainIndex = FindNearestIndex(kVhfRfSteps, attenuation);
        return SetArgument(ArgumentId::R82xxAttenuator, static_cast<uint16_t>(gainIndex));
    }

    const int halfDbSteps = ClampInt(static_cast<int>(std::lround(attenuation * 2.0)), 0, 63);
    return SetArgument(ArgumentId::Dat31Att, static_cast<uint16_t>(halfDbSteps));
}

int CyUsbDriverBackend::SetTunerIfAttenuation(double attenuation)
{
    if (rfMode_ == VHF_MODE)
    {
        const int gainIndex = FindNearestIndex(kVhfIfSteps, attenuation);
        return SetArgument(ArgumentId::R82xxVga, static_cast<uint16_t>(gainIndex));
    }

    const int gainIndex = FindNearestIndex(hfIfSteps_, attenuation);
    return SetArgument(ArgumentId::Ad8340Vga, EncodeHfIfGain(gainIndex));
}

int CyUsbDriverBackend::SetAdcDither(int dither) { return UpdateGpioBit(GpioPin::Dith, dither != 0); }
int CyUsbDriverBackend::SetAdcRandom(int random)
{
    std::lock_guard<std::mutex> ddcLock(ddcMutex_);
    ddc_.SetAdcRandom(random != 0);
    return UpdateGpioBit(GpioPin::Rando, random != 0);
}
int CyUsbDriverBackend::SetVhfBias(int bias) { return UpdateGpioBit(GpioPin::BiasVhf, bias != 0); }
int CyUsbDriverBackend::SetHfBias(int bias) { return UpdateGpioBit(GpioPin::BiasHf, bias != 0); }

int CyUsbDriverBackend::SetHfAttenuation(double attenuation)
{
    const int halfDbSteps = ClampInt(static_cast<int>(std::lround(attenuation * 2.0)), 0, 63);
    return SetArgument(ArgumentId::Dat31Att, static_cast<uint16_t>(halfDbSteps));
}

int CyUsbDriverBackend::StartStreaming()
{
    std::lock_guard<std::mutex> streamLock(streamMutex_);
    if (!open_)
    {
        return -1;
    }

    if (SendNoData(Fx3Command::StartFx3) != 0)
    {
        NativeLog("CyUsbDriverBackend::StartStreaming failed: STARTFX3 control request");
        return -1;
    }

    if (!ddcConfigured_)
    {
        ddc_.Configure(adcFrequencyHz_, sampleRateHz_, tunerFrequencyHz_, rfMode_);
        ddcConfigured_ = true;
    }

    streaming_ = true;
    ResetCallbackAggregationState();
    ResetProfile();
    StartCallbackWorker();
    StartDdcWorker();
    if (!InitializeUsbTransfers())
    {
        NativeLog("CyUsbDriverBackend::StartStreaming failed: bulk IN transfer initialization");
        streaming_ = false;
        StopDdcWorker();
        StopCallbackWorker();
        SendNoData(Fx3Command::StopFx3);
        return -1;
    }
    StartUsbWorker();
    return 0;
}

int CyUsbDriverBackend::HandleEvents()
{
    if (handle_ == INVALID_HANDLE_VALUE || callback_ == nullptr)
    {
        return -1;
    }
    return streaming_ ? 0 : -1;
}

int CyUsbDriverBackend::StopStreaming()
{
    std::lock_guard<std::mutex> streamLock(streamMutex_);
    if (!open_)
    {
        return -1;
    }

    streaming_ = false;
    StopUsbWorker();
    CleanupUsbTransfers();
    SendNoData(Fx3Command::StopFx3);
    StopDdcWorker();
    StopCallbackWorker();
    return 0;
}

int CyUsbDriverBackend::SendNoData(Fx3Command command)
{
    // Older FX3 firmware waits for an OUT data phase for these commands.
    unsigned char ignored = 0;
    return ControlTransfer(command, 0, 0, &ignored, sizeof(ignored), false) ? 0 : -1;
}

int CyUsbDriverBackend::SendU32(Fx3Command command, uint32_t value)
{
    return ControlTransfer(command, 0, 0, reinterpret_cast<unsigned char*>(&value), sizeof(value), false) ? 0 : -1;
}

int CyUsbDriverBackend::SendU64(Fx3Command command, uint64_t value)
{
    return ControlTransfer(command, 0, 0, reinterpret_cast<unsigned char*>(&value), sizeof(value), false) ? 0 : -1;
}

int CyUsbDriverBackend::SetArgument(ArgumentId argumentId, uint16_t value)
{
    // FX3 firmware waits for an OUT data phase even though the value is in wValue.
    unsigned char ignored = 0;
    return ControlTransfer(Fx3Command::SetArgFx3, value, static_cast<uint16_t>(argumentId), &ignored, sizeof(ignored), false) ? 0 : -1;
}

bool CyUsbDriverBackend::ReadHardwareInfo(uint32_t& hardwareInfo)
{
    hardwareInfo = 0;
    return ControlTransfer(Fx3Command::TestFx3, 0, 0, reinterpret_cast<unsigned char*>(&hardwareInfo), sizeof(hardwareInfo), true);
}

bool CyUsbDriverBackend::ControlTransfer(Fx3Command command, uint16_t value, uint16_t index, unsigned char* data, uint16_t length, bool directionIn)
{
    return handle_ != INVALID_HANDLE_VALUE &&
           CyControlTransfer(handle_, directionIn, 2, static_cast<uint8_t>(command), value, index, data, length, kControlTimeoutMs);
}

bool CyUsbDriverBackend::DownloadFx3Firmware(HANDLE handle, const std::string& imageFile)
{
    return DownloadFx3FirmwareImage(
        imageFile,
        [&](uint32_t address, const unsigned char* data, uint16_t length) -> bool
        {
            return CyControlTransfer(
                handle,
                false,
                2,
                kFx3BootloaderVendorRequest,
                static_cast<uint16_t>(address & 0xffffu),
                static_cast<uint16_t>((address >> 16) & 0xffffu),
                const_cast<unsigned char*>(data),
                length,
                kControlTimeoutMs);
        },
        "cyusb");
}

int CyUsbDriverBackend::UpdateGpioBit(uint32_t mask, bool enabled)
{
    if (enabled)
    {
        gpioState_ |= mask;
    }
    else
    {
        gpioState_ &= ~mask;
    }
    return SendU32(Fx3Command::GpioFx3, gpioState_);
}
