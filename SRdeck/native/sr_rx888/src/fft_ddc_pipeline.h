#ifndef FFT_DDC_PIPELINE_H
#define FFT_DDC_PIPELINE_H

#include "sr_rx888.h"
#include "fft_engine.h"
#include <vector>
#include <array>
#include <memory>
#include "rx888_types.h"

class FftDdcPipeline
{
public:
    struct ProfileSnapshot
    {
        uint64_t inputPackNanoseconds = 0;
        uint64_t blockPrepNanoseconds = 0;
        uint64_t forwardFftNanoseconds = 0;
        uint64_t binMathNanoseconds = 0;
        uint64_t inverseFftNanoseconds = 0;
        uint64_t outputPackNanoseconds = 0;
        uint64_t overlapCopyNanoseconds = 0;
        uint64_t blockCalls = 0;
    };

    FftDdcPipeline() = default;
    ~FftDdcPipeline();

    bool Available() const;
    void Reset();
    void SetAdcRandom(bool enabled);
    void SetMirrorOutput(bool enabled);
    bool Configure(double adcFrequencyHz, double sampleRateHz, double tunerFrequencyHz, RFMode rfMode);
    void Process(const int16_t* samples, size_t sampleCount, std::vector<float>& output);
    void ProcessAppend(const int16_t* samples, size_t sampleCount, std::vector<float>& output);
    void ResetProfileCounters();
    ProfileSnapshot ConsumeProfileSnapshot();

private:
    static constexpr int kHalfFftSize = 4096;
    static constexpr int kFullTimeFftSize = kHalfFftSize * 2;
    static constexpr int kInputHistory = kHalfFftSize;
    static constexpr int kBlockStrideSamples = (3 * kHalfFftSize) / 2;
    static constexpr int kFullR2cBins = kHalfFftSize + 1;

    bool PreparePlans(int decimation, int reducedFftSize);
    void DestroyPlans();
    void BuildFilterSpectrum();
    void RebuildBinMappings();
    void ProcessInternal(const int16_t* samples, size_t sampleCount, std::vector<float>& output);
    void RunBlockWindow(const float* timeWindow, std::vector<float>& output);
    void RebuildFineTunePhases();
    void ConvertSamplesToFloat(const int16_t* samples, size_t sampleCount, float* output);
    void BuildReducedSpectrum(const Complex32* fullFreqBlock, Complex32* reducedFreqBlock);
    static void MultiplyBinsRange(Complex32* destination, const Complex32* source, const Complex32* filter, int count);

    std::unique_ptr<FftEngine> filterFft_;
    std::unique_ptr<FftEngine> forwardFft_;
    std::unique_ptr<FftEngine> inverseFft_;
    int decimation_ = 1;
    int reducedFftSize_ = 0;
    int tuneBin_ = 0;
    double adcFrequencyHz_ = 0.0;
    double sampleRateHz_ = 0.0;
    double tunerFrequencyHz_ = 0.0;
    RFMode rfMode_ = NO_RF_MODE;
    bool primed_ = false;
    std::vector<float> overlapReal_;
    AlignedBuffer<float> batchedTimeReal_;
    size_t batchedTimeRealSize_ = 0;
    AlignedBuffer<float> fullTimeReal_;
    AlignedBuffer<Complex32> fullFreqReal_;
    AlignedBuffer<Complex32> filterTime_;
    AlignedBuffer<Complex32> filterSpectrum_;
    AlignedBuffer<Complex32> reducedFreq_;
    AlignedBuffer<Complex32> reducedTime_;
    int upperBinCount_ = 0;
    int lowerSourceStart_ = 0;
    int lowerFilterStart_ = 0;
    int lowerDestinationStart_ = 0;
    int lowerBinCount_ = 0;
    int zeroGapOffset_ = 0;
    int zeroGapCount_ = 0;
    int zeroTailOffset_ = 0;
    int zeroTailCount_ = 0;
    bool adcRandom_ = false;
    bool mirrorOutput_ = false;
    size_t outputSampleRemainder_ = 0;
    float fineTuneRelativeFrequency_ = 0.0f;
    float fineTuneOscI_ = 1.0f;
    float fineTuneOscQ_ = 0.0f;
    float fineTuneStepI_ = 1.0f;
    float fineTuneStepQ_ = 0.0f;
    float fineTuneAdvance4I_ = 1.0f;
    float fineTuneAdvance4Q_ = 0.0f;
    std::array<float, 8> fineTunePhaseVecI_{1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f};
    std::array<float, 8> fineTunePhaseVecQ_{0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f};
    uint32_t fineTuneRenormCounter_ = 0;
    uint64_t profileInputPackNanoseconds_ = 0;
    uint64_t profileBlockPrepNanoseconds_ = 0;
    uint64_t profileForwardFftNanoseconds_ = 0;
    uint64_t profileBinMathNanoseconds_ = 0;
    uint64_t profileInverseFftNanoseconds_ = 0;
    uint64_t profileOutputPackNanoseconds_ = 0;
    uint64_t profileOverlapCopyNanoseconds_ = 0;
    uint64_t profileBlockCalls_ = 0;
};

class HostDdcPipeline
{
public:
    struct ProfileSnapshot
    {
        const char* modeName = "unconfigured";
        uint64_t fftNanoseconds = 0;
        uint64_t fftCalls = 0;
        FftDdcPipeline::ProfileSnapshot fftDetail{};
    };

    HostDdcPipeline();
    ~HostDdcPipeline();

    void Reset();
    void SetAdcRandom(bool enabled);
    void Configure(double adcFrequencyHz, double sampleRateHz, double tunerFrequencyHz, RFMode rfMode);
    void Process(const int16_t* samples, size_t sampleCount, std::vector<float>& output);
    void ProcessAppend(const int16_t* samples, size_t sampleCount, std::vector<float>& output);
    const char* GetModeName() const;
    size_t GetPreferredCallbackComplexSamples() const;
    void ResetProfileCounters();
    ProfileSnapshot ConsumeProfileSnapshot();

private:
    std::unique_ptr<FftDdcPipeline> fftPipeline_;
    bool useFftPipeline_ = false;
    const char* modeName_ = "unconfigured";
    uint64_t profileFftNanoseconds_ = 0;
    uint64_t profileFftCalls_ = 0;
};

#endif // FFT_DDC_PIPELINE_H
