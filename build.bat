@echo off
REM NetControl - one-command build (requires .NET 8 SDK on Windows)
setlocal
cd /d "%~dp0"
echo === Restoring packages ===
dotnet restore src\NetControl\NetControl.csproj || goto :err
echo === Building Release ===
dotnet build src\NetControl\NetControl.csproj -c Release || goto :err
echo === Publishing win-x64 ===
dotnet publish src\NetControl\NetControl.csproj -c Release -r win-x64 --self-contained false -o publish\NetControl-win-x64 || goto :err
echo.
echo DONE. Run: publish\NetControl-win-x64\NetControl.exe
exit /b 0
:err
echo BUILD FAILED
exit /b 1
