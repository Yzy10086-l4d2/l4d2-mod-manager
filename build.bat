@echo off
rem ============================================================
rem  Build the installer exe with the C# compiler shipped with
rem  Windows. No SDK, no NuGet, no network. Keep this pure ASCII.
rem ============================================================
setlocal
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
"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /out:"%OUT%" %ICONARG% ^
  /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll ^
  /resource:"%~dp0desk\index.html",desk.html ^
  /resource:"%~dp0desk\three.min.js",three.js ^
  "%~dp0AssemblyInfo.cs" "%~dp0Theme.cs" "%~dp0Glyph.cs" "%~dp0Program.cs" "%~dp0Core.cs" "%~dp0Launch.cs" "%~dp0Dup.cs" "%~dp0Catalog.cs" "%~dp0Parts.cs" "%~dp0CatPrefs.cs" "%~dp0MyBranch.cs" "%~dp0Tweaks.cs" "%~dp0Work.cs" "%~dp0Mods.cs" "%~dp0Identify.cs" "%~dp0SelfTest.cs" "%~dp0CatTree.cs" "%~dp0TreeHost.cs" "%~dp0Desk.cs" "%~dp0DeskServer.cs" "%~dp0TreeModel.cs" "%~dp0TreeLayout.cs" "%~dp0TreeWhite.cs" "%~dp0TreeCanvas.cs" "%~dp0Ui.cs" "%~dp0SettingsForm.cs"
if errorlevel 1 (
  echo.
  echo [X] Build failed.
  exit /b 1
)
echo [OK] Built: %OUT%
