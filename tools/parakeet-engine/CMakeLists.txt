cmake_minimum_required(VERSION 3.13)
project(parakeet-engine LANGUAGES C CXX)
set(CMAKE_CXX_STANDARD 17)

# ── Paths ──
# SHERPA_ONNX_DIR should point to the sherpa-onnx build output directory.
# Set via: cmake -DSHERPA_ONNX_DIR=C:/path/to/sherpa-onnx/build-x64
if(NOT DEFINED SHERPA_ONNX_DIR)
  # Default: assume sherpa-onnx is built alongside in ../sherpa-onnx/build-x64
  set(SHERPA_ONNX_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../sherpa-onnx/build-x64" CACHE PATH "Path to sherpa-onnx build directory")
endif()

message(STATUS "SHERPA_ONNX_DIR = ${SHERPA_ONNX_DIR}")

# ── Find sherpa-onnx include directory ──
# The C API header is in the source tree
set(SHERPA_ONNX_SRC_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../sherpa-onnx")
if(NOT EXISTS "${SHERPA_ONNX_SRC_DIR}/sherpa-onnx/c-api/c-api.h")
  message(FATAL_ERROR "Cannot find sherpa-onnx/c-api/c-api.h in ${SHERPA_ONNX_SRC_DIR}")
endif()

# ── Find sherpa-onnx shared library ──
# The DLL/lib should be in the build output
find_library(SHERPA_ONNX_LIB
  NAMES sherpa-onnx-c-api sherpa-onnx sherpa_onnx_c_api
  PATHS
    "${SHERPA_ONNX_DIR}/lib"
    "${SHERPA_ONNX_DIR}/lib/Release"
    "${SHERPA_ONNX_DIR}/lib/x64/Release"
    "${SHERPA_ONNX_DIR}/bin/Release"
    "${SHERPA_ONNX_DIR}/Release"
  NO_DEFAULT_PATH
)

if(NOT SHERPA_ONNX_LIB)
  message(WARNING "sherpa-onnx library not found in ${SHERPA_ONNX_DIR}. Will try to link by name.")
  set(SHERPA_ONNX_LIB "sherpa-onnx-c-api")
endif()

message(STATUS "sherpa-onnx library: ${SHERPA_ONNX_LIB}")

# ── Build the daemon ──
add_executable(parakeet-engine main.cpp)

target_include_directories(parakeet-engine PRIVATE
  "${SHERPA_ONNX_SRC_DIR}"   # For sherpa-onnx/c-api/c-api.h
)

target_link_libraries(parakeet-engine PRIVATE ${SHERPA_ONNX_LIB})

# Use dynamic CRT (MD) to match the sherpa-onnx build
if(MSVC)
  set_target_properties(parakeet-engine PROPERTIES
    MSVC_RUNTIME_LIBRARY "MultiThreadedDLL"
  )
endif()

# Install target
install(TARGETS parakeet-engine RUNTIME DESTINATION bin)
