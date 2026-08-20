#include <algorithm>
#include <cmath>
#include <cstdint>
#include <memory>
#include <mutex>
#include <unordered_map>
#include <vector>
#include <immintrin.h>
#include <intrin.h>

struct Complex32
{
    float x;
    float y;
};

namespace
{
struct FftPlan
{
    int sampleSize = 0;
    int logN = 0;
    std::vector<int> bitReverse;
    std::vector<std::vector<float>> stageTwiddles;
};

static std::shared_ptr<const FftPlan> BuildPlan(int sampleSize, int logN)
{
    auto plan = std::make_shared<FftPlan>();
    plan->sampleSize = sampleSize;
    plan->logN = logN;
    plan->bitReverse.resize(sampleSize);
    plan->stageTwiddles.resize(logN);

    for (int i = 0; i < sampleSize; ++i)
    {
        int reversed = 0;
        int src = i;
        for (int bit = 0; bit < logN; ++bit)
        {
            reversed = (reversed << 1) | (src & 1);
            src >>= 1;
        }
        plan->bitReverse[i] = reversed;
    }

    for (int stage = 0; stage < logN; ++stage)
    {
        const int len = 1 << (stage + 1);
        const int halfLen = len >> 1;
        auto& twiddles = plan->stageTwiddles[stage];
        twiddles.resize(static_cast<size_t>(halfLen) * 2u);
        for (int j = 0; j < halfLen; ++j)
        {
            const float angle = -2.0f * 3.14159265358979323846f * static_cast<float>(j) / static_cast<float>(len);
            twiddles[static_cast<size_t>(j) * 2u] = std::cos(angle);
            twiddles[static_cast<size_t>(j) * 2u + 1u] = std::sin(angle);
        }
    }

    return plan;
}

static const FftPlan& GetPlan(int sampleSize, int logN)
{
    static std::mutex planMutex;
    static std::unordered_map<uint64_t, std::shared_ptr<const FftPlan>> planCache;

    const uint64_t key = (static_cast<uint64_t>(static_cast<uint32_t>(sampleSize)) << 32) |
                         static_cast<uint32_t>(logN);

    std::lock_guard<std::mutex> lock(planMutex);
    auto it = planCache.find(key);
    if (it != planCache.end())
    {
        return *it->second;
    }

    auto plan = BuildPlan(sampleSize, logN);
    const FftPlan& planRef = *plan;
    planCache.emplace(key, std::move(plan));
    return planRef;
}

static bool DetectAvx2()
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
}

static bool DetectAvx512()
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
    // xcr0 bit 1: SSE, bit 2: AVX, bit 5: OPMASK, bit 6: ZMM_Hi256, bit 7: Hi16_ZMM
    if ((xcr0 & 0xE6) != 0xE6)
    {
        return false;
    }

    __cpuidex(cpuInfo, 7, 0);
    return (cpuInfo[1] & (1 << 16)) != 0; // AVX512F
}

static bool Avx512Available()
{
    static const bool isAvailable = DetectAvx512();
    return isAvailable;
}

static void ExecuteButterfliesAvx512(Complex32* data, int sampleSize, const FftPlan& plan)
{
    for (int stage = 0; stage < plan.logN; ++stage)
    {
        const int len = 1 << (stage + 1);
        const int halfLen = len >> 1;
        const float* twiddles = plan.stageTwiddles[stage].data();

        if (halfLen >= 8)
        {
            for (int j = 0; j <= halfLen - 8; j += 8)
            {
                const __m512 twVec = _mm512_loadu_ps(twiddles + j * 2);
                const __m512 twReal = _mm512_moveldup_ps(twVec);
                const __m512 twImag = _mm512_movehdup_ps(twVec);

                for (int base = 0; base < sampleSize; base += len)
                {
                    float* evenPtr = reinterpret_cast<float*>(data + base + j);
                    float* oddPtr = reinterpret_cast<float*>(data + base + j + halfLen);

                    const __m512 evenVec = _mm512_loadu_ps(evenPtr);
                    const __m512 oddVec = _mm512_loadu_ps(oddPtr);

                    const __m512 oddSwapped = _mm512_permute_ps(oddVec, 0xB1);
                    const __m512 prod = _mm512_mul_ps(oddSwapped, twImag);
                    const __m512 rotated = _mm512_fmaddsub_ps(oddVec, twReal, prod);

                    _mm512_storeu_ps(evenPtr, _mm512_add_ps(evenVec, rotated));
                    _mm512_storeu_ps(oddPtr, _mm512_sub_ps(evenVec, rotated));
                }
            }
        }

        const int jStart = (halfLen >= 8) ? (halfLen & ~7) : 0;
        if (jStart < halfLen)
        {
            if (halfLen >= 4 && (halfLen - jStart) >= 4)
            {
                for (int j = jStart; j <= halfLen - 4; j += 4)
                {
                    const __m256 twVec = _mm256_loadu_ps(twiddles + j * 2);
                    const __m256 twReal = _mm256_moveldup_ps(twVec);
                    const __m256 twImag = _mm256_movehdup_ps(twVec);

                    for (int base = 0; base < sampleSize; base += len)
                    {
                        float* evenPtr = reinterpret_cast<float*>(data + base + j);
                        float* oddPtr = reinterpret_cast<float*>(data + base + j + halfLen);

                        const __m256 evenVec = _mm256_loadu_ps(evenPtr);
                        const __m256 oddVec = _mm256_loadu_ps(oddPtr);

                        const __m256 oddSwapped = _mm256_permute_ps(oddVec, 0xB1);
                        const __m256 prod = _mm256_mul_ps(oddSwapped, twImag);
                        const __m256 rotated = _mm256_fmaddsub_ps(oddVec, twReal, prod);

                        _mm256_storeu_ps(evenPtr, _mm256_add_ps(evenVec, rotated));
                        _mm256_storeu_ps(oddPtr, _mm256_sub_ps(evenVec, rotated));
                    }
                }
            }
            else
            {
                for (int base = 0; base < sampleSize; base += len)
                {
                    for (int j = jStart; j < halfLen; ++j)
                    {
                        Complex32& even = data[base + j];
                        Complex32& odd = data[base + j + halfLen];
                        const float wCos = twiddles[j * 2];
                        const float wSin = twiddles[j * 2 + 1];

                        const float vX = odd.x * wCos - odd.y * wSin;
                        const float vY = odd.x * wSin + odd.y * wCos;
                        const float uX = even.x;
                        const float uY = even.y;

                        even.x = uX + vX;
                        even.y = uY + vY;
                        odd.x = uX - vX;
                        odd.y = uY - vY;
                    }
                }
            }
        }
    }
}

static bool Avx2Available()
{
    static const bool isAvailable = DetectAvx2();
    return isAvailable;
}

static void ExecuteButterfliesAvx2(Complex32* data, int sampleSize, const FftPlan& plan)
{
    for (int stage = 0; stage < plan.logN; ++stage)
    {
        const int len = 1 << (stage + 1);
        const int halfLen = len >> 1;
        const float* twiddles = plan.stageTwiddles[stage].data();

        if (halfLen >= 4)
        {
            for (int j = 0; j <= halfLen - 4; j += 4)
            {
                const __m256 twVec = _mm256_loadu_ps(twiddles + j * 2);
                const __m256 twReal = _mm256_moveldup_ps(twVec);
                const __m256 twImag = _mm256_movehdup_ps(twVec);

                for (int base = 0; base < sampleSize; base += len)
                {
                    float* evenPtr = reinterpret_cast<float*>(data + base + j);
                    float* oddPtr = reinterpret_cast<float*>(data + base + j + halfLen);

                    const __m256 evenVec = _mm256_loadu_ps(evenPtr);
                    const __m256 oddVec = _mm256_loadu_ps(oddPtr);

                    const __m256 oddSwapped = _mm256_permute_ps(oddVec, 0xB1);
                    const __m256 prod = _mm256_mul_ps(oddSwapped, twImag);
                    const __m256 rotated = _mm256_fmaddsub_ps(oddVec, twReal, prod);

                    _mm256_storeu_ps(evenPtr, _mm256_add_ps(evenVec, rotated));
                    _mm256_storeu_ps(oddPtr, _mm256_sub_ps(evenVec, rotated));
                }
            }
        }

        const int jStart = (halfLen >= 4) ? (halfLen & ~3) : 0;
        if (jStart < halfLen)
        {
            for (int base = 0; base < sampleSize; base += len)
            {
                for (int j = jStart; j < halfLen; ++j)
                {
                    Complex32& even = data[base + j];
                    Complex32& odd = data[base + j + halfLen];
                    const float wCos = twiddles[j * 2];
                    const float wSin = twiddles[j * 2 + 1];

                    const float vX = odd.x * wCos - odd.y * wSin;
                    const float vY = odd.x * wSin + odd.y * wCos;
                    const float uX = even.x;
                    const float uY = even.y;

                    even.x = uX + vX;
                    even.y = uY + vY;
                    odd.x = uX - vX;
                    odd.y = uY - vY;
                }
            }
        }
    }
}

static void ExecuteButterfliesScalar(Complex32* data, int sampleSize, const FftPlan& plan)
{
    for (int stage = 0; stage < plan.logN; ++stage)
    {
        const int len = 1 << (stage + 1);
        const int halfLen = len >> 1;
        const float* twiddles = plan.stageTwiddles[stage].data();

        for (int base = 0; base < sampleSize; base += len)
        {
            for (int j = 0; j < halfLen; ++j)
            {
                Complex32& even = data[base + j];
                Complex32& odd = data[base + j + halfLen];
                const float wCos = twiddles[j * 2];
                const float wSin = twiddles[j * 2 + 1];

                const float vX = odd.x * wCos - odd.y * wSin;
                const float vY = odd.x * wSin + odd.y * wCos;
                const float uX = even.x;
                const float uY = even.y;

                even.x = uX + vX;
                even.y = uY + vY;
                odd.x = uX - vX;
                odd.y = uY - vY;
            }
        }
    }
}

static Complex32* PrepareScratch(const Complex32* samples, int sampleSize, const FftPlan& plan)
{
    thread_local std::vector<Complex32> scratch;
    if (static_cast<int>(scratch.size()) < sampleSize)
    {
        scratch.resize(sampleSize);
    }

    for (int i = 0; i < sampleSize; ++i)
    {
        scratch[i] = samples[plan.bitReverse[i]];
    }

    return scratch.data();
}
}

extern "C"
{
__declspec(dllexport) int cpufft_execute_db(Complex32* samples, int sampleSize, int logN, float bias, float* outputDb)
{
    if (samples == nullptr || outputDb == nullptr || sampleSize <= 0 || logN <= 0)
    {
        return -1;
    }

    if ((1 << logN) != sampleSize)
    {
        return -2;
    }

    const FftPlan& plan = GetPlan(sampleSize, logN);
    Complex32* work = PrepareScratch(samples, sampleSize, plan);

    if (Avx512Available())
    {
        ExecuteButterfliesAvx512(work, sampleSize, plan);
    }
    else if (Avx2Available())
    {
        ExecuteButterfliesAvx2(work, sampleSize, plan);
    }
    else
    {
        ExecuteButterfliesScalar(work, sampleSize, plan);
    }

    const int halfSize = sampleSize / 2;
    for (int i = 0; i < sampleSize; ++i)
    {
        const int targetIdx = (i + halfSize) % sampleSize;
        const float magSq = work[i].x * work[i].x + work[i].y * work[i].y;
        outputDb[targetIdx] = 10.0f * std::log10(std::max(magSq, 1.0e-30f)) + bias;
    }

    return 0;
}

__declspec(dllexport) int cpufft_execute_power(Complex32* samples, int sampleSize, int logN, float* outputPower)
{
    if (samples == nullptr || outputPower == nullptr || sampleSize <= 0 || logN <= 0)
    {
        return -1;
    }

    if ((1 << logN) != sampleSize)
    {
        return -2;
    }

    const FftPlan& plan = GetPlan(sampleSize, logN);
    Complex32* work = PrepareScratch(samples, sampleSize, plan);
    if (Avx512Available()) ExecuteButterfliesAvx512(work, sampleSize, plan);
    else if (Avx2Available()) ExecuteButterfliesAvx2(work, sampleSize, plan);
    else ExecuteButterfliesScalar(work, sampleSize, plan);

    const int halfSize = sampleSize / 2;
    for (int i = 0; i < sampleSize; ++i)
    {
        const int targetIdx = (i + halfSize) % sampleSize;
        outputPower[targetIdx] = work[i].x * work[i].x + work[i].y * work[i].y;
    }
    return 0;
}
}
