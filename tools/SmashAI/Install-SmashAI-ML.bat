@echo off
setlocal
cd /d "%~dp0"
echo Installing SmashSync ML dependencies...
echo.
python -m pip install --upgrade pip
if errorlevel 1 goto :fail
python -m pip install torch-directml
if errorlevel 1 goto :fail
echo.
echo DirectML/PyTorch installation complete.
echo You can now use Train and Play with AI from the SmashSync launcher.
pause
exit /b 0

:fail
echo.
echo Dependency installation failed.
echo Check that Python and pip are installed, then retry.
pause
exit /b 1
