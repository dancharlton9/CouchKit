@echo off
rem Builds bin\CouchKit.exe with the C# compiler that ships with Windows (.NET Framework 4.8),
rem so no SDK or Visual Studio is needed.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo .NET Framework 4 compiler not found at %CSC%
  exit /b 1
)
cd /d "%~dp0"
if not exist bin mkdir bin
"%CSC%" /nologo /target:winexe /platform:x64 /optimize ^
  /r:System.Drawing.dll /r:System.Web.Extensions.dll ^
  /out:bin\CouchKit.exe src\*.cs
if errorlevel 1 exit /b 1
copy /y couchkit.example.json bin\couchkit.example.json >nul
echo Built bin\CouchKit.exe
