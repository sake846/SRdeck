void FftDdcPipeline::ProcessInternal(const int16_t* samples, size_t sampleCount, std::vector<float>& output)
{
    if (!Available() || samples == nullptr || sampleCount == 0 || reducedFftSize_ == 0)
    {
        return;
    }

    const size_t outputStart = output.size();
    const size_t inputSamplesPerComplex = static_cast<size_t>(std::max(1, decimation_ * 2));
    const size_t outputInputSamples = sampleCount + outputSampleRemainder_;
    const size_t expectedComplexSamples = outputInputSamples / inputSamplesPerComplex;
    outputSampleRemainder_ = outputInputSamples % inputSamplesPerComplex;
    const size_t expectedFloatCount = expectedComplexSamples * 2u;

    const size_t totalWindowSamples = static_cast<size_t>(kInputHistory) + sampleCount;
    const size_t stride = static_cast<size_t>(kBlockStrideSamples);
    const size_t blockCount =
        totalWindowSamples >= static_cast<size_t>(kFullTimeFftSize)
            ? 1u + ((totalWindowSamples - static_cast<size_t>(kFullTimeFftSize)) / stride)
            : 0u;
    const size_t halfReduced = static_cast<size_t>(reducedFftSize_ / 2);
    const size_t primingFloatCount = primed_ ? 0u : static_cast<size_t>(halfReduced * 2u);
    const size_t steadyFloatCount =
        blockCount > (primed_ ? 0u : 1u)
            ? (blockCount - (primed_ ? 0u : 1u)) * static_cast<size_t>((3 * reducedFftSize_) / 2)
            : 0u;
    output.reserve(output.size() + primingFloatCount + steadyFloatCount);

    if (batchedTimeRealSize_ < totalWindowSamples)
    {
        if (!batchedTimeReal_.Resize(totalWindowSamples))
        {
            return;
        }
        batchedTimeRealSize_ = totalWindowSamples;
    }

    const bool profileEnabled = IsNativeProfileEnabled();
    const auto blockPrepStart = profileEnabled ? std::chrono::steady_clock::now() : std::chrono::steady_clock::time_point{};
    std::memcpy(batchedTimeReal_.data(), overlapReal_.data(), sizeof(float) * static_cast<size_t>(kInputHistory));
    if (profileEnabled)
    {
        profileBlockPrepNanoseconds_ += static_cast<uint64_t>(
            std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now() - blockPrepStart).count());
    }

    const auto inputPackStart = profileEnabled ? std::chrono::steady_clock::now() : std::chrono::steady_clock::time_point{};
    ConvertSamplesToFloat(
        samples,
        sampleCount,
        batchedTimeReal_.data() + kInputHistory);
    if (profileEnabled)
    {
        profileInputPackNanoseconds_ += static_cast<uint64_t>(
            std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now() - inputPackStart).count());
    }

    size_t windowOffset = 0;
    while ((windowOffset + static_cast<size_t>(kFullTimeFftSize)) <= totalWindowSamples)
    {
        RunBlockWindow(batchedTimeReal_.data() + windowOffset, output);
        windowOffset += stride;
    }

    const auto overlapCopyStart = profileEnabled ? std::chrono::steady_clock::now() : std::chrono::steady_clock::time_point{};
    std::memcpy(
        overlapReal_.data(),
        batchedTimeReal_.data() + sampleCount,
        sizeof(float) * static_cast<size_t>(kInputHistory));
    if (profileEnabled)
    {
        profileOverlapCopyNanoseconds_ += static_cast<uint64_t>(
            std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now() - overlapCopyStart).count());
    }

    const size_t producedFloatCount = output.size() - outputStart;
    if (producedFloatCount > expectedFloatCount)
    {
        output.resize(outputStart + expectedFloatCount);
    }
}

void FftDdcPipeline::RunBlockWindow(const float* timeWindow, std::vector<float>& output)
{
    const int halfReduced = reducedFftSize_ / 2;
    const int validStart = primed_ ? 0 : (reducedFftSize_ / 4);
    const int validCount = primed_ ? ((3 * reducedFftSize_) / 4) : halfReduced;
    const bool profileEnabled = IsNativeProfileEnabled();

    const auto forwardStart = profileEnabled ? std::chrono::steady_clock::now() : std::chrono::steady_clock::time_point{};
    forwardFft_->ExecuteR2c(timeWindow, fullFreqReal_.data());
    if (profileEnabled)
    {
        profileForwardFftNanoseconds_ += static_cast<uint64_t>(
            std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now() - forwardStart).count());
    }

    const auto binMathStart = profileEnabled ? std::chrono::steady_clock::now() : std::chrono::steady_clock::time_point{};
    BuildReducedSpectrum(fullFreqReal_.data(), reducedFreq_.data());
    if (profileEnabled)
    {
        profileBinMathNanoseconds_ += static_cast<uint64_t>(
            std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now() - binMathStart).count());
    }

    const auto inverseStart = profileEnabled ? std::chrono::steady_clock::now() : std::chrono::steady_clock::time_point{};
    inverseFft_->Execute(reducedFreq_.data(), reducedTime_.data());
    if (profileEnabled)
    {
        profileInverseFftNanoseconds_ += static_cast<uint64_t>(
            std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now() - inverseStart).count());
    }

    const auto outputPackStart = profileEnabled ? std::chrono::steady_clock::now() : std::chrono::steady_clock::time_point{};
    const size_t callbackOffset = output.size();
    const size_t callbackFloatCount = static_cast<size_t>(validCount) * 2u;
    output.resize(callbackOffset + callbackFloatCount);
    float* callbackWrite = output.data() + callbackOffset;
    const float* reducedRead = reinterpret_cast<const float*>(reducedTime_.data() + validStart);

    if (fineTuneRelativeFrequency_ == 0.0f && !mirrorOutput_)
    {
        std::memcpy(callbackWrite, reducedRead, callbackFloatCount * sizeof(float));
    }
    else if (fineTuneRelativeFrequency_ == 0.0f)
    {
        size_t i = 0;
        if (IsAvx2Supported())
        {
            const __m256 mirrorMask = _mm256_castsi256_ps(_mm256_setr_epi32(
                0, 0x80000000u, 0, 0x80000000u, 0, 0x80000000u, 0, 0x80000000u));
            for (; i + 8 <= callbackFloatCount; i += 8)
            {
                const __m256 iq = _mm256_loadu_ps(reducedRead + i);
                _mm256_storeu_ps(callbackWrite + i, _mm256_xor_ps(iq, mirrorMask));
            }
        }

        for (; i < callbackFloatCount; i += 2)
        {
            callbackWrite[i] = reducedRead[i];
            callbackWrite[i + 1] = -reducedRead[i + 1];
        }
    }
    else
    {
        size_t i = 0;
        if (IsAvx2Supported())
        {
            const __m256 mirrorMask = _mm256_castsi256_ps(_mm256_setr_epi32(
                0, 0x80000000u, 0, 0x80000000u, 0, 0x80000000u, 0, 0x80000000u));
            const __m256 phaseBaseI = _mm256_loadu_ps(fineTunePhaseVecI_.data());
            const __m256 phaseBaseQ = _mm256_loadu_ps(fineTunePhaseVecQ_.data());
            for (; i + 8 <= callbackFloatCount; i += 8)
            {
                const __m256 oscI = _mm256_set1_ps(fineTuneOscI_);
                const __m256 oscQ = _mm256_set1_ps(fineTuneOscQ_);
                __m256 phaseI;
                __m256 phaseQ;
                if (IsFmaSupported())
                {
                    phaseI = _mm256_fmsub_ps(oscI, phaseBaseI, _mm256_mul_ps(oscQ, phaseBaseQ));
                    phaseQ = _mm256_fmadd_ps(oscI, phaseBaseQ, _mm256_mul_ps(oscQ, phaseBaseI));
                }
                else
                {
                    phaseI = _mm256_sub_ps(
                        _mm256_mul_ps(oscI, phaseBaseI),
                        _mm256_mul_ps(oscQ, phaseBaseQ));
                    phaseQ = _mm256_add_ps(
                        _mm256_mul_ps(oscI, phaseBaseQ),
                        _mm256_mul_ps(oscQ, phaseBaseI));
                }
                const __m256 iq = _mm256_loadu_ps(reducedRead + i);
                const __m256 swapped = _mm256_permute_ps(iq, 0xB1);
                __m256 rotated;
                if (IsFmaSupported())
                {
                    const __m256 qMul = _mm256_mul_ps(swapped, phaseQ);
                    rotated = _mm256_fmaddsub_ps(iq, phaseI, qMul);
                }
                else
                {
                    rotated = _mm256_addsub_ps(
                        _mm256_mul_ps(iq, phaseI),
                        _mm256_mul_ps(swapped, phaseQ));
                }
                if (mirrorOutput_)
                {
                    rotated = _mm256_xor_ps(rotated, mirrorMask);
                }
                _mm256_storeu_ps(callbackWrite + i, rotated);

                const float nextOscI = (fineTuneOscI_ * fineTuneAdvance4I_) - (fineTuneOscQ_ * fineTuneAdvance4Q_);
                const float nextOscQ = (fineTuneOscI_ * fineTuneAdvance4Q_) + (fineTuneOscQ_ * fineTuneAdvance4I_);
                fineTuneOscI_ = nextOscI;
                fineTuneOscQ_ = nextOscQ;
                fineTuneRenormCounter_ += 4u;
                if ((fineTuneRenormCounter_ & 1023u) == 0u)
                {
                    const float magnitude = std::sqrt((fineTuneOscI_ * fineTuneOscI_) + (fineTuneOscQ_ * fineTuneOscQ_));
                    if (magnitude > 0.0f)
                    {
                        const float inverseMagnitude = 1.0f / magnitude;
                        fineTuneOscI_ *= inverseMagnitude;
                        fineTuneOscQ_ *= inverseMagnitude;
                    }
                }
            }
        }

        for (; i < callbackFloatCount; i += 2)
        {
            float iValue = reducedRead[i];
            float qValue = reducedRead[i + 1];
            if (fineTuneRelativeFrequency_ != 0.0f)
            {
                const float rotatedI = (iValue * fineTuneOscI_) - (qValue * fineTuneOscQ_);
                const float rotatedQ = (iValue * fineTuneOscQ_) + (qValue * fineTuneOscI_);
                iValue = rotatedI;
                qValue = rotatedQ;

                const float nextOscI = (fineTuneOscI_ * fineTuneStepI_) - (fineTuneOscQ_ * fineTuneStepQ_);
                const float nextOscQ = (fineTuneOscI_ * fineTuneStepQ_) + (fineTuneOscQ_ * fineTuneStepI_);
                fineTuneOscI_ = nextOscI;
                fineTuneOscQ_ = nextOscQ;
                ++fineTuneRenormCounter_;
                if ((fineTuneRenormCounter_ & 1023u) == 0u)
                {
                    const float magnitude = std::sqrt((fineTuneOscI_ * fineTuneOscI_) + (fineTuneOscQ_ * fineTuneOscQ_));
                    if (magnitude > 0.0f)
                    {
                        const float inverseMagnitude = 1.0f / magnitude;
                        fineTuneOscI_ *= inverseMagnitude;
                        fineTuneOscQ_ *= inverseMagnitude;
                    }
                }
            }

            callbackWrite[i] = iValue;
            callbackWrite[i + 1] = mirrorOutput_ ? -qValue : qValue;
        }
    }
    if (profileEnabled)
    {
        profileOutputPackNanoseconds_ += static_cast<uint64_t>(
            std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now() - outputPackStart).count());
        ++profileBlockCalls_;
    }
    primed_ = true;
}

void FftDdcPipeline::RebuildFineTunePhases()
{
    fineTuneAdvance4I_ = 1.0f;
    fineTuneAdvance4Q_ = 0.0f;
    std::fill(fineTunePhaseVecI_.begin(), fineTunePhaseVecI_.end(), 1.0f);
    std::fill(fineTunePhaseVecQ_.begin(), fineTunePhaseVecQ_.end(), 0.0f);

    float phaseI = 1.0f;
    float phaseQ = 0.0f;
    for (size_t sampleIndex = 0; sampleIndex < 4; ++sampleIndex)
    {
        const size_t laneIndex = sampleIndex * 2u;
        fineTunePhaseVecI_[laneIndex] = phaseI;
        fineTunePhaseVecI_[laneIndex + 1u] = phaseI;
        fineTunePhaseVecQ_[laneIndex] = phaseQ;
        fineTunePhaseVecQ_[laneIndex + 1u] = phaseQ;

        const float nextPhaseI = (phaseI * fineTuneStepI_) - (phaseQ * fineTuneStepQ_);
        const float nextPhaseQ = (phaseI * fineTuneStepQ_) + (phaseQ * fineTuneStepI_);
        phaseI = nextPhaseI;
        phaseQ = nextPhaseQ;
    }

    fineTuneAdvance4I_ = phaseI;
    fineTuneAdvance4Q_ = phaseQ;
}

void FftDdcPipeline::ConvertSamplesToFloat(const int16_t* samples, size_t sampleCount, float* output)
{
    constexpr float scale = 1.0f / 32768.0f;
    if (IsAvx2Supported())
    {
        const __m256 scaleVec = _mm256_set1_ps(scale);
        const __m256i one16 = _mm256_set1_epi16(1);
        const __m256i randomXor16 = _mm256_set1_epi16(-2);
        size_t i = 0;
        for (; i + 16u <= sampleCount; i += 16u)
        {
            __m256i packed = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(samples + i));
            if (adcRandom_)
            {
                const __m256i oddMask = _mm256_cmpeq_epi16(_mm256_and_si256(packed, one16), one16);
                packed = _mm256_xor_si256(packed, _mm256_and_si256(oddMask, randomXor16));
            }

            const __m128i packedLo = _mm256_castsi256_si128(packed);
            const __m128i packedHi = _mm256_extracti128_si256(packed, 1);
            const __m256 lo = _mm256_mul_ps(
                _mm256_cvtepi32_ps(_mm256_cvtepi16_epi32(packedLo)),
                scaleVec);
            const __m256 hi = _mm256_mul_ps(
                _mm256_cvtepi32_ps(_mm256_cvtepi16_epi32(packedHi)),
                scaleVec);
            _mm256_store_ps(output + i, lo);
            _mm256_store_ps(output + i + 8u, hi);
        }

        for (; i < sampleCount; ++i)
        {
            int16_t value = samples[i];
            if (adcRandom_ && (value & 1) != 0)
            {
                value = static_cast<int16_t>(value ^ static_cast<int16_t>(-2));
            }
            output[i] = static_cast<float>(value) * scale;
        }
        return;
    }

    const __m128 scaleVec = _mm_set1_ps(scale);
    const __m128i one16 = _mm_set1_epi16(1);
    const __m128i randomXor16 = _mm_set1_epi16(-2);
    size_t i = 0;
    for (; i + 8u <= sampleCount; i += 8u)
    {
        __m128i packed = _mm_loadu_si128(reinterpret_cast<const __m128i*>(samples + i));
        if (adcRandom_)
        {
            const __m128i oddMask = _mm_cmpeq_epi16(_mm_and_si128(packed, one16), one16);
            packed = _mm_xor_si128(packed, _mm_and_si128(oddMask, randomXor16));
        }
        const __m128i lo = _mm_cvtepi16_epi32(packed);
        const __m128i hi = _mm_cvtepi16_epi32(_mm_srli_si128(packed, 8));
        _mm_store_ps(output + i, _mm_mul_ps(_mm_cvtepi32_ps(lo), scaleVec));
        _mm_store_ps(output + i + 4u, _mm_mul_ps(_mm_cvtepi32_ps(hi), scaleVec));
    }
    for (; i < sampleCount; ++i)
    {
        int16_t value = samples[i];
        if (adcRandom_ && (value & 1) != 0)
        {
            value = static_cast<int16_t>(value ^ static_cast<int16_t>(-2));
        }
        output[i] = static_cast<float>(value) * scale;
    }
}
