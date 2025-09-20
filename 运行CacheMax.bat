@echo off
echo ======================================
echo     CacheMax 高性能缓存加速器 v2.0
echo      符号链接架构 - 无WinFsp依赖
echo ======================================
echo.
echo 重要提示：
echo 1. 需要管理员权限才能创建符号链接
echo 2. 如果遇到权限问题，请右键以管理员身份运行
echo 3. 现在使用符号链接，性能可达1500+ MB/s
echo.
echo 正在启动 CacheMax...
cd /d "%~dp0CacheMax.GUI\bin\Release\net8.0-windows"
start "" "CacheMax.exe"
echo.
echo CacheMax 已启动！
pause