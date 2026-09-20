@echo off
rem Compiles the thumbnail handler and registers it (run from an elevated prompt).
setlocal
set FW=C:\Windows\Microsoft.NET\Framework64\v4.0.30319
cd /d "%~dp0"
call compile.cmd
if errorlevel 1 exit /b 1
"%FW%\RegAsm.exe" /codebase ProcreateThumb.dll >nul
echo BUILT
