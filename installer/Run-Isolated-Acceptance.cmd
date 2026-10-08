@echo off
echo Zemax MCP isolated acceptance - run on the OpticStudio computer.
echo This starts separate loopback test services. Your normal service is not stopped.
echo Only the checked test Worker may be terminated during recovery testing.
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-IsolatedAcceptance.ps1" -PackageRoot "%~dp0runtime" -AllowWorkerTermination
set testResult=%ERRORLEVEL%
echo.
if not "%testResult%"=="0" echo Acceptance failed. Keep the results folder and logs for diagnosis.
pause
exit /b %testResult%
