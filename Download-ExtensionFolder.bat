@echo off
title JarvisType Extension — Auto Installer
echo.
echo  =============================================
echo   JarvisType Chrome Extension — Auto Install
echo  =============================================
echo.
echo  Downloading extension files...
echo.

:: Download the ZIP using built-in PowerShell (no 3rd party tools needed)
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "Invoke-WebRequest -Uri 'https://github.com/ershivshankar/TestDown/raw/main/jarvistype-chrome-extension.zip' -OutFile '%TEMP%\jarvistype-chrome-extension.zip' -UseBasicParsing"

if errorlevel 1 (
    echo.
    echo  ERROR: Download failed. Check your internet connection.
    pause
    exit /b 1
)

echo  Download complete! Extracting files...
echo.

:: Extract to current folder using built-in PowerShell Expand-Archive
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "Expand-Archive -Path '%TEMP%\jarvistype-chrome-extension.zip' -DestinationPath '%CD%\jarvistype-chrome-extension' -Force"

if errorlevel 1 (
    echo.
    echo  ERROR: Extraction failed.
    pause
    exit /b 1
)

:: Cleanup temp zip
del "%TEMP%\jarvistype-chrome-extension.zip" >nul 2>&1

echo  Done! Folder is ready: jarvistype-chrome-extension
echo.
echo  ============================================
echo   HOW TO LOAD IN CHROME:
echo  ============================================
echo   1. Open Chrome and go to: chrome://extensions
echo   2. Turn ON Developer Mode (top-right toggle)
echo   3. Click "Load unpacked"
echo   4. Select the folder: jarvistype-chrome-extension
echo  ============================================
echo.

:: Open the extracted folder in Windows Explorer
explorer "%CD%\jarvistype-chrome-extension"

echo  Press any key to exit...
pause >nul
