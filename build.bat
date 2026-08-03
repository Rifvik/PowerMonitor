@echo off
echo Installing required packages...
pip install -r requirements.txt

echo Building Power Monitor...
python -m PyInstaller --noconsole --onefile --name "PowerMonitor" --icon="icon.ico" --add-data="icon.png;." main.py

echo.
echo Build complete! The executable is located in the 'dist' folder.
pause
