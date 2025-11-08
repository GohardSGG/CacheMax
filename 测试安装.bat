@echo off
chcp 65001 >nul
echo ========================================
echo    CacheMax Installation Test
echo ========================================
echo.

REM Check admin rights
net session >nul 2>&1
if %errorLevel% == 0 (
    echo [OK] Running with administrator privileges
) else (
    echo [ERROR] Administrator privileges required
    echo.
    echo Please right-click this script and select:
    echo "Run as administrator"
    echo.
    pause
    exit /b 1
)

echo.
echo [OK] Admin check passed!
echo.
echo This is a test version with English messages.
echo If you see this message correctly, the encoding is fixed.
echo.
pause
