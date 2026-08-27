@echo off
setlocal enabledelayedexpansion

REM Parameters: NAME SOURCE [TFM] [PULSAR_OR_BIN64]
if "%~2" == "" (
    echo error : Deploy.bat missing required parameters
    exit /b 1
)

REM Extract parameters and remove quotes
set "NAME=%~1"
set "SOURCE=%~2"
set "TFM=%~3"
set "HINT=%~4"

REM Remove trailing backslash so quoted paths do not swallow the next argument
if "%SOURCE:~-1%"=="\" set "SOURCE=%SOURCE:~0,-1%"
if not "%HINT%"=="" if "%HINT:~-1%"=="\" set "HINT=%HINT:~0,-1%"

REM Resolve the built assembly
set "SRCFILE=%SOURCE%\%NAME%"
if not exist "%SRCFILE%" (
    echo error : Source not found: %SRCFILE%
    exit /b 1
)

REM Route by target framework:
REM   net4x  (.NET Framework) -> Pulsar\Legacy\Local
REM   others (.NET 5+)        -> Pulsar\Interim\Local (only if that edition folder exists)
set "EDITION=Interim"
echo(%TFM% | findstr /b /i "net4" >nul && set "EDITION=Legacy"
if "%TFM%"=="" set "EDITION=Legacy"

if defined PULSAR_LOCAL_DIR (
    set "PLUGIN_DIR=%PULSAR_LOCAL_DIR%"
    if not exist "!PLUGIN_DIR!" mkdir "!PLUGIN_DIR!"
    goto :copy
)

REM Locate the Pulsar install: env, %AppData%\Pulsar, or next to Space Engineers.
set "PULSAR="
if defined PULSAR_HOME if exist "%PULSAR_HOME%\Legacy.exe" set "PULSAR=%PULSAR_HOME%"

if not defined PULSAR if exist "%AppData%\Pulsar\Legacy.exe" set "PULSAR=%AppData%\Pulsar"
if not defined PULSAR if exist "%AppData%\Pulsar\Legacy\Local" set "PULSAR=%AppData%\Pulsar"

if not defined PULSAR if not "%HINT%"=="" (
    if exist "%HINT%\Legacy.exe" set "PULSAR=%HINT%"
    if not defined PULSAR if exist "%HINT%\Pulsar\Legacy.exe" set "PULSAR=%HINT%\Pulsar"
    if not defined PULSAR if exist "%HINT%\..\Pulsar\Legacy.exe" (
        for %%I in ("%HINT%\..\Pulsar") do set "PULSAR=%%~fI"
    )
)

if not defined PULSAR (
    echo warning : Pulsar not found, skipping %TFM% deploy of %NAME%.
    echo warning : Install Pulsar, or set PULSAR_HOME / PULSAR_LOCAL_DIR. Default is %%AppData%%\Pulsar; game-folder installs are also detected from Bin64.
    exit /b 0
)

if /i "%EDITION%"=="Interim" (
    REM Only deploy the .NET build when the Interim Pulsar edition folder exists
    if not exist "%PULSAR%\Interim" (
        echo warning : Pulsar Interim not installed, skipping %TFM% deploy: %PULSAR%\Interim
        exit /b 0
    )
    set "PLUGIN_DIR=%PULSAR%\Interim\Local"
) else (
    set "PLUGIN_DIR=%PULSAR%\Legacy\Local"
)

if not exist "!PLUGIN_DIR!" mkdir "!PLUGIN_DIR!"

:copy
echo Copying "%SRCFILE%" to "!PLUGIN_DIR!\"
copy /y "%SRCFILE%" "!PLUGIN_DIR!\"
if !ERRORLEVEL! NEQ 0 (
    echo error : Could not copy "%NAME%", make sure the game does not run and try again.
    exit /b 1
)

exit /b 0
