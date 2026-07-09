@echo off
rem Build and launch D4LootBench. Closes any running instance first: a running app locks
rem the output DLLs, so a plain build compiles fine but keeps the exe on STALE code.
cd /d "%~dp0"
tasklist /fi "imagename eq D4LootBench.exe" | find /i "D4LootBench.exe" >nul && (
    echo Closing the running D4LootBench ^(unsaved planner work is lost - use Save Project first^)...
    taskkill /im D4LootBench.exe /f >nul
    timeout /t 1 /nobreak >nul
)
dotnet build || (echo. & echo Build FAILED - app not started. & pause & exit /b 1)
start "" "src\D4LootBench.App\bin\Debug\net10.0-windows\D4LootBench.exe"
