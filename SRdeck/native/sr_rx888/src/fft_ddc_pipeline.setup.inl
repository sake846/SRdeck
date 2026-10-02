#include "fft_ddc_pipeline.h"
#include <immintrin.h>
#include <algorithm>
#include <cmath>
#include <chrono>

// Forward declarations of helper functions defined elsewhere
void NativeLog(const std::string& message);
bool IsNativeProfileEnabled();
bool GetEnvironmentBool(const char* name);
int GetEnvironmentInt(const char* name, int defaultValue, int minValue, int maxValue);

namespace
{
constexpr double kPi = 3.14159265358979323846;
constexpr double kTwoPi = 6.28318530717958647692;
constexpr float kRx888Mk2GainFactor = 1.08e-8f;
constexpr double kR828dIfCarrier = 4570000.0;

bool IsAvx2Supported()
{
#if defined(_M_X64) || defined(_M_IX86)
    static const bool supported = []()
    {
        int cpuInfo[4] = {};
        __cpuid(cpuInfo, 0);
        if (cpuInfo[0] < 7)
        {
            return false;
        }

        __cpuid(cpuInfo, 1);
        const bool osxsave = (cpuInfo[2] & (1 << 27)) != 0;
        const bool avx = (cpuInfo[2] & (1 << 28)) != 0;
        if (!osxsave || !avx)
        {
            return false;
        }

        const unsigned long long xcr0 = _xgetbv(0);
        if ((xcr0 & 0x6) != 0x6)
        {
            return false;
        }

        __cpuidex(cpuInfo, 7, 0);
        return (cpuInfo[1] & (1 << 5)) != 0;
    }();
    return supported;
#else
    return false;
#endif
}

bool IsFmaSupported()
{
#if defined(_M_X64) || defined(_M_IX86)
    static const bool supported = []()
    {
        int cpuInfo[4] = {};
        __cpuid(cpuInfo, 1);
        const bool osxsave = (cpuInfo[2] & (1 << 27)) != 0;
        const bool avx = (cpuInfo[2] & (1 << 28)) != 0;
        const bool fma = (cpuInfo[2] & (1 << 12)) != 0;
        if (!osxsave || !avx || !fma)
        {
            return false;
        }

        const unsigned long long xcr0 = _xgetbv(0);
        return (xcr0 & 0x6) == 0x6;
    }();
    return supported;
#else
    return false;
#endif
}

std::vector<float> DesignLowpassFir(int decimation)
{
    const int taps = decimation <= 2 ? 63 : 95;
    const int midpoint = taps / 2;
    const double cutoff = std::min(0.49 / static_cast<double>(std::max(1, decimation)), 0.49);

    std::vector<float> coefficients(static_cast<size_t>(taps), 0.0f);
    double sum = 0.0;
    for (int i = 0; i < taps; ++i)
    {
        const int n = i - midpoint;
        const double x = 2.0 * cutoff * static_cast<double>(n);
        const double sinc = n == 0
            ? 2.0 * cutoff
            : std::sin(kPi * x) / (kPi * static_cast<double>(n));
        const double window = 0.54 - 0.46 * std::cos((kTwoPi * static_cast<double>(i)) / static_cast<double>(taps - 1));
        const double coefficient = sinc * window;
        coefficients[static_cast<size_t>(i)] = static_cast<float>(coefficient);
        sum += coefficient;
    }

    if (sum != 0.0)
    {
        for (float& coefficient : coefficients)
        {
            coefficient = static_cast<float>(coefficient / sum);
        }
    }

    return coefficients;
}

double BesselI0(double value)
{
    const double halfValue = value * 0.5;
    double sum = 1.0;
    double term = 1.0;
    for (double index = 1.0;; index += 1.0)
    {
        const double ratio = halfValue / index;
        term *= ratio * ratio;
        sum += term;
        if (term < 1.0e-9 * sum)
        {
            return sum;
        }
    }
}

std::vector<float> DesignUpstreamFftFir(int taps, int decimation, int inverseFftSize)
{
    constexpr double stopbandAttenuationDb = 120.0;
    constexpr double relativePassband = 0.85;
    constexpr double relativeStopband = 1.1;
    constexpr double upstreamForwardFftSize = 8192.0;

    const double bandwidthMHz = 64.0 / static_cast<double>(std::max(1, decimation));
    const double normalizedPassband = relativePassband * bandwidthMHz / 128.0;
    const double normalizedStopband = relativeStopband * bandwidthMHz / 128.0;
    const double normalizedCutoff = (normalizedPassband + normalizedStopband) * 0.5;
    const double beta = 0.1102 * (stopbandAttenuationDb - 8.71);
    const double denominator = BesselI0(beta);
    const double midpoint = 0.5 * static_cast<double>(taps - 1);

    const double upstreamGain = static_cast<double>(kRx888Mk2GainFactor) * 2048.0 / upstreamForwardFftSize;
    const double nativeEquivalentGain =
        32768.0 * static_cast<double>(inverseFftSize) * upstreamGain;

    std::vector<float> coefficients(static_cast<size_t>(taps), 0.0f);
    for (int i = 0; i < taps; ++i)
    {
        const double x = static_cast<double>(i) - midpoint;
        const double sinc = x == 0.0
            ? 2.0 * normalizedCutoff
            : std::sin(kTwoPi * x * normalizedCutoff) / (kPi * x);
        const double windowPosition = x / midpoint;
        const double window = BesselI0(beta * std::sqrt(std::max(0.0, 1.0 - windowPosition * windowPosition))) /
            denominator;
        coefficients[static_cast<size_t>(i)] = static_cast<float>(nativeEquivalentGain * sinc * window);
    }

    return coefficients;
}
} // namespace

FftDdcPipeline::~FftDdcPipeline()
{
    DestroyPlans();
}

bool FftDdcPipeline::Available() const
{
    return true;
}

void FftDdcPipeline::Reset()
{
    overlapReal_.assign(kInputHistory, 0.0f);
    batchedTimeRealSize_ = 0;
    outputSampleRemainder_ = 0;
    fineTuneOscI_ = 1.0f;
    fineTuneOscQ_ = 0.0f;
    fineTuneRenormCounter_ = 0;
    primed_ = false;
    RebuildFineTunePhases();
    ResetProfileCounters();
}

void FftDdcPipeline::SetAdcRandom(bool enabled)
{
    adcRandom_ = enabled;
}

void FftDdcPipeline::SetMirrorOutput(bool enabled)
{
    mirrorOutput_ = enabled;
}

bool FftDdcPipeline::Configure(double adcFrequencyHz, double sampleRateHz, double tunerFrequencyHz, RFMode rfMode)
{
    if (!Available())
    {
        return false;
    }

    const double nextAdcFrequencyHz = std::max(1.0, adcFrequencyHz);
    const double nextSampleRateHz = std::max(1.0, sampleRateHz);
    const double nextTunerFrequencyHz = tunerFrequencyHz;
    const int nextInputDecimation = std::max(1, static_cast<int>(std::lround(nextAdcFrequencyHz / nextSampleRateHz)));
    if ((nextInputDecimation & 1) != 0)
    {
        return false;
    }

    const int nextDecimation = std::max(1, nextInputDecimation / 2);
    if ((kHalfFftSize % nextDecimation) != 0)
    {
        return false;
    }

    const int nextReducedFftSize = kHalfFftSize / nextDecimation;
    if ((nextReducedFftSize & 1) != 0)
    {
        return false;
    }

    const bool shapeChanged =
        nextDecimation != decimation_ ||
        nextReducedFftSize != reducedFftSize_;
    if (shapeChanged && !PreparePlans(nextDecimation, nextReducedFftSize))
    {
        return false;
    }

    const bool oscillatorChanged =
        adcFrequencyHz_ != nextAdcFrequencyHz ||
        tunerFrequencyHz_ != nextTunerFrequencyHz ||
        rfMode_ != rfMode;

    adcFrequencyHz_ = nextAdcFrequencyHz;
    sampleRateHz_ = nextSampleRateHz;
    tunerFrequencyHz_ = nextTunerFrequencyHz;
    rfMode_ = rfMode;

    if (oscillatorChanged)
    {
        const double mixFrequency = rfMode_ == VHF_MODE ? kR828dIfCarrier : tunerFrequencyHz_;
        const double wrapped = std::fmod(mixFrequency, adcFrequencyHz_);
        const double positiveWrapped = wrapped < 0.0 ? wrapped + adcFrequencyHz_ : wrapped;
        const double exactTuneBin =
            (positiveWrapped / adcFrequencyHz_) * static_cast<double>(kFullTimeFftSize);
        tuneBin_ = static_cast<int>(std::floor(exactTuneBin));
        tuneBin_ = std::clamp(tuneBin_ & ~3, 0, kHalfFftSize - 4);
        const double normalizedOffset = positiveWrapped / (adcFrequencyHz_ * 0.5);
        const double quantizedNormalizedOffset = static_cast<double>(tuneBin_) / static_cast<double>(kHalfFftSize);
        fineTuneRelativeFrequency_ =
            static_cast<float>((quantizedNormalizedOffset - normalizedOffset) * static_cast<double>(decimation_));
        const double phaseStep = kTwoPi * static_cast<double>(fineTuneRelativeFrequency_);
        fineTuneStepI_ = static_cast<float>(std::cos(phaseStep));
        fineTuneStepQ_ = static_cast<float>(std::sin(phaseStep));
        fineTuneOscI_ = 1.0f;
        fineTuneOscQ_ = 0.0f;
        fineTuneRenormCounter_ = 0;
        RebuildFineTunePhases();
        RebuildBinMappings();
    }

    return true;
}

void FftDdcPipeline::Process(const int16_t* samples, size_t sampleCount, std::vector<float>& output)
{
    output.clear();
    ProcessInternal(samples, sampleCount, output);
}

void FftDdcPipeline::ProcessAppend(const int16_t* samples, size_t sampleCount, std::vector<float>& output)
{
    ProcessInternal(samples, sampleCount, output);
}

void FftDdcPipeline::ResetProfileCounters()
{
    profileInputPackNanoseconds_ = 0;
    profileBlockPrepNanoseconds_ = 0;
    profileForwardFftNanoseconds_ = 0;
    profileBinMathNanoseconds_ = 0;
    profileInverseFftNanoseconds_ = 0;
    profileOutputPackNanoseconds_ = 0;
    profileOverlapCopyNanoseconds_ = 0;
    profileBlockCalls_ = 0;
}

FftDdcPipeline::ProfileSnapshot FftDdcPipeline::ConsumeProfileSnapshot()
{
    ProfileSnapshot snapshot;
    snapshot.inputPackNanoseconds = profileInputPackNanoseconds_;
    snapshot.blockPrepNanoseconds = profileBlockPrepNanoseconds_;
    snapshot.forwardFftNanoseconds = profileForwardFftNanoseconds_;
    snapshot.binMathNanoseconds = profileBinMathNanoseconds_;
    snapshot.inverseFftNanoseconds = profileInverseFftNanoseconds_;
    snapshot.outputPackNanoseconds = profileOutputPackNanoseconds_;
    snapshot.overlapCopyNanoseconds = profileOverlapCopyNanoseconds_;
    snapshot.blockCalls = profileBlockCalls_;
    ResetProfileCounters();
    return snapshot;
}

bool FftDdcPipeline::PreparePlans(int decimation, int reducedFftSize)
{
    DestroyPlans();

    decimation_ = decimation;
    reducedFftSize_ = reducedFftSize;
    if (!fullFreqReal_.Resize(static_cast<size_t>(kFullR2cBins)) ||
        !filterSpectrum_.Resize(static_cast<size_t>(kHalfFftSize)) ||
        !reducedFreq_.Resize(static_cast<size_t>(reducedFftSize_)) ||
        !reducedTime_.Resize(static_cast<size_t>(reducedFftSize_)) ||
        !filterTime_.Resize(static_cast<size_t>(kHalfFftSize)) ||
        !fullTimeReal_.Resize(static_cast<size_t>(kFullTimeFftSize)))
    {
        DestroyPlans();
        return false;
    }
    overlapReal_.assign(kInputHistory, 0.0f);
    batchedTimeReal_.Reset();
    batchedTimeRealSize_ = 0;
    outputSampleRemainder_ = 0;
    primed_ = false;

    std::fill(fullTimeReal_.begin(), fullTimeReal_.end(), 0.0f);

    try
    {
        filterFft_ = std::make_unique<FftEngine>(kHalfFftSize, FftEngine::Direction::Forward);
        forwardFft_ = std::make_unique<FftEngine>(kFullTimeFftSize, FftEngine::Direction::Forward);
        inverseFft_ = std::make_unique<FftEngine>(reducedFftSize_, FftEngine::Direction::Backward);
    }
    catch (...)
    {
        DestroyPlans();
        return false;
    }

    BuildFilterSpectrum();
    return true;
}

void FftDdcPipeline::DestroyPlans()
{
    filterFft_.reset();
    forwardFft_.reset();
    inverseFft_.reset();

    fullTimeReal_.Reset();
    fullFreqReal_.Reset();
    filterTime_.Reset();
    filterSpectrum_.Reset();
    reducedFreq_.Reset();
    reducedTime_.Reset();
    batchedTimeReal_.Reset();
    batchedTimeRealSize_ = 0;
}

void FftDdcPipeline::BuildFilterSpectrum()
{
    const std::vector<float> taps =
        DesignUpstreamFftFir((kHalfFftSize / 4) + 1, decimation_, reducedFftSize_);

    std::fill(filterTime_.begin(), filterTime_.end(), Complex32{});
    for (size_t i = 0; i < taps.size(); ++i)
    {
        const int index = kHalfFftSize - 1 - static_cast<int>(i);
        filterTime_[static_cast<size_t>(index)].re = taps[i];
    }

    filterFft_->Execute(filterTime_.data(), filterSpectrum_.data());
    const float inverseScale = 1.0f / static_cast<float>(std::max(1, reducedFftSize_));
    for (int i = 0; i < kHalfFftSize; ++i)
    {
        filterSpectrum_[static_cast<size_t>(i)].re *= inverseScale;
        filterSpectrum_[static_cast<size_t>(i)].im *= inverseScale;
    }
}

void FftDdcPipeline::RebuildBinMappings()
{
    if (reducedFftSize_ <= 0)
    {
        upperBinCount_ = 0;
        lowerBinCount_ = 0;
        return;
    }

    const int halfReduced = reducedFftSize_ / 2;
    upperBinCount_ = std::min(halfReduced, kHalfFftSize - tuneBin_);
    lowerDestinationStart_ = halfReduced + std::max(0, halfReduced - tuneBin_);
    lowerSourceStart_ = std::max(0, tuneBin_ - halfReduced);
    lowerFilterStart_ = kHalfFftSize - halfReduced + std::max(0, halfReduced - tuneBin_);
    lowerBinCount_ = halfReduced - std::max(0, halfReduced - tuneBin_);
    zeroGapOffset_ = upperBinCount_;
    zeroGapCount_ = std::max(0, lowerDestinationStart_ - upperBinCount_);
    zeroTailOffset_ = lowerDestinationStart_ + lowerBinCount_;
    zeroTailCount_ = std::max(0, reducedFftSize_ - zeroTailOffset_);
}
