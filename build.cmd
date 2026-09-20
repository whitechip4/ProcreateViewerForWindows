@echo off
rem Builds bin\ProcreateViewer.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
setlocal
set FW=C:\Windows\Microsoft.NET\Framework64\v4.0.30319
cd /d "%~dp0"
if not exist bin mkdir bin
set ICON=
if exist assets\app.ico set ICON=/win32icon:assets\app.ico
"%FW%\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ /unsafe /nowarn:1591 %ICON% ^
  /out:bin\ProcreateViewer.exe ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
  src\*.cs
if errorlevel 1 exit /b 1
echo built bin\ProcreateViewer.exe
