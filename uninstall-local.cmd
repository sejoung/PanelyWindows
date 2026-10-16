@echo off
rem tools\uninstall-local.ps1 wrapper. Arguments are passed through, e.g.  uninstall-local.cmd -SkipTests
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\uninstall-local.ps1" %*
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" if "%PANELY_NO_PAUSE%"=="" pause
exit /b %EXITCODE%
