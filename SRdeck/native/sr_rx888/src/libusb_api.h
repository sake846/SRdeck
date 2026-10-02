#ifndef LIBUSB_API_H
#define LIBUSB_API_H

#include <windows.h>
#include <cstdint>
#include <memory>

// libusb structures forward declarations
struct libusb_context;
struct libusb_device;
struct libusb_device_handle;
struct libusb_version;

struct libusb_device_descriptor
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

using libusb_ssize_t = ptrdiff_t;

class LibUsbApi
{
public:
    using fn_init = int (*)(libusb_context**);
    using fn_exit = void (*)(libusb_context*);
    using fn_get_device_list = libusb_ssize_t (*)(libusb_context*, libusb_device***);
    using fn_free_device_list = void (*)(libusb_device**, int);
    using fn_get_device_descriptor = int (*)(libusb_device*, libusb_device_descriptor*);
    using fn_open = int (*)(libusb_device*, libusb_device_handle**);
    using fn_close = void (*)(libusb_device_handle*);
    using fn_claim_interface = int (*)(libusb_device_handle*, int);
    using fn_release_interface = int (*)(libusb_device_handle*, int);
    using fn_get_string_descriptor_ascii = int (*)(libusb_device_handle*, uint8_t, unsigned char*, int);
    using fn_control_transfer = int (*)(libusb_device_handle*, uint8_t, uint8_t, uint16_t, uint16_t, unsigned char*, uint16_t, unsigned int);
    using fn_bulk_transfer = int (*)(libusb_device_handle*, unsigned char, unsigned char*, int, int*, unsigned int);

    ~LibUsbApi();

    static std::unique_ptr<LibUsbApi> Create();

    bool Available() const;

    fn_init init_ = nullptr;
    fn_exit exit_ = nullptr;
    fn_get_device_list getDeviceList_ = nullptr;
    fn_free_device_list freeDeviceList_ = nullptr;
    fn_get_device_descriptor getDeviceDescriptor_ = nullptr;
    fn_open open_ = nullptr;
    fn_close close_ = nullptr;
    fn_claim_interface claimInterface_ = nullptr;
    fn_release_interface releaseInterface_ = nullptr;
    fn_get_string_descriptor_ascii getStringDescriptorAscii_ = nullptr;
    fn_control_transfer controlTransfer_ = nullptr;
    fn_bulk_transfer bulkTransfer_ = nullptr;

private:
    LibUsbApi() = default;

    template <typename T>
    bool Bind(T& target, const char* name)
    {
        target = reinterpret_cast<T>(GetProcAddress(module_, name));
        return target != nullptr;
    }

    bool Load();

    HMODULE module_ = nullptr;
};

#endif // LIBUSB_API_H
