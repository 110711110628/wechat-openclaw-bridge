@echo off
chcp 65001 >nul
REM start-api.cmd - launch the wxapi HTTP service inside the Windows VM.
REM Register it as a scheduled task (name: wxapi) so it survives logoff.
REM
REM WXAPI_TOKEN must match WXAPI_TOKEN in bridge/wxbridge.service.
set DOTNET_ROOT=C:\dotnet10
set WXAPI_TOKEN=CHANGE_ME_WXAPI_TOKEN
set WXAPI_MONITOR_WHO=*
cd /d C:\setup\wxapi
echo ===== START %date% %time% ===== > C:\setup\api.log

REM Optional: some WeChat builds only expose their UIA tree after the window is
REM touched once. If /api/status says online but /api/visible is always empty,
REM build a tiny "warm" console app that instantiates WeChatClient once and run
REM it before the service, like this:
REM echo [1/2] warm >> C:\setup\api.log
REM C:\dotnet10\dotnet.exe C:\setup\wxtest\bin\Release\net10.0-windows\wxtest.dll warm >> C:\setup\api.log 2>&1

echo [2/2] start wxapi service >> C:\setup\api.log
C:\dotnet10\dotnet.exe C:\setup\wxapi\bin\Release\net10.0-windows\wxapi.dll >> C:\setup\api.log 2>&1
echo ===== EXIT %errorlevel% ===== >> C:\setup\api.log
