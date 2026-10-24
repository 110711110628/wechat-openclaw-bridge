@echo off
if not exist C:\setup\avatar mkdir C:\setup\avatar
schtasks /create /tn wxui /tr "powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File C:\setup\avatar\ui.ps1" /sc once /st 23:59 /f /it
schtasks /query /tn wxui /fo list
