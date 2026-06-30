@echo off
setlocal EnableDelayedExpansion

set "ARCH=%~1"
if "%ARCH%"=="" set "ARCH=x64"

if /I "%ARCH%"=="x64" (
    set "VCVARS_ARCH=x64"
    set "BUILD_ARCH=x64"
) else if /I "%ARCH%"=="arm64" (
    set "VCVARS_ARCH=x64_arm64"
    set "BUILD_ARCH=arm64"
) else (
    echo ERROR: Unsupported architecture "%ARCH%". Use x64 or arm64.
    exit /b 1
)

set "SCRIPT_DIR=%~dp0"
set "REPO_ROOT=%SCRIPT_DIR%.."
set "DAEMON_DIR=%SCRIPT_DIR%parakeet-engine"
set "BUILD_DIR=%DAEMON_DIR%\build-%BUILD_ARCH%"
set "CMAKE_EXE=C:\Program Files\CMake\bin\cmake.exe"
set "SHERPA_BUILD_DIR=%SCRIPT_DIR%sherpa-onnx\build-%BUILD_ARCH%"
set "RES_DIR=%REPO_ROOT%\app\windows\HyperWhisper\Resources\parakeet-engine\%BUILD_ARCH%"
set "NINJA_DIR=%LOCALAPPDATA%\Microsoft\WinGet\Packages\Ninja-build.Ninja_Microsoft.Winget.Source_8wekyb3d8bbwe"
set "PATH=%NINJA_DIR%;%PATH%"

if /I "%BUILD_ARCH%"=="arm64" goto BuildManagedArm64

if not exist "%SHERPA_BUILD_DIR%" (
    echo ERROR: sherpa-onnx %BUILD_ARCH% build not found at:
    echo   %SHERPA_BUILD_DIR%
    echo Run tools\build-sherpa-onnx.bat %BUILD_ARCH% first.
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

echo.
echo === Building parakeet-engine daemon: %BUILD_ARCH% ===
echo.

if exist "%BUILD_DIR%" rmdir /s /q "%BUILD_DIR%"

"%CMAKE_EXE%" -G Ninja -B "%BUILD_DIR%" -S "%DAEMON_DIR%" ^
  -DCMAKE_BUILD_TYPE=Release ^
  -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreadedDLL ^
  -DSHERPA_ONNX_DIR="%SHERPA_BUILD_DIR%"
if %ERRORLEVEL% neq 0 (
    echo ERROR: CMake configure failed
    exit /b 1
)

"%CMAKE_EXE%" --build "%BUILD_DIR%" --config Release
if %ERRORLEVEL% neq 0 (
    echo ERROR: CMake build failed
    exit /b 1
)

echo.
echo === parakeet-engine.exe %BUILD_ARCH% build complete! ===
echo.

echo Copying binaries to %RES_DIR%...
if not exist "%RES_DIR%" mkdir "%RES_DIR%"

copy /y "%BUILD_DIR%\parakeet-engine.exe" "%RES_DIR%\" >nul
copy /y "%SHERPA_BUILD_DIR%\bin\sherpa-onnx-c-api.dll" "%RES_DIR%\" >nul

if exist "%SHERPA_BUILD_DIR%\bin\Release\onnxruntime.dll" (
    copy /y "%SHERPA_BUILD_DIR%\bin\Release\onnxruntime.dll" "%RES_DIR%\" >nul
) else if exist "%SHERPA_BUILD_DIR%\bin\onnxruntime.dll" (
    copy /y "%SHERPA_BUILD_DIR%\bin\onnxruntime.dll" "%RES_DIR%\" >nul
) else (
    echo ERROR: onnxruntime.dll was not found in the sherpa-onnx %BUILD_ARCH% build output.
    exit /b 1
)

if exist "%SHERPA_BUILD_DIR%\bin\Release\DirectML.dll" (
    copy /y "%SHERPA_BUILD_DIR%\bin\Release\DirectML.dll" "%RES_DIR%\" >nul
) else if exist "%SHERPA_BUILD_DIR%\bin\DirectML.dll" (
    copy /y "%SHERPA_BUILD_DIR%\bin\DirectML.dll" "%RES_DIR%\" >nul
)

if not exist "%RES_DIR%\silero_vad.onnx" (
    if exist "%REPO_ROOT%\app\windows\HyperWhisper\Resources\parakeet-engine\x64\silero_vad.onnx" (
        copy /y "%REPO_ROOT%\app\windows\HyperWhisper\Resources\parakeet-engine\x64\silero_vad.onnx" "%RES_DIR%\" >nul
    )
)

if not exist "%RES_DIR%\parakeet-engine.exe" (
    echo ERROR: parakeet-engine.exe missing from %RES_DIR%
    exit /b 1
)
if not exist "%RES_DIR%\sherpa-onnx-c-api.dll" (
    echo ERROR: sherpa-onnx-c-api.dll missing from %RES_DIR%
    exit /b 1
)
if not exist "%RES_DIR%\silero_vad.onnx" (
    echo ERROR: silero_vad.onnx missing from %RES_DIR%
    exit /b 1
)

echo.
echo === All %BUILD_ARCH% binaries copied to Resources! ===
dir "%RES_DIR%"
exit /b 0

:BuildManagedArm64
set "MANAGED_DAEMON_DIR=%SCRIPT_DIR%parakeet-engine-dotnet"
set "MANAGED_PUBLISH_DIR=%TEMP%\hyperwhisper-parakeet-engine-arm64-publish"

if not exist "%MANAGED_DAEMON_DIR%\parakeet-engine-dotnet.csproj" (
    echo ERROR: managed ARM64 daemon project not found at:
    echo   %MANAGED_DAEMON_DIR%\parakeet-engine-dotnet.csproj
    exit /b 1
)

where dotnet >nul 2>nul
if %ERRORLEVEL% neq 0 (
    echo ERROR: dotnet SDK not found on PATH.
    exit /b 1
)

echo.
echo === Publishing managed parakeet-engine daemon: arm64 ===
echo.

if exist "%MANAGED_PUBLISH_DIR%" rmdir /s /q "%MANAGED_PUBLISH_DIR%"
dotnet publish "%MANAGED_DAEMON_DIR%\parakeet-engine-dotnet.csproj" -c Release -r win-arm64 --self-contained false -o "%MANAGED_PUBLISH_DIR%"
if %ERRORLEVEL% neq 0 (
    echo ERROR: managed ARM64 daemon publish failed
    exit /b 1
)

echo Copying managed ARM64 binaries to %RES_DIR%...
if exist "%RES_DIR%" rmdir /s /q "%RES_DIR%"
mkdir "%RES_DIR%"
xcopy /y /e /i "%MANAGED_PUBLISH_DIR%\*" "%RES_DIR%\" >nul
if %ERRORLEVEL% neq 0 (
    echo ERROR: failed to copy managed ARM64 daemon output.
    exit /b 1
)

if not exist "%RES_DIR%\silero_vad.onnx" (
    if exist "%REPO_ROOT%\app\windows\HyperWhisper\Resources\parakeet-engine\x64\silero_vad.onnx" (
        copy /y "%REPO_ROOT%\app\windows\HyperWhisper\Resources\parakeet-engine\x64\silero_vad.onnx" "%RES_DIR%\" >nul
    )
)

if not exist "%RES_DIR%\parakeet-engine.exe" (
    echo ERROR: parakeet-engine.exe missing from %RES_DIR%
    exit /b 1
)
if not exist "%RES_DIR%\sherpa-onnx-c-api.dll" (
    echo ERROR: sherpa-onnx-c-api.dll missing from %RES_DIR%
    exit /b 1
)
if not exist "%RES_DIR%\onnxruntime.dll" (
    echo ERROR: onnxruntime.dll missing from %RES_DIR%
    exit /b 1
)
if not exist "%RES_DIR%\silero_vad.onnx" (
    echo ERROR: silero_vad.onnx missing from %RES_DIR%
    exit /b 1
)

echo.
echo === Managed ARM64 binaries copied to Resources! ===
dir "%RES_DIR%"
