using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CacheMax.GUI.Services
{
    public class CacheManagerService
    {
        private readonly JunctionService _junctionService;
        private readonly FileSyncService _fileSyncService;
        private readonly ErrorRecoveryService _errorRecovery;
        private readonly PerformanceMonitoringService _performanceMonitor;
        private readonly ParallelSyncEngine _parallelSyncEngine;

        /// <summary>
        /// 公开FileSyncService以便UI订阅队列事件
        /// </summary>
        public FileSyncService FileSyncService => _fileSyncService;

        public CacheManagerService()
        {
            _junctionService = new JunctionService();
            _fileSyncService = new FileSyncService();
            _errorRecovery = new ErrorRecoveryService();
            _performanceMonitor = new PerformanceMonitoringService();
            _parallelSyncEngine = new ParallelSyncEngine();

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
                // 检查是否仍然是Junction（即加速仍然活跃）
                var isActive = IsAccelerated(folder.MountPoint);

                _errorRecovery.RecordAccelerationState(
                    folder.MountPoint,
                    folder.OriginalPath,
                    folder.CachePath,
                    isActive);

                // 如果加速仍然活跃，恢复文件同步监控和性能监控
                if (isActive && Directory.Exists(folder.CachePath) && Directory.Exists(folder.OriginalPath))
                {
                    // 恢复文件同步监控（这是关键！）
                    _fileSyncService.StartMonitoring(folder.CachePath, folder.OriginalPath, SyncMode.Batch, 3);
                    LogMessage?.Invoke(this, $"恢复文件同步监控：{folder.CachePath} -> {folder.OriginalPath}");

                    // 恢复性能监控
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
                    if (_junctionService.IsJunction(directory))
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
                // Junction不需要管理员权限，移除此检查
                progress?.Report("使用目录连接点，无需管理员权限");

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
                if (_junctionService.IsJunction(sourcePath))
                {
                    progress?.Report("目录已经是Junction，可能已加速");
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
                if (!_junctionService.SafeRenameDirectory(sourcePath, originalPath, progress))
                {
                    progress?.Report("重命名原始目录失败");
                    // 清理已复制的缓存
                    try { Directory.Delete(cachePath, true); } catch { }
                    return false;
                }

                // 步骤4：创建Junction
                progress?.Report($"创建Junction：{sourcePath} -> {cachePath}");
                if (!_junctionService.CreateDirectoryJunction(sourcePath, cachePath, progress))
                {
                    progress?.Report("创建Junction失败");
                    // 回滚：恢复原始目录
                    try
                    {
                        _junctionService.SafeRenameDirectory(originalPath, sourcePath, progress);
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

                // 步骤3：删除Junction
                progress?.Report($"删除Junction：{mountPoint}");
                if (_junctionService.IsJunction(mountPoint))
                {
                    if (!_junctionService.RemoveJunction(mountPoint, progress))
                    {
                        progress?.Report("删除Junction失败，但继续执行恢复");
                    }
                }

                // 步骤4：恢复原始目录
                progress?.Report($"恢复原始目录：{originalPath} -> {mountPoint}");
                if (Directory.Exists(originalPath))
                {
                    if (!_junctionService.SafeRenameDirectory(originalPath, mountPoint, progress))
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
            return _junctionService.IsJunction(path);
        }

        /// <summary>
        /// 获取Junction的目标路径
        /// </summary>
        public string? GetCachePath(string linkPath)
        {
            return _junctionService.GetJunctionTarget(linkPath);
        }

        /// <summary>
        /// 验证加速配置的完整性
        /// </summary>
        public bool ValidateAcceleration(string mountPoint, string originalPath, string cachePath, IProgress<string>? progress = null)
        {
            try
            {
                // 检查Junction
                if (!_junctionService.IsJunction(mountPoint))
                {
                    progress?.Report($"挂载点不是Junction：{mountPoint}");
                    return false;
                }

                // 检查Junction目标
                if (!_junctionService.ValidateJunction(mountPoint, cachePath, progress))
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
            return await Task.Run(async () =>
            {
                try
                {
                    await CopyDirectoryRecursiveAsync(sourcePath, targetPath, progress);
                    return true;
                }
                catch (Exception ex)
                {
                    progress?.Report($"复制目录失败：{ex.Message}");
                    return false;
                }
            });
        }

        // 新的异步批量复制方法 - 集成ParallelSyncEngine
        private async Task CopyDirectoryRecursiveAsync(string sourcePath, string targetPath, IProgress<string>? progress, CancellationToken cancellationToken = default)
        {
            // 创建目标目录
            if (!Directory.Exists(targetPath))
            {
                Directory.CreateDirectory(targetPath);
            }

            var batchProgressReporter = new BatchProgressReporter(progress, 100);
            var tasks = new List<Task>();
            const long LARGE_FILE_THRESHOLD = 50 * 1024 * 1024; // 50MB以上使用ParallelSyncEngine

            try
            {
                // 获取所有文件和目录
                var files = Directory.GetFiles(sourcePath);
                var directories = Directory.GetDirectories(sourcePath);

                // 分析文件大小，决定处理策略
                var largeFiles = new List<string>();
                var smallFiles = new List<string>();

                foreach (var file in files)
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.Length > LARGE_FILE_THRESHOLD)
                    {
                        largeFiles.Add(file);
                    }
                    else
                    {
                        smallFiles.Add(file);
                    }
                }

                // 小文件使用传统并行复制
                var semaphore = new SemaphoreSlim(Environment.ProcessorCount * 2);
                foreach (var file in smallFiles)
                {
                    var task = CopyFileWithSemaphoreAsync(file, sourcePath, targetPath, batchProgressReporter, semaphore, cancellationToken);
                    tasks.Add(task);
                }

                // 大文件使用ParallelSyncEngine
                foreach (var file in largeFiles)
                {
                    var task = CopyLargeFileAsync(file, sourcePath, targetPath, batchProgressReporter, cancellationToken);
                    tasks.Add(task);
                }

                // 等待所有文件复制完成
                await Task.WhenAll(tasks);
                tasks.Clear();

                // 递归处理子目录
                foreach (var directory in directories)
                {
                    var dirName = Path.GetFileName(directory);
                    var targetDir = Path.Combine(targetPath, dirName);
                    var task = CopyDirectoryRecursiveAsync(directory, targetDir, progress, cancellationToken);
                    tasks.Add(task);
                }

                await Task.WhenAll(tasks);
            }
            finally
            {
                batchProgressReporter.FlushPendingProgress();
            }
        }

        private async Task CopyFileWithSemaphoreAsync(string sourceFile, string sourcePath, string targetPath,
            BatchProgressReporter progressReporter, SemaphoreSlim semaphore, CancellationToken cancellationToken)
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var fileName = Path.GetFileName(sourceFile);
                var targetFile = Path.Combine(targetPath, fileName);

                // 异步复制文件
                using var sourceStream = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
                using var targetStream = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);

                await sourceStream.CopyToAsync(targetStream, cancellationToken);
                await targetStream.FlushAsync(cancellationToken);

                // 保持文件属性
                var sourceInfo = new FileInfo(sourceFile);
                var targetInfo = new FileInfo(targetFile)
                {
                    CreationTime = sourceInfo.CreationTime,
                    LastWriteTime = sourceInfo.LastWriteTime,
                    Attributes = sourceInfo.Attributes
                };

                progressReporter.ReportProgress(fileName);
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// 使用ParallelSyncEngine复制大文件，获得最佳性能
        /// </summary>
        private async Task CopyLargeFileAsync(string sourceFile, string sourcePath, string targetPath,
            BatchProgressReporter progressReporter, CancellationToken cancellationToken)
        {
            try
            {
                var fileName = Path.GetFileName(sourceFile);
                var targetFile = Path.Combine(targetPath, fileName);

                // 使用ParallelSyncEngine的高性能文件操作
                var success = await _parallelSyncEngine.SubmitFileOperationAsync(
                    sourceFile, targetFile, FileOperationType.Copy, cancellationToken);

                if (success)
                {
                    // 保持文件属性
                    var sourceInfo = new FileInfo(sourceFile);
                    var targetInfo = new FileInfo(targetFile)
                    {
                        CreationTime = sourceInfo.CreationTime,
                        LastWriteTime = sourceInfo.LastWriteTime,
                        Attributes = sourceInfo.Attributes
                    };

                    progressReporter.ReportProgress($"{fileName} (大文件-并行处理)");
                }
                else
                {
                    // 如果ParallelSyncEngine失败，回退到传统方法
                    await FallbackCopyLargeFileAsync(sourceFile, targetFile, progressReporter, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                // 出错时回退到传统复制方法
                var fileName = Path.GetFileName(sourceFile);
                var targetFile = Path.Combine(targetPath, fileName);
                await FallbackCopyLargeFileAsync(sourceFile, targetFile, progressReporter, cancellationToken);
            }
        }

        /// <summary>
        /// 大文件复制的回退方法
        /// </summary>
        private async Task FallbackCopyLargeFileAsync(string sourceFile, string targetFile,
            BatchProgressReporter progressReporter, CancellationToken cancellationToken)
        {
            var fileName = Path.GetFileName(sourceFile);

            // 使用大缓冲区优化大文件复制
            using var sourceStream = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            using var targetStream = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.WriteThrough);

            await sourceStream.CopyToAsync(targetStream, cancellationToken);
            await targetStream.FlushAsync(cancellationToken);

            // 保持文件属性
            var sourceInfo = new FileInfo(sourceFile);
            var targetInfo = new FileInfo(targetFile)
            {
                CreationTime = sourceInfo.CreationTime,
                LastWriteTime = sourceInfo.LastWriteTime,
                Attributes = sourceInfo.Attributes
            };

            progressReporter.ReportProgress($"{fileName} (大文件-传统方法)");
        }

        // 兼容性包装器 - 逐步废弃
        private void CopyDirectoryRecursive(string sourcePath, string targetPath, IProgress<string>? progress)
        {
            // 同步包装器，用于向后兼容
            Task.Run(async () => await CopyDirectoryRecursiveAsync(sourcePath, targetPath, progress)).Wait();
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

                // Junction不需要管理员权限
                progress?.Report("✅ 使用Junction无需管理员权限");

                // 检查各个服务组件
                progress?.Report("检查服务组件状态...");

                if (_junctionService == null)
                {
                    progress?.Report("❌ Junction服务未初始化");
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
            _parallelSyncEngine?.Dispose();
        }
    }

    /// <summary>
    /// 批量进度报告器 - 减少UI更新频率，提高性能
    /// </summary>
    public class BatchProgressReporter
    {
        private readonly IProgress<string>? _progress;
        private readonly int _batchSize;
        private readonly List<string> _pendingItems;
        private readonly Timer _flushTimer;
        private readonly object _lock = new();
        private readonly DateTime _startTime;

        public BatchProgressReporter(IProgress<string>? progress, int batchSize = 100)
        {
            _progress = progress;
            _batchSize = batchSize;
            _pendingItems = new List<string>();
            _startTime = DateTime.Now;

            // 每秒强制刷新一次，防止长时间无反馈
            _flushTimer = new Timer(ForceFlush, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        public void ReportProgress(string fileName)
        {
            if (_progress == null) return;

            lock (_lock)
            {
                _pendingItems.Add(fileName);

                // 达到批量大小立即报告
                if (_pendingItems.Count >= _batchSize)
                {
                    FlushBatch();
                }
            }
        }

        private void FlushBatch()
        {
            if (_pendingItems.Count == 0) return;

            var elapsed = DateTime.Now - _startTime;
            var speed = _pendingItems.Count / elapsed.TotalSeconds;

            _progress?.Report($"已处理 {_pendingItems.Count} 个文件 (速度: {speed:F1} 文件/秒)");
            _pendingItems.Clear();
        }

        private void ForceFlush(object? state)
        {
            lock (_lock)
            {
                if (_pendingItems.Count > 0)
                {
                    FlushBatch();
                }
            }
        }

        public void FlushPendingProgress()
        {
            lock (_lock)
            {
                FlushBatch();
            }
            _flushTimer?.Dispose();
        }
    }
}