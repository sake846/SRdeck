#ifndef CYUSB_BACKEND_H
#define CYUSB_BACKEND_H

#include "rx888_backend.h"
#include "fft_ddc_pipeline.h"
#include "rx888_types.h"
#include <windows.h>
#include <winioctl.h>
#include <setupapi.h>
#include <functional>
#include <vector>
#include <string>
#include <mutex>
#include <thread>
#include <atomic>
#include <deque>
#include <condition_variable>
#include <sstream>
#include <iomanip>

// Forward declarations of helper functions defined elsewhere
void NativeLog(const std::string& message);
bool IsNativeProfileEnabled();
int FindNearestIndex(const std::vector<float>& steps, double value);
int ClampInt(int value, int minValue, int maxValue);

struct DeviceInfoSnapshot
{
    std::string manufacturer;
    std::string product;
    std::string serialNumber;
    uint16_t productId = 0;
    std::string devicePath;
};

#pragma pack(push, 1)
struct CyBmRequestType
{
    uint8_t recipient : 2;
    uint8_t reserved : 3;
    uint8_t type : 2;
    uint8_t direction : 1;
};

struct CySetupPacket
{
    union
    {
        CyBmRequestType bmReqType;
        uint8_t bmRequest;
    };

    uint8_t bRequest;
    uint16_t wValue;
    uint16_t wIndex;
    uint16_t wLength;
    uint32_t ulTimeOut;
};

struct CySingleTransfer
{
    CySetupPacket SetupPacket;
    uint8_t reserved;
    uint8_t ucEndpointAddress;
    uint32_t NtStatus;
    uint32_t UsbdStatus;
    uint32_t IsoPacketOffset;
    uint32_t IsoPacketLength;
    uint32_t BufferOffset;
    uint32_t BufferLength;
};

struct CySetTransferSizeInfo
{
    uint8_t EndpointAddress;
    uint32_t TransferSize;
};
#pragma pack(pop)

struct CyBulkInAsyncTransfer
{
    OVERLAPPED overlapped{};
    CySingleTransfer transfer{};
    std::vector<unsigned char> buffer;
    bool pending = false;
};

// Forward declarations of CyAPI helper functions defined in sr_rx888.cpp
std::vector<DeviceInfoSnapshot> EnumerateMatchingCyDevices();
HANDLE CyOpenDevicePath(const std::string& devicePath);
bool CySetTransferSize(HANDLE handle, uint8_t endpointAddress, uint32_t transferSize);
bool CyControlTransfer(HANDLE handle, bool directionIn, uint8_t reqType, uint8_t reqCode, uint16_t value, uint16_t index, unsigned char* data, uint16_t length, uint32_t timeoutMs, uint32_t* bytesTransferred = nullptr);
bool CyBeginBulkInTransfer(HANDLE handle, uint8_t endpointAddress, CyBulkInAsyncTransfer& transfer, uint32_t transferSize);
void CyCleanupBulkInTransfer(HANDLE handle, CyBulkInAsyncTransfer& transfer);
bool CyFinishBulkInTransfer(HANDLE handle, CyBulkInAsyncTransfer& transfer, uint32_t timeoutMs, uint32_t* transferredBytes);

class CyUsbDriverBackend final : public Rx888Backend
{
public:
    const char* GetBackendName() const override
    {
        return "cyusb";
    }

    CyUsbDriverBackend();
    ~CyUsbDriverBackend() override;

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

    int SendNoData(Fx3Command command);
    int SendU32(Fx3Command command, uint32_t value);
    int SendU64(Fx3Command command, uint64_t value);
    int SetArgument(ArgumentId argumentId, uint16_t value);
    bool ReadHardwareInfo(uint32_t& hardwareInfo);
    bool ControlTransfer(Fx3Command command, uint16_t value, uint16_t index, unsigned char* data, uint16_t length, bool directionIn);
    bool DownloadFx3Firmware(HANDLE handle, const std::string& imageFile);
    int UpdateGpioBit(uint32_t mask, bool enabled);
    int SetRfModeImpl(RFMode rfMode);
    uint16_t EncodeHfIfGain(int gainIndex) const;
    size_t GetTransferBytes() const;
    void ResetStreamingState();
    void ResetCallbackAggregationState();
    void CompactPendingCallbackBuffer();
    bool InitializeUsbTransfers();
    void CleanupUsbTransfers();
    void StartCallbackWorker();
    void StartDdcWorker();
    void StopCallbackWorker();
    void StopDdcWorker();
    void StartUsbWorker();
    void StopUsbWorker();
    void UsbLoop();
    bool AcquireRawDdcBuffer(size_t& bufferIndex);
    void SubmitCallbackJob(std::shared_ptr<std::vector<float>> buffer, size_t floatOffset, size_t floatCount);
    void SubmitRawDdcBuffer(size_t bufferIndex);
    void ReleaseRawDdcBuffer(size_t bufferIndex);
    void DdcLoop();
    void CallbackLoop();
    void ResetProfile();
    void ReportProfile();

    HANDLE handle_ = INVALID_HANDLE_VALUE;
    bool open_ = false;
    bool streaming_ = false;
    uint32_t hardwareInfo_ = 0;
    uint8_t radioModel_ = 0;
    RFMode rfMode_ = NO_RF_MODE;
    uint32_t gpioState_ = 0;
    std::vector<CyBulkInAsyncTransfer> usbTransfers_;
    size_t usbReadIndex_ = 0;
    std::array<std::vector<int16_t>, kRawDdcBufferCount> rawDdcBuffers_;
    std::array<size_t, kRawDdcBufferCount> rawDdcSampleCounts_{};
    std::deque<size_t> freeRawDdcBuffers_;
    std::deque<size_t> readyRawDdcBuffers_;
    std::mutex ddcQueueMutex_;
    std::condition_variable ddcQueueCondition_;
    std::thread ddcThread_;
    bool ddcStopping_ = true;
    std::thread usbThread_;
    bool usbStopping_ = true;
    std::vector<float> pendingCallbackFloatBuffer_;
    size_t pendingCallbackFloatReadOffset_ = 0;
    std::mutex ddcMutex_;
    std::mutex streamMutex_;
    std::deque<CallbackJob> readyCallbackJobs_;
    std::mutex callbackMutex_;
    std::condition_variable callbackCondition_;
    std::thread callbackThread_;
    bool callbackStopping_ = true;
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
    std::chrono::steady_clock::time_point profileStart_{};
    std::chrono::steady_clock::duration profileUsbTime_{};
    std::atomic<uint64_t> profileDdcNanoseconds_{0};
    std::atomic<uint64_t> profileCallbackNanoseconds_{0};
    std::atomic<uint64_t> profileCallbackCount_{0};
    std::atomic<uint64_t> profileCallbackWaitNanoseconds_{0};
    std::atomic<uint64_t> profileCallbackWaitCount_{0};
    uint64_t profileInputBytes_ = 0;
    uint64_t profileTransfers_ = 0;
};

#endif // CYUSB_BACKEND_H
