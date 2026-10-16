@echo off
rem tools\verify.ps1 wrapper. Arguments are passed through, e.g.  verify.cmd -Quick   /   verify.cmd -Fix
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\verify.ps1" %*
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" if "%PANELY_NO_PAUSE%"=="" pause
exit /b %EXITCODE%
