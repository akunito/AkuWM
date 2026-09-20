@echo off
REM Put the desk back.
REM
REM Double-click this when windows have gone missing or ended up somewhere
REM they should not be. It stops AkuWM -- asking first, then not asking --
REM and gives back every window AkuWM hid or moved, working from the records
REM on disk rather than from the program that made the mess.
REM
REM It cannot make things worse: it only ever shows windows and puts them
REM back where they were. If even this will not run, restart the machine:
REM nothing of AkuWM is in the Startup folder, so a reboot comes up on the
REM old stack with every window visible.

setlocal
set AKUWM=%ProgramFiles%\AkuWM\akuwm.exe

if not exist "%AKUWM%" (
    echo AkuWM is not installed at "%AKUWM%".
    echo Nothing to rescue from. Restart the machine if the desk still looks wrong.
    pause
    exit /b 1
)

echo Putting the desk back...
echo.

REM A uiAccess process cannot have its output redirected by the caller, so it
REM writes the result itself and this reads the file.
"%AKUWM%" rescue --out "%TEMP%\akuwm-rescue.txt"
if exist "%TEMP%\akuwm-rescue.txt" type "%TEMP%\akuwm-rescue.txt"

echo.
echo If windows are still missing, run this again with --all:
echo     "%AKUWM%" rescue --all
echo (that shows every hidden window on the desk, including any another
echo  window manager is deliberately keeping out of sight).
echo.
pause
