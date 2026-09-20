@echo off
rem Compiles the thumbnail handler only (no registration) - used by CI and the installer build.
setlocal
set FW=C:\Windows\Microsoft.NET\Framework64\v4.0.30319
cd /d "%~dp0"
"%FW%\csc.exe" /nologo /target:library /platform:x64 /optimize+ /out:ProcreateThumb.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ProcreateThumb.cs
if errorlevel 1 exit /b 1
echo compiled ProcreateThumb.dll
