@echo off
rem tools\release.ps1 wrapper. Arguments are passed through, e.g.  release.cmd patch   /   release.cmd minor -DryRun
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\release.ps1" %*
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" if "%PANELY_NO_PAUSE%"=="" pause
exit /b %EXITCODE%
