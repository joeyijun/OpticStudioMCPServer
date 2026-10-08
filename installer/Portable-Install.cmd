@echo off
setlocal

set "SOURCE=%~dp0"
set "SOURCE_DIR=%SOURCE:~0,-1%"
set "TARGET=%LOCALAPPDATA%\ZemaxMCP"

if /I "%SOURCE_DIR%"=="%TARGET%" goto launch_installed

if exist "%TARGET%\Start-Zemax-MCP.exe" (
  if exist "%SOURCE%ZemaxMCP.Updater.exe" (
    echo Updating the installed copy with rollback protection...
    "%SOURCE%ZemaxMCP.Updater.exe" --staging "%SOURCE_DIR%" --install "%TARGET%" --parent-pid 0 --restart false
    if not errorlevel 1 goto launch_installed
    echo.
    echo Installed update failed. See %%LOCALAPPDATA%%\ZemaxMCP\update.log.
    echo Launching the extracted package explicitly in portable mode instead.
    goto launch_portable
  )
)

if not exist "%TARGET%" mkdir "%TARGET%"
robocopy "%SOURCE%" "%TARGET%" /E /XD logs snapshots shortcut-icons /XF Install.exe Portable-Install.cmd release.zip release-manifest.json launcher-settings.json launcher-settings.json.bak clients.json update.log .update.lock >nul
set "COPY_RESULT=%ERRORLEVEL%"
if %COPY_RESULT% LSS 8 goto launch_installed

echo.
echo Copy to the local installation failed with robocopy code %COPY_RESULT%.
echo Launching the extracted package explicitly in portable mode.
goto launch_portable

:launch_installed
if exist "%TARGET%\Start-Zemax-MCP.exe" (
  start "" "%TARGET%\Start-Zemax-MCP.exe"
  endlocal
  exit /b 0
)

:launch_portable
if exist "%SOURCE%Start-Zemax-MCP.exe" (
  start "" "%SOURCE%Start-Zemax-MCP.exe"
  endlocal
  exit /b 0
)

echo Could not find Start-Zemax-MCP.exe.
echo Ensure every file in ZemaxMCP-win-x64.zip was extracted to one folder.
pause
endlocal
exit /b 1
