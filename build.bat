@echo off
rem Builds ClaudeCarry.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
rem The UI (ui\index.html) and the WebView2 DLLs (lib\) are embedded, so the exe is a single file.
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set OUT=%~1
if "%OUT%"=="" set OUT=%~dp0ClaudeCarry.exe
"%CSC%" /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ /out:"%OUT%" /win32icon:"%~dp0assets\ClaudeCarry.ico" ^
  /r:System.IO.Compression.dll /r:"%FW%\System.IO.Compression.FileSystem.dll" /r:"%FW%\System.Web.Extensions.dll" ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /r:"%~dp0lib\Microsoft.Web.WebView2.Core.dll" /r:"%~dp0lib\Microsoft.Web.WebView2.WinForms.dll" ^
  /resource:"%~dp0ui\index.html",index.html ^
  /resource:"%~dp0lib\Microsoft.Web.WebView2.Core.dll",Microsoft.Web.WebView2.Core.dll ^
  /resource:"%~dp0lib\Microsoft.Web.WebView2.WinForms.dll",Microsoft.Web.WebView2.WinForms.dll ^
  /resource:"%~dp0lib\WebView2Loader.dll",WebView2Loader.dll ^
  "%~dp0src\Carry.cs" "%~dp0src\Host.cs" "%~dp0src\Program.cs"
