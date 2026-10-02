#ifndef RX888_TYPES_H
#define RX888_TYPES_H

#include <cstdint>
#include <vector>
#include <string>
#include <functional>
#include <windows.h>
#include <winioctl.h>

constexpr size_t kRawDdcBufferCount = 24;

enum class Fx3Command : uint8_t
{
    StartFx3 = 0xAA,
    StopFx3 = 0xAB,
    TestFx3 = 0xAC,
    GpioFx3 = 0xAD,
    I2cWriteFx3 = 0xAE,
    I2cReadFx3 = 0xAF,
    ResetFx3 = 0xB1,
    StartAdc = 0xB2,
    TunerInit = 0xB4,
    TunerTune = 0xB5,
    SetArgFx3 = 0xB6,
    TunerStandby = 0xB8,
    ReadInfoDebug = 0xBA
};

enum class ArgumentId : uint16_t
{
    R82xxAttenuator = 1,
    R82xxVga = 2,
    Dat31Att = 10,
    Ad8340Vga = 11
};

enum GpioPin : uint32_t
{
    Dith = 1u << 6,
    Rando = 1u << 7,
    BiasHf = 1u << 8,
    BiasVhf = 1u << 9,
    VhfEn = 1u << 15,
    PgaEn = 1u << 16
};

#pragma pack(push, 1)
struct UsbDeviceDescriptor
{
    uint8_t bLength;
    uint8_t bDescriptorType;
    uint16_t bcdUSB;
    uint8_t bDeviceClass;
    uint8_t bDeviceSubClass;
    uint8_t bDeviceProtocol;
    uint8_t bMaxPacketSize0;
    uint16_t idVendor;
    uint16_t idProduct;
    uint16_t bcdDevice;
    uint8_t iManufacturer;
    uint8_t iProduct;
    uint8_t iSerialNumber;
    uint8_t bNumConfigurations;
};

struct UsbStringDescriptor
{
    uint8_t bLength;
    uint8_t bDescriptorType;
    wchar_t bString[1];
};
#pragma pack(pop)

#define kIoctlAdaptSendEp0ControlTransfer CTL_CODE(FILE_DEVICE_UNKNOWN, 8, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define kIoctlAdaptSetTransferSize CTL_CODE(FILE_DEVICE_UNKNOWN, 14, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define kIoctlAdaptSendNonEp0Transfer CTL_CODE(FILE_DEVICE_UNKNOWN, 9, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define kIoctlAdaptSendNonEp0Direct CTL_CODE(FILE_DEVICE_UNKNOWN, 18, METHOD_NEITHER, FILE_ANY_ACCESS)

// Shared Constants
inline const std::vector<float> kVhfRfSteps = {
    0.0f, 0.9f, 1.4f, 2.7f, 3.7f, 7.7f, 8.7f, 12.5f, 14.4f, 15.7f,
    16.6f, 19.7f, 20.7f, 22.9f, 25.4f, 28.0f, 29.7f, 32.8f,
    33.8f, 36.4f, 37.2f, 38.6f, 40.2f, 42.1f, 43.4f, 43.9f,
    44.5f, 48.0f, 49.6f
};

inline const std::vector<float> kVhfIfSteps = {
    -4.7f, -2.1f, 0.5f, 3.5f, 7.7f, 11.2f, 13.6f, 14.9f,
    16.3f, 19.5f, 23.1f, 26.5f, 30.0f, 33.7f, 37.2f, 40.8f
};

// RAII helper for thread priority management
class ScopedHighPriorityThread
{
public:
    ScopedHighPriorityThread()
    {
        previousPriority_ = GetThreadPriority(GetCurrentThread());
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
    }
    ~ScopedHighPriorityThread()
    {
        SetThreadPriority(GetCurrentThread(), previousPriority_);
    }
private:
    int previousPriority_ = THREAD_PRIORITY_NORMAL;
};

// Forward declaration of global firmware helper
bool DownloadFx3FirmwareImage(
    const std::string& imageFile,
    std::function<bool(uint32_t, const unsigned char*, uint16_t)>&& transferChunk,
    const char* backendName);

#endif // RX888_TYPES_H
