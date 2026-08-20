cmake_minimum_required(VERSION 3.20)
project(sr_fft LANGUAGES CXX)

add_library(sr_fft SHARED cpufft.cpp)
target_compile_features(sr_fft PRIVATE cxx_std_17)
target_compile_definitions(sr_fft PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
if (MSVC)
    target_compile_options(sr_fft PRIVATE /O2 /arch:AVX2)
endif()
