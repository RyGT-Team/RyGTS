@echo off
rem Builds RyGTS.exe (the launcher) with the C# compiler that ships with Windows' .NET Framework 4.x.
rem Usage: build.cmd [output folder] [Release|Diagnostics]
setlocal
set OUT=%~1
if "%OUT%"=="" set OUT=%~dp0
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
set SYMBOLS=
if /i "%~2"=="Diagnostics" set SYMBOLS=/debug:full /pdb:"%OUT%\RyGTS.pdb"
"%CSC%" /nologo /target:winexe /optimize+ %SYMBOLS% /win32icon:"%~dp0ryGT.ico" /resource:"%~dp0ryGT.ico",ryGT.ico /out:"%OUT%\RyGTS.exe" ^
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ^
    /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
    "%~dp0RenderResolution.cs" "%~dp0VulkanDevices.cs" "%~dp0Version.cs" "%~dp0RyGTLoader.cs" "%~dp0ControlsForm.cs" "%~dp0PortsForm.cs" "%~dp0Logging.cs" "%~dp0WheelForm.cs" "%~dp0FeelForm.cs" "%~dp0CustomEnvironment.cs" "%~dp0AdvancedPage.cs" "%~dp0Updater.cs" "%~dp0ObsoleteSettings.cs"
