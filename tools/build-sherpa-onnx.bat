@echo off
setlocal EnableDelayedExpansion

set "ARCH=%~1"
if "%ARCH%"=="" set "ARCH=x64"

if /I "%ARCH%"=="x64" (
    set "VCVARS_ARCH=x64"
    set "BUILD_ARCH=x64"
    set "CMAKE_PROCESSOR=AMD64"
    set "CMAKE_PLATFORM=x64"
    set "ENABLE_DIRECTML=ON"
) else if /I "%ARCH%"=="arm64" (
    set "VCVARS_ARCH=x64_arm64"
    set "BUILD_ARCH=arm64"
    set "CMAKE_PROCESSOR=ARM64"
    set "CMAKE_PLATFORM=ARM64"
    set "ENABLE_DIRECTML=OFF"
) else (
    echo ERROR: Unsupported architecture "%ARCH%". Use x64 or arm64.
    exit /b 1
)

set "SCRIPT_DIR=%~dp0"
set "SHERPA_DIR=%SCRIPT_DIR%sherpa-onnx"
set "BUILD_DIR=%SHERPA_DIR%\build-%BUILD_ARCH%"
set "CMAKE_EXE=C:\Program Files\CMake\bin\cmake.exe"
set "NINJA_DIR=%LOCALAPPDATA%\Microsoft\WinGet\Packages\Ninja-build.Ninja_Microsoft.Winget.Source_8wekyb3d8bbwe"
set "PATH=%NINJA_DIR%;%LOCALAPPDATA%\Microsoft\WinGet\Links;%PATH%"

if not exist "%SHERPA_DIR%" (
    echo ERROR: sherpa-onnx source tree not found at:
    echo   %SHERPA_DIR%
    echo Clone https://github.com/k2-fsa/sherpa-onnx into tools\sherpa-onnx first.
    exit /b 1
)

if not exist "%CMAKE_EXE%" (
    echo ERROR: CMake not found at %CMAKE_EXE%
    exit /b 1
)

call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat" %VCVARS_ARCH%
if %ERRORLEVEL% neq 0 (
    echo ERROR: vcvarsall.bat failed for %VCVARS_ARCH%
    echo Install the Visual Studio 2022 C++ %BUILD_ARCH% build tools.
    exit /b 1
)

if /I not "%BUILD_ARCH%"=="arm64" goto AfterArm64ToolchainCheck
if not exist "!VCToolsInstallDir!bin\Hostx64\arm64\cl.exe" goto MissingArm64Compiler
if not exist "!VCToolsInstallDir!lib\arm64\msvcrt.lib" goto MissingArm64Runtime
goto AfterArm64ToolchainCheck

:MissingArm64Compiler
echo ERROR: Visual Studio ARM64 C++ compiler not found.
echo Expected: !VCToolsInstallDir!bin\Hostx64\arm64\cl.exe
echo Install the "MSVC v143 - VS 2022 C++ ARM64 build tools" component.
exit /b 1

:MissingArm64Runtime
echo ERROR: Visual Studio ARM64 C++ runtime libraries not found.
echo Expected: !VCToolsInstallDir!lib\arm64\msvcrt.lib
echo Install the "MSVC v143 - VS 2022 C++ ARM64 build tools" component.
exit /b 1

:AfterArm64ToolchainCheck

:: ===========================================================================
:: PINNED sherpa-onnx VERSION
:: Qwen3-ASR needs >= v1.12.36. Multilingual Nemotron-3.5 needs >= v1.13.3
:: (PR #3671). Pin one tag that covers both: v1.13.3.
::
:: ABI FOOTGUN: the parakeet-engine daemon links sherpa-onnx/c-api/c-api.h from
:: THIS source tree and loads the DLL built from it. Header and DLL MUST come
:: from the exact same tag, or StructLayout misaligns Qwen3/Nemotron config.
::
:: Qwen3 requires an ONNX Runtime that supports ONNX IR v9. If recognizer
:: creation reports "Unsupported model IR version: 9", override sherpa's pinned
:: onnxruntime package with a newer architecture-matching ONNX Runtime build.
:: ===========================================================================
set "SHERPA_TAG=v1.13.3"

if exist "%SHERPA_DIR%\.git" (
    echo Pinning sherpa-onnx to %SHERPA_TAG%...
    git -C "%SHERPA_DIR%" fetch --tags --quiet
    git -C "%SHERPA_DIR%" checkout %SHERPA_TAG%
    if %ERRORLEVEL% neq 0 (
        echo ERROR: failed to checkout sherpa-onnx %SHERPA_TAG%
        exit /b 1
    )
)

echo Cleaning previous %BUILD_ARCH% build...
if exist "%BUILD_DIR%" rmdir /s /q "%BUILD_DIR%"

echo.
echo === CMake Configure: sherpa-onnx %BUILD_ARCH% ===
echo.

"%CMAKE_EXE%" -G Ninja -B "%BUILD_DIR%" -S "%SHERPA_DIR%" ^
  -DCMAKE_BUILD_TYPE=Release ^
  -DCMAKE_SYSTEM_PROCESSOR=%CMAKE_PROCESSOR% ^
  -DCMAKE_VS_PLATFORM_NAME=%CMAKE_PLATFORM% ^
  -DSHERPA_ONNX_ENABLE_DIRECTML=%ENABLE_DIRECTML% ^
  -DSHERPA_ONNX_ENABLE_BINARY=OFF ^
  -DBUILD_SHARED_LIBS=ON ^
  -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreadedDLL ^
  -DSHERPA_ONNX_ENABLE_TTS=OFF ^
  -DSHERPA_ONNX_ENABLE_SPEAKER_DIARIZATION=OFF ^
  -DSHERPA_ONNX_ENABLE_AUDIO_TAGGING=OFF ^
  -DSHERPA_ONNX_ENABLE_PUNCTUATION=OFF ^
  -DSHERPA_ONNX_ENABLE_GPU=OFF
if %ERRORLEVEL% neq 0 (
    echo ERROR: CMake configure failed
    exit /b 1
)

echo.
echo === CMake Build: sherpa-onnx %BUILD_ARCH% ===
echo.
"%CMAKE_EXE%" --build "%BUILD_DIR%" --config Release --parallel
if %ERRORLEVEL% neq 0 (
    echo ERROR: CMake build failed
    exit /b 1
)

echo.
echo === sherpa-onnx %BUILD_ARCH% build complete! ===
