@echo off
rem CloudMusicLayoutFix - build the single-file launcher+injector.
rem Uses the C# compiler that ships with the .NET Framework (no extra tooling).
rem The layout CSS is embedded as a resource, so the result is a single .exe.
rem ASCII-only on purpose.
setlocal
chcp 65001 >nul
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [ERROR] csc.exe not found - .NET Framework 4.x is required.
    pause
    exit /b 1
)

set "OUT=%~dp0CloudMusicLayoutFix.exe"
set "SRC=%~dp0src\CloudMusicLayoutFix.cs"
set "RES=%~dp0css\layout-fix.css"

if not exist "%SRC%" ( echo [ERROR] missing %SRC% & pause & exit /b 1 )
if not exist "%RES%" ( echo [ERROR] missing %RES% & pause & exit /b 1 )

echo Compiling %OUT% ...
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 ^
    /out:"%OUT%" ^
    /reference:System.dll ^
    /reference:System.Drawing.dll ^
    /reference:System.Windows.Forms.dll ^
    /resource:"%RES%",layout-fix.css ^
    "%SRC%"

if errorlevel 1 (
    echo.
    echo BUILD FAILED
    pause
    exit /b 1
)
echo.
echo Built: %OUT%
exit /b 0
