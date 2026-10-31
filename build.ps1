# NetControl - one-command build (requires .NET 8 SDK on Windows)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
dotnet restore src/NetControl/NetControl.csproj
dotnet build src/NetControl/NetControl.csproj -c Release
dotnet publish src/NetControl/NetControl.csproj -c Release -r win-x64 --self-contained false -o publish/NetControl-win-x64
Write-Host "DONE. Run: publish/NetControl-win-x64/NetControl.exe"
