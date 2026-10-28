@echo off
rem ============================================================
rem  SchitayKa (СчитайКа) - one-time setup (run this ONCE after git pull)
rem  After that, just press F5 in Visual Studio. It will:
rem    - restore NuGet packages automatically
rem    - build the frontend automatically (npm run build)
rem    - start server and open browser at http://localhost:5023
rem ============================================================
setlocal
title SchitayKa Setup

echo [1/4] Checking Node.js...
where npm >nul 2>&1
if errorlevel 1 (
    echo.
    echo [ERROR] Node.js not found.
    echo Install LTS from https://nodejs.org , then RE-OPEN this script.
    pause
    exit /b 1
)
for /f "delims=" %%v in ('node --version') do echo       Node.js OK: %%v

echo [2/4] Checking .NET SDK...
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ERROR] .NET SDK not found. Install .NET 8 SDK or use Visual Studio.
    pause
    exit /b 1
)
for /f "delims=" %%v in ('dotnet --version') do echo       .NET OK: %%v

echo [3/4] Installing frontend dependencies (npm install)...
pushd "%~dp0frontend"
call npm install
if errorlevel 1 (
    echo [ERROR] npm install failed. See messages above.
    popd
    pause
    exit /b 1
)

echo [4/4] Building frontend (npm run build)...
call npm run build
if errorlevel 1 (
    echo [ERROR] npm run build failed. See messages above.
    popd
    pause
    exit /b 1
)
popd

if exist "%~dp0backend\wwwroot\index.html" (
    echo.
    echo ============================================================
    echo  SUCCESS! Frontend is ready: backend\wwwroot\index.html
    echo  Now open SchitayKa.sln in Visual Studio and press F5.
    echo  From now on VS builds everything automatically.
    echo ============================================================
) else (
    echo [ERROR] Build finished but backend\wwwroot\index.html missing.
)
pause
