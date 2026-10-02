int LibUsbBackend::StartStreaming()
{
    if (!open_)
    {
        return -1;
    }

    if (SendNoData(Fx3Command::StartFx3) != 0)
    {
        NativeLog("LibUsbBackend::StartStreaming failed: STARTFX3 control request");
        return -1;
    }

    streaming_ = true;
    ResetCallbackAggregationState();
    return 0;
}

int LibUsbBackend::HandleEvents()
{
    if (!streaming_ || api_ == nullptr || handle_ == nullptr || callback_ == nullptr)
    {
        return -1;
    }

    const size_t transferBytes = GetTransferBytes();
    rawTransferBuffer_.resize(transferBytes);

    if (!ddcConfigured_)
    {
        ddc_.Configure(adcFrequencyHz_, sampleRateHz_, tunerFrequencyHz_, rfMode_);
        ddcConfigured_ = true;
    }

    const int burstCount = GetHandleEventsBurstCount();
    bool processedAny = false;
    const size_t callbackFloatBlockSize = ddc_.GetPreferredCallbackComplexSamples() * 2u;
    if (pendingCallbackFloatBuffer_.capacity() < (callbackFloatBlockSize * 2u))
    {
        pendingCallbackFloatBuffer_.reserve(callbackFloatBlockSize * 2u);
    }
    for (int burst = 0; burst < burstCount; ++burst)
    {
        int transferred = 0;
        const int rc = api_->bulkTransfer_(
            handle_,
            kBulkInEndpoint,
            rawTransferBuffer_.data(),
            static_cast<int>(rawTransferBuffer_.size()),
            &transferred,
            kControlTimeoutMs);

        if (rc < 0 || transferred <= 1)
        {
            return processedAny ? 1 : -1;
        }

        const size_t sampleCount = static_cast<size_t>(transferred / static_cast<int>(sizeof(int16_t)));
        if (sampleCount == 0)
        {
            if (!processedAny)
            {
                return 0;
            }
            break;
        }

        const auto* samples = reinterpret_cast<const int16_t*>(rawTransferBuffer_.data());
        ddc_.ProcessAppend(samples, sampleCount, pendingCallbackFloatBuffer_);
        processedAny = true;
    }

    while ((pendingCallbackFloatBuffer_.size() - pendingCallbackFloatReadOffset_) >= callbackFloatBlockSize)
    {
        callback_(
            static_cast<uint32_t>(callbackFloatBlockSize * sizeof(float)),
            pendingCallbackFloatBuffer_.data() + pendingCallbackFloatReadOffset_,
            callbackContext_);
        pendingCallbackFloatReadOffset_ += callbackFloatBlockSize;
    }
    CompactPendingCallbackBuffer();
    return processedAny ? 1 : 0;
}

int LibUsbBackend::StopStreaming()
{
    if (!open_)
    {
        return -1;
    }

    SendNoData(Fx3Command::StopFx3);
    streaming_ = false;
    return 0;
}

int LibUsbBackend::SendNoData(Fx3Command command)
{
    // Older FX3 firmware waits for an OUT data phase for these commands.
    unsigned char ignored = 0;
    return ControlTransfer(kVendorOut, command, 0, 0, &ignored, sizeof(ignored));
}

size_t LibUsbBackend::GetTransferBytes() const
{
    if (frameSize_ != 0)
    {
        return std::max<size_t>(frameSize_, sizeof(int16_t));
    }

    if (numFrames_ != 0)
    {
        return std::max<size_t>(static_cast<size_t>(numFrames_) * 16384u, sizeof(int16_t));
    }

    return kDefaultTransferBytes;
}

void LibUsbBackend::ResetStreamingState()
{
    ddc_.Reset();
    ddcConfigured_ = false;
    ResetCallbackAggregationState();
}

void LibUsbBackend::ResetCallbackAggregationState()
{
    pendingCallbackFloatBuffer_.clear();
    pendingCallbackFloatReadOffset_ = 0;
}

void LibUsbBackend::CompactPendingCallbackBuffer()
{
    if (pendingCallbackFloatReadOffset_ == 0)
    {
        return;
    }

    if (pendingCallbackFloatReadOffset_ >= pendingCallbackFloatBuffer_.size())
    {
        pendingCallbackFloatBuffer_.clear();
        pendingCallbackFloatReadOffset_ = 0;
        return;
    }

    if (pendingCallbackFloatReadOffset_ >= (pendingCallbackFloatBuffer_.size() / 2u))
    {
        const size_t remainingCount = pendingCallbackFloatBuffer_.size() - pendingCallbackFloatReadOffset_;
        std::memmove(
            pendingCallbackFloatBuffer_.data(),
            pendingCallbackFloatBuffer_.data() + pendingCallbackFloatReadOffset_,
            remainingCount * sizeof(float));
        pendingCallbackFloatBuffer_.resize(remainingCount);
        pendingCallbackFloatReadOffset_ = 0;
    }
}

int LibUsbBackend::SendGpioState()
{
    uint32_t state = gpioState_;
    return ControlTransfer(
        kVendorOut,
        Fx3Command::GpioFx3,
        0,
        0,
        reinterpret_cast<unsigned char*>(&state),
        sizeof(state));
}

int LibUsbBackend::SendU32(Fx3Command command, uint32_t value)
{
    return ControlTransfer(kVendorOut, command, 0, 0, reinterpret_cast<unsigned char*>(&value), sizeof(value));
}

int LibUsbBackend::SendU64(Fx3Command command, uint64_t value)
{
    return ControlTransfer(kVendorOut, command, 0, 0, reinterpret_cast<unsigned char*>(&value), sizeof(value));
}

int LibUsbBackend::SetArgument(ArgumentId argumentId, uint16_t value)
{
    // FX3 firmware waits for an OUT data phase even though the value is in wValue.
    unsigned char ignored = 0;
    return ControlTransfer(
        kVendorOut,
        Fx3Command::SetArgFx3,
        value,
        static_cast<uint16_t>(argumentId),
        &ignored,
        sizeof(ignored));
}

uint16_t LibUsbBackend::EncodeHfIfGain(int gainIndex) const
{
    gainIndex = ClampInt(gainIndex, 0, static_cast<int>(hfIfSteps_.size()) - 1);
    if (gainIndex > kGainSweetPoint)
    {
        return static_cast<uint16_t>(kHighModeFlag | static_cast<uint8_t>(gainIndex - kGainSweetPoint + 3));
    }

    return static_cast<uint16_t>(gainIndex + 1);
}

int LibUsbBackend::UpdateGpioBit(GpioPin pin, bool enabled)
{
    if (enabled)
    {
        gpioState_ |= static_cast<uint32_t>(pin);
    }
    else
    {
        gpioState_ &= ~static_cast<uint32_t>(pin);
    }

    return SendGpioState();
}

int LibUsbBackend::SetRfModeImpl(RFMode mode)
{
    if (mode != HF_MODE && mode != VHF_MODE)
    {
        return -1;
    }

    if (mode == rfMode_)
    {
        return 0;
    }

    if (mode == VHF_MODE)
    {
        if (SetArgument(ArgumentId::Dat31Att, 63) != 0)
        {
            return -1;
        }

        gpioState_ |= static_cast<uint32_t>(GpioPin::VhfEn);
        if (SendGpioState() != 0)
        {
            return -1;
        }

        if (SetArgument(ArgumentId::Ad8340Vga, static_cast<uint16_t>(kHighModeFlag | 3)) != 0)
        {
            return -1;
        }

        if (SendU32(Fx3Command::TunerInit, kR828dReferenceFrequency) != 0)
        {
            return -1;
        }
    }
    else
    {
        if (SendNoData(Fx3Command::TunerStandby) != 0)
        {
            return -1;
        }

        gpioState_ &= ~static_cast<uint32_t>(GpioPin::VhfEn);
        if (SendGpioState() != 0)
        {
            return -1;
        }
    }

    rfMode_ = mode;
    return 0;
}

bool LibUsbBackend::ReadHardwareInfo(uint32_t& hardwareInfo)
{
    if (ControlTransfer(kVendorIn, Fx3Command::TestFx3, 0, 0, reinterpret_cast<unsigned char*>(&hardwareInfo), sizeof(hardwareInfo)) < 0)
    {
        return false;
    }
    return true;
}

int LibUsbBackend::ControlTransfer(
    uint8_t requestType,
    Fx3Command command,
    uint16_t value,
    uint16_t index,
    unsigned char* data,
    uint16_t size)
{
    if (api_ == nullptr || handle_ == nullptr)
    {
        return -1;
    }

    const int rc = api_->controlTransfer_(
        handle_,
        requestType,
        static_cast<uint8_t>(command),
        value,
        index,
        data,
        size,
        kControlTimeoutMs);

    if (rc < 0)
    {
        return -1;
    }

    return 0;
}

bool LibUsbBackend::DownloadFx3Firmware(libusb_device_handle* handle, const std::string& imageFile)
{
    return DownloadFx3FirmwareImage(
        imageFile,
        [&](uint32_t address, const unsigned char* data, uint16_t length) -> bool
        {
            const int rc = api_->controlTransfer_(
                handle,
                kVendorOut,
                kFx3BootloaderVendorRequest,
                static_cast<uint16_t>(address & 0xffffu),
                static_cast<uint16_t>((address >> 16) & 0xffffu),
                const_cast<unsigned char*>(data),
                length,
                kControlTimeoutMs);
            return rc >= 0;
        },
        "libusb");
}
