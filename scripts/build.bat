@echo off
rem Builds both brands into dist\<version>\, asking for the version to publish.
rem Double-click it, or run it from a terminal. Extra arguments go to build.ps1.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
if errorlevel 1 (
    echo.
    echo The build failed.
)
echo.
pause
