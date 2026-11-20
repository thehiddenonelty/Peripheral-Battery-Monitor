@echo off
setlocal DisableDelayedExpansion
pushd "%~dp0" || exit /b 1
if exist "BatteryBar-DeviceEdition-0.1.3.exe" goto launch

set "BATTERY_FRAMEWORK=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%BATTERY_FRAMEWORK%\csc.exe" set "BATTERY_FRAMEWORK=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%BATTERY_FRAMEWORK%\csc.exe" (
    echo Microsoft .NET Framework 4.x compiler was not found.
    goto failed
)

call :find_assembly WindowsBase BATTERY_WINDOWS_BASE
call :find_assembly UIAutomationClient BATTERY_UI_CLIENT
call :find_assembly UIAutomationTypes BATTERY_UI_TYPES
if not defined BATTERY_WINDOWS_BASE goto compatibility
if not defined BATTERY_UI_CLIENT goto compatibility
if not defined BATTERY_UI_TYPES goto compatibility
call :compile /define:BATTERY_UI_AUTOMATION /r:"%BATTERY_WINDOWS_BASE%" /r:"%BATTERY_UI_CLIENT%" /r:"%BATTERY_UI_TYPES%"
if not errorlevel 1 goto check

:compatibility
echo Taskbar alignment is unavailable. Building with the above-taskbar layout.
call :compile
if errorlevel 1 goto failed

:check
start "" /wait "%CD%\BatteryBar-DeviceEdition-build.exe" --self-test
if errorlevel 1 (
    echo Internal checks failed with code %errorlevel%.
    goto failed
)
move /y "BatteryBar-DeviceEdition-build.exe" "BatteryBar-DeviceEdition-0.1.3.exe" >nul
if errorlevel 1 goto failed

:launch
if /i "%~1"=="--diagnose" goto diagnose
if /i "%~1"=="--demo" (
    start "" "%CD%\BatteryBar-DeviceEdition-0.1.3.exe" --demo
) else (
    start "" "%CD%\BatteryBar-DeviceEdition-0.1.3.exe"
)
if errorlevel 1 goto failed
popd
exit /b 0

:diagnose
start "" /wait "%CD%\BatteryBar-DeviceEdition-0.1.3.exe" --diagnose
if errorlevel 1 goto failed
echo Created BatteryBar-Device-Diagnostics.txt in this folder.
pause
popd
exit /b 0

:failed
if exist "BatteryBar-DeviceEdition-build.exe" del "BatteryBar-DeviceEdition-build.exe"
echo.
echo Could not start BatteryBar.
pause
popd
exit /b 1

:compile
"%BATTERY_FRAMEWORK%\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /win32icon:BatteryBar.ico /out:BatteryBar-DeviceEdition-build.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll %* BatteryBar.cs Devices.cs Dashboard.cs Preferences.cs TaskbarDock.cs Typography.cs Discovery.cs StandardHid.cs GearPairProtocols.cs AtkHyperXProtocols.cs BatteryDiagnostics.cs DeviceEditionProtocols.cs ExpandedDeviceProtocols.cs
exit /b %errorlevel%

:find_assembly
set "%~2="
for %%P in ("%BATTERY_FRAMEWORK%\WPF\%~1.dll" "%BATTERY_FRAMEWORK%\%~1.dll") do (
    if exist "%%~P" (
        set "%~2=%%~P"
        exit /b 0
    )
)
for %%G in (GAC_MSIL GAC_64 GAC_32) do (
    for /d %%D in ("%SystemRoot%\Microsoft.NET\assembly\%%G\%~1\v4.0_*") do (
        if exist "%%~D\%~1.dll" (
            set "%~2=%%~D\%~1.dll"
            exit /b 0
        )
    )
)
exit /b 1
