using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace CacheMax.GUI.Services
{
    public class CacheManagerService
    {
        private readonly SymbolicLinkService _symbolicLinkService;
        private readonly FileSyncService _fileSyncService;
        private readonly ErrorRecoveryService _errorRecovery;
        private readonly PerformanceMonitoringService _performanceMonitor;

        public CacheManagerService()
        {
            _symbolicLinkService = new SymbolicLinkService();
            _fileSyncService = new FileSyncService();
            _errorRecovery = new ErrorRecoveryService();
            _performanceMonitor = new PerformanceMonitoringService();

            // 订阅同步事件
            _fileSyncService.LogMessage += (sender, message) => LogMessage?.Invoke(this, message);
            _fileSyncService.SyncFailed += OnSyncFailed;

            // 订阅错误恢复事件
            _errorRecovery.LogMessage += (sender, message) => LogMessage?.Invoke(this, message);
            _errorRecovery.RecoveryStarted += (sender, args) => LogMessage?.Invoke(this, $"开始恢复：{args.MountPoint} - {args.Action}");
            _errorRecovery.RecoveryCompleted += (sender, args) => LogMessage?.Invoke(this, $"恢复成功：{args.MountPoint}");
            _errorRecovery.RecoveryFailed += (sender, args) => LogMessage?.Invoke(this, $"恢复失败：{args.MountPoint} - {args.Message}");

            // 订阅性能监控事件
            _performanceMonitor.LogMessage += (sender, message) => LogMessage?.Invoke(this, message);
            _performanceMonitor.StatsUpdated += OnPerformanceStatsUpdated;
        }

        /// <summary>
        /// 从配置中恢复加速状态到错误恢复服务
        /// </summary>
        public void RestoreAccelerationStates(List<AcceleratedFolder> folders)
        {
            foreach (var folder in folders)
            {
                // 检查是否仍然是符号链接（即加速仍然活跃）
                var isActive = IsAccelerated(folder.MountPoint);

                _errorRecovery.RecordAccelerationState(
                    folder.MountPoint,
                    folder.OriginalPath,
                    folder.CachePath,
                    isActive);

                // 如果加速仍然活跃，恢复性能监控
                if (isActive && Directory.Exists(folder.CachePath))
                {
                    _performanceMonitor.StartMonitoring(folder.MountPoint, folder.CachePath);
                    LogMessage?.Invoke(this, $"恢复性能监控：{folder.MountPoint}");
                }

                LogMessage?.Invoke(this, $"恢复加速状态记录：{folder.MountPoint} - {(isActive ? "活跃" : "非活跃")}");
            }
        }

        public event EventHandler<string>? LogMessage;
        public event EventHandler<CacheStatsEventArgs>? StatsUpdated;
        public event EventHandler<PerformanceMonitoringService.PerformanceStatsEventArgs>? PerformanceStatsUpdated;

        private void OnSyncFailed(object? sender, FileSyncService.SyncEventArgs e)
        {
            // 记录同步失败错误
            var mountPoint = FindMountPointForPath(e.FilePath);
            if (!string.IsNullOrEmpty(mountPoint))
            {
                _errorRecovery.RecordError(
                    mountPoint,
                    "SyncFailure",
                    e.Message ?? "同步失败",
                    null,
                    ErrorRecoveryService.ErrorSeverity.Medium);
            }
        }

        private void OnPerformanceStatsUpdated(object? sender, PerformanceMonitoringService.PerformanceStatsEventArgs e)
        {
            // 转发性能统计事件到UI
            PerformanceStatsUpdated?.Invoke(this, e);
        }

        private string FindMountPointForPath(string filePath)
        {
            // 在实际实现中，这里需要维护一个路径到挂载点的映射
            // 现在简化实现
            try
            {
                var directory = Path.GetDirectoryName(filePath);
                while (!string.IsNullOrEmpty(directory))
                {
                    if (_symbolicLinkService.IsSymbolicLink(directory))
                    {
                        return directory;
                    }
                    directory = Path.GetDirectoryName(directory);
                }
            }
            catch { }
            return string.Empty;
        }

        public class CacheStatsEventArgs : EventArgs
        {
            public long TotalCacheSize { get; set; }
            public int FileCount { get; set; }
            public int SyncQueueCount { get; set; }
            public DateTime? OldestPendingSync { get; set; }
            public string CachePath { get; set; } = string.Empty;
        }

        /// <summary>
        /// 初始化缓存加速
        /// </summary>
        public async Task<bool> InitializeCacheAcceleration(
            string sourcePath,
            string cacheRoot,
            SyncMode syncMode = SyncMode.Batch,
            int syncDelaySeconds = 3,
            IProgress<string>? progress = null)
        {
            try
            {
                // 检查管理员权限
                if (!_symbolicLinkService.IsRunningAsAdministrator())
                {
                    progress?.Report("错误：需要管理员权限才能创建符号链接");
                    _errorRecovery.RecordError(sourcePath, "PermissionError", "需要管理员权限", null, ErrorRecoveryService.ErrorSeverity.Critical);
                    return false;
                }

                // 验证输入路径
                if (!Directory.Exists(sourcePath))
                {
                    progress?.Report($"源目录不存在：{sourcePath}");
                    return false;
                }

                if (!Directory.Exists(cacheRoot))
                {
                    progress?.Report($"缓存根目录不存在：{cacheRoot}");
                    return false;
                }

                var folderName = Path.GetFileName(sourcePath);
                var originalPath = $"{sourcePath}.original";
                var cachePath = Path.Combine(cacheRoot, folderName);

                progress?.Report("开始缓存加速初始化...");

                // 步骤1：检查是否已经加速
                if (_symbolicLinkService.IsSymbolicLink(sourcePath))
                {
                    progress?.Report("目录已经是符号链接，可能已加速");
                    return false;
                }

                // 步骤2：复制到缓存（使用FastCopy提高性能）
                progress?.Report($"复制数据到缓存：{sourcePath} -> {cachePath}");
                if (!await CopyDirectoryAsync(sourcePath, cachePath, progress))
                {
                    progress?.Report("复制到缓存失败");
                    _errorRecovery.RecordError(sourcePath, "CopyFailure", "复制到缓存失败", null, ErrorRecoveryService.ErrorSeverity.High);
                    return false;
                }

                // 步骤3：重命名原始目录
                progress?.Report($"备份原始目录：{sourcePath} -> {originalPath}");
                if (!_symbolicLinkService.SafeRenameDirectory(sourcePath, originalPath, progress))
                {
                    progress?.Report("重命名原始目录失败");
                    // 清理已复制的缓存
                    try { Directory.Delete(cachePath, true); } catch { }
                    return false;
                }

                // 步骤4：创建符号链接
                progress?.Report($"创建符号链接：{sourcePath} -> {cachePath}");
                if (!_symbolicLinkService.CreateDirectorySymbolicLink(sourcePath, cachePath, progress))
                {
                    progress?.Report("创建符号链接失败");
                    // 回滚：恢复原始目录
                    try
                    {
                        _symbolicLinkService.SafeRenameDirectory(originalPath, sourcePath, progress);
                        Directory.Delete(cachePath, true);
                    }
                    catch (Exception ex)
                    {
                        progress?.Report($"回滚失败：{ex.Message}");
                    }
                    return false;
                }

                // 步骤5：启动文件同步监控
                progress?.Report("启动文件同步监控...");
                if (!_fileSyncService.StartMonitoring(cachePath, originalPath, syncMode, syncDelaySeconds, progress))
                {
                    progress?.Report("启动文件同步监控失败");
                    // 可以继续，因为符号链接已经工作了
                }

                // 步骤6：启动性能监控
                progress?.Report("启动性能监控...");
                if (!_performanceMonitor.StartMonitoring(sourcePath, cachePath))
                {
                    progress?.Report("启动性能监控失败");
                    // 可以继续，不影响核心功能
                }

                progress?.Report("缓存加速初始化完成！");
                LogMessage?.Invoke(this, $"缓存加速已启用：{sourcePath}");

                // 记录成功的加速状态
                _errorRecovery.RecordAccelerationState(sourcePath, originalPath, cachePath, true);

                // 触发初始统计更新
                _ = Task.Run(() => UpdateCacheStats(cachePath));

                return true;
            }
            catch (Exception ex)
            {
                progress?.Report($"初始化缓存加速异常：{ex.Message}");
                LogMessage?.Invoke(this, $"初始化缓存加速异常：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 停止缓存加速
        /// </summary>
        public async Task<bool> StopCacheAcceleration(
            string mountPoint,
            string originalPath,
            string cachePath,
            bool deleteCacheFiles = false,
            IProgress<string>? progress = null)
        {
            try
            {
                progress?.Report("开始停止缓存加速...");

                // 步骤1：停止文件同步监控
                progress?.Report("停止文件同步监控...");
                _fileSyncService.StopMonitoring(cachePath, progress);

                // 步骤1.5：停止性能监控
                progress?.Report("停止性能监控...");
                _performanceMonitor.StopMonitoring(mountPoint);

                // 步骤2：执行最后一次同步
                progress?.Report("执行最后一次同步...");
                await _fileSyncService.ForceSync(cachePath, progress);

                // 步骤3：删除符号链接
                progress?.Report($"删除符号链接：{mountPoint}");
                if (_symbolicLinkService.IsSymbolicLink(mountPoint))
                {
                    if (!_symbolicLinkService.RemoveSymbolicLink(mountPoint, progress))
                    {
                        progress?.Report("删除符号链接失败，但继续执行恢复");
                    }
                }

                // 步骤4：恢复原始目录
                progress?.Report($"恢复原始目录：{originalPath} -> {mountPoint}");
                if (Directory.Exists(originalPath))
                {
                    if (!_symbolicLinkService.SafeRenameDirectory(originalPath, mountPoint, progress))
                    {
                        progress?.Report("恢复原始目录失败");
                        return false;
                    }
                }

                // 步骤5：可选删除缓存文件
                if (deleteCacheFiles && Directory.Exists(cachePath))
                {
                    progress?.Report($"删除缓存文件：{cachePath}");
                    try
                    {
                        Directory.Delete(cachePath, true);
                        progress?.Report("缓存文件删除成功");
                    }
                    catch (Exception ex)
                    {
                        progress?.Report($"删除缓存文件失败：{ex.Message}");
                    }
                }

                progress?.Report("缓存加速停止完成！");
                LogMessage?.Invoke(this, $"缓存加速已停止：{mountPoint}");
                return true;
            }
            catch (Exception ex)
            {
                progress?.Report($"停止缓存加速异常：{ex.Message}");
                LogMessage?.Invoke(this, $"停止缓存加速异常：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 检查目录是否已加速
        /// </summary>
        public bool IsAccelerated(string path)
        {
            return _symbolicLinkService.IsSymbolicLink(path);
        }

        /// <summary>
        /// 获取符号链接的目标路径
        /// </summary>
        public string? GetCachePath(string linkPath)
        {
            return _symbolicLinkService.GetSymbolicLinkTarget(linkPath);
        }

        /// <summary>
        /// 验证加速配置的完整性
        /// </summary>
        public bool ValidateAcceleration(string mountPoint, string originalPath, string cachePath, IProgress<string>? progress = null)
        {
            try
            {
                // 检查符号链接
                if (!_symbolicLinkService.IsSymbolicLink(mountPoint))
                {
                    progress?.Report($"挂载点不是符号链接：{mountPoint}");
                    return false;
                }

                // 检查符号链接目标
                if (!_symbolicLinkService.ValidateSymbolicLink(mountPoint, cachePath, progress))
                {
                    return false;
                }

                // 检查原始目录
                if (!Directory.Exists(originalPath))
                {
                    progress?.Report($"原始目录不存在：{originalPath}");
                    return false;
                }

                // 检查缓存目录
                if (!Directory.Exists(cachePath))
                {
                    progress?.Report($"缓存目录不存在：{cachePath}");
                    return false;
                }

                progress?.Report("加速配置验证成功");
                return true;
            }
            catch (Exception ex)
            {
                progress?.Report($"验证加速配置异常：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 立即同步缓存到原始位置
        /// </summary>
        public async Task<bool> SyncToOriginal(string cachePath, IProgress<string>? progress = null)
        {
            return await _fileSyncService.ForceSync(cachePath, progress);
        }

        /// <summary>
        /// 获取缓存统计信息
        /// </summary>
        public async Task<CacheStatsEventArgs> GetCacheStats(string cachePath)
        {
            return await Task.Run(() =>
            {
                var stats = new CacheStatsEventArgs
                {
                    CachePath = cachePath
                };

                try
                {
                    if (Directory.Exists(cachePath))
                    {
                        var dirInfo = new DirectoryInfo(cachePath);
                        var files = dirInfo.GetFiles("*", SearchOption.AllDirectories);

                        stats.FileCount = files.Length;
                        stats.TotalCacheSize = files.Sum(f => f.Length);
                    }

                    var queueStatus = _fileSyncService.GetQueueStatus();
                    stats.SyncQueueCount = queueStatus.Count;
                    stats.OldestPendingSync = queueStatus.OldestTimestamp;
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke(this, $"获取缓存统计失败：{ex.Message}");
                }

                return stats;
            });
        }

        /// <summary>
        /// 清理缓存（删除最旧的文件直到释放指定空间）
        /// </summary>
        public Task<bool> CleanupCache(string cachePath, long targetFreeBytes, IProgress<string>? progress = null)
        {
            return Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(cachePath))
                    {
                        progress?.Report($"缓存目录不存在：{cachePath}");
                        return false;
                    }

                    progress?.Report($"开始清理缓存，目标释放 {targetFreeBytes / 1024 / 1024} MB");

                    var dirInfo = new DirectoryInfo(cachePath);
                    var files = dirInfo.GetFiles("*", SearchOption.AllDirectories)
                        .OrderBy(f => f.LastAccessTime)
                        .ToList();

                    long freedBytes = 0;
                    int deletedCount = 0;

                    foreach (var file in files)
                    {
                        if (freedBytes >= targetFreeBytes)
                            break;

                        try
                        {
                            var fileSize = file.Length;
                            file.Delete();
                            freedBytes += fileSize;
                            deletedCount++;

                            progress?.Report($"删除文件：{file.Name} ({fileSize / 1024} KB)");
                        }
                        catch (Exception ex)
                        {
                            progress?.Report($"删除文件失败：{file.Name} - {ex.Message}");
                        }
                    }

                    progress?.Report($"缓存清理完成：删除 {deletedCount} 个文件，释放 {freedBytes / 1024 / 1024} MB");
                    LogMessage?.Invoke(this, $"缓存清理：{cachePath}，删除 {deletedCount} 个文件");
                    return true;
                }
                catch (Exception ex)
                {
                    progress?.Report($"清理缓存异常：{ex.Message}");
                    LogMessage?.Invoke(this, $"清理缓存异常：{ex.Message}");
                    return false;
                }
            });
        }

        /// <summary>
        /// 更新同步模式
        /// </summary>
        public bool UpdateSyncMode(string cachePath, string originalPath, SyncMode newMode, int delaySeconds = 3, IProgress<string>? progress = null)
        {
            // 重新启动监控以应用新模式
            _fileSyncService.StopMonitoring(cachePath, progress);
            return _fileSyncService.StartMonitoring(cachePath, originalPath, newMode, delaySeconds, progress);
        }

        private async Task<bool> CopyDirectoryAsync(string sourcePath, string targetPath, IProgress<string>? progress)
        {
            return await Task.Run(() =>
            {
                try
                {
                    CopyDirectoryRecursive(sourcePath, targetPath, progress);
                    return true;
                }
                catch (Exception ex)
                {
                    progress?.Report($"复制目录失败：{ex.Message}");
                    return false;
                }
            });
        }

        private void CopyDirectoryRecursive(string sourcePath, string targetPath, IProgress<string>? progress)
        {
            // 创建目标目录
            if (!Directory.Exists(targetPath))
            {
                Directory.CreateDirectory(targetPath);
            }

            // 复制所有文件
            foreach (var file in Directory.GetFiles(sourcePath))
            {
                var fileName = Path.GetFileName(file);
                var targetFile = Path.Combine(targetPath, fileName);

                File.Copy(file, targetFile, true);

                // 保持文件属性
                var sourceInfo = new FileInfo(file);
                var targetInfo = new FileInfo(targetFile);
                targetInfo.CreationTime = sourceInfo.CreationTime;
                targetInfo.LastWriteTime = sourceInfo.LastWriteTime;
                targetInfo.Attributes = sourceInfo.Attributes;

                progress?.Report($"已复制：{fileName}");
            }

            // 递归复制子目录
            foreach (var directory in Directory.GetDirectories(sourcePath))
            {
                var dirName = Path.GetFileName(directory);
                var targetDir = Path.Combine(targetPath, dirName);
                CopyDirectoryRecursive(directory, targetDir, progress);
            }
        }

        private void UpdateCacheStats(string cachePath)
        {
            try
            {
                var stats = GetCacheStats(cachePath).Result;
                StatsUpdated?.Invoke(this, stats);
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"更新缓存统计失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 执行系统健康检查
        /// </summary>
        public async Task<bool> PerformHealthCheck(IProgress<string>? progress = null)
        {
            try
            {
                progress?.Report("初始化健康检查系统...");

                // 先进行基本的系统检查
                progress?.Report("检查基本系统要求...");

                // 检查管理员权限
                if (!_symbolicLinkService.IsRunningAsAdministrator())
                {
                    progress?.Report("⚠️ 警告：当前没有管理员权限，无法创建符号链接");
                    LogMessage?.Invoke(this, "健康检查：缺少管理员权限");
                }
                else
                {
                    progress?.Report("✅ 管理员权限检查通过");
                }

                // 检查各个服务组件
                progress?.Report("检查服务组件状态...");

                if (_symbolicLinkService == null)
                {
                    progress?.Report("❌ 符号链接服务未初始化");
                    return false;
                }

                if (_fileSyncService == null)
                {
                    progress?.Report("❌ 文件同步服务未初始化");
                    return false;
                }

                if (_errorRecovery == null)
                {
                    progress?.Report("❌ 错误恢复服务未初始化");
                    return false;
                }

                progress?.Report("✅ 所有服务组件状态正常");

                // 执行详细的错误恢复检查
                progress?.Report("开始详细的加速项目检查...");
                var hasProblems = await _errorRecovery.PerformHealthCheck(this, progress);

                if (hasProblems)
                {
                    progress?.Report("⚠️ 健康检查发现问题，请查看详细日志");
                }
                else
                {
                    progress?.Report("🎉 系统健康检查完全通过！");
                }

                return hasProblems;
            }
            catch (Exception ex)
            {
                progress?.Report($"❌ 健康检查系统异常：{ex.Message}");
                LogMessage?.Invoke(this, $"健康检查异常：{ex.Message}");
                return true; // 返回true表示有问题
            }
        }

        /// <summary>
        /// 手动触发恢复
        /// </summary>
        public async Task<bool> TriggerRecovery(string mountPoint, IProgress<string>? progress = null)
        {
            try
            {
                progress?.Report($"开始手动恢复：{mountPoint}");
                return await _errorRecovery.TriggerRecovery(mountPoint, this, progress);
            }
            catch (Exception ex)
            {
                progress?.Report($"手动恢复异常：{ex.Message}");
                LogMessage?.Invoke(this, $"手动恢复异常：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取错误统计信息
        /// </summary>
        public Dictionary<string, object> GetErrorStatistics()
        {
            return _errorRecovery.GetErrorStatistics();
        }

        /// <summary>
        /// 获取性能统计信息
        /// </summary>
        public PerformanceMonitoringService.PerformanceSnapshot? GetPerformanceStats(string mountPoint)
        {
            return _performanceMonitor.GetCurrentStats(mountPoint);
        }

        /// <summary>
        /// 获取所有加速项目的性能统计
        /// </summary>
        public List<PerformanceMonitoringService.PerformanceSnapshot> GetAllPerformanceStats()
        {
            return _performanceMonitor.GetAllStats();
        }

        public void Dispose()
        {
            _fileSyncService?.Dispose();
            _errorRecovery?.Dispose();
            _performanceMonitor?.Dispose();
        }
    }
}