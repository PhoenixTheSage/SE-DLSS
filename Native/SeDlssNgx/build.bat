@echo off
setlocal EnableDelayedExpansion
cd /d "%~dp0"

set "MSBUILD="

where msbuild >nul 2>&1
if not errorlevel 1 (
    set "MSBUILD=msbuild"
    goto :build
)

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" set "VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" (
    for /f "usebackq tokens=*" %%I in (`"%VSWHERE%" -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do set "MSBUILD=%%I"
)

if not defined MSBUILD if exist "%ProgramFiles%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%ProgramFiles%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
if not defined MSBUILD if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
if not defined MSBUILD if exist "%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"

if not defined MSBUILD (
    echo warning : MSBuild / Visual Studio C++ toolset not found.
    echo warning : Install VS 2022 Build Tools with the Desktop C++ workload, then rebuild this project.
    echo warning : Open Native\SeDlssNgx\SeDlssNgx.vcxproj in Visual Studio and build x64 Release.
    exit /b 0
)

:build
"%MSBUILD%" SeDlssNgx.vcxproj /p:Configuration=Release /p:Platform=x64 /v:m
if errorlevel 1 exit /b 1

echo Native wrapper copied to Assets\SeDlssNgx.dll
exit /b 0
