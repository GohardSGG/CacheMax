@echo off
echo ======================================
echo    CacheMax 高性能缓存加速器 v3.0
echo         DEBUG 调试版本
echo ======================================
echo.
echo 调试版本特性：
echo 1. 允许多实例运行，方便开发调试
echo 2. 详细的调试日志输出
echo 3. 支持热重载和实时调试
echo 4. 包含完整的调试符号信息
echo.
echo 正在启动 CacheMax 调试版本...
cd /d "%~dp0CacheMax.GUI\bin\Debug\net8.0-windows"
start "" "CacheMax.exe"
echo.
echo CacheMax 调试版本已启动！
echo 注意：调试版本可以启动多个实例
pause