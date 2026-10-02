bool CyGetDeviceDescriptor(HANDLE device, UsbDeviceDescriptor& descriptor)
{
    std::memset(&descriptor, 0, sizeof(descriptor));
    return CyControlTransfer(
        device,
        true,
        0,
        0x06,
        static_cast<uint16_t>(0x0100),
        0,
        reinterpret_cast<unsigned char*>(&descriptor),
        sizeof(descriptor),
        kControlTimeoutMs);
}

std::string CyReadStringDescriptorAscii(HANDLE device, uint8_t index)
{
    if (index == 0)
    {
        return {};
    }

    std::array<unsigned char, 256> raw{};
    uint32_t transferred = 0;
    if (!CyControlTransfer(device, true, 0, 0x06, static_cast<uint16_t>(0x0300u | index), 0x0409, raw.data(), static_cast<uint16_t>(raw.size()), kControlTimeoutMs, &transferred))
    {
        return {};
    }

    if (transferred < 2 || raw[1] != 0x03)
    {
        return {};
    }

    const auto* descriptor = reinterpret_cast<const UsbStringDescriptor*>(raw.data());
    const size_t wcharCount = std::min<size_t>((descriptor->bLength >= 2 ? (descriptor->bLength - 2) / sizeof(wchar_t) : 0), 126);
    if (wcharCount == 0)
    {
        return {};
    }

    int ansiLength = WideCharToMultiByte(CP_ACP, 0, descriptor->bString, static_cast<int>(wcharCount), nullptr, 0, nullptr, nullptr);
    if (ansiLength <= 0)
    {
        return {};
    }

    std::string result(static_cast<size_t>(ansiLength), '\0');
    WideCharToMultiByte(CP_ACP, 0, descriptor->bString, static_cast<int>(wcharCount), result.data(), ansiLength, nullptr, nullptr);
    return result;
}

bool CySetTransferSize(HANDLE device, uint8_t endpointAddress, uint32_t transferSize)
{
    CySetTransferSizeInfo info{};
    info.EndpointAddress = endpointAddress;
    info.TransferSize = transferSize;
    DWORD returned = 0;
    return CyDeviceIoControlSync(
        device,
        kIoctlAdaptSetTransferSize,
        &info,
        sizeof(info),
        &info,
        sizeof(info),
        kControlTimeoutMs,
        &returned);
}

bool CyBeginBulkInTransfer(
    HANDLE device,
    uint8_t endpointAddress,
    CyBulkInAsyncTransfer& context,
    uint32_t payloadLength)
{
    if (context.overlapped.hEvent == nullptr)
    {
        context.overlapped.hEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (context.overlapped.hEvent == nullptr)
        {
            return false;
        }
    }

    const HANDLE eventHandle = context.overlapped.hEvent;
    context.buffer.resize(payloadLength);
    std::memset(&context.overlapped, 0, sizeof(context.overlapped));
    context.overlapped.hEvent = eventHandle;
    std::memset(&context.transfer, 0, sizeof(context.transfer));
    context.transfer.ucEndpointAddress = endpointAddress;

    DWORD returned = 0;
    ResetEvent(context.overlapped.hEvent);
    const BOOL ok = DeviceIoControl(
        device,
        kIoctlAdaptSendNonEp0Direct,
        &context.transfer,
        sizeof(context.transfer),
        context.buffer.data(),
        static_cast<DWORD>(context.buffer.size()),
        &returned,
        &context.overlapped);
    if (!ok && GetLastError() != ERROR_IO_PENDING)
    {
        return false;
    }

    context.pending = true;
    return true;
}

bool CyFinishBulkInTransfer(
    HANDLE device,
    CyBulkInAsyncTransfer& context,
    uint32_t timeoutMs,
    uint32_t* bytesTransferred)
{
    if (!context.pending)
    {
        return false;
    }

    const DWORD waitResult = WaitForSingleObject(context.overlapped.hEvent, timeoutMs);
    if (waitResult != WAIT_OBJECT_0)
    {
        CancelIoEx(device, &context.overlapped);
        WaitForSingleObject(context.overlapped.hEvent, INFINITE);
        context.pending = false;
        return false;
    }

    DWORD transferred = 0;
    const BOOL ok = GetOverlappedResult(device, &context.overlapped, &transferred, FALSE);
    context.pending = false;
    if (!ok || context.transfer.NtStatus != 0 || context.transfer.UsbdStatus != 0)
    {
        return false;
    }

    if (bytesTransferred != nullptr)
    {
        *bytesTransferred = std::min<uint32_t>(static_cast<uint32_t>(context.buffer.size()), transferred);
    }
    return true;
}

void CyCleanupBulkInTransfer(HANDLE device, CyBulkInAsyncTransfer& context)
{
    if (context.pending && device != INVALID_HANDLE_VALUE)
    {
        CancelIoEx(device, &context.overlapped);
        if (context.overlapped.hEvent != nullptr)
        {
            WaitForSingleObject(context.overlapped.hEvent, INFINITE);
        }
        context.pending = false;
    }
    if (context.overlapped.hEvent != nullptr)
    {
        CloseHandle(context.overlapped.hEvent);
        context.overlapped.hEvent = nullptr;
    }
    context.buffer.clear();
}

std::vector<DeviceInfoSnapshot> EnumerateMatchingDevices(LibUsbApi& api)
{
    std::vector<DeviceInfoSnapshot> result;
    libusb_context* context = nullptr;
    if (api.init_(&context) != 0)
    {
        return result;
    }

    libusb_device** devices = nullptr;
    const libusb_ssize_t count = api.getDeviceList_(context, &devices);
    if (count < 0 || devices == nullptr)
    {
        api.exit_(context);
        return result;
    }

    for (libusb_ssize_t i = 0; i < count; ++i)
    {
        libusb_device* device = devices[i];
        if (device == nullptr)
        {
            continue;
        }

        libusb_device_descriptor descriptor{};
        if (api.getDeviceDescriptor_(device, &descriptor) != 0)
        {
            continue;
        }

        if (descriptor.idVendor != kVendorId)
        {
            continue;
        }

        if (descriptor.idProduct != kStreamerProductId &&
            descriptor.idProduct != kBootloaderProductId)
        {
            continue;
        }

        DeviceInfoSnapshot snapshot{};
        snapshot.productId = descriptor.idProduct;

        libusb_device_handle* handle = nullptr;
        if (api.open_(device, &handle) == 0 && handle != nullptr)
        {
            snapshot.manufacturer = ReadDescriptorString(api, handle, descriptor.iManufacturer);
            snapshot.product = ReadDescriptorString(api, handle, descriptor.iProduct);
            snapshot.serialNumber = ReadDescriptorString(api, handle, descriptor.iSerialNumber);
            api.close_(handle);
        }

        result.push_back(std::move(snapshot));
    }

    api.freeDeviceList_(devices, 1);
    api.exit_(context);
    return result;
}

std::vector<DeviceInfoSnapshot> EnumerateMatchingCyDevices()
{
    std::vector<DeviceInfoSnapshot> result;
    std::vector<std::string> devicePaths;
    if (!CyEnumerateDevicePaths(devicePaths))
    {
        return result;
    }

    for (const std::string& devicePath : devicePaths)
    {
        HANDLE device = CyOpenDevicePath(devicePath);
        if (device == INVALID_HANDLE_VALUE)
        {
            continue;
        }

        UsbDeviceDescriptor descriptor{};
        if (!CyGetDeviceDescriptor(device, descriptor))
        {
            NativeLog("CyGetDeviceDescriptor failed path=" + devicePath + " err=" + std::to_string(GetLastError()));
            CloseHandle(device);
            continue;
        }

        if (descriptor.idVendor != kVendorId)
        {
            CloseHandle(device);
            continue;
        }

        if (descriptor.idProduct != kStreamerProductId &&
            descriptor.idProduct != kBootloaderProductId)
        {
            CloseHandle(device);
            continue;
        }

        DeviceInfoSnapshot snapshot{};
        snapshot.productId = descriptor.idProduct;
        snapshot.manufacturer = CyReadStringDescriptorAscii(device, descriptor.iManufacturer);
        snapshot.product = CyReadStringDescriptorAscii(device, descriptor.iProduct);
        snapshot.serialNumber = CyReadStringDescriptorAscii(device, descriptor.iSerialNumber);
        snapshot.devicePath = devicePath;
        NativeLog("CyEnumerate path=" + devicePath + " vid=" + std::to_string(descriptor.idVendor) + " pid=" + std::to_string(descriptor.idProduct) + " product=" + snapshot.product);
        result.push_back(std::move(snapshot));
        CloseHandle(device);
    }

    return result;
}

std::filesystem::path GetCurrentModulePath()
{
    HMODULE currentModule = nullptr;
    if (!GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&GetCurrentModulePath),
            &currentModule))
    {
        return {};
    }

    wchar_t modulePath[MAX_PATH] = {};
    if (GetModuleFileNameW(currentModule, modulePath, static_cast<DWORD>(std::size(modulePath))) == 0)
    {
        return {};
    }

    return std::filesystem::path(modulePath);
}

void NativeLog(const std::string& message)
{
    static const bool enabled = true;

    const std::filesystem::path path = GetCurrentModulePath().parent_path() / "native_rx888.log";
    std::ofstream stream(path, std::ios::app);
    if (!stream)
    {
        return;
    }

    SYSTEMTIME now{};
    GetLocalTime(&now);
    stream << '['
           << std::setfill('0')
           << std::setw(4) << now.wYear << '-'
           << std::setw(2) << now.wMonth << '-'
           << std::setw(2) << now.wDay << ' '
           << std::setw(2) << now.wHour << ':'
           << std::setw(2) << now.wMinute << ':'
           << std::setw(2) << now.wSecond << "] "
           << message
           << "\n";
}

bool GetEnvironmentBool(const char* name)
{
    char buffer[16] = {};
    const DWORD length = GetEnvironmentVariableA(name, buffer, static_cast<DWORD>(std::size(buffer)));
    if (length == 0 || length >= std::size(buffer))
    {
        return false;
    }

    std::string value(buffer, buffer + length);
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char c)
    {
        return static_cast<char>(std::tolower(c));
    });
    return value == "1" || value == "true" || value == "on" || value == "yes";
}

bool IsNativeProfileEnabled()
{
    static const bool enabled = GetEnvironmentBool("SRDECK_RX888_NATIVE_PROFILE");
    return enabled;
}

int GetEnvironmentInt(const char* name, int defaultValue, int minValue, int maxValue)
{
    char buffer[64] = {};
    const DWORD length = GetEnvironmentVariableA(name, buffer, static_cast<DWORD>(std::size(buffer)));
    if (length == 0 || length >= std::size(buffer))
    {
        return defaultValue;
    }

    char* end = nullptr;
    const long parsed = std::strtol(buffer, &end, 10);
    if (end == buffer || (end != nullptr && *end != '\0'))
    {
        return defaultValue;
    }

    return ClampInt(static_cast<int>(parsed), minValue, maxValue);
}

int GetHandleEventsBurstCount()
{
    static const int burstCount = GetEnvironmentInt("SRDECK_RX888_NATIVE_BURST", kHandleEventsBurstCount, 1, 64);
    return burstCount;
}

int GetUsbReadConcurrentCount()
{
    static const int usbReads = GetEnvironmentInt("SRDECK_RX888_USB_READS", static_cast<int>(kUsbReadConcurrent), 4, 64);
    return usbReads;
}

struct rx888
{
    std::mutex mutex;
    std::unique_ptr<Rx888Backend> backend;
    std::string imageFile;
    RX888Status status = RX888_STATUS_OFF;
    RFMode rfMode = NO_RF_MODE;
    double sampleRate = 32000000.0;
    double adcFrequency = 64000000.0;
    double tunerFrequency = 0.0;
    double tunerRfAttenuation = 0.0;
    double tunerIfAttenuation = 0.0;
    double hfAttenuation = 0.0;
    int adcDither = 0;
    int adcRandom = 0;
    int vhfBias = 0;
    int hfBias = 0;
    uint32_t frameSize = 0;
    uint32_t numFrames = 0;
    rx888_read_async_cb_t callback = nullptr;
    void* callbackContext = nullptr;
};
