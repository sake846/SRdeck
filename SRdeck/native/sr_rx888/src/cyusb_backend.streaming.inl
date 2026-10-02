int CyUsbDriverBackend::SetRfModeImpl(RFMode rfMode)
{
    rfMode_ = rfMode;
    if (rfMode == VHF_MODE)
    {
        gpioState_ |= GpioPin::VhfEn;
        gpioState_ |= GpioPin::PgaEn;
        if (SendU32(Fx3Command::GpioFx3, gpioState_) != 0)
        {
            return -1;
        }
        return SendU32(Fx3Command::TunerInit, kR828dReferenceFrequency);
    }

    gpioState_ &= ~GpioPin::VhfEn;
    gpioState_ &= ~GpioPin::PgaEn;
    if (SendU32(Fx3Command::GpioFx3, gpioState_) != 0)
    {
        return -1;
    }
    SendNoData(Fx3Command::TunerStandby);
    return 0;
}

uint16_t CyUsbDriverBackend::EncodeHfIfGain(int gainIndex) const
{
    if (gainIndex > kGainSweetPoint)
    {
        return static_cast<uint16_t>(kHighModeFlag | (gainIndex - kGainSweetPoint));
    }

    return static_cast<uint16_t>(gainIndex);
}

size_t CyUsbDriverBackend::GetTransferBytes() const
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

void CyUsbDriverBackend::ResetStreamingState()
{
    std::lock_guard<std::mutex> ddcLock(ddcMutex_);
    ddc_.Reset();
    ddcConfigured_ = false;
    ResetCallbackAggregationState();
}

void CyUsbDriverBackend::ResetCallbackAggregationState()
{
    pendingCallbackFloatBuffer_.clear();
    pendingCallbackFloatReadOffset_ = 0;
}

void CyUsbDriverBackend::CompactPendingCallbackBuffer()
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

bool CyUsbDriverBackend::InitializeUsbTransfers()
{
    CleanupUsbTransfers();
    usbTransfers_.assign(static_cast<size_t>(GetUsbReadConcurrentCount()), {});
    usbReadIndex_ = 0;
    for (auto& transfer : usbTransfers_)
    {
        if (!CyBeginBulkInTransfer(
                handle_,
                kBulkInEndpoint,
                transfer,
                static_cast<uint32_t>(GetTransferBytes())))
        {
            CleanupUsbTransfers();
            return false;
        }
    }
    return true;
}

void CyUsbDriverBackend::CleanupUsbTransfers()
{
    for (auto& transfer : usbTransfers_)
    {
        CyCleanupBulkInTransfer(handle_, transfer);
    }
    usbTransfers_.clear();
    usbReadIndex_ = 0;
}

void CyUsbDriverBackend::StartCallbackWorker()
{
    StopCallbackWorker();
    {
        std::lock_guard<std::mutex> lock(callbackMutex_);
        callbackStopping_ = false;
        readyCallbackJobs_.clear();
    }
    callbackThread_ = std::thread([this]() { CallbackLoop(); });
}

void CyUsbDriverBackend::StartDdcWorker()
{
    StopDdcWorker();
    const size_t transferSamples = std::max<size_t>(GetTransferBytes() / sizeof(int16_t), 1u);
    const size_t callbackFloatBlockSize = ddc_.GetPreferredCallbackComplexSamples() * 2u;
    {
        std::lock_guard<std::mutex> lock(ddcQueueMutex_);
        ddcStopping_ = false;
        freeRawDdcBuffers_.clear();
        readyRawDdcBuffers_.clear();
        pendingCallbackFloatBuffer_.clear();
        pendingCallbackFloatReadOffset_ = 0;
        pendingCallbackFloatBuffer_.reserve(callbackFloatBlockSize * 2u);
        for (size_t i = 0; i < rawDdcBuffers_.size(); ++i)
        {
            rawDdcBuffers_[i].assign(transferSamples, 0);
            rawDdcSampleCounts_[i] = 0;
            freeRawDdcBuffers_.push_back(i);
        }
    }
    ddcThread_ = std::thread([this]() { DdcLoop(); });
}

void CyUsbDriverBackend::StopCallbackWorker()
{
    {
        std::lock_guard<std::mutex> lock(callbackMutex_);
        callbackStopping_ = true;
    }
    callbackCondition_.notify_all();
    if (callbackThread_.joinable())
    {
        callbackThread_.join();
    }
}

void CyUsbDriverBackend::StopDdcWorker()
{
    {
        std::lock_guard<std::mutex> lock(ddcQueueMutex_);
        ddcStopping_ = true;
    }
    ddcQueueCondition_.notify_all();
    if (ddcThread_.joinable())
    {
        ddcThread_.join();
    }
}

void CyUsbDriverBackend::StartUsbWorker()
{
    StopUsbWorker();
    usbStopping_ = false;
    usbThread_ = std::thread([this]() { UsbLoop(); });
}

void CyUsbDriverBackend::StopUsbWorker()
{
    usbStopping_ = true;
    if (usbThread_.joinable())
    {
        usbThread_.join();
    }
}

void CyUsbDriverBackend::UsbLoop()
{
    ScopedHighPriorityThread highPriorityThread;
    const bool profileEnabled = IsNativeProfileEnabled();
    while (!usbStopping_ && handle_ != INVALID_HANDLE_VALUE)
    {
        const auto usbStart = profileEnabled ? std::chrono::steady_clock::now() : std::chrono::steady_clock::time_point{};
        auto& usbTransfer = usbTransfers_[usbReadIndex_];
        uint32_t transferred = 0;
        if (!CyFinishBulkInTransfer(handle_, usbTransfer, kBulkTransferTimeoutMs, &transferred) ||
            transferred <= 1)
        {
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
            continue;
        }
        if (profileEnabled)
        {
            profileUsbTime_ += std::chrono::steady_clock::now() - usbStart;
        }

        const size_t sampleCount = static_cast<size_t>(transferred / sizeof(int16_t));
        if (sampleCount > 0)
        {
            const auto* samples = reinterpret_cast<const int16_t*>(usbTransfer.buffer.data());
            size_t rawBufferIndex = 0;
            if (AcquireRawDdcBuffer(rawBufferIndex))
            {
                auto& rawBuffer = rawDdcBuffers_[rawBufferIndex];
                std::memcpy(rawBuffer.data(), samples, sampleCount * sizeof(int16_t));
                rawDdcSampleCounts_[rawBufferIndex] = sampleCount;
                SubmitRawDdcBuffer(rawBufferIndex);
            }
        }

        if (!CyBeginBulkInTransfer(
                handle_,
                kBulkInEndpoint,
                usbTransfer,
                static_cast<uint32_t>(GetTransferBytes())))
        {
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
            continue;
        }
        usbReadIndex_ = (usbReadIndex_ + 1) % usbTransfers_.size();
        if (profileEnabled)
        {
            profileInputBytes_ += transferred;
            ++profileTransfers_;
            ReportProfile();
        }
    }
}

bool CyUsbDriverBackend::AcquireRawDdcBuffer(size_t& bufferIndex)
{
    std::unique_lock<std::mutex> lock(ddcQueueMutex_);
    ddcQueueCondition_.wait(lock, [this]()
    {
        return ddcStopping_ || !freeRawDdcBuffers_.empty();
    });
    if (ddcStopping_)
    {
        return false;
    }

    bufferIndex = freeRawDdcBuffers_.front();
    freeRawDdcBuffers_.pop_front();
    return true;
}

void CyUsbDriverBackend::SubmitCallbackJob(std::shared_ptr<std::vector<float>> buffer, size_t floatOffset, size_t floatCount)
{
    {
        std::lock_guard<std::mutex> lock(callbackMutex_);
        readyCallbackJobs_.push_back(CallbackJob{std::move(buffer), floatOffset, floatCount});
    }
    callbackCondition_.notify_one();
}

void CyUsbDriverBackend::SubmitRawDdcBuffer(size_t bufferIndex)
{
    {
        std::lock_guard<std::mutex> lock(ddcQueueMutex_);
        readyRawDdcBuffers_.push_back(bufferIndex);
    }
    ddcQueueCondition_.notify_one();
}

void CyUsbDriverBackend::ReleaseRawDdcBuffer(size_t bufferIndex)
{
    {
        std::lock_guard<std::mutex> lock(ddcQueueMutex_);
        freeRawDdcBuffers_.push_back(bufferIndex);
    }
    ddcQueueCondition_.notify_one();
}
