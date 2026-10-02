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
