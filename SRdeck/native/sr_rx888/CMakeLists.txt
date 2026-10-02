cmake_minimum_required(VERSION 3.20)
project(sr_rx888 LANGUAGES CXX)

if(NOT CMAKE_CONFIGURATION_TYPES AND NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "Build type" FORCE)
endif()

add_library(sr_rx888 SHARED
    src/sr_rx888.cpp
    src/fft_engine.cpp
    src/libusb_api.cpp
    src/fft_ddc_pipeline.cpp
    src/cyusb_backend.cpp
    src/libusb_backend.cpp
)

set_target_properties(sr_rx888 PROPERTIES
    OUTPUT_NAME sr_rx888
)

target_include_directories(sr_rx888
    PUBLIC
        ${CMAKE_CURRENT_SOURCE_DIR}/include
    PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/src
)

target_compile_features(sr_rx888 PRIVATE cxx_std_17)
target_compile_definitions(sr_rx888 PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
target_link_libraries(sr_rx888 PRIVATE setupapi)

if(MSVC)
    target_compile_options(sr_rx888 PRIVATE /EHsc /arch:AVX2
        "$<$<CONFIG:Release>:/O2>"
        "$<$<CONFIG:Release>:/Ot>"
        "$<$<CONFIG:Release>:/Oi>"
        "$<$<CONFIG:Release>:/Gy>")
endif()
