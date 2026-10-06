@echo off
rem Builds dist\DevJunkCleaner.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
rem Nothing to install.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (echo .NET Framework 4.x compiler not found: %CSC% & exit /b 1)
if not exist "%~dp0dist" mkdir "%~dp0dist"
"%CSC%" /nologo /target:winexe /optimize+ /out:"%~dp0dist\DevJunkCleaner.exe" ^
  /win32manifest:"%~dp0src\app.manifest" /win32icon:"%~dp0src\app.ico" ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll "%~dp0src\DevJunkCleaner.cs"
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo Built %~dp0dist\DevJunkCleaner.exe
