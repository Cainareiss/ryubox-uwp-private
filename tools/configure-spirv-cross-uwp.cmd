@echo off
setlocal

call "%~dp0..\artifacts\vs-buildtools\VC\Auxiliary\Build\vcvars64.bat"
if errorlevel 1 exit /b %errorlevel%

set "RYUJINX_ROOT=%~dp0.."
set "TEMP=%RYUJINX_ROOT%\.cache\temp"
set "TMP=%TEMP%"
if not exist "%TEMP%" mkdir "%TEMP%"

cmake -S "%RYUJINX_ROOT%\third_party\SPIRV-Cross" ^
      -B "%RYUJINX_ROOT%\third_party\SPIRV-Cross\build-uwp-x64-nmake" ^
      -G "NMake Makefiles" ^
      -DCMAKE_SYSTEM_NAME=WindowsStore ^
      -DCMAKE_SYSTEM_VERSION=10.0 ^
      -DCMAKE_BUILD_TYPE=Release ^
      -DSPIRV_CROSS_SHARED=ON ^
      -DSPIRV_CROSS_STATIC=OFF ^
      -DSPIRV_CROSS_CLI=OFF ^
      -DSPIRV_CROSS_ENABLE_TESTS=OFF ^
      -DSPIRV_CROSS_ENABLE_GLSL=ON ^
      -DSPIRV_CROSS_ENABLE_HLSL=ON ^
      -DSPIRV_CROSS_ENABLE_MSL=OFF ^
      -DSPIRV_CROSS_ENABLE_CPP=OFF ^
      -DSPIRV_CROSS_ENABLE_REFLECT=OFF ^
      -DSPIRV_CROSS_ENABLE_UTIL=OFF ^
      -DSPIRV_CROSS_SKIP_INSTALL=ON

exit /b %errorlevel%
