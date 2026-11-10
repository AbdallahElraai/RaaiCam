# RaaiCam Phase 0 spike: virtual camera media source DLL + registrar + capture probe.
cmake_minimum_required(VERSION 3.24)
project(RaaiCamVCam LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>") # static CRT: nothing extra for Frame Server to load

# WIL (header-only), same version as VCamSample's packages.config. Only fetched, not built.
include(FetchContent)
FetchContent_Declare(wil
  GIT_REPOSITORY https://github.com/microsoft/wil.git
  GIT_TAG v1.0.260126.7
  GIT_SHALLOW TRUE
  SOURCE_SUBDIR do-not-add)
FetchContent_MakeAvailable(wil)

add_compile_definitions(UNICODE _UNICODE NOMINMAX)
add_compile_options(/W3 /permissive- /Zc:__cplusplus /EHsc /utf-8 /bigobj)

add_library(RaaiCamVCam SHARED
  src/dllmain.cpp
  src/Activator.cpp
  src/MediaSource.cpp
  src/MediaStream.cpp
  src/FrameSource.cpp
  src/Log.cpp
  src/Tools.cpp
  src/MFTools.cpp
  src/EnumNames.cpp
  src/WinTrace.cpp
  src/RaaiCamVCam.def)
target_include_directories(RaaiCamVCam PRIVATE src ${wil_SOURCE_DIR}/include)
target_link_libraries(RaaiCamVCam PRIVATE mfplat mfuuid mf mfsensorgroup advapi32 ole32 propsys runtimeobject)

add_executable(registrar registrar/main.cpp)
add_executable(probe probe/main.cpp)

# keep all three side by side (registrar resolves the DLL from its own folder)
set_target_properties(RaaiCamVCam registrar probe PROPERTIES
  RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin
  LIBRARY_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin)
