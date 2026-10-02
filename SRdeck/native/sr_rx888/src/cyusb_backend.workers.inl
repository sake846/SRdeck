void CyUsbDriverBackend::DdcLoop()
{
    ScopedHighPriorityThread highPriorityThread;
    const size_t callbackFloatBlockSize = ddc_.GetPreferredCallbackComplexSamples() * 2u;
    for (;;)
    {
        size_t bufferIndex = 0;
        {
            std::unique_lock<std::mutex> lock(ddcQueueMutex_);
            ddcQueueCondition_.wait(lock, [this]()
            {
                return ddcStopping_ || !readyRawDdcBuffers_.empty();
            });
            if (readyRawDdcBuffers_.empty())
            {
                if (ddcStopping_)
                {
                    return;
                }
                continue;
            }

            bufferIndex = readyRawDdcBuffers_.front();
            readyRawDdcBuffers_.pop_front();
        }

        std::lock_guard<std::mutex> ddcLock(ddcMutex_);

        auto producedBuffer = std::make_shared<std::vector<float>>();
        if (!pendingCallbackFloatBuffer_.empty())
        {
            producedBuffer->reserve(pendingCallbackFloatBuffer_.size() + callbackFloatBlockSize * 2u);
            producedBuffer->insert(
                producedBuffer->end(),
                pendingCallbackFloatBuffer_.begin(),
                pendingCallbackFloatBuffer_.end());
            pendingCallbackFloatBuffer_.clear();
            pendingCallbackFloatReadOffset_ = 0;
        }
        else
        {
            producedBuffer->reserve(callbackFloatBlockSize * 2u);
        }

        const bool profileEnabled = IsNativeProfileEnabled();
        const auto ddcStart = profileEnabled ? std::chrono::steady_clock::now() : std::chrono::steady_clock::time_point{};
        if (!ddcConfigured_)
        {
            ddc_.Configure(adcFrequencyHz_, sampleRateHz_, tunerFrequencyHz_, rfMode_);
            ddcConfigured_ = true;
        }
        ddc_.ProcessAppend(
            rawDdcBuffers_[bufferIndex].data(),
            rawDdcSampleCounts_[bufferIndex],
            *producedBuffer);
        if (profileEnabled)
        {
            const auto ddcElapsed = std::chrono::steady_clock::now() - ddcStart;
            profileDdcNanoseconds_.fetch_add(
                static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(ddcElapsed).count()),
                std::memory_order_relaxed);
        }

        size_t floatOffset = 0;
        while ((floatOffset + callbackFloatBlockSize) <= producedBuffer->size())
        {
            SubmitCallbackJob(producedBuffer, floatOffset, callbackFloatBlockSize);
            floatOffset += callbackFloatBlockSize;
        }

        if (floatOffset < producedBuffer->size())
        {
            pendingCallbackFloatBuffer_.assign(
                producedBuffer->begin() + static_cast<std::ptrdiff_t>(floatOffset),
                producedBuffer->end());
        }
        else
        {
            pendingCallbackFloatBuffer_.clear();
        }
        pendingCallbackFloatReadOffset_ = 0;
        ReleaseRawDdcBuffer(bufferIndex);
    }
}

void CyUsbDriverBackend::CallbackLoop()
{
    ScopedHighPriorityThread highPriorityThread;
    for (;;)
    {
        CallbackJob job;
        {
            std::unique_lock<std::mutex> lock(callbackMutex_);
            callbackCondition_.wait(lock, [this]()
            {
                return callbackStopping_ || !readyCallbackJobs_.empty();
            });
            if (readyCallbackJobs_.empty())
            {
                if (callbackStopping_)
                {
                    return;
                }
                continue;
            }

            job = std::move(readyCallbackJobs_.front());
            readyCallbackJobs_.pop_front();
        }

        const bool profileEnabled = IsNativeProfileEnabled();
        const auto callbackStart = profileEnabled ? std::chrono::steady_clock::now() : std::chrono::steady_clock::time_point{};
        callback_(
            static_cast<uint32_t>(job.floatCount * sizeof(float)),
            job.buffer->data() + job.floatOffset,
            callbackContext_);
        if (profileEnabled)
        {
            const auto callbackElapsed = std::chrono::steady_clock::now() - callbackStart;
            profileCallbackNanoseconds_.fetch_add(
                static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(callbackElapsed).count()),
                std::memory_order_relaxed);
            profileCallbackCount_.fetch_add(1, std::memory_order_relaxed);
        }
    }
}

void CyUsbDriverBackend::ResetProfile()
{
    if (!IsNativeProfileEnabled())
    {
        return;
    }

    profileStart_ = std::chrono::steady_clock::now();
    profileUsbTime_ = {};
    profileDdcNanoseconds_.exchange(0, std::memory_order_relaxed);
    profileCallbackNanoseconds_.exchange(0, std::memory_order_relaxed);
    profileCallbackCount_.exchange(0, std::memory_order_relaxed);
    profileCallbackWaitNanoseconds_.exchange(0, std::memory_order_relaxed);
    profileCallbackWaitCount_.exchange(0, std::memory_order_relaxed);
    profileInputBytes_ = 0;
    profileTransfers_ = 0;
    std::lock_guard<std::mutex> ddcLock(ddcMutex_);
    ddc_.ResetProfileCounters();
}

void CyUsbDriverBackend::ReportProfile()
{
    if (!IsNativeProfileEnabled())
    {
        return;
    }

    const auto now = std::chrono::steady_clock::now();
    const auto elapsed = now - profileStart_;
    if (elapsed < std::chrono::seconds(2))
    {
        return;
    }

    const double wallMs = std::chrono::duration<double, std::milli>(elapsed).count();
    const double usbMs = std::chrono::duration<double, std::milli>(profileUsbTime_).count();
    const double ddcMs =
        static_cast<double>(profileDdcNanoseconds_.exchange(0, std::memory_order_relaxed)) / 1'000'000.0;
    HostDdcPipeline::ProfileSnapshot ddcProfile;
    {
        std::lock_guard<std::mutex> ddcLock(ddcMutex_);
        ddcProfile = ddc_.ConsumeProfileSnapshot();
    }
    const double callbackMs =
        static_cast<double>(profileCallbackNanoseconds_.exchange(0, std::memory_order_relaxed)) / 1'000'000.0;
    const uint64_t callbackCount = profileCallbackCount_.exchange(0, std::memory_order_relaxed);
    const double callbackWaitMs =
        static_cast<double>(profileCallbackWaitNanoseconds_.exchange(0, std::memory_order_relaxed)) / 1'000'000.0;
    const uint64_t callbackWaitCount = profileCallbackWaitCount_.exchange(0, std::memory_order_relaxed);
    const double inputMsps = wallMs > 0.0
        ? (static_cast<double>(profileInputBytes_) / sizeof(int16_t)) / (wallMs * 1000.0)
        : 0.0;
    const double callbacksPerSec = wallMs > 0.0
        ? static_cast<double>(callbackCount) * 1000.0 / wallMs
        : 0.0;
    const double samplesPerCallback = callbackCount > 0
        ? (static_cast<double>(profileInputBytes_) / sizeof(int16_t)) / static_cast<double>(callbackCount)
        : 0.0;
    const double ddcFftMs = static_cast<double>(ddcProfile.fftNanoseconds) / 1'000'000.0;
    const double fftInputPackMs = static_cast<double>(ddcProfile.fftDetail.inputPackNanoseconds) / 1'000'000.0;
    const double fftBlockPrepMs = static_cast<double>(ddcProfile.fftDetail.blockPrepNanoseconds) / 1'000'000.0;
    const double fftForwardMs = static_cast<double>(ddcProfile.fftDetail.forwardFftNanoseconds) / 1'000'000.0;
    const double fftBinMathMs = static_cast<double>(ddcProfile.fftDetail.binMathNanoseconds) / 1'000'000.0;
    const double fftInverseMs = static_cast<double>(ddcProfile.fftDetail.inverseFftNanoseconds) / 1'000'000.0;
    const double fftOutputPackMs = static_cast<double>(ddcProfile.fftDetail.outputPackNanoseconds) / 1'000'000.0;
    const double fftOverlapCopyMs = static_cast<double>(ddcProfile.fftDetail.overlapCopyNanoseconds) / 1'000'000.0;

    std::ostringstream message;
    message << std::fixed << std::setprecision(2)
            << "native_profile wall_ms=" << wallMs
            << " burst=" << GetHandleEventsBurstCount()
            << " usb_reads=" << GetUsbReadConcurrentCount()
            << " input_msps=" << inputMsps
            << " transfers=" << profileTransfers_
            << " callbacks=" << callbackCount
            << " callbacks_per_sec=" << callbacksPerSec
            << " samples_per_callback=" << samplesPerCallback
            << " usb_ms=" << usbMs
            << " usb_pct=" << (wallMs > 0.0 ? usbMs * 100.0 / wallMs : 0.0)
            << " ddc_mode=" << ddcProfile.modeName
            << " ddc_ms=" << ddcMs
            << " ddc_pct=" << (wallMs > 0.0 ? ddcMs * 100.0 / wallMs : 0.0)
            << " ddc_fft_ms=" << ddcFftMs
            << " ddc_fft_pct=" << (wallMs > 0.0 ? ddcFftMs * 100.0 / wallMs : 0.0)
            << " ddc_fft_calls=" << ddcProfile.fftCalls
            << " fft_blocks=" << ddcProfile.fftDetail.blockCalls
            << " fft_input_pack_ms=" << fftInputPackMs
            << " fft_input_pack_pct=" << (wallMs > 0.0 ? fftInputPackMs * 100.0 / wallMs : 0.0)
            << " fft_block_prep_ms=" << fftBlockPrepMs
            << " fft_block_prep_pct=" << (wallMs > 0.0 ? fftBlockPrepMs * 100.0 / wallMs : 0.0)
            << " fft_forward_ms=" << fftForwardMs
            << " fft_forward_pct=" << (wallMs > 0.0 ? fftForwardMs * 100.0 / wallMs : 0.0)
            << " fft_bin_math_ms=" << fftBinMathMs
            << " fft_bin_math_pct=" << (wallMs > 0.0 ? fftBinMathMs * 100.0 / wallMs : 0.0)
            << " fft_inverse_ms=" << fftInverseMs
            << " fft_inverse_pct=" << (wallMs > 0.0 ? fftInverseMs * 100.0 / wallMs : 0.0)
            << " fft_output_pack_ms=" << fftOutputPackMs
            << " fft_output_pack_pct=" << (wallMs > 0.0 ? fftOutputPackMs * 100.0 / wallMs : 0.0)
            << " fft_overlap_copy_ms=" << fftOverlapCopyMs
            << " fft_overlap_copy_pct=" << (wallMs > 0.0 ? fftOverlapCopyMs * 100.0 / wallMs : 0.0)
            << " callback_ms=" << callbackMs
            << " callback_pct=" << (wallMs > 0.0 ? callbackMs * 100.0 / wallMs : 0.0)
            << " callback_wait_ms=" << callbackWaitMs
            << " callback_wait_pct=" << (wallMs > 0.0 ? callbackWaitMs * 100.0 / wallMs : 0.0)
            << " callback_wait_count=" << callbackWaitCount;
    NativeLog(message.str());
    ResetProfile();
}
