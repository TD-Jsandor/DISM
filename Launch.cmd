@echo off
cd /d "%~dp0"
dotnet run --project src\DismGui --configuration Release
if errorlevel 1 pause
