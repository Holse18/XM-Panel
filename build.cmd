@echo off
rem XM Panel - build script.
rem   build.cmd            builds into %LOCALAPPDATA%\XMPanel and adds a Start Menu shortcut
rem   build.cmd <folder>   builds XMPanel.exe into <folder> only (used by the GitHub Actions build)
rem Needs only Windows 10/11 (uses the C# compiler that ships with .NET Framework 4.x).
rem headphones.png (black drawing on white), if present, is embedded as the artwork.
rem app.ico is the exe's file icon. Regenerate with: XMPanel.exe --make-icon app.ico
rem "Start with Windows" is a switch in the app's Settings page.
cd /d "%~dp0"
set OUT=%LOCALAPPDATA%\XMPanel
set SHORTCUT=1
if not "%~1"=="" (set "OUT=%~1" & set SHORTCUT=0)
if not exist "%OUT%" mkdir "%OUT%"
set RES=
if exist headphones.png set RES=/resource:headphones.png,headphones.png
if exist app.ico set RES=%RES% /win32icon:app.ico
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize /codepage:65001 /out:"%OUT%\XMPanel.exe" ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll %RES% XMPanel.cs || exit /b 1
if "%SHORTCUT%"=="1" powershell -NoProfile -Command "$s=(New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Programs')+'\XM Panel.lnk'); $s.TargetPath='%OUT%\XMPanel.exe'; $s.Save()"
echo Built %OUT%\XMPanel.exe
