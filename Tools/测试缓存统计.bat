@echo off
echo 测试 CacheMax 缓存统计功能
echo ================================
echo.
echo 启动passthrough-mod，每5秒输出一次缓存统计信息...
echo 你会看到：
echo - Cache Hits: 缓存命中次数
echo - Cache Misses: 缓存未命中次数
echo - Hit Rate: 缓存命中率（百分比）
echo - Read/Write 操作统计和速度
echo.
echo 按 Ctrl+C 停止测试
echo.
cd /d "C:\Code\CacheMax\CacheMax.FileSystem\Release"
passthrough-mod.exe -p "S:\Cache" -c "S:\Cache" -m "Y:"