@echo off
setlocal EnableDelayedExpansion

rem Dev workflow: fetch origin -> merge origin/master into current branch
rem -> rebuild Debug -> start server (foreground, press q to quit).
rem Same usage as start-server.bat.

set "ROOT=%~dp0"
cd /d "%ROOT%"

set "SERVER_IP=%SERVER_IP%"
set "EXTRA_ARGS="

:parse_args
if "%~1"=="" goto after_parse
if /I "%~1"=="--server-ip" (
  set "SERVER_IP=%~2"
  shift
  shift
  goto parse_args
)
if /I "%~1"=="--help" (
  call :usage
  exit /b 0
)
set "EXTRA_ARGS=!EXTRA_ARGS! %~1"
shift
goto parse_args

:after_parse
if not defined SERVER_IP set "SERVER_IP=127.0.0.1"

echo [1/4] Fetching origin...
git fetch origin
if errorlevel 1 (
  echo ERROR: git fetch failed. Check network or remote access.
  exit /b 1
)

echo [2/4] Merging origin/master into current branch...
git merge origin/master --no-edit
if errorlevel 1 (
  echo ERROR: merge failed. Resolve conflicts manually, then re-run this script.
  echo        ^(The working tree is left in merge state; see git status^)
  exit /b 1
)

echo [3/4] Building Debug...
dotnet build Server\DfoServer\DfoServer.csproj -c Debug --nologo
if errorlevel 1 (
  echo ERROR: build failed. Fix errors before starting the server.
  exit /b 1
)

rem Warn when the old server still holds port 7001 (no auto-kill).
for /f "tokens=5" %%P in ('netstat -ano ^| findstr ":7001" ^| findstr "LISTENING"') do (
  echo WARNING: port 7001 is already used by PID %%P.
  echo          Stop it first ^(taskkill /PID %%P^) or close that console.
)

rem PVF lookup order: PVF_ARCHIVE_PATH env > dist package > Debug data dir.
set "PVF=%PVF_ARCHIVE_PATH%"
if not defined PVF if exist "%ROOT%dist\win-x64\Data\Pvf\Script.pvf" set "PVF=%ROOT%dist\win-x64\Data\Pvf\Script.pvf"
if not defined PVF if exist "%ROOT%Server\DfoServer\bin\Debug\Data\Pvf\Script.pvf" set "PVF=%ROOT%Server\DfoServer\bin\Debug\Data\Pvf\Script.pvf"
if not defined PVF (
  echo ERROR: Script.pvf not found.
  echo        Put it in dist\win-x64\Data\Pvf\ or Server\DfoServer\bin\Debug\Data\Pvf\,
  echo        or set the PVF_ARCHIVE_PATH environment variable.
  exit /b 1
)

set "SERVER_EXE=%ROOT%Server\DfoServer\bin\Debug\DfoServer.exe"
if not exist "%SERVER_EXE%" (
  echo ERROR: %SERVER_EXE% not found. Build first.
  exit /b 1
)

echo [4/4] Starting server...
echo Using SERVER_IP=%SERVER_IP%
echo Using PVF=%PVF%
echo Database: Server\DfoServer\bin\Debug\Data\inventory.db
cd /d "%ROOT%Server\DfoServer\bin\Debug"
set "PVF_ARCHIVE_PATH=%PVF%"
"%SERVER_EXE%" --server-ip %SERVER_IP% %EXTRA_ARGS%
exit /b %ERRORLEVEL%

:usage
echo Usage:
echo   update-and-start.bat [--server-ip ^<ip^|auto^>] [extra server args...]
echo   set SERVER_IP=^<ip^> ^& update-and-start.bat
echo.
echo   Steps: git fetch origin ^& merge origin/master -^> build Debug -^> start server.
echo   --server-ip   IP sent to the game client. Default: 127.0.0.1
echo   auto          Pick this machine's LAN IPv4
exit /b 0
