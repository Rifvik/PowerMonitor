@echo off
:: This script restarts itself with administrative privileges to allow the Power Monitor
:: to read low-level hardware sensors (WMI and battery stats) properly.

>nul 2>&1 "%SYSTEMROOT%\system32\cacls.exe" "%SYSTEMROOT%\system32\config\system"
if '%errorlevel%' NEQ '0' (
    echo Requesting administrative privileges...
    goto UACPrompt
) else ( goto gotAdmin )

:UACPrompt
    echo Set UAC = CreateObject^("Shell.Application"^) > "%temp%\getadmin.vbs"
    set params= %*
    echo UAC.ShellExecute "cmd.exe", "/c ""%~s0"" %params%", "", "runas", 1 >> "%temp%\getadmin.vbs"
    "%temp%\getadmin.vbs"
    del "%temp%\getadmin.vbs"
    exit /B

:gotAdmin
    pushd "%CD%"
    CD /D "%~dp0"
    
echo Admin rights granted. Starting Power Monitor...
if exist "dist\PowerMonitor.exe" (
    start "" "dist\PowerMonitor.exe"
) else (
    echo Executable not found. Please run build.bat first!
    pause
)
