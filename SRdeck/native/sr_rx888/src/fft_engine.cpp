#include "fft_engine.h"

#include <cmath>
#include <immintrin.h>
#include <algorithm>

FftEngine::FftEngine(size_t size, Direction dir)
    : size_(size), dir_(dir)
{
    InitTwiddles();
    InitBitReversal();
    work_.Resize(size_);
}

void FftEngine::InitTwiddles()
{
    const size_t N = size_;
    twiddles_.Resize(N);

    constexpr double kPi = 3.14159265358979323846;
    const double angle_factor = (dir_ == Direction::Forward) ? -2.0 * kPi : 2.0 * kPi;

    for (size_t L = 1; L < N; L <<= 1)
    {
        size_t offset = L - 1;
        for (size_t j = 0; j < L; ++j)
        {
            double angle = angle_factor * static_cast<double>(j) / static_cast<double>(2 * L);
            twiddles_[offset + j].re = static_cast<float>(std::cos(angle));
            twiddles_[offset + j].im = static_cast<float>(std::sin(angle));
        }
    }
}

void FftEngine::InitBitReversal()
{
    bit_rev_.Resize(size_);
    int log2N = 0;
    for (size_t temp = size_; temp > 1; temp >>= 1)
    {
        log2N++;
    }

    for (size_t i = 0; i < size_; ++i)
    {
        size_t rev = 0;
        size_t temp = i;
        for (int step = 0; step < log2N; ++step)
        {
            rev = (rev << 1) | (temp & 1);
            temp >>= 1;
        }
        bit_rev_[i] = static_cast<uint16_t>(rev);
    }

    if (size_ == 8192)
    {
        const size_t half = 4096;
        bit_rev_half_.Resize(half);
        for (size_t i = 0; i < half; ++i)
        {
            size_t rev = 0;
            size_t temp = i;
            for (int step = 0; step < 12; ++step) // log2(4096) = 12
            {
                rev = (rev << 1) | (temp & 1);
                temp >>= 1;
            }
            bit_rev_half_[i] = static_cast<uint16_t>(rev);
        }
    }
}

void FftEngine::Execute(const Complex32* in, Complex32* out)
{
    if (in == nullptr || out == nullptr || size_ == 0 || bit_rev_.size() < size_)
    {
        return;
    }

    // Copy input to output buffer using precalculated bit reversal index
    for (size_t i = 0; i < size_; ++i)
    {
        out[bit_rev_[i]] = in[i];
    }

    ExecuteCooleyTukey(out, size_);
}

void FftEngine::ExecuteCooleyTukey(Complex32* out, size_t N)
{
    if (out == nullptr || N == 0)
    {
        return;
    }
    // Stage 1: stride = 1
    if (N >= 2)
    {
        size_t i = 0;
        for (; i + 8 <= N; i += 8)
        {
            __m256 a = _mm256_loadu_ps(reinterpret_cast<const float*>(&out[i]));
            __m256 b = _mm256_loadu_ps(reinterpret_cast<const float*>(&out[i + 4]));

            __m256 u_a = _mm256_permute_ps(a, 0x44);
            __m256 v_a = _mm256_permute_ps(a, 0xEE);
            __m256 res_a = _mm256_blend_ps(_mm256_add_ps(u_a, v_a), _mm256_sub_ps(u_a, v_a), 0xCC);

            __m256 u_b = _mm256_permute_ps(b, 0x44);
            __m256 v_b = _mm256_permute_ps(b, 0xEE);
            __m256 res_b = _mm256_blend_ps(_mm256_add_ps(u_b, v_b), _mm256_sub_ps(u_b, v_b), 0xCC);

            _mm256_storeu_ps(reinterpret_cast<float*>(&out[i]), res_a);
            _mm256_storeu_ps(reinterpret_cast<float*>(&out[i + 4]), res_b);
        }
        for (; i < N; i += 2)
        {
            Complex32 u = out[i];
            Complex32 v = out[i + 1];
            out[i].re = u.re + v.re;
            out[i].im = u.im + v.im;
            out[i + 1].re = u.re - v.re;
            out[i + 1].im = u.im - v.im;
        }
    }

    // Stage 2: stride = 2
    if (N >= 4)
    {
        const float sign = (dir_ == Direction::Forward) ? -1.0f : 1.0f;
        size_t i = 0;
        const __m256 t_signs = _mm256_setr_ps(1.0f, 1.0f, -sign, sign, 1.0f, 1.0f, -sign, sign);
        for (; i + 8 <= N; i += 8)
        {
            __m256 a = _mm256_loadu_ps(reinterpret_cast<const float*>(&out[i]));
            __m256 b = _mm256_loadu_ps(reinterpret_cast<const float*>(&out[i + 4]));

            __m256 u_a = _mm256_permute2f128_ps(a, a, 0x00);
            __m256 v_a = _mm256_permute2f128_ps(a, a, 0x11);
            __m256 v_a_swap = _mm256_permute_ps(v_a, 0xB4);
            __m256 v_a_t = _mm256_mul_ps(v_a_swap, t_signs);
            __m256 res_a = _mm256_blend_ps(_mm256_add_ps(u_a, v_a_t), _mm256_sub_ps(u_a, v_a_t), 0xF0);

            __m256 u_b = _mm256_permute2f128_ps(b, b, 0x00);
            __m256 v_b = _mm256_permute2f128_ps(b, b, 0x11);
            __m256 v_b_swap = _mm256_permute_ps(v_b, 0xB4);
            __m256 v_b_t = _mm256_mul_ps(v_b_swap, t_signs);
            __m256 res_b = _mm256_blend_ps(_mm256_add_ps(u_b, v_b_t), _mm256_sub_ps(u_b, v_b_t), 0xF0);

            _mm256_storeu_ps(reinterpret_cast<float*>(&out[i]), res_a);
            _mm256_storeu_ps(reinterpret_cast<float*>(&out[i + 4]), res_b);
        }
        for (; i < N; i += 4)
        {
            // j = 0
            {
                Complex32 u = out[i];
                Complex32 v = out[i + 2];
                out[i].re = u.re + v.re;
                out[i].im = u.im + v.im;
                out[i + 2].re = u.re - v.re;
                out[i + 2].im = u.im - v.im;
            }
            // j = 1, twiddle factor is -i (forward) or +i (backward)
            {
                Complex32 u = out[i + 1];
                Complex32 v = out[i + 3];
                float t_re = -v.im * sign;
                float t_im = v.re * sign;
                out[i + 1].re = u.re + t_re;
                out[i + 1].im = u.im + t_im;
                out[i + 3].re = u.re - t_re;
                out[i + 3].im = u.im - t_im;
            }
        }
    }

    // Stage 3 to log2N: stride >= 4
    for (size_t stride = 4; stride < N; stride <<= 1)
    {
        const size_t group_size = stride << 1;
        const size_t twiddle_offset = stride - 1;

        for (size_t j = 0; j < stride; j += 4)
        {
            const __m256 w = _mm256_loadu_ps(reinterpret_cast<const float*>(&twiddles_[twiddle_offset + j]));
            const __m256 w_swap = _mm256_permute_ps(w, 0xB1);

            for (size_t g = 0; g < N; g += group_size)
            {
                const size_t idx0 = g + j;
                const size_t idx1 = idx0 + stride;

                const __m256 u = _mm256_loadu_ps(reinterpret_cast<const float*>(&out[idx0]));
                const __m256 v = _mm256_loadu_ps(reinterpret_cast<const float*>(&out[idx1]));

                const __m256 vr = _mm256_moveldup_ps(v);
                const __m256 vi = _mm256_movehdup_ps(v);

                // Complex multiply using FMA (fmaddsub performs sub/add operation)
                const __m256 prod = _mm256_mul_ps(vi, w_swap);
                const __m256 t = _mm256_fmaddsub_ps(vr, w, prod);

                const __m256 res0 = _mm256_add_ps(u, t);
                const __m256 res1 = _mm256_sub_ps(u, t);

                _mm256_storeu_ps(reinterpret_cast<float*>(&out[idx0]), res0);
                _mm256_storeu_ps(reinterpret_cast<float*>(&out[idx1]), res1);
            }
        }
    }
}

void FftEngine::ExecuteR2c(const float* in, Complex32* out)
{
    const size_t N = size_;
    const size_t half = N / 2;

    if (in == nullptr || out == nullptr || half == 0 || bit_rev_half_.size() < half || work_.size() < half)
    {
        return;
    }

    const Complex32* packed_in = reinterpret_cast<const Complex32*>(in);

    // Perform complex FFT on the packed complex input using half-size bit reversal table
    for (size_t i = 0; i < half; ++i)
    {
        out[bit_rev_half_[i]] = packed_in[i];
    }

    ExecuteCooleyTukey(out, half);

    // Copy result to temporary work buffer to avoid overlapping access
    std::memcpy(work_.data(), out, sizeof(Complex32) * half);

    out[0].re = work_[0].re + work_[0].im;
    out[0].im = 0.0f;
    out[half].re = work_[0].re - work_[0].im;
    out[half].im = 0.0f;

    const size_t r2c_offset = half - 1;

    size_t k = 1;
    // Process 4 complex pairs at a time using AVX2
    for (; k + 4 <= half; k += 4)
    {
        // Load z_k = work_[k ... k+3]
        __m256 zk = _mm256_loadu_ps(reinterpret_cast<const float*>(&work_[k]));

        // Load z_nk = work_[half-k-3 ... half-k] and reverse it
        // The loaded vector layout: [z_{half-k-3}, z_{half-k-2}, z_{half-k-1}, z_{half-k}]
        __m256 v = _mm256_loadu_ps(reinterpret_cast<const float*>(&work_[half - k - 3]));
        // Swap 128-bit lanes: [z_{half-k-1}, z_{half-k}, z_{half-k-3}, z_{half-k-2}]
        __m256 v_swap = _mm256_permute2f128_ps(v, v, 0x01);
        // Permute elements in 128-bit lanes to reverse order: [z_{half-k}, z_{half-k-1}, z_{half-k-2}, z_{half-k-3}]
        __m256 znk = _mm256_permute_ps(v_swap, 0x4E); // 0x4E = 01 00 11 10 in binary

        // Add and subtract z_k and z_nk
        __m256 add = _mm256_add_ps(zk, znk);
        __m256 sub = _mm256_sub_ps(zk, znk);

        // Calculate raw a and b (without 0.5 multiplication to save operations)
        __m256 a_raw = _mm256_blend_ps(add, sub, 0xAA); // 0xAA = 10101010
        __m256 b_raw = _mm256_blend_ps(sub, add, 0xAA);

        // Load twiddle factors w
        __m256 w = _mm256_loadu_ps(reinterpret_cast<const float*>(&twiddles_[r2c_offset + k]));

        // Complex multiply-subtract: out = a - b_prime * w
        // where b_prime = [-b.im, b.re, -b.im, b.re, ...]
        __m256 b_swap = _mm256_permute_ps(b_raw, 0xB1); // Swap real and imaginary
        __m256 sign_mask = _mm256_castsi256_ps(_mm256_setr_epi32(
            0x80000000u, 0, 0x80000000u, 0, 0x80000000u, 0, 0x80000000u, 0));
        __m256 b_prime = _mm256_xor_ps(b_swap, sign_mask);

        __m256 br_prime = _mm256_moveldup_ps(b_prime);
        __m256 bi_prime = _mm256_movehdup_ps(b_prime);
        __m256 w_swap = _mm256_permute_ps(w, 0xB1);

        // Complex multiply using FMA (fmaddsub performs sub/add operation)
        __m256 prod = _mm256_mul_ps(bi_prime, w_swap);
        __m256 t = _mm256_fmaddsub_ps(br_prime, w, prod);

        // Scale the combined result by 0.5 at the end
        __m256 res_raw = _mm256_sub_ps(a_raw, t);
        __m256 res = _mm256_mul_ps(res_raw, _mm256_set1_ps(0.5f));

        _mm256_storeu_ps(reinterpret_cast<float*>(&out[k]), res);
    }

    // Remaining scalar loop
    for (; k < half; ++k)
    {
        Complex32 z_k = work_[k];
        Complex32 z_nk = work_[half - k];

        Complex32 a;
        a.re = 0.5f * (z_k.re + z_nk.re);
        a.im = 0.5f * (z_k.im - z_nk.im);

        Complex32 b;
        b.re = 0.5f * (z_k.re - z_nk.re);
        b.im = 0.5f * (z_k.im + z_nk.im);

        Complex32 w = twiddles_[r2c_offset + k];

        out[k].re = a.re + (w.re * b.im + w.im * b.re);
        out[k].im = a.im - (w.re * b.re - w.im * b.im);
    }
}
