@echo off
echo ======================================
echo     CacheMax 高性能缓存加速器 v3.0
echo    目录连接点架构 - 无需管理员权限
echo ======================================
echo.
echo 重要提示：
echo 1. 使用目录连接点(Junction)技术，无需管理员权限
echo 2. 支持跨驱动器操作（C: -> D:）
echo 3. 新增实时同步队列监控界面
echo 4. 性能可达1500+ MB/s，更安全可靠
echo.
echo 正在启动 CacheMax...
cd /d "%~dp0CacheMax.GUI\bin\Release\net8.0-windows"
start "" "CacheMax.exe"
echo.
echo CacheMax 已启动！
pause