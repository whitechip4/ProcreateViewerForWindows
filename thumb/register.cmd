@echo off
rem One-time registration of the thumbnail handler for .procreate (run elevated). build.cmd must have run first.
setlocal
set FW=C:\Windows\Microsoft.NET\Framework64\v4.0.30319
set DLL=%~dp0ProcreateThumb.dll
set CLSID={7C1E7A6B-2F0D-4B39-9C55-5B1A2D3E4F60}
"%FW%\RegAsm.exe" /codebase "%DLL%"
if errorlevel 1 exit /b 1
reg add "HKCR\.procreate\shellex\{E357FCCD-A995-4576-B01F-234630154E96}" /ve /d "%CLSID%" /f
reg add "HKCR\Procreate.Document\shellex\{E357FCCD-A995-4576-B01F-234630154E96}" /ve /d "%CLSID%" /f
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved" /v "%CLSID%" /d "Procreate Thumbnail Provider" /f
reg add "HKCR\CLSID\%CLSID%" /v "DisableProcessIsolation" /t REG_DWORD /d 0 /f
echo REGISTERED
