@echo off
setlocal EnableDelayedExpansion
cd /d "%~dp0"

set "OUT=%~dp0Shaders\bytecode"
if not exist "%OUT%" mkdir "%OUT%"

set "FXC="
where fxc >nul 2>&1
if not errorlevel 1 set "FXC=fxc"

if not defined FXC (
    for /d %%K in ("%ProgramFiles(x86)%\Windows Kits\10\bin\10.*") do (
        if exist "%%K\x64\fxc.exe" set "FXC=%%K\x64\fxc.exe"
    )
)
if not defined FXC if exist "%ProgramFiles(x86)%\Windows Kits\10\bin\x64\fxc.exe" set "FXC=%ProgramFiles(x86)%\Windows Kits\10\bin\x64\fxc.exe"

if not defined FXC (
    echo error : fxc.exe not found. Install the Windows 10/11 SDK.
    exit /b 1
)

"%FXC%" /nologo /T vs_5_0 /E VSMain /Fh "%OUT%\FullscreenVs.h" /Vn kFullscreenVsBytecode Shaders\Fullscreen.hlsl
if errorlevel 1 exit /b 1
"%FXC%" /nologo /T ps_5_0 /E PSMain /Fh "%OUT%\MvPs.h" /Vn kMvPsBytecode Shaders\Mv.hlsl
if errorlevel 1 exit /b 1
"%FXC%" /nologo /T ps_5_0 /E PSMain /Fh "%OUT%\MvDilatePs.h" /Vn kMvDilatePsBytecode Shaders\MvDilate.hlsl
if errorlevel 1 exit /b 1
"%FXC%" /nologo /T ps_5_0 /E PSMain /Fh "%OUT%\DepthUpsamplePs.h" /Vn kDepthUpsamplePsBytecode Shaders\DepthUpsample.hlsl
if errorlevel 1 exit /b 1

echo Compiled shader bytecode into Shaders\bytecode
exit /b 0
