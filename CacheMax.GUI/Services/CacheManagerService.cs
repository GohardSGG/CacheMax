using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CacheMax.GUI.Services
{
    public class CacheManagerService
    {
        private readonly JunctionService _junctionService;
        private readonly FileSyncService _fileSyncService;
        private readonly ErrorRecoveryService _errorRecovery;
        private readonly FastCopyService _fastCopyService;

        /// <summary>
        /// 公开FileSyncService以便UI订阅队列事件
        /// </summary>
        public FileSyncService FileSyncService => _fileSyncService;

        public CacheManagerService()
        {
            _junctionService = new JunctionService();
            _fileSyncService = new FileSyncService();
            _errorRecovery = new ErrorRecoveryService();
            _fastCopyService = FastCopyService.Instance;

            // 订阅同步事件
            _fileSyncService.LogMessage += (sender, message) => LogMessage?.Invoke(this, message);
            _fileSyncService.SyncFailed += OnSyncFailed;

            // 订阅错误恢复事件
            _errorRecovery.LogMessage += (sender, message) => LogMessage?.Invoke(this, message);
            _errorRecovery.RecoveryStarted += (sender, args) => LogMessage?.Invoke(this, $"开始恢复：{args.MountPoint} - {args.Action}");
            _errorRecovery.RecoveryCompleted += (sender, args) => LogMessage?.Invoke(this, $"恢复成功：{args.MountPoint}");
            _errorRecovery.RecoveryFailed += (sender, args) => LogMessage?.Invoke(this, $"恢复失败：{args.MountPoint} - {args.Message}");

        }

        /// <summary>
        /// 从配置中恢复加速状态到错误恢复服务
        /// </summary>
        public void RestoreAccelerationStates(List<AcceleratedFolder> folders)
        {
            LogMessage?.Invoke(this, $"开始恢复 {folders.Count} 个加速项目的状态...");

            int successCount = 0;
            int failureCount = 0;

            foreach (var folder in folders)
            {
                LogMessage?.Invoke(this, $"正在检查加速项目：{folder.MountPoint}");

                // 检查是否仍然是Junction（即加速仍然活跃）
                var isActive = IsAccelerated(folder.MountPoint);
                LogMessage?.Invoke(this, $"Junction状态检查：{folder.MountPoint} - {(isActive ? "是Junction" : "不是Junction")}");

                _errorRecovery.RecordAccelerationState(
                    folder.MountPoint,
                    folder.OriginalPath,
                    folder.CachePath,
                    isActive);

                bool restoreSuccess = false;

                // 如果加速仍然活跃，恢复文件同步监控
                if (isActive)
                {
                    LogMessage?.Invoke(this, $"检查目录存在性：缓存({Directory.Exists(folder.CachePath)}) 原始({Directory.Exists(folder.OriginalPath)})");

                    if (Directory.Exists(folder.CachePath) && Directory.Exists(folder.OriginalPath))
                    {
                        LogMessage?.Invoke(this, $"准备启动文件同步监控：{folder.CachePath} -> {folder.OriginalPath}");

                        // 恢复文件同步监控（这是关键！）
                        var monitoringStarted = _fileSyncService.StartMonitoring(folder.CachePath, folder.OriginalPath, SyncMode.Immediate, 3);

                        if (monitoringStarted)
                        {
                            LogMessage?.Invoke(this, $"✓ 成功恢复文件同步监控：{folder.CachePath} -> {folder.OriginalPath}");
                            folder.Status = "已加速";
                            restoreSuccess = true;
                        }
                        else
                        {
                            LogMessage?.Invoke(this, $"✗ 恢复文件同步监控失败：{folder.CachePath} -> {folder.OriginalPath}");
                            folder.Status = "监控失败";
                            restoreSuccess = false;
                        }
                    }
                    else
                    {
                        LogMessage?.Invoke(this, $"✗ 目录不存在，跳过监控恢复：缓存({folder.CachePath}) 原始({folder.OriginalPath})");
                        folder.Status = "目录丢失";
                        restoreSuccess = false;
                    }
                }
                else
                {
                    LogMessage?.Invoke(this, $"✗ Junction不活跃，跳过监控恢复：{folder.MountPoint}");
                    folder.Status = "未加速";
                    restoreSuccess = false;
                }

                if (restoreSuccess)
                {
                    successCount++;
                }
                else
                {
                    failureCount++;
                }

                LogMessage?.Invoke(this, $"恢复加速状态记录：{folder.MountPoint} - {folder.Status}");
            }

            LogMessage?.Invoke(this, $"加速状态恢复完成！成功：{successCount}，失败：{failureCount}");
        }

        public event EventHandler<string>? LogMessage;
        public event EventHandler<CacheStatsEventArgs>? StatsUpdated;

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
            SyncMode syncMode = SyncMode.Immediate,
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

                progress?.Report("开始缓存加速初始化...");

                // 步骤1：检查是否已经加速
                if (_junctionService.IsJunction(sourcePath))
                {
                    progress?.Report("目录已经是Junction，可能已加速");
                    return false;
                }

                // 步骤2：生成路径并检查缓存冲突
                // 使用完整路径结构避免同名文件夹冲突
                var driveLetter = Path.GetPathRoot(sourcePath)?.Replace(":", "").Replace("\\", "") ?? "Unknown";
                var driveSpecificCacheRoot = Path.Combine(cacheRoot, driveLetter);

                // 获取不包含盘符的完整路径，保持正常的目录结构
                var pathWithoutDrive = sourcePath.Substring(Path.GetPathRoot(sourcePath)?.Length ?? 0);
                // 直接使用路径结构，不进行任何替换
                var cachePath = Path.Combine(driveSpecificCacheRoot, pathWithoutDrive);

                bool useSyncMode = false;
                if (Directory.Exists(cachePath))
                {
                    progress?.Report($"检测到缓存目录已存在：{cachePath}");

                    var choice = await ShowCacheConflictDialog(cachePath, progress);

                    if (choice == CacheConflictChoice.Cancel)
                    {
                        progress?.Report("用户取消操作");
                        return false;
                    }

                    useSyncMode = (choice == CacheConflictChoice.SyncMode);
                    progress?.Report("用户选择同步模式：与现有缓存同步");
                }
                else
                {
                    progress?.Report("缓存目录不存在，将创建新的缓存");
                }

                // 步骤3：确认开始后才进行路径操作
                progress?.Report("开始创建缓存目录和备份路径...");

                // 确保驱动器专用缓存根目录存在
                if (!Directory.Exists(driveSpecificCacheRoot))
                {
                    Directory.CreateDirectory(driveSpecificCacheRoot);
                    progress?.Report($"创建驱动器专用缓存目录：{driveSpecificCacheRoot}");
                }

                var originalPath = $"{sourcePath}.original";

                // 步骤4：复制到缓存（使用Robocopy+FastCopy组合）
                progress?.Report($"复制数据到缓存：{sourcePath} -> {cachePath}");
                if (!await CopyDirectoryUsingRobocopyWithFastCopyVerify(sourcePath, cachePath, useSyncMode, progress))
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

                // 步骤3.5：设置原始目录隐藏属性
                progress?.Report("设置原始目录隐藏属性...");
                SetDirectoryHidden(originalPath, progress);

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
        /// <summary>
        /// 暂停缓存加速，只移除Junction链接但保留配置和缓存文件
        /// </summary>
        public async Task<bool> PauseCacheAcceleration(
            string mountPoint,
            string originalPath,
            string cachePath,
            IProgress<string>? progress = null)
        {
            try
            {
                progress?.Report("开始暂停缓存加速...");

                // 步骤1：执行最后一次同步
                progress?.Report("执行最后一次同步...");
                await _fileSyncService.ForceSync(cachePath, progress);

                // 步骤2：停止文件同步监控
                progress?.Report("停止文件同步监控...");
                _fileSyncService.StopMonitoring(cachePath, progress);

                // 步骤3：删除Junction
                progress?.Report($"删除Junction：{mountPoint}");
                if (_junctionService.IsJunction(mountPoint))
                {
                    if (!_junctionService.RemoveJunction(mountPoint, progress))
                    {
                        progress?.Report("删除Junction失败");
                        return false;
                    }
                }

                // 步骤4：恢复原始目录
                var actualOriginalPath = originalPath;
                var expectedOriginalPath = mountPoint + ".original";

                // 优先查找 mountPoint + ".original" 目录
                if (Directory.Exists(expectedOriginalPath))
                {
                    actualOriginalPath = expectedOriginalPath;
                    progress?.Report($"找到备份目录：{expectedOriginalPath}");
                }

                if (Directory.Exists(actualOriginalPath))
                {
                    progress?.Report($"恢复原始目录：{actualOriginalPath} -> {mountPoint}");
                    Directory.Move(actualOriginalPath, mountPoint);
                }
                else
                {
                    progress?.Report($"警告：未找到原始目录 {actualOriginalPath}");
                    return false;
                }

                progress?.Report("暂停完成");
                return true;
            }
            catch (Exception ex)
            {
                progress?.Report($"暂停缓存加速时出错: {ex.Message}");
                return false;
            }
        }

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
                // 智能查找.original目录（解决OriginalPath状态不同步问题）
                var actualOriginalPath = originalPath;
                var expectedOriginalPath = mountPoint + ".original";

                // 优先查找 mountPoint + ".original" 目录（更可靠）
                if (Directory.Exists(expectedOriginalPath))
                {
                    actualOriginalPath = expectedOriginalPath;
                    progress?.Report($"找到备份目录：{expectedOriginalPath}");
                }
                else if (!Directory.Exists(originalPath))
                {
                    // 如果expected目录不存在，且original目录也不存在，尝试其他可能的备份位置
                    progress?.Report($"警告：未找到预期的备份目录 {expectedOriginalPath}，也未找到 {originalPath}");
                }
                else if (originalPath == mountPoint)
                {
                    // 如果originalPath和mountPoint相同，说明状态不同步，强制查找.original目录
                    progress?.Report($"检测到状态不同步（originalPath == mountPoint），强制查找备份目录");
                    if (Directory.Exists(expectedOriginalPath))
                    {
                        actualOriginalPath = expectedOriginalPath;
                        progress?.Report($"找到备份目录：{expectedOriginalPath}");
                    }
                    else
                    {
                        progress?.Report($"错误：无法找到备份目录 {expectedOriginalPath}");
                    }
                }

                progress?.Report($"恢复原始目录：{actualOriginalPath} -> {mountPoint}");
                if (Directory.Exists(actualOriginalPath))
                {
                    if (!_junctionService.SafeRenameDirectory(actualOriginalPath, mountPoint, progress))
                    {
                        progress?.Report("恢复原始目录失败");
                        return false;
                    }
                }
                else
                {
                    progress?.Report($"警告：未找到原始目录备份，跳过恢复步骤");
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

                // 检查原始目录是否具有隐藏属性
                if (!IsDirectoryHidden(originalPath))
                {
                    progress?.Report($"警告：原始目录缺少隐藏属性：{originalPath}");
                    progress?.Report("正在设置隐藏属性...");
                    SetDirectoryHidden(originalPath, progress);
                }
                else
                {
                    progress?.Report($"原始目录隐藏属性正常：{originalPath}");
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

        // 新的异步批量复制方法 - 使用FastCopy
        private async Task CopyDirectoryRecursiveAsync(string sourcePath, string targetPath, IProgress<string>? progress, CancellationToken cancellationToken = default)
        {
            // 创建目标目录
            if (!Directory.Exists(targetPath))
            {
                Directory.CreateDirectory(targetPath);
            }

            var batchProgressReporter = new BatchProgressReporter(progress, 100);
            var tasks = new List<Task>();
            const long LARGE_FILE_THRESHOLD = 50 * 1024 * 1024; // 50MB以上使用FastCopy

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

                // 大文件使用FastCopy
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
        /// 使用FastCopy复制大文件，获得最佳性能
        /// </summary>
        private async Task CopyLargeFileAsync(string sourceFile, string sourcePath, string targetPath,
            BatchProgressReporter progressReporter, CancellationToken cancellationToken)
        {
            try
            {
                var fileName = Path.GetFileName(sourceFile);
                var targetFile = Path.Combine(targetPath, fileName);

                // 使用FastCopy的高性能文件操作
                var success = await _fastCopyService.CopyWithVerifyAsync(sourceFile, targetFile);

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
                    // 如果FastCopy失败，回退到传统方法
                    await FallbackCopyLargeFileAsync(sourceFile, targetFile, progressReporter, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                // 出错时回退到传统复制方法
                LogMessage?.Invoke(this, $"FastCopy失败，回退到传统复制: {ex.Message}");
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
        /// 显示缓存冲突对话框（目前简化实现，后续应该改为WPF对话框）
        /// </summary>
        private async Task<CacheConflictChoice> ShowCacheConflictDialog(string cachePath, IProgress<string>? progress)
        {
            progress?.Report("缓存目录已存在，可选择：");
            progress?.Report("1. 同步模式：保留现有缓存，仅同步差异文件（推荐）");
            progress?.Report("2. 取消操作");
            progress?.Report("当前默认使用同步模式");

            // TODO: 实现真正的WPF对话框供用户选择
            // 目前返回同步模式作为默认选择
            return await Task.FromResult(CacheConflictChoice.SyncMode);
        }

        /// <summary>
        /// 使用Robocopy+FastCopy组合进行高性能目录复制和校验
        /// </summary>
        private async Task<bool> CopyDirectoryUsingRobocopyWithFastCopyVerify(string sourcePath, string targetPath, bool useSyncMode, IProgress<string>? progress)
        {
            try
            {
                if (useSyncMode)
                {
                    // 同步模式：使用Robocopy智能同步
                    progress?.Report("使用Robocopy同步模式：智能同步差异文件");
                    return await ExecuteRobocopyForSyncAsync(sourcePath, targetPath, progress);
                }
                else
                {
                    // 新目录：使用Robocopy高速复制 + FastCopy校验
                    progress?.Report("使用Robocopy高速多线程复制 + FastCopy完整性校验");

                    // 阶段1：Robocopy 高速多线程复制
                    progress?.Report("阶段1/2：Robocopy多线程复制中...");
                    bool copySuccess = await ExecuteRobocopyAsync(sourcePath, targetPath, progress);

                    if (!copySuccess)
                    {
                        progress?.Report("Robocopy复制失败");
                        return false;
                    }

                    progress?.Report("阶段1/2：Robocopy复制完成");

                    // 阶段2：FastCopy 完整性校验
                    progress?.Report("阶段2/2：FastCopy完整性校验中...");
                    bool verifySuccess = await ExecuteFastCopyVerifyAsync(sourcePath, targetPath, progress);

                    if (!verifySuccess)
                    {
                        progress?.Report("FastCopy校验失败，数据可能不完整");
                        return false;
                    }

                    progress?.Report("✅ Robocopy+FastCopy组合复制和校验完成");
                    return true;
                }
            }
            catch (Exception ex)
            {
                progress?.Report($"Robocopy+FastCopy组合复制异常：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 执行Robocopy多线程复制
        /// </summary>
        private async Task<bool> ExecuteRobocopyAsync(string sourcePath, string targetPath, IProgress<string>? progress)
        {
            try
            {
                // 从配置读取Robocopy参数
                var robocopyConfig = GetRobocopyConfig();

                // 构建最优化的Robocopy命令参数
                var argumentsList = new List<string>
                {
                    $"\"{sourcePath}\"",
                    $"\"{targetPath}\"",
                    "/E",                              // 复制所有子目录（包括空目录）
                    "/COPYALL",                        // 复制所有文件信息（数据+属性+时间戳+安全性+所有者+审计）
                    $"/MT:{robocopyConfig.ThreadCount}", // 多线程复制
                    $"/R:{robocopyConfig.RetryCount}",   // 重试次数
                    $"/W:{robocopyConfig.RetryWaitSeconds}", // 重试等待时间
                };

                // 性能优化参数
                if (robocopyConfig.EnableUnbufferedIO)
                    argumentsList.Add("/J");           // 无缓冲I/O（大文件优化）

                // 不使用可重启模式，确保缓存一致性
                // if (robocopyConfig.EnableRestart)
                //     argumentsList.Add("/ZB");      // 已禁用：为确保缓存一致性从头构建

                if (robocopyConfig.EnableLowSpaceMode)
                    argumentsList.Add("/LFSM");        // 低空间模式

                if (robocopyConfig.EnableCompression)
                    argumentsList.Add("/COMPRESS");    // 网络压缩

                if (robocopyConfig.EnableSparseFiles)
                    argumentsList.Add("/SPARSE");      // 保持稀疏文件

                // 日志和进度参数
                if (robocopyConfig.ShowProgress)
                {
                    argumentsList.Add("/BYTES");       // 以字节显示大小
                    argumentsList.Add("/FP");          // 显示完整路径
                }

                if (robocopyConfig.ShowETA)
                    argumentsList.Add("/ETA");         // 显示预计完成时间

                // 日志参数（简化输出以减少性能影响）
                argumentsList.Add("/NFL");             // 不记录文件名
                argumentsList.Add("/NDL");             // 不记录目录名

                var arguments = string.Join(" ", argumentsList);

                // 输出完整的命令行，便于手动测试验证
                var fullCommandLine = $"robocopy {arguments}";
                progress?.Report($"执行Robocopy命令: {fullCommandLine}");
                progress?.Report($"工作目录: {Environment.CurrentDirectory}");
                progress?.Report($"当前用户: {Environment.UserName}");
                progress?.Report($"============ 请复制上面的命令到PowerShell手动测试 ============");

                var processStartInfo = new ProcessStartInfo
                {
                    FileName = "robocopy",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Environment.CurrentDirectory
                };

                using var process = new Process { StartInfo = processStartInfo };

                var outputBuilder = new List<string>();
                var errorBuilder = new List<string>();

                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        outputBuilder.Add(e.Data);

                        // 捕捉所有有用的进度信息
                        if (e.Data.Contains("Files :") ||
                            e.Data.Contains("Dirs :") ||
                            e.Data.Contains("Bytes :") ||
                            e.Data.Contains("Times :") ||
                            e.Data.Contains("Speed :") ||
                            e.Data.Contains("ETA:") ||
                            e.Data.Contains("%") ||
                            (e.Data.Contains("New File") && robocopyConfig.ShowProgress))
                        {
                            progress?.Report($"Robocopy: {e.Data.Trim()}");
                        }
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        errorBuilder.Add(e.Data);
                    }
                };

                if (!process.Start())
                {
                    progress?.Report("Robocopy进程启动失败");
                    return false;
                }

                progress?.Report($"Robocopy进程已启动，PID: {process.Id}");

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync();

                progress?.Report($"Robocopy进程已退出，PID: {process.Id}");

                // Robocopy退出码详细分析：
                // 0: 没有文件被复制 (成功)
                // 1: 所有文件被成功复制 (成功)
                // 2: 有额外文件/目录被检测到 (成功)
                // 3: 文件被复制且有额外文件/目录 (成功)
                // 4: 有不匹配文件/目录 (警告但可接受)
                // 8: 有文件无法复制 (部分失败但主体成功)
                // 16+: 严重错误

                // 判断是否为可接受的结果 (0-15，排除严重错误)
                bool isAcceptable = process.ExitCode < 16;
                bool hasPartialFailure = (process.ExitCode & 8) != 0; // 检查是否有部分失败
                bool hasSuccessfulCopy = (process.ExitCode & 1) != 0; // 检查是否有成功复制

                // 智能成功判断：对比总数和复制列是否完全一致
                bool isCompletelySuccessful = IsRobocopyCompletelySuccessful(outputBuilder);
                bool hasSignificantDataTransfer = CheckForSignificantDataTransfer(outputBuilder);
                bool isOfficialSuccess = process.ExitCode < 8;

                // 调试输出
                progress?.Report($"🔍 调试信息: 退出码={process.ExitCode}, 官方成功={isOfficialSuccess}, 完全成功={isCompletelySuccessful}, 数据传输={hasSignificantDataTransfer}");

                // 判断最终成功状态
                bool success;
                if (isOfficialSuccess)
                {
                    // 退出码 < 8，官方认为成功
                    success = true;
                    progress?.Report($"✅ Robocopy成功完成，退出码: {process.ExitCode}");
                }
                else if (isCompletelySuccessful && hasSignificantDataTransfer)
                {
                    // 虽然退出码 >= 8，但总数和复制列完全一致，视为成功
                    success = true;
                    progress?.Report($"✅ Robocopy实质成功，退出码: {process.ExitCode} (所有项目完全复制成功)");
                }
                else
                {
                    // 真正的失败
                    success = false;
                    progress?.Report($"❌ Robocopy失败，退出码: {process.ExitCode}");
                    if (errorBuilder.Count > 0)
                    {
                        progress?.Report($"错误信息: {string.Join("; ", errorBuilder.Take(5))}");
                    }
                    if (outputBuilder.Count > 0)
                    {
                        progress?.Report($"输出信息: {string.Join("; ", outputBuilder.TakeLast(5))}");
                    }
                }

                return success;
            }
            catch (Exception ex)
            {
                progress?.Report($"执行Robocopy异常：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 执行FastCopy完整性校验
        /// </summary>
        private async Task<bool> ExecuteFastCopyVerifyAsync(string sourcePath, string targetPath, IProgress<string>? progress)
        {
            try
            {
                // 使用FastCopy进行校验
                var verifyOptions = new List<string>
                {
                    "/cmd=diff_only",  // 仅对比差异
                    "/verify",         // 启用校验
                    "/auto_close",     // 自动关闭
                    "/log"             // 输出日志
                };

                progress?.Report("FastCopy校验：检查复制完整性...");
                bool verifyResult = await _fastCopyService.CopyDirectoryAsync(sourcePath, targetPath, verifyOptions.ToArray());

                if (verifyResult)
                {
                    progress?.Report("✅ FastCopy校验通过：文件完整性确认");
                }
                else
                {
                    progress?.Report("❌ FastCopy校验失败：发现文件差异或损坏");
                }

                return verifyResult;
            }
            catch (Exception ex)
            {
                progress?.Report($"FastCopy校验异常：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 执行Robocopy同步模式复制（智能同步）
        /// </summary>
        private async Task<bool> ExecuteRobocopyForSyncAsync(string sourcePath, string targetPath, IProgress<string>? progress)
        {
            try
            {
                // 从配置读取Robocopy参数
                var robocopyConfig = GetRobocopyConfig();

                // 构建同步模式的Robocopy命令参数
                var argumentsList = new List<string>
                {
                    $"\"{sourcePath}\"",
                    $"\"{targetPath}\"",
                    "/E",                              // 复制所有子目录（包括空目录）
                    "/COPYALL",                        // 复制所有文件信息
                    $"/MT:{robocopyConfig.ThreadCount}", // 多线程复制
                    $"/R:{robocopyConfig.RetryCount}",   // 重试次数
                    $"/W:{robocopyConfig.RetryWaitSeconds}", // 重试等待时间
                    "/IM",                             // 包含修改的文件（确保同步一致性）
                };

                // 性能优化参数（同新建模式）
                if (robocopyConfig.EnableUnbufferedIO)
                    argumentsList.Add("/J");

                // 不使用可重启模式，确保同步一致性
                // if (robocopyConfig.EnableRestart)
                //     argumentsList.Add("/ZB");

                if (robocopyConfig.EnableLowSpaceMode)
                    argumentsList.Add("/LFSM");

                if (robocopyConfig.EnableCompression)
                    argumentsList.Add("/COMPRESS");

                if (robocopyConfig.EnableSparseFiles)
                    argumentsList.Add("/SPARSE");

                // 日志和进度参数
                if (robocopyConfig.ShowProgress)
                {
                    argumentsList.Add("/BYTES");
                    argumentsList.Add("/FP");
                }

                if (robocopyConfig.ShowETA)
                    argumentsList.Add("/ETA");

                argumentsList.Add("/NFL");
                argumentsList.Add("/NDL");

                var arguments = string.Join(" ", argumentsList);

                progress?.Report($"执行Robocopy同步: robocopy {arguments}");

                var processStartInfo = new ProcessStartInfo
                {
                    FileName = "robocopy",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = processStartInfo };

                var outputBuilder = new List<string>();
                var errorBuilder = new List<string>();

                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        outputBuilder.Add(e.Data);

                        // 捕捉同步模式的详细进度信息
                        if (e.Data.Contains("Files :") ||
                            e.Data.Contains("Dirs :") ||
                            e.Data.Contains("Bytes :") ||
                            e.Data.Contains("Times :") ||
                            e.Data.Contains("Speed :") ||
                            e.Data.Contains("ETA:") ||
                            e.Data.Contains("%") ||
                            e.Data.Contains("Modified File") ||
                            e.Data.Contains("Newer File") ||
                            e.Data.Contains("Same File"))
                        {
                            progress?.Report($"Robocopy同步: {e.Data.Trim()}");
                        }
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        errorBuilder.Add(e.Data);
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync();

                // Robocopy退出码：0-3表示成功
                bool success = process.ExitCode <= 3;

                if (success)
                {
                    progress?.Report($"Robocopy同步完成，退出码: {process.ExitCode}");
                }
                else
                {
                    progress?.Report($"Robocopy同步失败，退出码: {process.ExitCode}");
                    if (errorBuilder.Count > 0)
                    {
                        progress?.Report($"错误信息: {string.Join("; ", errorBuilder.Take(3))}");
                    }
                }

                return success;
            }
            catch (Exception ex)
            {
                progress?.Report($"执行Robocopy同步异常：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取Robocopy配置
        /// </summary>
        private RobocopyConfig GetRobocopyConfig()
        {
            try
            {
                var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                if (File.Exists(configPath))
                {
                    var configContent = File.ReadAllText(configPath);
                    var config = JsonSerializer.Deserialize<JsonElement>(configContent);

                    if (config.TryGetProperty("Robocopy", out var robocopyConfig))
                    {
                        return new RobocopyConfig
                        {
                            ThreadCount = robocopyConfig.TryGetProperty("ThreadCount", out var tc) ? tc.GetInt32() : 64,
                            RetryCount = robocopyConfig.TryGetProperty("RetryCount", out var rc) ? rc.GetInt32() : 3600,
                            RetryWaitSeconds = robocopyConfig.TryGetProperty("RetryWaitSeconds", out var rw) ? rw.GetInt32() : 1,
                            EnableUnbufferedIO = robocopyConfig.TryGetProperty("EnableUnbufferedIO", out var uio) ? uio.GetBoolean() : true,
                            EnableRestart = robocopyConfig.TryGetProperty("EnableRestart", out var er) ? er.GetBoolean() : false,
                            EnableLowSpaceMode = robocopyConfig.TryGetProperty("EnableLowSpaceMode", out var lsm) ? lsm.GetBoolean() : false,
                            EnableCompression = robocopyConfig.TryGetProperty("EnableCompression", out var comp) ? comp.GetBoolean() : false,
                            EnableSparseFiles = robocopyConfig.TryGetProperty("EnableSparseFiles", out var sf) ? sf.GetBoolean() : false,
                            ShowProgress = robocopyConfig.TryGetProperty("ShowProgress", out var sp) ? sp.GetBoolean() : true,
                            ShowETA = robocopyConfig.TryGetProperty("ShowETA", out var eta) ? eta.GetBoolean() : true
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                // 配置读取失败时使用默认值
                LogMessage?.Invoke(this, $"读取Robocopy配置失败，使用默认值: {ex.Message}");
            }

            // 返回默认配置
            return new RobocopyConfig
            {
                ThreadCount = 64,
                RetryCount = 3600,
                RetryWaitSeconds = 1,
                EnableUnbufferedIO = true,
                EnableRestart = false,
                EnableLowSpaceMode = false,
                EnableCompression = false,
                EnableSparseFiles = false,
                ShowProgress = true,
                ShowETA = true
            };
        }

        /// <summary>
        /// 检查Robocopy是否完全成功：对比总数和复制列是否完全一致
        /// </summary>
        private bool IsRobocopyCompletelySuccessful(List<string> outputLines)
        {
            bool allRowsMatch = true;
            int checkedRows = 0;

            foreach (var line in outputLines)
            {
                // 匹配目录、文件、字节这三行的统计
                if (line.Contains("目录:") || line.Contains("文件:") || line.Contains("字节:"))
                {
                    var numbers = System.Text.RegularExpressions.Regex.Matches(line, @"\d+");
                    if (numbers.Count >= 2)
                    {
                        // 对比第1列（总数）和第2列（复制）是否相等
                        if (long.TryParse(numbers[0].Value, out long totalCount) &&
                            long.TryParse(numbers[1].Value, out long copiedCount))
                        {
                            checkedRows++;
                            bool rowMatches = (totalCount == copiedCount);

                            string rowType = line.Contains("目录:") ? "目录" :
                                           line.Contains("文件:") ? "文件" : "字节";

                            if (!rowMatches)
                            {
                                allRowsMatch = false;
                                LogMessage?.Invoke(this, $"❌ {rowType}行不匹配: 总数={totalCount}, 复制={copiedCount}");
                            }
                            else
                            {
                                LogMessage?.Invoke(this, $"✅ {rowType}行完全匹配: 总数={totalCount}, 复制={copiedCount}");
                            }
                        }
                    }
                }
            }

            LogMessage?.Invoke(this, $"🔍 检查结果: 检查了{checkedRows}行, 全部匹配={allRowsMatch}");
            return allRowsMatch && checkedRows >= 2; // 至少检查到2行（文件和字节）
        }

        /// <summary>
        /// 检查是否有显著的数据传输
        /// </summary>
        private bool CheckForSignificantDataTransfer(List<string> outputLines)
        {
            foreach (var line in outputLines)
            {
                // 检查字节传输统计：字节: 2970355320 2970355320 0 0 0 0
                if (line.Contains("字节:"))
                {
                    var numbers = System.Text.RegularExpressions.Regex.Matches(line, @"\d+");
                    if (numbers.Count >= 2)
                    {
                        // 第二个数字是实际复制的字节数
                        if (long.TryParse(numbers[1].Value, out long copiedBytes))
                        {
                            return copiedBytes > 0;
                        }
                    }
                }

                // 检查文件复制统计：文件: 289 289 0 0 0 0
                if (line.Contains("文件:"))
                {
                    var numbers = System.Text.RegularExpressions.Regex.Matches(line, @"\d+");
                    if (numbers.Count >= 2)
                    {
                        // 第二个数字是复制的文件数
                        if (int.TryParse(numbers[1].Value, out int copiedFiles))
                        {
                            return copiedFiles > 0;
                        }
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// 设置目录隐藏属性
        /// </summary>
        private bool SetDirectoryHidden(string directoryPath, IProgress<string>? progress = null)
        {
            try
            {
                if (!Directory.Exists(directoryPath))
                {
                    progress?.Report($"目录不存在，无法设置隐藏属性：{directoryPath}");
                    return false;
                }

                var directoryInfo = new DirectoryInfo(directoryPath);

                // 检查是否已经有隐藏属性
                if ((directoryInfo.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
                {
                    progress?.Report($"目录已具有隐藏属性：{directoryPath}");
                    return true;
                }

                // 添加隐藏属性
                directoryInfo.Attributes |= FileAttributes.Hidden;
                progress?.Report($"已设置目录隐藏属性：{directoryPath}");
                return true;
            }
            catch (Exception ex)
            {
                progress?.Report($"设置目录隐藏属性失败：{directoryPath}, 错误：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 检查目录是否具有隐藏属性
        /// </summary>
        public bool IsDirectoryHidden(string directoryPath)
        {
            try
            {
                if (!Directory.Exists(directoryPath))
                {
                    return false;
                }

                var directoryInfo = new DirectoryInfo(directoryPath);
                return (directoryInfo.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Dispose()
        {
            _fileSyncService?.Dispose();
            _errorRecovery?.Dispose();
            // FastCopyService 无需手动释放
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

    /// <summary>
    /// 缓存冲突选择
    /// </summary>
    public enum CacheConflictChoice
    {
        Cancel,
        SyncMode
    }

    /// <summary>
    /// Robocopy配置类
    /// </summary>
    public class RobocopyConfig
    {
        public int ThreadCount { get; set; } = 64;
        public int RetryCount { get; set; } = 3;
        public int RetryWaitSeconds { get; set; } = 1;
        public bool EnableUnbufferedIO { get; set; } = true;
        public bool EnableRestart { get; set; } = true;
        public bool EnableLowSpaceMode { get; set; } = true;
        public bool EnableCompression { get; set; } = true;
        public bool EnableSparseFiles { get; set; } = true;
        public bool ShowProgress { get; set; } = true;
        public bool ShowETA { get; set; } = true;
    }
}