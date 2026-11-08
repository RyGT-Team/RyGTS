@echo off
setlocal
rem Usage: test-shader-policy.cmd output-folder [--probe]
rem Use an isolated output directory: the fixture writes a disposable launcher config beside its exe.
set "OUT=%~1"
if "%OUT%"=="" exit /b 2
for %%F in (GTSport.exe RyGTS.exe RyGT.exe RyGT.ini) do (
 if exist "%OUT%\%%F" (
  echo Use a separate test output folder, not a launcher installation.
  exit /b 1
 )
)
if exist "%OUT%\app0\" (
 echo Use a separate test output folder, not a game installation.
 exit /b 1
)
if not exist "%OUT%" mkdir "%OUT%"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
"%CSC%" /nologo /target:exe /optimize+ /main:RyGT.ShaderCompilationPolicyTests /resource:"%~dp0ryGT.ico",ryGT.ico /out:"%OUT%\ShaderCompilationPolicyTests.exe" ^
 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ^
 /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
 "%~dp0RenderResolution.cs" "%~dp0VulkanDevices.cs" "%~dp0RyGTLoader.cs" "%~dp0ControlsForm.cs" "%~dp0PortsForm.cs" "%~dp0Logging.cs" "%~dp0WheelForm.cs" "%~dp0shader-cache-tests\ShaderCompilationPolicyTests.cs"
if errorlevel 1 exit /b %errorlevel%
if /i "%~2"=="--probe" (
 "%OUT%\ShaderCompilationPolicyTests.exe" --probe > "%OUT%\results.log" 2>&1
) else (
 "%OUT%\ShaderCompilationPolicyTests.exe" > "%OUT%\results.log" 2>&1
)
set "RESULT=%errorlevel%"
type "%OUT%\results.log"
exit /b %RESULT%
