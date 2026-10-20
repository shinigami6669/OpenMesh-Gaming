@echo off
setlocal enabledelayedexpansion
title OpenMesh Gaming - Client Build Tool

echo ========================================================
echo       OpenMesh Gaming Client - Automated Compiler
echo ========================================================
echo.

:: Detect 64-bit or 32-bit .NET v4.0.30319 CSC compiler
set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    set "CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)

if not exist "%CSC%" (
    echo [ERROR] Microsoft .NET Framework 4.0/4.5+ C# compiler not found.
    echo Please make sure .NET Framework is installed.
    pause
    exit /b 1
)

echo [*] Using C# Compiler: %CSC%
echo [*] Compiling OpenMesh Gaming Windows Client...

set "OUT_EXE=%~dp0OpenMeshGaming.exe"
set "SRC_DIR=%~dp0src"

"%CSC%" /target:winexe ^
    /optimize+ ^
    /platform:anycpu ^
    /win32manifest:"%SRC_DIR%\app.manifest" ^
    /r:System.dll,System.Drawing.dll,System.Windows.Forms.dll,System.ServiceProcess.dll ^
    /out:"%OUT_EXE%" ^
    "%SRC_DIR%\Program.cs"

if %ERRORLEVEL% equ 0 (
    echo.
    echo [SUCCESS] Compilation completed successfully!
    echo Binary: %OUT_EXE%
    echo.
) else (
    echo.
    echo [ERROR] Compilation failed! Please check compiler errors above.
    echo.
    pause
    exit /b %ERRORLEVEL%
)

endlocal
