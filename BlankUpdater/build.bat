@echo off
setlocal
cd /d "%~dp0"

rem ChilloutVR install path (same default as in BlankUpdater.csproj)
if "%CVRPATH%"=="" set "CVRPATH=D:\SteamLibrary\steamapps\common\ChilloutVR"
set "PLUGINDLL=%CVRPATH%\Plugins\CVRModUpdater.BlankLoader.dll"

echo Building BlankUpdater...
echo.
dotnet build -c Release -p:CVRPATH="%CVRPATH%"
if errorlevel 1 goto failed

if not exist "%PLUGINDLL%" goto missing

echo.
echo ==================================================
echo  Done! CVRModUpdater.BlankLoader.dll is in:
echo  %CVRPATH%\Plugins
echo ==================================================
goto end

:missing
echo.
echo Build finished, but CVRModUpdater.BlankLoader.dll was not found in:
echo %CVRPATH%\Plugins
echo Please check the ChilloutVR path at the top of build.bat.
goto end

:failed
echo.
echo ==================================================
echo  Build FAILED, see the errors above.
echo  Tip: if ChilloutVR is running, close it first,
echo  because the game locks the plugin DLL.
echo ==================================================

:end
echo.
pause
