@echo off
rem Dedicated co-op server (TEST). Runs hidden, nobody plays on it. Stop it with Stop-Server.bat.
rem Log: BepInEx\LogOutput.log in this folder (look for [server] lines).
start "" "%~dp0FarLoneSails.exe" -batchmode -farcoop-dedicated -farcoop-name Server
