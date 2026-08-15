@echo off
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" @dsh-tray.rsp
if errorlevel 1 ( echo BUILD FAILED & exit /b 1 )
echo BUILD OK: dsh-tray.exe
