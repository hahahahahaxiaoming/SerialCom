@echo off
setlocal
set "CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 /out:"%~dp0SerialCom.exe" "%~dp0SerialCom.cs"
if errorlevel 1 (
    echo Build failed.
) else (
    echo Build succeeded. Open SerialCom.exe in this folder.
)
