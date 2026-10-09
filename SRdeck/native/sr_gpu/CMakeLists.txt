cmake_minimum_required(VERSION 3.20)
project(sr_gpu LANGUAGES CXX)

add_library(sr_gpu SHARED gpufft.cpp gpufft_draw.cpp gpufft_channel.cpp)
target_compile_features(sr_gpu PRIVATE cxx_std_17)
target_compile_definitions(sr_gpu PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
target_link_libraries(sr_gpu PRIVATE d3d11 d3dcompiler dxgi)

# Explicit opt-in target; GPU readback is confined to this test executable.
add_executable(sr_gpu_draw_tests EXCLUDE_FROM_ALL tests/gpudraw_tests.cpp)
target_compile_features(sr_gpu_draw_tests PRIVATE cxx_std_17)
target_compile_definitions(sr_gpu_draw_tests PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
target_link_libraries(sr_gpu_draw_tests PRIVATE d3d11 d3dcompiler dxgi)

# Compares fused FFT stages with the original kernel and verifies embedded bytecode.
add_executable(sr_gpu_fft_tests EXCLUDE_FROM_ALL tests/gpufft_tests.cpp)
target_compile_features(sr_gpu_fft_tests PRIVATE cxx_std_17)
target_compile_definitions(sr_gpu_fft_tests PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
target_link_libraries(sr_gpu_fft_tests PRIVATE d3d11 d3dcompiler dxgi)

add_executable(sr_gpu_fft_tune EXCLUDE_FROM_ALL tests/gpufft_tune.cpp)
target_compile_features(sr_gpu_fft_tune PRIVATE cxx_std_17)
target_compile_definitions(sr_gpu_fft_tune PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
target_link_libraries(sr_gpu_fft_tune PRIVATE d3d11 d3dcompiler dxgi)

# Compile fixed shader variants once at build time; startup only creates GPU objects.
add_executable(sr_gpu_shader_compiler EXCLUDE_FROM_ALL tools/compile_shaders.cpp)
target_compile_features(sr_gpu_shader_compiler PRIVATE cxx_std_17)
target_compile_definitions(sr_gpu_shader_compiler PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
target_link_libraries(sr_gpu_shader_compiler PRIVATE d3dcompiler)
set(shader_header "${CMAKE_CURRENT_BINARY_DIR}/generated/gpufft_precompiled.h")
file(MAKE_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/generated")
add_custom_command(OUTPUT "${shader_header}"
    COMMAND $<TARGET_FILE:sr_gpu_shader_compiler> "${shader_header}"
    DEPENDS sr_gpu_shader_compiler gpufft_shaders.h
    VERBATIM)
add_custom_target(sr_gpu_shader_bytecode DEPENDS "${shader_header}")
foreach(target IN ITEMS sr_gpu sr_gpu_fft_tests sr_gpu_fft_tune sr_gpu_draw_tests)
    add_dependencies(${target} sr_gpu_shader_bytecode)
    target_include_directories(${target} PRIVATE "${CMAKE_CURRENT_BINARY_DIR}/generated")
    target_compile_definitions(${target} PRIVATE SR_GPU_PRECOMPILED)
endforeach()
