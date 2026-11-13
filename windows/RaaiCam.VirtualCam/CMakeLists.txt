# RaaiCam production virtual camera: media source DLL (RaaiCamVCam) + pure-logic unit tests (vcam_tests).
cmake_minimum_required(VERSION 3.24)
project(RaaiCamVirtualCam LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>") # static CRT: nothing extra for Frame Server to load

include(FetchContent)
FetchContent_Declare(doctest
  GIT_REPOSITORY https://github.com/doctest/doctest.git
  GIT_TAG ae7a13539fb71f270b87eb2e874fbac80bc8dda2 # v2.4.11
  GIT_SHALLOW TRUE
  SOURCE_SUBDIR do-not-add)
# WIL (header-only), same version as the Phase 0 spike / VCamSample. Only fetched, not built.
FetchContent_Declare(wil
  GIT_REPOSITORY https://github.com/microsoft/wil.git
  GIT_TAG cbf677fb0a942557d08fd129f4c106a76247b2ec # v1.0.260126.7
  GIT_SHALLOW TRUE
  SOURCE_SUBDIR do-not-add)
FetchContent_MakeAvailable(doctest wil)

add_compile_definitions(UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN _WIN32_WINNT=0x0A00 WINVER=0x0A00)
add_compile_options(/W4 /WX /external:W0 /permissive- /Zc:__cplusplus /EHsc /utf-8 /bigobj)

# Pure logic shared by the DLL and the tests.
set(VCAM_CORE_SOURCES
  src/FrameReader.cpp
  src/Security.cpp
  src/PlaceholderCard.cpp)

add_executable(vcam_tests
  ${VCAM_CORE_SOURCES}
  tests/vcam_tests.cpp)
target_include_directories(vcam_tests PRIVATE src)
# third-party headers are system includes: our /W4 /WX applies to our code only
target_include_directories(vcam_tests SYSTEM PRIVATE ${doctest_SOURCE_DIR})
target_link_libraries(vcam_tests PRIVATE advapi32)

# Media source DLL loaded by the Windows Frame Server service.
add_library(RaaiCamVCam SHARED
  ${VCAM_CORE_SOURCES}
  src/dllmain.cpp
  src/Activator.cpp
  src/MediaSource.cpp
  src/MediaStream.cpp
  src/DeliveryClock.cpp
  src/SharedRegion.cpp
  src/Log.cpp
  src/Tools.cpp
  src/MFTools.cpp
  src/EnumNames.cpp
  src/WinTrace.cpp
  src/RaaiCamVCam.def)
target_include_directories(RaaiCamVCam PRIVATE src)
target_include_directories(RaaiCamVCam SYSTEM PRIVATE ${wil_SOURCE_DIR}/include)
# C++/WinRT and SDK headers come from the INCLUDE environment of the dev shell: treat them as external.
target_compile_options(RaaiCamVCam PRIVATE /external:env:INCLUDE /external:anglebrackets)
target_link_libraries(RaaiCamVCam PRIVATE mfplat mfuuid mfsensorgroup advapi32 ole32 propsys runtimeobject)

set_target_properties(vcam_tests RaaiCamVCam PROPERTIES
  RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin
  LIBRARY_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin)

# Control CLI: create / remove / status for the virtual camera (shares only Ids.h with the DLL).
add_executable(vcamctl vcamctl/main.cpp)
target_include_directories(vcamctl PRIVATE src)
target_link_libraries(vcamctl PRIVATE mf mfplat mfuuid mfsensorgroup advapi32 ole32)
set_target_properties(vcamctl PROPERTIES RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin)

# Headless capture probe: classifies card / live / torn frames from the installed camera.
add_executable(vcam-probe probe/main.cpp)
target_include_directories(vcam-probe PRIVATE src)
target_link_libraries(vcam-probe PRIVATE mf mfplat mfreadwrite mfuuid ole32)
set_target_properties(vcam-probe PROPERTIES RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin)
