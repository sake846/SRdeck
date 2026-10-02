#include "libusb_api.h"

LibUsbApi::~LibUsbApi()
{
    if (module_ != nullptr)
    {
        FreeLibrary(module_);
    }
}

std::unique_ptr<LibUsbApi> LibUsbApi::Create()
{
    auto api = std::unique_ptr<LibUsbApi>(new LibUsbApi());
    if (!api->Load())
    {
        return nullptr;
    }
    return api;
}

bool LibUsbApi::Available() const
{
    return init_ && exit_ && getDeviceList_ && freeDeviceList_ && getDeviceDescriptor_ &&
           open_ && close_ && claimInterface_ && releaseInterface_ &&
           getStringDescriptorAscii_ && controlTransfer_ && bulkTransfer_;
}

bool LibUsbApi::Load()
{
    module_ = LoadLibraryW(L"libusb-1.0.dll");
    if (module_ == nullptr)
    {
        return false;
    }

    return Bind(init_, "libusb_init") &&
           Bind(exit_, "libusb_exit") &&
           Bind(getDeviceList_, "libusb_get_device_list") &&
           Bind(freeDeviceList_, "libusb_free_device_list") &&
           Bind(getDeviceDescriptor_, "libusb_get_device_descriptor") &&
           Bind(open_, "libusb_open") &&
           Bind(close_, "libusb_close") &&
           Bind(claimInterface_, "libusb_claim_interface") &&
           Bind(releaseInterface_, "libusb_release_interface") &&
           Bind(getStringDescriptorAscii_, "libusb_get_string_descriptor_ascii") &&
           Bind(controlTransfer_, "libusb_control_transfer") &&
           Bind(bulkTransfer_, "libusb_bulk_transfer");
}
