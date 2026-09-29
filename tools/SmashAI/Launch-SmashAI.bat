@echo off
setlocal
set "SMASH_AI_PORT=24872"
echo Smash AI bridge: UDP 127.0.0.1:%SMASH_AI_PORT%
echo Configure the controller slot you want the trainer to override, then launch SSBU.
start "" "%~dp0Ryujinx.exe"
endlocal
