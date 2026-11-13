@echo off
rem Put this file next to FarLoneSails.exe in the FIRST game folder.
rem Starts the game windowed on monitor 1 and hosts automatically.
start "" "%~dp0FarLoneSails.exe" -screen-fullscreen 0 -screen-width 1280 -screen-height 720 -monitor 1 -farcoop-host -farcoop-name Host
