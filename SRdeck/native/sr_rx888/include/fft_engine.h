#ifndef SRDECK_SDDC_FFT_ENGINE_H
#define SRDECK_SDDC_FFT_ENGINE_H

#include <cstddef>
#include <cstring>
#include <cstdlib>
#include <malloc.h>
#include <cstdint>

struct Complex32
{
    float re = 0.0f;
    float im = 0.0f;
};

// 32-byte aligned memory buffer class replacing FftwfBuffer
template <typename T>
class AlignedBuffer
{
public:
    AlignedBuffer() = default;

    explicit AlignedBuffer(size_t size)
    {
        Resize(size);
    }

    ~AlignedBuffer()
    {
        Reset();
    }

    AlignedBuffer(const AlignedBuffer&) = delete;
    AlignedBuffer& operator=(const AlignedBuffer&) = delete;

    AlignedBuffer(AlignedBuffer&& other) noexcept
        : data_(other.data_), size_(other.size_)
    {
        other.data_ = nullptr;
        other.size_ = 0;
    }

    AlignedBuffer& operator=(AlignedBuffer&& other) noexcept
    {
        if (this != &other)
        {
            Reset();
            data_ = other.data_;
            size_ = other.size_;
            other.data_ = nullptr;
            other.size_ = 0;
        }
        return *this;
    }

    bool Resize(size_t size)
    {
        if (data_ != nullptr && size_ == size)
        {
            std::memset(data_, 0, sizeof(T) * size_);
            return true;
        }

        Reset();
        if (size == 0)
        {
            return true;
        }

        void* raw = _aligned_malloc(sizeof(T) * size, 32);
        if (raw == nullptr)
        {
            return false;
        }

        data_ = static_cast<T*>(raw);
        size_ = size;
        std::memset(data_, 0, sizeof(T) * size_);
        return true;
    }

    void Reset()
    {
        if (data_ != nullptr)
        {
            _aligned_free(data_);
            data_ = nullptr;
        }
        size_ = 0;
    }

    T* data() { return data_; }
    const T* data() const { return data_; }
    size_t size() const { return size_; }
    T* begin() { return data_; }
    T* end() { return data_ + size_; }
    const T* begin() const { return data_; }
    const T* end() const { return data_ + size_; }
    T& operator[](size_t index) { return data_[index]; }
    const T& operator[](size_t index) const { return data_[index]; }

private:
    T* data_ = nullptr;
    size_t size_ = 0;
};

class FftEngine
{
public:
    enum class Direction
    {
        Forward = -1,
        Backward = 1
    };

    FftEngine(size_t size, Direction dir);
    ~FftEngine() = default;

    // Complex-to-Complex FFT/IFFT (Out-of-place)
    // "in" and "out" must be 32-byte aligned and size == size_.
    void Execute(const Complex32* in, Complex32* out);

    // Real-to-Complex (R2C) FFT (Out-of-place)
    // Only supported when size_ is 8192.
    // "in" is 8192 floats, "out" is 4097 Complex32.
    void ExecuteR2c(const float* in, Complex32* out);

private:
    size_t size_;
    Direction dir_;

    // Twiddle factors table
    AlignedBuffer<Complex32> twiddles_;

    // Work buffer for FFT operations
    AlignedBuffer<Complex32> work_;

    // Bit reversal tables
    AlignedBuffer<uint16_t> bit_rev_;
    AlignedBuffer<uint16_t> bit_rev_half_;

    void InitTwiddles();
    void InitBitReversal();
    void ExecuteCooleyTukey(Complex32* out, size_t N);
};

#endif // SRDECK_SDDC_FFT_ENGINE_H
