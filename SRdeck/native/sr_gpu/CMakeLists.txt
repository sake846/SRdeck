cmake_minimum_required(VERSION 3.20)
project(sr_gpu LANGUAGES CXX)

add_library(sr_gpu SHARED gpufft.cpp gpufft_draw.cpp gpufft_channel.cpp)
target_compile_features(sr_gpu PRIVATE cxx_std_17)
target_compile_definitions(sr_gpu PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
target_link_libraries(sr_gpu PRIVATE d3d11 d3dcompiler dxgi)
