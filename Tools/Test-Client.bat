@echo off
rem Put this file next to FarLoneSails.exe in a SECOND copy of the game folder.
rem Starts the game windowed on monitor 2 and joins the host on this PC.
start "" "%~dp0FarLoneSails.exe" -screen-fullscreen 0 -screen-width 1280 -screen-height 720 -monitor 2 -farcoop-join 127.0.0.1:7777 -farcoop-name Client
