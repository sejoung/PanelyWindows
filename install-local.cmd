@echo off
rem tools\install-local.ps1 wrapper. Arguments are passed through, e.g.  install-local.cmd -SkipTests
rem _common.ps1 switches the console to UTF-8 (65001) for dotnet output; restore the original code page afterwards.
for /f "tokens=2 delims=:." %%c in ('chcp') do set "PANELY_CP=%%c"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\install-local.ps1" %*
set EXITCODE=%ERRORLEVEL%
if defined PANELY_CP chcp %PANELY_CP% >nul
if not "%EXITCODE%"=="0" if "%PANELY_NO_PAUSE%"=="" pause
exit /b %EXITCODE%
