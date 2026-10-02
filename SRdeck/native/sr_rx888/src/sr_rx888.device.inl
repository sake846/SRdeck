#include "sr_rx888.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstring>
#include <cctype>
#include <cstdint>
#include <deque>
#include <filesystem>
#include <iomanip>
#include <limits>
#include <memory>
#include <mutex>
#include <new>
#include <fstream>
#include <immintrin.h>
#include <sstream>
#include <string>
#include <system_error>
#include <thread>
#include <utility>
#include <vector>

#include <windows.h>
#include <winioctl.h>
#include <setupapi.h>

// Forward declarations of core utility functions
bool GetEnvironmentBool(const char* name);
bool IsNativeProfileEnabled();
int ClampInt(int value, int minValue, int maxValue);
int GetEnvironmentInt(const char* name, int defaultValue, int minValue, int maxValue);
void NativeLog(const std::string& message);

#include "rx888_types.h"
#include "libusb_api.h"
#include "fft_ddc_pipeline.h"
#include "rx888_backend.h"
#include "cyusb_backend.h"
#include "libusb_backend.h"

namespace
{
constexpr uint16_t kVendorId = 0x04B4;
constexpr uint16_t kStreamerProductId = 0x00F1;
constexpr uint16_t kBootloaderProductId = 0x00F3;
constexpr uint8_t kBulkInEndpoint = 0x81;
constexpr uint8_t kInterfaceNumber = 0;
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
constexpr double kPi = 3.14159265358979323846;
constexpr double kTwoPi = 6.28318530717958647692;
constexpr float kRx888Mk2GainFactor = 1.08e-8f;
constexpr int kGainSweetPoint = 18;
constexpr float kHighGainRatio = 0.409f;
constexpr float kLowGainRatio = 0.059f;
constexpr uint8_t kHighModeFlag = 0x80;

constexpr GUID kCyUsbDriverGuid = {0xae18aa60, 0x7f6a, 0x11d4, {0x97, 0xdd, 0x00, 0x01, 0x02, 0x29, 0xb9, 0x59}};

constexpr uint8_t kFx3BootloaderVendorRequest = 0xA0;
constexpr size_t kFx3FirmwareChunkBytes = 2048;
constexpr DWORD kFx3FirmwareReenumerationDelayMs = 800;
constexpr int kFx3FirmwareReenumerationPollCount = 20;
constexpr DWORD kFx3FirmwareReenumerationPollDelayMs = 100;

struct DeviceInfoBlockHeader
{
    static constexpr uint32_t kMagic = 0x38385852; // "RX88"
    uint32_t magic = kMagic;
    size_t count = 0;
};
} // namespace

static_assert(sizeof(UsbDeviceDescriptor) == 18, "UsbDeviceDescriptor size mismatch");
static_assert(sizeof(CySingleTransfer) == 38, "CySingleTransfer size mismatch");

bool IsSupportedSampleRate(double sampleRate)
{
    return sampleRate == 64000000.0 ||
           sampleRate == 32000000.0 ||
           sampleRate == 16000000.0 ||
           sampleRate == 8000000.0 ||
           sampleRate == 4000000.0 ||
           sampleRate == 2000000.0;
}

bool IsWholeNumber(double value)
{
    return std::isfinite(value) && std::floor(value) == value;
}

int ClampInt(int value, int minValue, int maxValue)
{
    return std::max(minValue, std::min(value, maxValue));
}

int FindNearestIndex(const std::vector<float>& steps, double value)
{
    int bestIndex = 0;
    double bestDistance = std::numeric_limits<double>::infinity();

    for (size_t i = 0; i < steps.size(); ++i)
    {
        const double distance = std::abs(static_cast<double>(steps[i]) - value);
        if (distance < bestDistance)
        {
            bestDistance = distance;
            bestIndex = static_cast<int>(i);
        }
    }

    return bestIndex;
}

std::string ReadDescriptorString(
    LibUsbApi& api,
    libusb_device_handle* handle,
    uint8_t index)
{
    if (index == 0 || handle == nullptr)
    {
        return {};
    }

    std::array<unsigned char, 256> buffer{};
    const int rc = api.getStringDescriptorAscii_(handle, index, buffer.data(), static_cast<int>(buffer.size()));
    if (rc <= 0)
    {
        return {};
    }

    return std::string(reinterpret_cast<const char*>(buffer.data()), static_cast<size_t>(rc));
}

uint32_t ReadLe32(const unsigned char* data)
{
    return static_cast<uint32_t>(data[0]) |
           (static_cast<uint32_t>(data[1]) << 8) |
           (static_cast<uint32_t>(data[2]) << 16) |
           (static_cast<uint32_t>(data[3]) << 24);
}

bool ReadBinaryFile(const std::string& path, std::vector<unsigned char>& bytes)
{
    bytes.clear();

    std::ifstream stream(path, std::ios::binary);
    if (!stream)
    {
        return false;
    }

    stream.seekg(0, std::ios::end);
    const std::streamoff length = stream.tellg();
    if (length <= 0)
    {
        return false;
    }

    stream.seekg(0, std::ios::beg);
    bytes.resize(static_cast<size_t>(length));
    stream.read(reinterpret_cast<char*>(bytes.data()), length);
    return stream.good() || stream.eof();
}

bool DownloadFx3FirmwareImage(
    const std::string& imageFile,
    std::function<bool(uint32_t, const unsigned char*, uint16_t)>&& transferChunk,
    const char* backendName)
{
    std::vector<unsigned char> image;
    if (!ReadBinaryFile(imageFile, image))
    {
        NativeLog(std::string(backendName) + " firmware read failed path=" + imageFile);
        return false;
    }

    if (image.size() < 12 || image[0] != 0x43 || image[1] != 0x59)
    {
        NativeLog(std::string(backendName) + " firmware invalid signature path=" + imageFile);
        return false;
    }

    size_t offset = 4;
    uint32_t computedChecksum = 0;
    uint32_t entryAddress = 0;
    bool sawTrailer = false;
    while (true)
    {
        if ((offset + 8) > image.size())
        {
            NativeLog(std::string(backendName) + " firmware truncated header path=" + imageFile);
            return false;
        }

        const uint32_t sectionWords = ReadLe32(image.data() + offset);
        offset += 4;
        const uint32_t sectionAddress = ReadLe32(image.data() + offset);
        offset += 4;

        if (sectionWords == 0)
        {
            entryAddress = sectionAddress;
            sawTrailer = true;
            break;
        }

        const size_t sectionBytes = static_cast<size_t>(sectionWords) * sizeof(uint32_t);
        if ((offset + sectionBytes) > image.size())
        {
            NativeLog(std::string(backendName) + " firmware truncated payload path=" + imageFile);
            return false;
        }

        for (size_t i = 0; i < sectionBytes; i += sizeof(uint32_t))
        {
            computedChecksum += ReadLe32(image.data() + offset + i);
        }

        size_t remaining = sectionBytes;
        uint32_t chunkAddress = sectionAddress;
        const unsigned char* chunkData = image.data() + offset;
        while (remaining > 0)
        {
            const uint16_t chunkSize = static_cast<uint16_t>(std::min(remaining, kFx3FirmwareChunkBytes));
            if (!transferChunk(chunkAddress, chunkData, chunkSize))
            {
                NativeLog(std::string(backendName) + " firmware transfer failed addr=" + std::to_string(chunkAddress));
                return false;
            }

            chunkAddress += chunkSize;
            chunkData += chunkSize;
            remaining -= chunkSize;
        }

        offset += sectionBytes;
    }

    if (!sawTrailer || (offset + 4) > image.size())
    {
        NativeLog(std::string(backendName) + " firmware missing trailer path=" + imageFile);
        return false;
    }

    const uint32_t expectedChecksum = ReadLe32(image.data() + offset);

    if (computedChecksum != expectedChecksum)
    {
        NativeLog(
            std::string(backendName) + " firmware checksum mismatch expected=" +
            std::to_string(expectedChecksum) + " actual=" + std::to_string(computedChecksum));
    }

    if (!transferChunk(entryAddress, nullptr, 0))
    {
        NativeLog(std::string(backendName) + " firmware jump returned failure addr=" + std::to_string(entryAddress));
    }

    return true;
}

bool CyEnumerateDevicePaths(std::vector<std::string>& devicePaths)
{
    devicePaths.clear();

    HDEVINFO hwDeviceInfo = SetupDiGetClassDevsA(
        const_cast<LPGUID>(&kCyUsbDriverGuid),
        nullptr,
        nullptr,
        DIGCF_PRESENT | DIGCF_INTERFACEDEVICE);
    if (hwDeviceInfo == INVALID_HANDLE_VALUE)
    {
        return false;
    }

    SP_DEVICE_INTERFACE_DATA interfaceData{};
    interfaceData.cbSize = sizeof(interfaceData);

    for (DWORD index = 0;; ++index)
    {
        if (!SetupDiEnumDeviceInterfaces(hwDeviceInfo, nullptr, const_cast<LPGUID>(&kCyUsbDriverGuid), index, &interfaceData))
        {
            if (GetLastError() == ERROR_NO_MORE_ITEMS)
            {
                break;
            }
            continue;
        }

        DWORD requiredLength = 0;
        SetupDiGetDeviceInterfaceDetailA(hwDeviceInfo, &interfaceData, nullptr, 0, &requiredLength, nullptr);
        if (requiredLength == 0)
        {
            continue;
        }

        auto detailStorage = std::make_unique<unsigned char[]>(requiredLength);
        auto* detail = reinterpret_cast<SP_DEVICE_INTERFACE_DETAIL_DATA_A*>(detailStorage.get());
        detail->cbSize = sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_A);

        if (SetupDiGetDeviceInterfaceDetailA(hwDeviceInfo, &interfaceData, detail, requiredLength, &requiredLength, nullptr))
        {
            devicePaths.emplace_back(detail->DevicePath);
        }
    }

    SetupDiDestroyDeviceInfoList(hwDeviceInfo);
    return true;
}

HANDLE CyOpenDevicePath(const std::string& devicePath)
{
    HANDLE handle = CreateFileA(
        devicePath.c_str(),
        GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr,
        OPEN_EXISTING,
        FILE_FLAG_OVERLAPPED,
        nullptr);
    if (handle == INVALID_HANDLE_VALUE)
    {
        NativeLog("CyOpenDevicePath failed path=" + devicePath + " err=" + std::to_string(GetLastError()));
    }
    return handle;
}

bool CyDeviceIoControlSync(
    HANDLE device,
    ULONG ioctlCode,
    void* inBuffer,
    DWORD inBufferSize,
    void* outBuffer,
    DWORD outBufferSize,
    DWORD timeoutMs,
    DWORD* bytesReturned)
{
    OVERLAPPED operation{};
    operation.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (operation.hEvent == nullptr)
    {
        return false;
    }

    DWORD localBytesReturned = 0;
    BOOL ok = DeviceIoControl(
        device,
        ioctlCode,
        inBuffer,
        inBufferSize,
        outBuffer,
        outBufferSize,
        &localBytesReturned,
        &operation);
    if (!ok && GetLastError() == ERROR_IO_PENDING)
    {
        const DWORD waitResult = WaitForSingleObject(operation.hEvent, timeoutMs);
        if (waitResult == WAIT_OBJECT_0)
        {
            ok = GetOverlappedResult(device, &operation, &localBytesReturned, FALSE);
        }
        else
        {
            CancelIoEx(device, &operation);
            GetOverlappedResult(device, &operation, &localBytesReturned, TRUE);
            CloseHandle(operation.hEvent);
            SetLastError(waitResult == WAIT_TIMEOUT ? ERROR_TIMEOUT : ERROR_GEN_FAILURE);
            return false;
        }
    }

    const DWORD error = ok ? ERROR_SUCCESS : GetLastError();
    CloseHandle(operation.hEvent);
    if (!ok)
    {
        SetLastError(error);
        return false;
    }

    if (bytesReturned != nullptr)
    {
        *bytesReturned = localBytesReturned;
    }
    return true;
}

bool CyControlTransfer(
    HANDLE device,
    bool directionIn,
    uint8_t requestType,
    uint8_t request,
    uint16_t value,
    uint16_t index,
    unsigned char* payload,
    uint16_t payloadLength,
    uint32_t timeoutMs,
    uint32_t* bytesTransferred)
{
    const size_t totalSize = sizeof(CySingleTransfer) + payloadLength;
    std::vector<unsigned char> buffer(totalSize, 0);
    auto* transfer = reinterpret_cast<CySingleTransfer*>(buffer.data());
    transfer->SetupPacket.bmReqType.recipient = 0;
    transfer->SetupPacket.bmReqType.type = requestType;
    transfer->SetupPacket.bmReqType.direction = directionIn ? 1 : 0;
    transfer->SetupPacket.bRequest = request;
    transfer->SetupPacket.wValue = value;
    transfer->SetupPacket.wIndex = index;
    transfer->SetupPacket.wLength = payloadLength;
    transfer->SetupPacket.ulTimeOut = std::max<uint32_t>(1, timeoutMs / 1000);
    transfer->ucEndpointAddress = 0;
    transfer->BufferOffset = sizeof(CySingleTransfer);
    transfer->BufferLength = payloadLength;

    if (!directionIn && payload != nullptr && payloadLength > 0)
    {
        std::memcpy(buffer.data() + sizeof(CySingleTransfer), payload, payloadLength);
    }

    DWORD returned = 0;
    const BOOL ok = CyDeviceIoControlSync(
        device,
        kIoctlAdaptSendEp0ControlTransfer,
        buffer.data(),
        static_cast<DWORD>(buffer.size()),
        buffer.data(),
        static_cast<DWORD>(buffer.size()),
        timeoutMs,
        &returned);
    if (!ok || transfer->NtStatus != 0 || transfer->UsbdStatus != 0)
    {
        return false;
    }

    const uint32_t transferred = returned >= transfer->BufferOffset ? returned - transfer->BufferOffset : 0;
    if (directionIn && payload != nullptr && payloadLength > 0 && transferred > 0)
    {
        std::memcpy(payload, buffer.data() + transfer->BufferOffset, std::min<uint32_t>(payloadLength, transferred));
    }

    if (bytesTransferred != nullptr)
    {
        *bytesTransferred = transferred;
    }
    return true;
}
