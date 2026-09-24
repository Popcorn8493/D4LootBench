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
rem Ask MSBuild for the output folder instead of hardcoding the TFM (a stale folder from an
rem older TFM would otherwise launch old code).
set "OUTDIR="
for /f "delims=" %%d in ('dotnet msbuild src\D4LootBench.App\D4LootBench.App.csproj -nologo -getProperty:TargetDir') do set "OUTDIR=%%d"
if exist "%OUTDIR%D4LootBench.exe" goto :launch
echo Could not find D4LootBench.exe under "%OUTDIR%".
pause
exit /b 1
:launch
start "" "%OUTDIR%D4LootBench.exe"
