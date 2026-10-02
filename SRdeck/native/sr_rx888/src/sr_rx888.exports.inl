extern "C"
{
RX888_API int rx888_get_device_count(void)
{
    const int cyCount = static_cast<int>(EnumerateMatchingCyDevices().size());
    if (cyCount > 0)
    {
        return cyCount;
    }

    auto api = LibUsbApi::Create();
    if (!api || !api->Available())
    {
        return 0;
    }

    return static_cast<int>(EnumerateMatchingDevices(*api).size());
}

RX888_API int rx888_get_device_info(struct rx888_device_info** infos)
{
    if (infos == nullptr)
    {
        return -1;
    }

    *infos = nullptr;

    const std::vector<DeviceInfoSnapshot> cyDevices = EnumerateMatchingCyDevices();
    if (!cyDevices.empty())
    {
        const std::vector<DeviceInfoSnapshot>& devices = cyDevices;
        size_t stringBytes = 0;
        for (const DeviceInfoSnapshot& device : devices)
        {
            stringBytes += device.manufacturer.size() + 1;
            stringBytes += device.product.size() + 1;
            stringBytes += device.serialNumber.size() + 1;
        }

        const size_t totalBytes =
            sizeof(DeviceInfoBlockHeader) +
            (sizeof(rx888_device_info) * devices.size()) +
            stringBytes;

        auto* storage = new (std::nothrow) unsigned char[totalBytes];
        if (storage == nullptr)
        {
            return -1;
        }

        auto* header = reinterpret_cast<DeviceInfoBlockHeader*>(storage);
        header->magic = DeviceInfoBlockHeader::kMagic;
        header->count = devices.size();

        auto* result = reinterpret_cast<rx888_device_info*>(storage + sizeof(DeviceInfoBlockHeader));
        char* stringCursor = reinterpret_cast<char*>(result + devices.size());

        for (size_t i = 0; i < devices.size(); ++i)
        {
            const DeviceInfoSnapshot& device = devices[i];
            const size_t manufacturerLength = device.manufacturer.size() + 1;
            result[i].manufacturer = stringCursor;
            std::memcpy(stringCursor, device.manufacturer.c_str(), manufacturerLength);
            stringCursor += manufacturerLength;

            const size_t productLength = device.product.size() + 1;
            result[i].product = stringCursor;
            std::memcpy(stringCursor, device.product.c_str(), productLength);
            stringCursor += productLength;

            const size_t serialLength = device.serialNumber.size() + 1;
            result[i].serial_number = stringCursor;
            std::memcpy(stringCursor, device.serialNumber.c_str(), serialLength);
            stringCursor += serialLength;
        }

        *infos = result;
        return static_cast<int>(devices.size());
    }

    auto api = LibUsbApi::Create();
    if (!api || !api->Available())
    {
        return 0;
    }

    const std::vector<DeviceInfoSnapshot> devices = EnumerateMatchingDevices(*api);
    if (devices.empty())
    {
        return 0;
    }

    size_t stringBytes = 0;
    for (const DeviceInfoSnapshot& device : devices)
    {
        stringBytes += device.manufacturer.size() + 1;
        stringBytes += device.product.size() + 1;
        stringBytes += device.serialNumber.size() + 1;
    }

    const size_t totalBytes =
        sizeof(DeviceInfoBlockHeader) +
        (sizeof(rx888_device_info) * devices.size()) +
        stringBytes;

    auto* storage = new (std::nothrow) unsigned char[totalBytes];
    if (storage == nullptr)
    {
        return -1;
    }

    auto* header = reinterpret_cast<DeviceInfoBlockHeader*>(storage);
    header->magic = DeviceInfoBlockHeader::kMagic;
    header->count = devices.size();

    auto* result = reinterpret_cast<rx888_device_info*>(storage + sizeof(DeviceInfoBlockHeader));
    char* stringCursor = reinterpret_cast<char*>(result + devices.size());

    for (size_t i = 0; i < devices.size(); ++i)
    {
        const DeviceInfoSnapshot& device = devices[i];
        const size_t manufacturerLength = device.manufacturer.size() + 1;
        result[i].manufacturer = stringCursor;
        std::memcpy(stringCursor, device.manufacturer.c_str(), manufacturerLength);
        stringCursor += manufacturerLength;

        const size_t productLength = device.product.size() + 1;
        result[i].product = stringCursor;
        std::memcpy(stringCursor, device.product.c_str(), productLength);
        stringCursor += productLength;

        const size_t serialLength = device.serialNumber.size() + 1;
        result[i].serial_number = stringCursor;
        std::memcpy(stringCursor, device.serialNumber.c_str(), serialLength);
        stringCursor += serialLength;
    }

    *infos = result;
    return static_cast<int>(devices.size());
}

RX888_API int rx888_free_device_info(struct rx888_device_info* infos)
{
    if (infos == nullptr)
    {
        return 0;
    }

    auto* header = reinterpret_cast<DeviceInfoBlockHeader*>(
        reinterpret_cast<unsigned char*>(infos) - sizeof(DeviceInfoBlockHeader));
    if (header->magic == DeviceInfoBlockHeader::kMagic)
    {
        auto* storage = reinterpret_cast<unsigned char*>(header);
        delete[] storage;
        return 0;
    }

    return -1;
}

RX888_API rx888_t* rx888_open(int index, const char* imagefile)
{
    if (index < 0 || imagefile == nullptr)
    {
        return nullptr;
    }

    auto handle = std::make_unique<rx888>();
    handle->imageFile = imagefile;
    NativeLog("rx888_open starting native backend");

    auto tryNativeBackends = [&]() -> bool
    {
        handle->backend = std::make_unique<CyUsbDriverBackend>();
        if (handle->backend->Open(index, handle->imageFile))
        {
            NativeLog("rx888_open selected_backend=cyusb");
            return true;
        }

        handle->backend = std::make_unique<LibUsbBackend>();
        if (handle->backend->Open(index, handle->imageFile))
        {
            NativeLog("rx888_open selected_backend=libusb");
            return true;
        }

        return false;
    };

    const bool opened = tryNativeBackends();
    if (!opened)
    {
        NativeLog("rx888_open failed to open native backend");
        return nullptr;
    }

    handle->status = RX888_STATUS_READY;
    return handle.release();
}

RX888_API void rx888_close(rx888_t* handle)
{
    if (handle == nullptr)
    {
        return;
    }

    {
        std::lock_guard<std::mutex> lock(handle->mutex);
        if (handle->backend)
        {
            handle->backend->Close();
        }
        handle->status = RX888_STATUS_OFF;
    }

    delete handle;
}

RX888_API const char* rx888_get_backend_name(rx888_t* handle)
{
    if (handle == nullptr)
    {
        return "unknown";
    }

    std::lock_guard<std::mutex> lock(handle->mutex);
    if (!handle->backend)
    {
        return "unknown";
    }

    return handle->backend->GetBackendName();
}

RX888_API int rx888_set_sample_rate(rx888_t* handle, double sample_rate)
{
    if (handle == nullptr || !IsSupportedSampleRate(sample_rate))
    {
        return -1;
    }

    std::lock_guard<std::mutex> lock(handle->mutex);
    handle->sampleRate = sample_rate;
    return handle->backend->SetSampleRate(sample_rate);
}

RX888_API int rx888_set_adc_frequency(rx888_t* handle, double adc_frequency)
{
    if (handle == nullptr || !IsWholeNumber(adc_frequency) || adc_frequency <= 0.0)
    {
        return -1;
    }

    std::lock_guard<std::mutex> lock(handle->mutex);
    handle->adcFrequency = adc_frequency;
    return handle->backend->SetAdcFrequency(adc_frequency);
}

RX888_API int rx888_set_rf_mode(rx888_t* handle, RFMode rf_mode)
{
    if (handle == nullptr || (rf_mode != HF_MODE && rf_mode != VHF_MODE))
    {
        return -1;
    }

    std::lock_guard<std::mutex> lock(handle->mutex);
    handle->rfMode = rf_mode;
    return handle->backend->SetRfMode(rf_mode);
}

RX888_API int rx888_set_tuner_frequency(rx888_t* handle, double frequency)
{
    if (handle == nullptr || !IsWholeNumber(frequency) || frequency <= 0.0)
    {
        return -1;
    }

    std::lock_guard<std::mutex> lock(handle->mutex);
    handle->tunerFrequency = frequency;
    return handle->backend->SetTunerFrequency(frequency);
}

RX888_API int rx888_set_tuner_rf_attenuation(rx888_t* handle, double attenuation)
{
    if (handle == nullptr || !std::isfinite(attenuation))
    {
        return -1;
    }

    std::lock_guard<std::mutex> lock(handle->mutex);
    handle->tunerRfAttenuation = attenuation;
    return handle->backend->SetTunerRfAttenuation(attenuation);
}

RX888_API int rx888_set_tuner_if_attenuation(rx888_t* handle, double attenuation)
{
    if (handle == nullptr || !std::isfinite(attenuation))
    {
        return -1;
    }

    std::lock_guard<std::mutex> lock(handle->mutex);
    handle->tunerIfAttenuation = attenuation;
    return handle->backend->SetTunerIfAttenuation(attenuation);
}

RX888_API int rx888_set_adc_dither(rx888_t* handle, int dither)
{
    if (handle == nullptr)
    {
        return -1;
    }

    std::lock_guard<std::mutex> lock(handle->mutex);
    handle->adcDither = dither != 0 ? 1 : 0;
    return handle->backend->SetAdcDither(handle->adcDither);
}

RX888_API int rx888_set_adc_random(rx888_t* handle, int random)
{
    if (handle == nullptr)
    {
        return -1;
    }

    std::lock_guard<std::mutex> lock(handle->mutex);
    handle->adcRandom = random != 0 ? 1 : 0;
    return handle->backend->SetAdcRandom(handle->adcRandom);
}

RX888_API int rx888_set_vhf_bias(rx888_t* handle, int bias)
{
    if (handle == nullptr)
    {
        return -1;
    }

    std::lock_guard<std::mutex> lock(handle->mutex);
    handle->vhfBias = bias != 0 ? 1 : 0;
    return handle->backend->SetVhfBias(handle->vhfBias);
}

RX888_API int rx888_set_hf_bias(rx888_t* handle, int bias)
{
    if (handle == nullptr)
    {
        return -1;
    }

    std::lock_guard<std::mutex> lock(handle->mutex);
    handle->hfBias = bias != 0 ? 1 : 0;
    return handle->backend->SetHfBias(handle->hfBias);
}

RX888_API int rx888_set_hf_attenuation(rx888_t* handle, double attenuation)
{
    if (handle == nullptr || !std::isfinite(attenuation))
    {
        return -1;
    }

    std::lock_guard<std::mutex> lock(handle->mutex);
    handle->hfAttenuation = attenuation;
    return handle->backend->SetHfAttenuation(handle->hfAttenuation);
}

RX888_API int rx888_set_async_params(
    rx888_t* handle,
    uint32_t frame_size,
    uint32_t num_frames,
    rx888_read_async_cb_t callback,
    void* callback_context)
{
    if (handle == nullptr)
    {
        return -1;
    }

    std::lock_guard<std::mutex> lock(handle->mutex);
    handle->frameSize = frame_size;
    handle->numFrames = num_frames;
    handle->callback = callback;
    handle->callbackContext = callback_context;
    return handle->backend->ConfigureAsync(frame_size, num_frames, callback, callback_context);
}

RX888_API int rx888_start_streaming(rx888_t* handle)
{
    if (handle == nullptr)
    {
        return -1;
    }

    std::lock_guard<std::mutex> lock(handle->mutex);
    const int rc = handle->backend->StartStreaming();
    if (rc == 0)
    {
        handle->status = RX888_STATUS_STREAMING;
    }
    return rc;
}

RX888_API int rx888_handle_events(rx888_t* handle)
{
    if (handle == nullptr)
    {
        return -1;
    }

    std::lock_guard<std::mutex> lock(handle->mutex);
    return handle->backend->HandleEvents();
}

RX888_API int rx888_stop_streaming(rx888_t* handle)
{
    if (handle == nullptr)
    {
        return -1;
    }

    std::lock_guard<std::mutex> lock(handle->mutex);
    const int rc = handle->backend->StopStreaming();
    handle->status = RX888_STATUS_READY;
    return rc;
}
}
