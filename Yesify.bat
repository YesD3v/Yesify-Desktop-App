@echo off
rem Launches Yesify: the published exe if it exists, otherwise builds and runs from source.
cd /d "%~dp0"

rem A newer build waiting in publish-update gets swapped in. The running Yesify (even one closed to the
rem tray) is asked to quit first; if it doesn't within a few seconds, it's closed.
if not exist "publish-update\Yesify.exe" goto launch
tasklist /FI "IMAGENAME eq Yesify.exe" 2>nul | find /I "Yesify.exe" >nul || goto swap
echo Updating Yesify...
if exist "publish\Yesify.exe" "publish\Yesify.exe" --exit
set tries=0
:waitexit
tasklist /FI "IMAGENAME eq Yesify.exe" 2>nul | find /I "Yesify.exe" >nul || goto swap
set /a tries+=1
if %tries% geq 5 (
    taskkill /F /IM Yesify.exe >nul 2>&1
    ping -n 2 127.0.0.1 >nul
    goto swap
)
ping -n 2 127.0.0.1 >nul
goto waitexit

:swap
move /y "publish-update\Yesify.exe" "publish\Yesify.exe" >nul 2>&1 && rmdir /s /q "publish-update"
if exist "publish-update\Yesify.exe" echo Couldn't update (Yesify is still running). Starting the current version.

:launch
if exist "publish\Yesify.exe" (
    start "" "publish\Yesify.exe" %*
) else (
    dotnet run -c Release -- %*
)
