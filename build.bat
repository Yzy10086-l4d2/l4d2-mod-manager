@echo off
rem ============================================================
rem  Build the installer exe with the C# compiler shipped with
rem  Windows. No SDK, no NuGet, no network. Keep this pure ASCII.
rem
rem  Source files are collected with a wildcard on purpose. The old
rem  hardcoded list had to be kept in sync by hand -- a new .cs file
rem  nobody remembered to add simply never got compiled, and the
rem  "why is my fix not in the exe" hunt took longer than the fix.
rem ============================================================
setlocal enabledelayedexpansion
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [X] csc.exe not found - needs .NET Framework 4.x
  exit /b 1
)

rem  Where to put the exe. In the USB kit layout the exe lives at the drive
rem  root (4 levels up: src\pkg\mods\<drive>). A standalone clone has no such
rem  parent, so fall back to building next to this script -- "clone and
rem  double-click" then still works. Check is ASCII-only on purpose.
set "ROOT=%~dp0..\..\..\.."
for %%I in ("%ROOT%") do set "ROOT=%%~fI"
if "%ROOT:~2%"=="\" (set "OUT=%ROOT%L4D2Setup.exe") else (set "OUT=%~dp0L4D2Setup.exe")
set "ICONARG="
if exist "%~dp0app.ico" set "ICONARG=/win32icon:%~dp0app.ico"

rem  Collect every .cs sitting next to this script. Order does not matter
rem  to csc, so the wildcard is enough.
set "SRCS="
set /a N=0
for %%F in ("%~dp0*.cs") do (
  set "SRCS=!SRCS! "%%~fF""
  set /a N+=1
)
if %N%==0 (
  echo [X] No .cs files next to build.bat
  exit /b 1
)
echo [i] compiling %N% source files

"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /out:"%OUT%" %ICONARG% ^
  /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll ^
  /resource:"%~dp0desk\index.html",desk.html ^
  /resource:"%~dp0desk\three.min.js",three.js ^
  %SRCS%
if errorlevel 1 (
  echo.
  echo [X] Build failed.
  exit /b 1
)
echo [OK] Built: %OUT%
