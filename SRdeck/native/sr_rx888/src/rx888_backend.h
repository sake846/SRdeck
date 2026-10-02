#ifndef RX888_BACKEND_H
#define RX888_BACKEND_H

#include "sr_rx888.h"
#include <string>

class Rx888Backend
{
public:
    virtual ~Rx888Backend() = default;

    virtual const char* GetBackendName() const = 0;
    virtual bool Open(int index, const std::string& imageFile) = 0;
    virtual void Close() = 0;
    virtual int SetSampleRate(double sampleRate) = 0;
    virtual int SetAdcFrequency(double adcFrequency) = 0;
    virtual int SetRfMode(RFMode rfMode) = 0;
    virtual int SetTunerFrequency(double frequency) = 0;
    virtual int SetTunerRfAttenuation(double attenuation) = 0;
    virtual int SetTunerIfAttenuation(double attenuation) = 0;
    virtual int SetAdcDither(int dither) = 0;
    virtual int SetAdcRandom(int random) = 0;
    virtual int SetVhfBias(int bias) = 0;
    virtual int SetHfBias(int bias) = 0;
    virtual int SetHfAttenuation(double attenuation) = 0;

    virtual int ConfigureAsync(
        uint32_t frameSize,
        uint32_t numFrames,
        rx888_read_async_cb_t callback,
        void* callbackContext) = 0;

    virtual int StartStreaming() = 0;
    virtual int HandleEvents() = 0;
    virtual int StopStreaming() = 0;
};

#endif // RX888_BACKEND_H
