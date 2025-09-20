using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CacheMax.GUI.ViewModels;

namespace CacheMax.GUI.Services
{
    public enum SyncMode
    {
        Immediate,  // 即时同步 (延迟 < 100ms)
        Batch,      // 批量同步 (延迟 1-5秒)
        Periodic    // 定期同步 (延迟 30-60秒)
    }

    public class FileSyncService : IDisposable
    {
        private readonly Dictionary<string, FileSystemWatcher> _watchers = new();
        private readonly ConcurrentQueue<SyncOperation> _syncQueue = new();
        private readonly Dictionary<string, SyncConfiguration> _syncConfigs = new();
        private readonly Dictionary<string, FileOperationAnalyzer> _analyzers = new();
        private readonly Dictionary<string, SyncQueueItemViewModel> _queueItems = new();

        private Timer? _batchTimer;
        private Timer? _periodicTimer;
        private Timer? _intelligentTimer;
        private Timer? _statsTimer;
        private readonly object _lockObject = new object();
        private bool _disposed = false;

        // 统计数据
        private int _completedOperations = 0;
        private int _failedOperations = 0;
        private long _bytesProcessed = 0;
        private readonly List<TimeSpan> _processingTimes = new();
        private const int MaxProcessingTimeRecords = 100;

        public event EventHandler<SyncEventArgs>? SyncCompleted;
        public event EventHandler<SyncEventArgs>? SyncFailed;
        public event EventHandler<string>? LogMessage;
        public event EventHandler<SyncStatsEventArgs>? StatsUpdated;
        public event EventHandler<SyncQueueEventArgs>? QueueItemAdded;
        public event EventHandler<SyncQueueEventArgs>? QueueItemUpdated;
        public event EventHandler<SyncQueueEventArgs>? QueueItemRemoved;

        public class SyncConfiguration
        {
            public string CachePath { get; set; } = string.Empty;
            public string OriginalPath { get; set; } = string.Empty;
            public SyncMode Mode { get; set; } = SyncMode.Batch;
            public int DelaySeconds { get; set; } = 3;
            public bool Enabled { get; set; } = true;
        }

        public class SyncOperation
        {
            public string FilePath { get; set; } = string.Empty;
            public string SourceRoot { get; set; } = string.Empty;
            public string TargetRoot { get; set; } = string.Empty;
            public WatcherChangeTypes ChangeType { get; set; }
            public DateTime Timestamp { get; set; } = DateTime.Now;
            public SyncMode Mode { get; set; }
        }

        public class SyncEventArgs : EventArgs
        {
            public string FilePath { get; set; } = string.Empty;
            public bool Success { get; set; }
            public string? Message { get; set; }
            public TimeSpan Duration { get; set; }
        }

        public class SyncStatsEventArgs : EventArgs
        {
            public int QueueCount { get; set; }
            public int CompletedOperations { get; set; }
            public int FailedOperations { get; set; }
            public long BytesProcessed { get; set; }
            public TimeSpan AverageProcessingTime { get; set; }
            public DateTime LastUpdate { get; set; } = DateTime.Now;
            public string SyncMode { get; set; } = string.Empty;
        }

        public class FileOperationAnalyzer
        {
            private readonly Dictionary<string, FileAccessPattern> _patterns = new();
            private readonly Queue<FileOperationRecord> _recentOperations = new();
            private const int MaxRecentOperations = 1000;

            public class FileAccessPattern
            {
                public int AccessCount { get; set; }
                public DateTime LastAccess { get; set; }
                public double AverageInterval { get; set; }
                public bool IsFrequentlyAccessed => AccessCount > 5 && AverageInterval < 60;
                public SyncPriority Priority { get; set; } = SyncPriority.Normal;
            }

            public class FileOperationRecord
            {
                public string FilePath { get; set; } = string.Empty;
                public DateTime Timestamp { get; set; }
                public WatcherChangeTypes OperationType { get; set; }
                public long FileSize { get; set; }
            }

            public enum SyncPriority
            {
                Low,      // 很少访问的文件，延迟同步
                Normal,   // 正常文件，按配置同步
                High,     // 频繁访问的文件，优先同步
                Critical  // 重要文件（如数据库），立即同步
            }

            public void RecordOperation(string filePath, WatcherChangeTypes operationType, long fileSize = 0)
            {
                var operation = new FileOperationRecord
                {
                    FilePath = filePath,
                    Timestamp = DateTime.Now,
                    OperationType = operationType,
                    FileSize = fileSize
                };

                _recentOperations.Enqueue(operation);
                if (_recentOperations.Count > MaxRecentOperations)
                {
                    _recentOperations.Dequeue();
                }

                UpdateAccessPattern(filePath);
            }

            private void UpdateAccessPattern(string filePath)
            {
                if (!_patterns.TryGetValue(filePath, out var pattern))
                {
                    pattern = new FileAccessPattern
                    {
                        LastAccess = DateTime.Now,
                        AccessCount = 1
                    };
                    _patterns[filePath] = pattern;
                }
                else
                {
                    var interval = (DateTime.Now - pattern.LastAccess).TotalSeconds;
                    pattern.AverageInterval = (pattern.AverageInterval * pattern.AccessCount + interval) / (pattern.AccessCount + 1);
                    pattern.AccessCount++;
                    pattern.LastAccess = DateTime.Now;
                }

                // 智能确定优先级
                pattern.Priority = DeterminePriority(filePath, pattern);
            }

            private SyncPriority DeterminePriority(string filePath, FileAccessPattern pattern)
            {
                var fileName = Path.GetFileName(filePath).ToLower();
                var extension = Path.GetExtension(filePath).ToLower();

                // 关键文件类型立即同步
                if (extension == ".db" || extension == ".sqlite" || extension == ".mdb" ||
                    fileName.Contains("database") || fileName.Contains("config"))
                {
                    return SyncPriority.Critical;
                }

                // 频繁访问的文件高优先级
                if (pattern.IsFrequentlyAccessed)
                {
                    return SyncPriority.High;
                }

                // 很少访问的大文件低优先级
                if (pattern.AccessCount < 3 && pattern.AverageInterval > 300)
                {
                    return SyncPriority.Low;
                }

                return SyncPriority.Normal;
            }

            public SyncPriority GetFilePriority(string filePath)
            {
                return _patterns.TryGetValue(filePath, out var pattern) ? pattern.Priority : SyncPriority.Normal;
            }

            public bool IsFrequentlyAccessed(string filePath)
            {
                return _patterns.TryGetValue(filePath, out var pattern) && pattern.IsFrequentlyAccessed;
            }

            public int GetRecentOperationCount(TimeSpan window)
            {
                var cutoff = DateTime.Now - window;
                return _recentOperations.Count(op => op.Timestamp >= cutoff);
            }
        }

        public FileSyncService()
        {
            // 启动批量处理定时器
            _batchTimer = new Timer(ProcessBatchSync, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            _periodicTimer = new Timer(ProcessPeriodicSync, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            _intelligentTimer = new Timer(ProcessIntelligentSync, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// 开始监控目录
        /// </summary>
        public bool StartMonitoring(string cachePath, string originalPath, SyncMode mode = SyncMode.Batch, int delaySeconds = 3, IProgress<string>? progress = null)
        {
            try
            {
                if (!Directory.Exists(cachePath))
                {
                    progress?.Report($"缓存目录不存在：{cachePath}");
                    return false;
                }

                if (!Directory.Exists(originalPath))
                {
                    progress?.Report($"原始目录不存在：{originalPath}");
                    return false;
                }

                lock (_lockObject)
                {
                    // 如果已经在监控，先停止
                    if (_watchers.ContainsKey(cachePath))
                    {
                        StopMonitoring(cachePath, progress);
                    }

                    progress?.Report($"开始监控：{cachePath} -> {originalPath}");

                    // 创建配置
                    _syncConfigs[cachePath] = new SyncConfiguration
                    {
                        CachePath = cachePath,
                        OriginalPath = originalPath,
                        Mode = mode,
                        DelaySeconds = delaySeconds,
                        Enabled = true
                    };

                    // 创建操作分析器
                    _analyzers[cachePath] = new FileOperationAnalyzer();

                    // 创建文件监控器
                    var watcher = new FileSystemWatcher(cachePath)
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                     NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                        IncludeSubdirectories = true,
                        EnableRaisingEvents = true
                    };

                    // 绑定事件处理器
                    watcher.Created += (s, e) => OnFileChanged(e.FullPath, cachePath, WatcherChangeTypes.Created);
                    watcher.Changed += (s, e) => OnFileChanged(e.FullPath, cachePath, WatcherChangeTypes.Changed);
                    watcher.Deleted += (s, e) => OnFileChanged(e.FullPath, cachePath, WatcherChangeTypes.Deleted);
                    watcher.Renamed += (s, e) => OnFileRenamed(e.OldFullPath, e.FullPath, cachePath);

                    _watchers[cachePath] = watcher;

                    LogMessage?.Invoke(this, $"开始监控：{cachePath} (模式：{mode}，延迟：{delaySeconds}秒)");
                    progress?.Report("文件监控启动成功");
                    return true;
                }
            }
            catch (Exception ex)
            {
                progress?.Report($"启动监控异常：{ex.Message}");
                LogMessage?.Invoke(this, $"启动监控异常：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 停止监控目录
        /// </summary>
        public bool StopMonitoring(string cachePath, IProgress<string>? progress = null)
        {
            try
            {
                lock (_lockObject)
                {
                    if (!_watchers.TryGetValue(cachePath, out var watcher))
                    {
                        progress?.Report($"没有找到对应的监控器：{cachePath}");
                        return false;
                    }

                    progress?.Report($"停止监控：{cachePath}");

                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                    _watchers.Remove(cachePath);
                    _syncConfigs.Remove(cachePath);
                    _analyzers.Remove(cachePath);

                    LogMessage?.Invoke(this, $"停止监控：{cachePath}");
                    progress?.Report("文件监控停止成功");
                    return true;
                }
            }
            catch (Exception ex)
            {
                progress?.Report($"停止监控异常：{ex.Message}");
                LogMessage?.Invoke(this, $"停止监控异常：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 立即同步指定路径的所有文件
        /// </summary>
        public async Task<bool> ForceSync(string cachePath, IProgress<string>? progress = null)
        {
            try
            {
                if (!_syncConfigs.TryGetValue(cachePath, out var config))
                {
                    progress?.Report($"未找到同步配置：{cachePath}");
                    return false;
                }

                progress?.Report($"开始强制同步：{cachePath} -> {config.OriginalPath}");

                var (totalSyncCount, totalErrorCount) = await SyncDirectoryRecursiveAsync(config.CachePath, config.OriginalPath, progress);

                progress?.Report($"强制同步完成：成功 {totalSyncCount} 个文件，失败 {totalErrorCount} 个文件");
                LogMessage?.Invoke(this, $"强制同步完成：{cachePath}，成功 {totalSyncCount}，失败 {totalErrorCount}");

                return totalErrorCount == 0;
            }
            catch (Exception ex)
            {
                progress?.Report($"强制同步异常：{ex.Message}");
                LogMessage?.Invoke(this, $"强制同步异常：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取同步队列状态
        /// </summary>
        public (int Count, DateTime? OldestTimestamp) GetQueueStatus()
        {
            if (_syncQueue.IsEmpty)
                return (0, null);

            var items = _syncQueue.ToArray();
            return (items.Length, items.Min(x => x.Timestamp));
        }

        /// <summary>
        /// 获取所有监控状态
        /// </summary>
        public Dictionary<string, SyncConfiguration> GetMonitoringStatus()
        {
            lock (_lockObject)
            {
                return new Dictionary<string, SyncConfiguration>(_syncConfigs);
            }
        }

        private void OnFileChanged(string filePath, string cacheRoot, WatcherChangeTypes changeType)
        {
            if (!_syncConfigs.TryGetValue(cacheRoot, out var config) || !config.Enabled)
                return;

            // 过滤临时文件
            if (IsTemporaryFile(filePath))
                return;

            // 记录操作用于智能分析
            if (_analyzers.TryGetValue(cacheRoot, out var analyzer))
            {
                var fileSize = 0L;
                try
                {
                    if (File.Exists(filePath))
                    {
                        fileSize = new FileInfo(filePath).Length;
                    }
                }
                catch { }

                analyzer.RecordOperation(filePath, changeType, fileSize);
            }

            var operation = new SyncOperation
            {
                FilePath = filePath,
                SourceRoot = config.CachePath,
                TargetRoot = config.OriginalPath,
                ChangeType = changeType,
                Timestamp = DateTime.Now,
                Mode = config.Mode
            };

            // 创建队列项目视图模型
            var queueItem = new SyncQueueItemViewModel
            {
                FilePath = filePath,
                Status = "等待中",
                CreatedAt = DateTime.Now
            };

            // 尝试获取文件大小
            try
            {
                if (File.Exists(filePath))
                {
                    queueItem.FileSize = new FileInfo(filePath).Length;
                }
            }
            catch { }

            // 添加到队列项目字典
            var itemKey = $"{filePath}_{DateTime.Now.Ticks}";
            lock (_lockObject)
            {
                _queueItems[itemKey] = queueItem;
            }

            // 触发队列项目添加事件
            QueueItemAdded?.Invoke(this, new SyncQueueEventArgs(queueItem, "Added"));

            // 智能同步决策
            var priority = analyzer?.GetFilePriority(filePath) ?? FileOperationAnalyzer.SyncPriority.Normal;

            if (config.Mode == SyncMode.Immediate || priority == FileOperationAnalyzer.SyncPriority.Critical)
            {
                // 即时同步（包括关键文件）
                queueItem.Status = "处理中";
                QueueItemUpdated?.Invoke(this, new SyncQueueEventArgs(queueItem, "Processing"));

                Task.Run(() => ProcessSyncOperationWithTracking(operation, queueItem, itemKey));
                LogMessage?.Invoke(this, $"立即同步文件：{Path.GetFileName(filePath)} (优先级：{priority})");
            }
            else if (priority == FileOperationAnalyzer.SyncPriority.High)
            {
                // 高优先级文件，缩短延迟
                operation.Mode = SyncMode.Batch; // 强制批量模式以便快速处理
                _syncQueue.Enqueue(operation);
                LogMessage?.Invoke(this, $"高优先级文件入队：{Path.GetFileName(filePath)}");
            }
            else
            {
                // 加入队列等待批量处理
                _syncQueue.Enqueue(operation);
            }
        }

        private void OnFileRenamed(string oldPath, string newPath, string cacheRoot)
        {
            OnFileChanged(oldPath, cacheRoot, WatcherChangeTypes.Deleted);
            OnFileChanged(newPath, cacheRoot, WatcherChangeTypes.Created);
        }

        private void ProcessBatchSync(object? state)
        {
            if (_syncQueue.IsEmpty)
                return;

            var operations = new List<SyncOperation>();
            var cutoffTime = DateTime.Now;

            // 收集需要处理的操作
            while (_syncQueue.TryDequeue(out var operation))
            {
                if (operation.Mode == SyncMode.Batch)
                {
                    var config = _syncConfigs.GetValueOrDefault(operation.SourceRoot);
                    if (config != null && (cutoffTime - operation.Timestamp).TotalSeconds >= config.DelaySeconds)
                    {
                        operations.Add(operation);
                    }
                    else
                    {
                        // 还没到时间，重新入队
                        _syncQueue.Enqueue(operation);
                    }
                }
                else if (operation.Mode == SyncMode.Periodic)
                {
                    // 周期性同步由另一个定时器处理
                    _syncQueue.Enqueue(operation);
                }
            }

            // 批量处理操作
            if (operations.Count > 0)
            {
                Task.Run(() =>
                {
                    foreach (var op in operations)
                    {
                        ProcessSyncOperation(op);
                    }
                });
            }
        }

        private void ProcessPeriodicSync(object? state)
        {
            var operations = new List<SyncOperation>();

            // 收集周期性同步操作
            while (_syncQueue.TryDequeue(out var operation))
            {
                if (operation.Mode == SyncMode.Periodic)
                {
                    operations.Add(operation);
                }
                else
                {
                    _syncQueue.Enqueue(operation);
                }
            }

            // 按文件分组，只保留最新的操作
            var latestOperations = operations
                .GroupBy(op => op.FilePath)
                .Select(g => g.OrderByDescending(op => op.Timestamp).First())
                .ToList();

            if (latestOperations.Count > 0)
            {
                Task.Run(() =>
                {
                    foreach (var op in latestOperations)
                    {
                        ProcessSyncOperation(op);
                    }
                });
            }
        }

        /// <summary>
        /// 智能同步处理：基于文件访问模式和系统负载优化同步策略
        /// </summary>
        private void ProcessIntelligentSync(object? state)
        {
            if (_syncQueue.IsEmpty)
                return;

            var operations = new List<SyncOperation>();
            var now = DateTime.Now;

            // 收集队列中的操作进行智能处理
            while (_syncQueue.TryDequeue(out var operation))
            {
                operations.Add(operation);
            }

            if (operations.Count == 0)
                return;

            // 按优先级和时间排序
            var prioritizedOperations = operations
                .GroupBy(op => op.FilePath)
                .Select(g => g.OrderByDescending(op => op.Timestamp).First()) // 每个文件最新操作
                .Where(op => ShouldProcessOperation(op, now))
                .OrderByDescending(op => GetOperationPriority(op))
                .ThenBy(op => op.Timestamp)
                .ToList();

            // 将未处理的操作重新入队
            foreach (var op in operations.Except(prioritizedOperations))
            {
                _syncQueue.Enqueue(op);
            }

            // 智能批量处理
            if (prioritizedOperations.Count > 0)
            {
                LogMessage?.Invoke(this, $"智能同步处理：{prioritizedOperations.Count} 个操作");

                Task.Run(() =>
                {
                    foreach (var op in prioritizedOperations)
                    {
                        ProcessSyncOperation(op);
                    }
                });
            }
        }

        private bool ShouldProcessOperation(SyncOperation operation, DateTime now)
        {
            var config = _syncConfigs.GetValueOrDefault(operation.SourceRoot);
            if (config == null) return false;

            var analyzer = _analyzers.GetValueOrDefault(operation.SourceRoot);
            var priority = analyzer?.GetFilePriority(operation.FilePath) ?? FileOperationAnalyzer.SyncPriority.Normal;

            // 根据优先级决定处理时机
            var ageSeconds = (now - operation.Timestamp).TotalSeconds;

            return priority switch
            {
                FileOperationAnalyzer.SyncPriority.Critical => true, // 立即处理
                FileOperationAnalyzer.SyncPriority.High => ageSeconds >= Math.Max(1, config.DelaySeconds / 2), // 减半延迟
                FileOperationAnalyzer.SyncPriority.Normal => ageSeconds >= config.DelaySeconds, // 正常延迟
                FileOperationAnalyzer.SyncPriority.Low => ageSeconds >= config.DelaySeconds * 2, // 双倍延迟
                _ => ageSeconds >= config.DelaySeconds
            };
        }

        private int GetOperationPriority(SyncOperation operation)
        {
            var analyzer = _analyzers.GetValueOrDefault(operation.SourceRoot);
            var priority = analyzer?.GetFilePriority(operation.FilePath) ?? FileOperationAnalyzer.SyncPriority.Normal;

            return priority switch
            {
                FileOperationAnalyzer.SyncPriority.Critical => 100,
                FileOperationAnalyzer.SyncPriority.High => 75,
                FileOperationAnalyzer.SyncPriority.Normal => 50,
                FileOperationAnalyzer.SyncPriority.Low => 25,
                _ => 50
            };
        }

        private async void ProcessSyncOperationWithTracking(SyncOperation operation, SyncQueueItemViewModel queueItem, string itemKey)
        {
            try
            {
                queueItem.Status = "处理中";
                queueItem.Progress = 0;
                QueueItemUpdated?.Invoke(this, new SyncQueueEventArgs(queueItem, "Processing"));

                var startTime = DateTime.Now;
                var relativePath = Path.GetRelativePath(operation.SourceRoot, operation.FilePath);
                var targetPath = Path.Combine(operation.TargetRoot, relativePath);

                bool success = false;
                string message = "";

                switch (operation.ChangeType)
                {
                    case WatcherChangeTypes.Created:
                    case WatcherChangeTypes.Changed:
                        var syncResult = await SyncFileWithProgressAsync(operation.FilePath, targetPath, queueItem);
                        success = syncResult.Success;
                        message = syncResult.Message;
                        break;

                    case WatcherChangeTypes.Deleted:
                        var deleteResult = await DeleteFileAsync(targetPath);
                        success = deleteResult.Success;
                        message = deleteResult.Message;
                        queueItem.Progress = 100;
                        QueueItemUpdated?.Invoke(this, new SyncQueueEventArgs(queueItem, "Progress"));
                        break;
                }

                var duration = DateTime.Now - startTime;

                if (success)
                {
                    queueItem.Status = "完成";
                    queueItem.Progress = 100;
                    QueueItemUpdated?.Invoke(this, new SyncQueueEventArgs(queueItem, "Completed"));

                    // 延迟移除完成的项目
                    Task.Delay(5000).ContinueWith(_ =>
                    {
                        lock (_lockObject)
                        {
                            if (_queueItems.Remove(itemKey))
                            {
                                QueueItemRemoved?.Invoke(this, new SyncQueueEventArgs(queueItem, "Removed"));
                            }
                        }
                    });

                    SyncCompleted?.Invoke(this, new SyncEventArgs
                    {
                        FilePath = operation.FilePath,
                        Success = true,
                        Message = message,
                        Duration = duration
                    });
                }
                else
                {
                    queueItem.Status = "失败";
                    queueItem.ErrorMessage = message;
                    QueueItemUpdated?.Invoke(this, new SyncQueueEventArgs(queueItem, "Failed"));

                    SyncFailed?.Invoke(this, new SyncEventArgs
                    {
                        FilePath = operation.FilePath,
                        Success = false,
                        Message = message,
                        Duration = duration
                    });
                }
            }
            catch (Exception ex)
            {
                queueItem.Status = "失败";
                queueItem.ErrorMessage = ex.Message;
                QueueItemUpdated?.Invoke(this, new SyncQueueEventArgs(queueItem, "Failed"));

                SyncFailed?.Invoke(this, new SyncEventArgs
                {
                    FilePath = operation.FilePath,
                    Success = false,
                    Message = ex.Message,
                    Duration = TimeSpan.Zero
                });
            }
        }

        private async void ProcessSyncOperation(SyncOperation operation)
        {
            var startTime = DateTime.Now;
            try
            {
                var relativePath = Path.GetRelativePath(operation.SourceRoot, operation.FilePath);
                var targetPath = Path.Combine(operation.TargetRoot, relativePath);

                bool success = false;
                string message = "";

                switch (operation.ChangeType)
                {
                    case WatcherChangeTypes.Created:
                    case WatcherChangeTypes.Changed:
                        var syncResult = await SyncFileAsync(operation.FilePath, targetPath);
                        success = syncResult.Success;
                        message = syncResult.Message;
                        break;

                    case WatcherChangeTypes.Deleted:
                        var deleteResult = await DeleteFileAsync(targetPath);
                        success = deleteResult.Success;
                        message = deleteResult.Message;
                        break;
                }

                var duration = DateTime.Now - startTime;
                var eventArgs = new SyncEventArgs
                {
                    FilePath = operation.FilePath,
                    Success = success,
                    Message = message,
                    Duration = duration
                };

                if (success)
                {
                    SyncCompleted?.Invoke(this, eventArgs);
                    if (message.Contains("经过") && message.Contains("次尝试"))
                    {
                        LogMessage?.Invoke(this, $"同步成功（重试后）：{Path.GetFileName(operation.FilePath)} - {message}");
                    }
                }
                else
                {
                    SyncFailed?.Invoke(this, eventArgs);
                    LogMessage?.Invoke(this, $"同步失败：{operation.FilePath} - {message}");
                }
            }
            catch (Exception ex)
            {
                var duration = DateTime.Now - startTime;
                var eventArgs = new SyncEventArgs
                {
                    FilePath = operation.FilePath,
                    Success = false,
                    Message = ex.Message,
                    Duration = duration
                };

                SyncFailed?.Invoke(this, eventArgs);
                LogMessage?.Invoke(this, $"同步异常：{operation.FilePath} - {ex.Message}");
            }
        }

        private async Task<SafeFileOperations.FileOperationResult> SyncFileWithProgressAsync(string sourcePath, string targetPath, SyncQueueItemViewModel queueItem)
        {
            try
            {
                if (File.Exists(sourcePath))
                {
                    var retryConfig = new SafeFileOperations.RetryConfig
                    {
                        MaxAttempts = 5,
                        InitialDelay = TimeSpan.FromMilliseconds(200),
                        MaxDelay = TimeSpan.FromSeconds(5),
                        BackoffMultiplier = 1.5
                    };

                    var progress = new Progress<string>(msg =>
                    {
                        LogMessage?.Invoke(this, msg);

                        // 解析进度信息并更新队列项目
                        if (msg.Contains("正在复制") && msg.Contains("%"))
                        {
                            var percentIndex = msg.LastIndexOf("%");
                            if (percentIndex > 0)
                            {
                                var spaceIndex = msg.LastIndexOf(" ", percentIndex - 1);
                                if (spaceIndex >= 0 && double.TryParse(msg.Substring(spaceIndex + 1, percentIndex - spaceIndex - 1), out var percent))
                                {
                                    queueItem.Progress = percent;
                                    QueueItemUpdated?.Invoke(this, new SyncQueueEventArgs(queueItem, "Progress"));
                                }
                            }
                        }
                    });

                    return await SafeFileOperations.SafeCopyFileAsync(sourcePath, targetPath, true, retryConfig, progress);
                }
                else if (Directory.Exists(sourcePath))
                {
                    try
                    {
                        if (!Directory.Exists(targetPath))
                        {
                            Directory.CreateDirectory(targetPath);
                        }
                        queueItem.Progress = 100;
                        QueueItemUpdated?.Invoke(this, new SyncQueueEventArgs(queueItem, "Progress"));

                        return new SafeFileOperations.FileOperationResult
                        {
                            Success = true,
                            Message = "目录创建成功",
                            AttemptCount = 1
                        };
                    }
                    catch (Exception ex)
                    {
                        return new SafeFileOperations.FileOperationResult
                        {
                            Success = false,
                            Message = $"目录创建失败：{ex.Message}",
                            AttemptCount = 1,
                            LastException = ex
                        };
                    }
                }
                else
                {
                    return new SafeFileOperations.FileOperationResult
                    {
                        Success = false,
                        Message = "源文件不存在",
                        AttemptCount = 1
                    };
                }
            }
            catch (Exception ex)
            {
                return new SafeFileOperations.FileOperationResult
                {
                    Success = false,
                    Message = $"同步异常：{ex.Message}",
                    AttemptCount = 1,
                    LastException = ex
                };
            }
        }

        private async Task<SafeFileOperations.FileOperationResult> SyncFileAsync(string sourcePath, string targetPath)
        {
            try
            {
                if (File.Exists(sourcePath))
                {
                    // 使用安全文件操作进行复制
                    var retryConfig = new SafeFileOperations.RetryConfig
                    {
                        MaxAttempts = 5,
                        InitialDelay = TimeSpan.FromMilliseconds(200),
                        MaxDelay = TimeSpan.FromSeconds(5),
                        BackoffMultiplier = 1.5
                    };

                    var progress = new Progress<string>(msg => LogMessage?.Invoke(this, msg));
                    return await SafeFileOperations.SafeCopyFileAsync(sourcePath, targetPath, true, retryConfig, progress);
                }
                else if (Directory.Exists(sourcePath))
                {
                    // 创建目录
                    try
                    {
                        if (!Directory.Exists(targetPath))
                        {
                            Directory.CreateDirectory(targetPath);
                        }
                        return new SafeFileOperations.FileOperationResult
                        {
                            Success = true,
                            Message = "目录创建成功",
                            AttemptCount = 1
                        };
                    }
                    catch (Exception ex)
                    {
                        return new SafeFileOperations.FileOperationResult
                        {
                            Success = false,
                            Message = $"目录创建失败：{ex.Message}",
                            AttemptCount = 1,
                            LastException = ex
                        };
                    }
                }
                else
                {
                    return new SafeFileOperations.FileOperationResult
                    {
                        Success = false,
                        Message = "源文件不存在",
                        AttemptCount = 1
                    };
                }
            }
            catch (Exception ex)
            {
                return new SafeFileOperations.FileOperationResult
                {
                    Success = false,
                    Message = $"同步异常：{ex.Message}",
                    AttemptCount = 1,
                    LastException = ex
                };
            }
        }

        private async Task<SafeFileOperations.FileOperationResult> DeleteFileAsync(string targetPath)
        {
            try
            {
                if (File.Exists(targetPath))
                {
                    var retryConfig = new SafeFileOperations.RetryConfig
                    {
                        MaxAttempts = 3,
                        InitialDelay = TimeSpan.FromMilliseconds(100),
                        MaxDelay = TimeSpan.FromSeconds(2),
                        BackoffMultiplier = 2.0
                    };

                    var progress = new Progress<string>(msg => LogMessage?.Invoke(this, msg));
                    return await SafeFileOperations.SafeDeleteFileAsync(targetPath, retryConfig, progress);
                }
                else if (Directory.Exists(targetPath))
                {
                    try
                    {
                        Directory.Delete(targetPath, true);
                        return new SafeFileOperations.FileOperationResult
                        {
                            Success = true,
                            Message = "目录删除成功",
                            AttemptCount = 1
                        };
                    }
                    catch (Exception ex)
                    {
                        return new SafeFileOperations.FileOperationResult
                        {
                            Success = false,
                            Message = $"目录删除失败：{ex.Message}",
                            AttemptCount = 1,
                            LastException = ex
                        };
                    }
                }
                else
                {
                    return new SafeFileOperations.FileOperationResult
                    {
                        Success = true,
                        Message = "目标文件不存在（已删除）",
                        AttemptCount = 1
                    };
                }
            }
            catch (Exception ex)
            {
                return new SafeFileOperations.FileOperationResult
                {
                    Success = false,
                    Message = $"删除异常：{ex.Message}",
                    AttemptCount = 1,
                    LastException = ex
                };
            }
        }

        private async Task<(int syncCount, int errorCount)> SyncDirectoryRecursiveAsync(string sourceDir, string targetDir, IProgress<string>? progress)
        {
            var totalSyncCount = 0;
            var totalErrorCount = 0;

            try
            {
                // 确保目标目录存在
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                // 同步文件
                var files = Directory.GetFiles(sourceDir);
                foreach (var sourceFile in files)
                {
                    try
                    {
                        var fileName = Path.GetFileName(sourceFile);
                        var targetFile = Path.Combine(targetDir, fileName);

                        var result = await SyncFileAsync(sourceFile, targetFile);
                        if (result.Success)
                        {
                            totalSyncCount++;
                            if (result.AttemptCount > 1)
                            {
                                progress?.Report($"同步成功（重试后）：{fileName}");
                            }
                        }
                        else
                        {
                            totalErrorCount++;
                            progress?.Report($"同步文件失败：{fileName} - {result.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        totalErrorCount++;
                        progress?.Report($"同步文件异常：{sourceFile} - {ex.Message}");
                    }
                }

                // 递归同步子目录
                foreach (var sourceSubDir in Directory.GetDirectories(sourceDir))
                {
                    var dirName = Path.GetFileName(sourceSubDir);
                    var targetSubDir = Path.Combine(targetDir, dirName);
                    var (subSyncCount, subErrorCount) = await SyncDirectoryRecursiveAsync(sourceSubDir, targetSubDir, progress);
                    totalSyncCount += subSyncCount;
                    totalErrorCount += subErrorCount;
                }
            }
            catch (Exception ex)
            {
                totalErrorCount++;
                progress?.Report($"同步目录异常：{sourceDir} - {ex.Message}");
            }

            return (totalSyncCount, totalErrorCount);
        }

        private static bool IsTemporaryFile(string filePath)
        {
            var fileName = Path.GetFileName(filePath);

            // 过滤常见的临时文件
            if (fileName.StartsWith("~") ||
                fileName.StartsWith(".tmp") ||
                fileName.EndsWith(".tmp") ||
                fileName.EndsWith(".temp") ||
                fileName.Contains("~$"))
            {
                return true;
            }

            return false;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            lock (_lockObject)
            {
                // 停止所有监控
                foreach (var watcher in _watchers.Values)
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                _watchers.Clear();
                _syncConfigs.Clear();
            }

            // 停止定时器
            _batchTimer?.Dispose();
            _periodicTimer?.Dispose();
            _intelligentTimer?.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}