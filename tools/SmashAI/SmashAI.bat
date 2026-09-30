@echo off
setlocal
cd /d "%~dp0"
python "%~dp0SmashAI-tools\smash_ai_launcher.py"
if errorlevel 1 (
  echo.
  echo SmashSync launcher exited with an error.
  pause
)
endlocal
