@echo off
rem Dedicated co-op server (TEST) in a small window - use this if Test-Server.bat does not start.
start "" "%~dp0FarLoneSails.exe" -screen-fullscreen 0 -screen-width 480 -screen-height 270 -farcoop-dedicated -farcoop-name Server
