@echo off
setlocal enabledelayedexpansion
title RamDrive Setup

:: ============================================================
::  Check for administrator privileges
:: ============================================================
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo.
    echo  ERROR: This script requires administrator privileges.
    echo.
    echo  Please right-click Setup.bat and select "Run as administrator".
    echo.
    pause
    exit /b 1
)

echo.
echo  RamDrive Setup
echo  ==============
echo.

:: ============================================================
::  1. Check if WinFsp is already installed
:: ============================================================
set "WINFSP_KEY=HKLM\SOFTWARE\WOW6432Node\WinFsp"

reg query "%WINFSP_KEY%" /v InstallDir >nul 2>&1
if %errorlevel% equ 0 (
    echo  [OK] WinFsp is already installed.
    goto :configure
)

:: ============================================================
::  2. Download WinFsp MSI
:: ============================================================
set "WINFSP_VERSION=2.1.25079"
set "WINFSP_MSI=winfsp-%WINFSP_VERSION%.msi"
set "WINFSP_URL=https://github.com/winfsp/winfsp/releases/download/v2.1/%WINFSP_MSI%"
set "WINFSP_PATH=%TEMP%\%WINFSP_MSI%"

echo  [..] Downloading WinFsp %WINFSP_VERSION%...

:: Try PowerShell download
powershell -NoProfile -Command "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; Invoke-WebRequest -Uri '%WINFSP_URL%' -OutFile '%WINFSP_PATH%'" 2>nul
if not exist "%WINFSP_PATH%" (
    :: Fallback to curl
    curl -fSL -o "%WINFSP_PATH%" "%WINFSP_URL%" 2>nul
)

if not exist "%WINFSP_PATH%" (
    echo  [FAIL] Could not download WinFsp.
    echo         Please install manually from: https://winfsp.dev/rel/
    pause
    exit /b 1
)

echo  [OK] Downloaded WinFsp.

:: ============================================================
::  3. Silent install WinFsp (core only, no reboot)
:: ============================================================
echo  [..] Installing WinFsp...
msiexec /i "%WINFSP_PATH%" /qn /norestart
if %errorlevel% neq 0 (
    echo  [FAIL] WinFsp installation failed (error %errorlevel%).
    pause
    exit /b 1
)

echo  [OK] WinFsp installed successfully.

:: Clean up downloaded MSI
del "%WINFSP_PATH%" >nul 2>&1

:: ============================================================
::  4. Configure MountUseMountmgrFromFSD
:: ============================================================
:configure
echo  [..] Configuring WinFsp Mount Manager...
reg add "%WINFSP_KEY%" /v MountUseMountmgrFromFSD /t REG_DWORD /d 1 /f >nul 2>&1
if %errorlevel% equ 0 (
    echo  [OK] Mount Manager configured.
) else (
    echo  [WARN] Could not set registry value. Drive may not be visible to disk benchmark tools.
)

:: ============================================================
::  Done
:: ============================================================
echo.
echo  Setup complete! You can now run RamDrive.exe (no admin required).
echo.
pause
