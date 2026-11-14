@echo off
rem Stops the hidden test server (FarLoneSails.exe without a window). Normal game windows stay open.
powershell -NoProfile -Command "Get-Process FarLoneSails -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -eq 0 } | Stop-Process"
