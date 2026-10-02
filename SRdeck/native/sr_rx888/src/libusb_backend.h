#ifndef LIBUSB_BACKEND_H
#define LIBUSB_BACKEND_H

#include "rx888_backend.h"
#include "fft_ddc_pipeline.h"
#include "libusb_api.h"
#include "rx888_types.h"
#include <windows.h>
#include <vector>
#include <string>
#include <mutex>
#include <thread>
#include <atomic>
#include <deque>
#include <condition_variable>
#include <sstream>
#include <iomanip>
#include <functional>

// Forward declarations of helper functions defined elsewhere
void NativeLog(const std::string& message);
bool IsNativeProfileEnabled();
int FindNearestIndex(const std::vector<float>& steps, double value);
int ClampInt(int value, int minValue, int maxValue);

struct DeviceInfoSnapshot;

// Forward declarations of LibUsb helper functions defined in sr_rx888.cpp
std::vector<DeviceInfoSnapshot> EnumerateMatchingDevices(LibUsbApi& api);
bool DownloadFx3FirmwareImage(
    const std::string& imageFile,
    std::function<bool(uint32_t, const unsigned char*, uint16_t)>&& transferChunk,
    const char* backendName);

class LibUsbBackend final : public Rx888Backend
{
public:
    const char* GetBackendName() const override
    {
        return "libusb";
    }

    LibUsbBackend();
    ~LibUsbBackend() override;

    bool Open(int index, const std::string& imageFile) override;
    void Close() override;
    int SetSampleRate(double sampleRate) override;
    int SetAdcFrequency(double adcFrequency) override;
    int SetRfMode(RFMode rfMode) override;
    int SetTunerFrequency(double frequency) override;
    int SetTunerRfAttenuation(double attenuation) override;
    int SetTunerIfAttenuation(double attenuation) override;
    int SetAdcDither(int dither) override;
    int SetAdcRandom(int random) override;
    int SetVhfBias(int bias) override;
    int SetHfBias(int bias) override;
    int SetHfAttenuation(double attenuation) override;
    int StartStreaming() override;
    int HandleEvents() override;
    int StopStreaming() override;

    int ConfigureAsync(uint32_t frameSize, uint32_t numFrames, rx888_read_async_cb_t callback, void* callbackContext) override;

private:
    struct CallbackJob
    {
        std::shared_ptr<std::vector<float>> buffer;
        size_t floatOffset = 0;
        size_t floatCount = 0;
    };

    static constexpr uint8_t kVendorOut = 0x40;
    static constexpr uint8_t kVendorIn = 0xC0;
    static constexpr uint16_t kVendorId = 0x04B4;
    static constexpr uint16_t kStreamerProductId = 0x00F1;
    static constexpr uint16_t kBootloaderProductId = 0x00F3;
    static constexpr uint8_t kBulkInEndpoint = 0x81;
    static constexpr uint8_t kInterfaceNumber = 0;
    static constexpr uint16_t kControlTimeoutMs = 1000;
    static constexpr size_t kDefaultTransferBytes = 131072u;
    static constexpr size_t kRawDdcBufferCount = 24;
    static constexpr uint32_t kR828dReferenceFrequency = 16000000;
    static constexpr double kR828dIfCarrier = 4570000.0;
    static constexpr int kGainSweetPoint = 18;
    static constexpr uint8_t kHighModeFlag = 0x80;
    static constexpr DWORD kFx3FirmwareReenumerationDelayMs = 800;
    static constexpr int kFx3FirmwareReenumerationPollCount = 20;
    static constexpr DWORD kFx3FirmwareReenumerationPollDelayMs = 100;
    static constexpr uint8_t kFx3BootloaderVendorRequest = 0xA0;

    int SendNoData(Fx3Command command);
    size_t GetTransferBytes() const;
    void ResetStreamingState();
    void ResetCallbackAggregationState();
    void CompactPendingCallbackBuffer();
    int SendGpioState();
    int SendU32(Fx3Command command, uint32_t value);
    int SendU64(Fx3Command command, uint64_t value);
    int SetArgument(ArgumentId argumentId, uint16_t value);
    uint16_t EncodeHfIfGain(int gainIndex) const;
    int UpdateGpioBit(GpioPin pin, bool enabled);
    int SetRfModeImpl(RFMode mode);
    bool ReadHardwareInfo(uint32_t& hardwareInfo);
    int ControlTransfer(
        uint8_t requestType,
        Fx3Command command,
        uint16_t value,
        uint16_t index,
        unsigned char* data,
        uint16_t size);
    bool DownloadFx3Firmware(libusb_device_handle* handle, const std::string& imageFile);

    inline static int GetHandleEventsBurstCount()
    {
        return 16;
    }

    std::unique_ptr<LibUsbApi> api_;
    libusb_context* context_ = nullptr;
    libusb_device_handle* handle_ = nullptr;
    bool open_ = false;
    bool streaming_ = false;
    uint32_t hardwareInfo_ = 0;
    uint8_t radioModel_ = 0;
    RFMode rfMode_ = NO_RF_MODE;
    uint32_t gpioState_ = 0;
    std::vector<unsigned char> rawTransferBuffer_;
    std::vector<float> pendingCallbackFloatBuffer_;
    size_t pendingCallbackFloatReadOffset_ = 0;
    std::mutex ddcMutex_;
    std::mutex streamMutex_;
    rx888_read_async_cb_t callback_ = nullptr;
    void* callbackContext_ = nullptr;
    uint32_t frameSize_ = 0;
    uint32_t numFrames_ = 0;
    double adcFrequencyHz_ = 64000000.0;
    double sampleRateHz_ = 32000000.0;
    double tunerFrequencyHz_ = 0.0;
    std::vector<float> hfIfSteps_;
    HostDdcPipeline ddc_;
    bool ddcConfigured_ = false;
};

#endif // LIBUSB_BACKEND_H
