@echo off
echo Installing required packages...
pip install -r requirements.txt

echo Building Power Monitor...
python -m PyInstaller --noconsole --onefile --uac-admin --name "PowerMonitor" --icon="icon.ico" --add-data="icon.png;." --add-data="LibreHardwareMonitorLib.dll;." --add-data="System.Memory.dll;." --add-data="System.Buffers.dll;." --add-data="System.Numerics.Vectors.dll;." --add-data="System.Runtime.CompilerServices.Unsafe.dll;." --add-data="HidSharp.dll;." main.py

echo.
echo Build complete! The executable is located in the 'dist' folder.
pause
