@echo off
REM restart-wxapi.cmd - force kill a wedged wxapi and relaunch it (no rebuild needed)
setlocal
set PORT=18800
echo [%date% %time%] restart-wxapi start >> C:\setup\wxapi-restart.log

for /f "tokens=5" %%p in ('netstat -ano ^| findstr LISTENING ^| findstr :%PORT%') do (
  echo [%date% %time%] kill PID %%p >> C:\setup\wxapi-restart.log
  taskkill /PID %%p /F >> C:\setup\wxapi-restart.log 2>&1
)

schtasks /end /tn wxapi >> C:\setup\wxapi-restart.log 2>&1
timeout /t 3 /nobreak >nul
schtasks /run /tn wxapi >> C:\setup\wxapi-restart.log 2>&1
echo [%date% %time%] restart-wxapi done >> C:\setup\wxapi-restart.log
endlocal
