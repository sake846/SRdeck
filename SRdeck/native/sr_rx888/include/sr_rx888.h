#ifndef SRDECK_RX888_H
#define SRDECK_RX888_H

#include <stdint.h>

#ifdef _WIN32
#define RX888_API __declspec(dllexport)
#else
#define RX888_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct rx888 rx888_t;

struct rx888_device_info
{
    const char* manufacturer;
    const char* product;
    const char* serial_number;
};

typedef enum RX888Status
{
    RX888_STATUS_OFF = 0,
    RX888_STATUS_READY = 1,
    RX888_STATUS_STREAMING = 2,
    RX888_STATUS_FAILED = 0xff
} RX888Status;

typedef enum RFMode
{
    NO_RF_MODE = 0,
    HF_MODE = 1,
    VHF_MODE = 2
} RFMode;

typedef void (*rx888_read_async_cb_t)(uint32_t data_size, void* data, void* context);

RX888_API int rx888_get_device_count(void);
RX888_API int rx888_get_device_info(struct rx888_device_info** infos);
RX888_API int rx888_free_device_info(struct rx888_device_info* infos);

RX888_API rx888_t* rx888_open(int index, const char* imagefile);
RX888_API void rx888_close(rx888_t* handle);
RX888_API const char* rx888_get_backend_name(rx888_t* handle);

RX888_API int rx888_set_sample_rate(rx888_t* handle, double sample_rate);
RX888_API int rx888_set_adc_frequency(rx888_t* handle, double adc_frequency);
RX888_API int rx888_set_rf_mode(rx888_t* handle, RFMode rf_mode);
RX888_API int rx888_set_tuner_frequency(rx888_t* handle, double frequency);
RX888_API int rx888_set_tuner_rf_attenuation(rx888_t* handle, double attenuation);
RX888_API int rx888_set_tuner_if_attenuation(rx888_t* handle, double attenuation);
RX888_API int rx888_set_adc_dither(rx888_t* handle, int dither);
RX888_API int rx888_set_adc_random(rx888_t* handle, int random);
RX888_API int rx888_set_vhf_bias(rx888_t* handle, int bias);
RX888_API int rx888_set_hf_bias(rx888_t* handle, int bias);
RX888_API int rx888_set_hf_attenuation(rx888_t* handle, double attenuation);

RX888_API int rx888_set_async_params(
    rx888_t* handle,
    uint32_t frame_size,
    uint32_t num_frames,
    rx888_read_async_cb_t callback,
    void* callback_context);

RX888_API int rx888_start_streaming(rx888_t* handle);
RX888_API int rx888_handle_events(rx888_t* handle);
RX888_API int rx888_stop_streaming(rx888_t* handle);

#ifdef __cplusplus
}
#endif

#endif
