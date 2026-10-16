@echo off
rem tools\build.ps1 wrapper. Arguments are passed through, e.g.  build.cmd -SkipTests
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\build.ps1" %*
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" if "%PANELY_NO_PAUSE%"=="" pause
exit /b %EXITCODE%
