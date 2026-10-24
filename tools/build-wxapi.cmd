@echo off
REM build-wxapi.cmd - stop service, rebuild Release, leave it stopped (restart-wxapi.cmd will start it)
setlocal
set DOTNET_ROOT=C:\dotnet10
for /f "tokens=5" %%p in ('netstat -ano ^| findstr LISTENING ^| findstr :18800') do (
  taskkill /PID %%p /F >> C:\setup\build.log 2>&1
)
schtasks /end /tn wxapi >> C:\setup\build.log 2>&1
cd /d C:\setup\wxapi
echo ===== BUILD %date% %time% ===== > C:\setup\build.log
C:\dotnet10\dotnet.exe build -c Release >> C:\setup\build.log 2>&1
echo BUILD_EXIT=%errorlevel% >> C:\setup\build.log
endlocal
