@echo off
setlocal
cd /d "%~dp0"
echo ========================================================
echo Trusting AudioStatus Developer Certificate in LocalMachine
echo ========================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -Command "Import-Certificate -FilePath '%~dp0AudioStatusDevCert.cer' -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople'"
if %errorlevel% neq 0 (
    echo.
    echo [ERROR] Failed to import certificate.
    echo Please make sure you right-clicked this file and selected "Run as administrator".
    echo.
    pause
    exit /b %errorlevel%
)

echo.
echo Certificate successfully imported to LocalMachine\TrustedPeople!
echo.
echo Installing AudioStatusExtension for ARM64...
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Arm64.ps1"

echo.
pause
