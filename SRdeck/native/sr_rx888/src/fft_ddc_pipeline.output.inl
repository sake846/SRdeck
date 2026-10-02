void FftDdcPipeline::BuildReducedSpectrum(const Complex32* fullFreqBlock, Complex32* reducedFreqBlock)
{
    if (zeroGapCount_ > 0)
    {
        std::memset(
            reducedFreqBlock + static_cast<size_t>(zeroGapOffset_),
            0,
            static_cast<size_t>(zeroGapCount_) * sizeof(Complex32));
    }

    if (zeroTailCount_ > 0)
    {
        std::memset(
            reducedFreqBlock + static_cast<size_t>(zeroTailOffset_),
            0,
            static_cast<size_t>(zeroTailCount_) * sizeof(Complex32));
    }

    MultiplyBinsRange(reducedFreqBlock, fullFreqBlock + static_cast<size_t>(tuneBin_), filterSpectrum_.data(), upperBinCount_);
    MultiplyBinsRange(
        reducedFreqBlock + static_cast<size_t>(lowerDestinationStart_),
        fullFreqBlock + static_cast<size_t>(lowerSourceStart_),
        filterSpectrum_.data() + static_cast<size_t>(lowerFilterStart_),
        lowerBinCount_);
}

void FftDdcPipeline::MultiplyBinsRange(Complex32* destination, const Complex32* source, const Complex32* filter, int count)
{
    int i = 0;
    if (IsAvx2Supported())
    {
        for (; i + 4 <= count; i += 4)
        {
            const __m256 a = _mm256_loadu_ps(reinterpret_cast<const float*>(source + i));
            const __m256 b = _mm256_loadu_ps(reinterpret_cast<const float*>(filter + i));
            const __m256 ar = _mm256_moveldup_ps(a);
            const __m256 ai = _mm256_movehdup_ps(a);
            const __m256 bSwap = _mm256_permute_ps(b, 0xB1);
            const __m256 result = _mm256_addsub_ps(
                _mm256_mul_ps(ar, b),
                _mm256_mul_ps(ai, bSwap));
            _mm256_storeu_ps(reinterpret_cast<float*>(destination + i), result);
        }
    }

    const __m128 signMask = _mm_castsi128_ps(_mm_set_epi32(0, 0x80000000u, 0, 0x80000000u));
    for (; i + 2 <= count; i += 2)
    {
        const __m128 a = _mm_loadu_ps(reinterpret_cast<const float*>(source + i));
        const __m128 b = _mm_loadu_ps(reinterpret_cast<const float*>(filter + i));
        const __m128 ar = _mm_shuffle_ps(a, a, _MM_SHUFFLE(2, 2, 0, 0));
        const __m128 ai = _mm_shuffle_ps(a, a, _MM_SHUFFLE(3, 3, 1, 1));
        const __m128 bSwap = _mm_shuffle_ps(b, b, _MM_SHUFFLE(2, 3, 0, 1));
        const __m128 prod1 = _mm_mul_ps(ar, b);
        const __m128 prod2 = _mm_xor_ps(_mm_mul_ps(ai, bSwap), signMask);
        _mm_storeu_ps(reinterpret_cast<float*>(destination + i), _mm_add_ps(prod1, prod2));
    }

    for (; i < count; ++i)
    {
        destination[i] = {
            (source[i].re * filter[i].re) - (source[i].im * filter[i].im),
            (source[i].im * filter[i].re) + (source[i].re * filter[i].im)
        };
    }
}

HostDdcPipeline::HostDdcPipeline()
    : fftPipeline_(std::make_unique<FftDdcPipeline>())
{
}

HostDdcPipeline::~HostDdcPipeline() = default;

void HostDdcPipeline::Reset()
{
    if (fftPipeline_)
    {
        fftPipeline_->Reset();
    }
    modeName_ = "unconfigured";
    ResetProfileCounters();
}

void HostDdcPipeline::SetAdcRandom(bool enabled)
{
    if (fftPipeline_)
    {
        fftPipeline_->SetAdcRandom(enabled);
    }
}

void HostDdcPipeline::Configure(double adcFrequencyHz, double sampleRateHz, double tunerFrequencyHz, RFMode rfMode)
{
    const bool mirrorOutput = rfMode == VHF_MODE;
    if (fftPipeline_)
    {
        fftPipeline_->SetMirrorOutput(mirrorOutput);
    }

    if (fftPipeline_ && fftPipeline_->Configure(adcFrequencyHz, sampleRateHz, tunerFrequencyHz, rfMode))
    {
        useFftPipeline_ = true;
        modeName_ = "fft";
        return;
    }

    useFftPipeline_ = false;
    modeName_ = "unsupported";
}

void HostDdcPipeline::Process(const int16_t* samples, size_t sampleCount, std::vector<float>& output)
{
    output.clear();
    if (useFftPipeline_ && fftPipeline_)
    {
        const bool profileEnabled = IsNativeProfileEnabled();
        const auto start = profileEnabled ? std::chrono::steady_clock::now() : std::chrono::steady_clock::time_point{};
        fftPipeline_->Process(samples, sampleCount, output);
        if (profileEnabled)
        {
            profileFftNanoseconds_ += static_cast<uint64_t>(
                std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now() - start).count());
            ++profileFftCalls_;
        }
    }
}

void HostDdcPipeline::ProcessAppend(const int16_t* samples, size_t sampleCount, std::vector<float>& output)
{
    if (useFftPipeline_ && fftPipeline_)
    {
        const bool profileEnabled = IsNativeProfileEnabled();
        const auto start = profileEnabled ? std::chrono::steady_clock::now() : std::chrono::steady_clock::time_point{};
        fftPipeline_->ProcessAppend(samples, sampleCount, output);
        if (profileEnabled)
        {
            profileFftNanoseconds_ += static_cast<uint64_t>(
                std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now() - start).count());
            ++profileFftCalls_;
        }
    }
}

const char* HostDdcPipeline::GetModeName() const
{
    return modeName_;
}

size_t HostDdcPipeline::GetPreferredCallbackComplexSamples() const
{
    return static_cast<size_t>(GetEnvironmentInt(
        "SRDECK_SDDC_CALLBACK_COMPLEX",
        32768,
        1024,
        262144));
}

void HostDdcPipeline::ResetProfileCounters()
{
    profileFftNanoseconds_ = 0;
    profileFftCalls_ = 0;
}

HostDdcPipeline::ProfileSnapshot HostDdcPipeline::ConsumeProfileSnapshot()
{
    ProfileSnapshot snapshot;
    snapshot.modeName = modeName_;
    snapshot.fftNanoseconds = profileFftNanoseconds_;
    snapshot.fftCalls = profileFftCalls_;
    if (fftPipeline_)
    {
        snapshot.fftDetail = fftPipeline_->ConsumeProfileSnapshot();
    }
    ResetProfileCounters();
    return snapshot;
}
