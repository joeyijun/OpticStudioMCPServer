@echo off
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0Run-ZrdDiagnostics.ps1"
if errorlevel 1 echo Diagnostic failed. Preserve the JSON report and temporary model.
pause
