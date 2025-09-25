using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.Text.Json;
using CacheMax.GUI.ViewModels;

namespace CacheMax.GUI.Services
{
    public enum SyncMode
    {
        Immediate,  // 即时同步 (延迟 < 100ms)
        Periodic    // 定期同步 (延迟 30-60秒)
    }

    public class FileSyncService : IDisposable
    {
        private readonly Dictionary<string, FileSystemWatcher> _watchers = new();
        private readonly ConcurrentQueue<SyncOperation> _syncQueue = new();
        private readonly Dictionary<string, SyncConfiguration> _syncConfigs = new();
        private readonly Dictionary<string, FileOperationAnalyzer> _analyzers = new();
        private readonly Dictionary<string, SyncQueueItemViewModel> _queueItems = new();
        private readonly Dictionary<SyncOperation, (SyncQueueItemViewModel item, string key)> _operationToQueueItem = new();

        // 文件处理去重和并发控制
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _processingFiles = new();
        private SemaphoreSlim _fastCopyLimiter; // 可配置的FastCopy并发限制

        // 事件去抖动机制
        private readonly ConcurrentDictionary<string, DateTime> _lastEventTime = new();
        private const int EVENT_DEBOUNCE_MS = 500; // 500ms内的重复事件视为同一操作

        // 简化的文件操作服务
        private readonly FastCopyService _fastCopyService;

        // 异步日志系统
        private readonly AsyncLogger _logger;

        // Channel通信系统
        private readonly Channel<UIUpdateMessage> _uiUpdateChannel;
        private readonly CancellationTokenSource _serviceCancellation;

        private Timer? _periodicTimer;
        private Timer? _intelligentTimer;
        private Timer? _statsTimer;
        private readonly object _lockObject = new object();
        private bool _disposed = false;

        public FileSyncService()
        {
            _fastCopyService = FastCopyService.Instance;
            _logger = AsyncLogger.Instance;
            _serviceCancellation = new CancellationTokenSource();

            // 从配置文件读取FastCopy并发数
            var maxConcurrency = GetFastCopyMaxConcurrency();
            _fastCopyLimiter = new SemaphoreSlim(maxConcurrency, maxConcurrency);
            _logger.LogInfo($"FastCopy最大并发数设置为: {maxConcurrency}", "FileSyncService");

            // 创建UI更新Channel
            _uiUpdateChannel = Channel.CreateUnbounded<UIUpdateMessage>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            // 启动UI更新处理循环
            _ = Task.Run(() => ProcessUIUpdatesAsync(_serviceCancellation.Token));

            // 延迟启动定时器，避免初始化时阻塞
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000); // 延迟2秒启动
                _periodicTimer = new Timer(ProcessPeriodicSync, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
                _intelligentTimer = new Timer(ProcessIntelligentSync, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
                _statsTimer = new Timer(UpdateStats, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));

            });

            _logger.LogInfo("FileSyncService 初始化完成", "FileSyncService");
        }

        /// <summary>
        /// 完全异步的事件调用方法 - 避免阻塞UI线程
        /// </summary>
        private void InvokeEventAsync<T>(EventHandler<T>? eventHandler, T args, string eventName) where T : EventArgs
        {
            if (eventHandler == null) return;

            // 完全异步调用，不阻塞当前线程
            _ = Task.Run(() =>
            {
                try
                {
                    eventHandler.Invoke(this, args);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"事件调用异常: {eventName}", ex, "FileSyncService");
                }
            });
        }

        /// <summary>
        /// 完全异步的LogMessage事件调用
        /// </summary>
        private void InvokeLogMessageAsync(string message)
        {
            if (LogMessage == null) return;

            _ = Task.Run(() =>
            {
                try
                {
                    LogMessage.Invoke(this, message);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"LogMessage事件调用异常: {message}", ex, "FileSyncService");
                }
            });
        }

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
            public SyncMode Mode { get; set; } = SyncMode.Immediate;
            public int DelaySeconds { get; set; } = 3;
            public bool Enabled { get; set; } = true;
        }

        public class SyncOperation : IEquatable<SyncOperation>
        {
            public string FilePath { get; set; } = string.Empty;
            public string SourceRoot { get; set; } = string.Empty;
            public string TargetRoot { get; set; } = string.Empty;
            public WatcherChangeTypes ChangeType { get; set; }
            public DateTime Timestamp { get; set; } = DateTime.Now;
            public SyncMode Mode { get; set; }
            public long LastFileSize { get; set; } = -1;
            public DateTime LastSizeCheck { get; set; } = DateTime.Now;
            public int StabilityCheckCount { get; set; } = 0;

            public bool Equals(SyncOperation? other)
            {
                if (other == null) return false;
                return FilePath == other.FilePath && Timestamp == other.Timestamp;
            }

            public override bool Equals(object? obj)
            {
                return Equals(obj as SyncOperation);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(FilePath, Timestamp);
            }
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

        public class SyncQueueEventArgs : EventArgs
        {
            public SyncQueueItemViewModel Item { get; }
            public string Key { get; }

            public SyncQueueEventArgs(SyncQueueItemViewModel item, string key = "")
            {
                Item = item;
                Key = key;
            }
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
            }

            public class FileOperationRecord
            {
                public string FilePath { get; set; } = string.Empty;
                public DateTime Timestamp { get; set; }
                public WatcherChangeTypes OperationType { get; set; }
                public long FileSize { get; set; }
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


        /// <summary>
        /// 开始监控目录
        /// </summary>
        public bool StartMonitoring(string cachePath, string originalPath, SyncMode mode = SyncMode.Immediate, int delaySeconds = 3, IProgress<string>? progress = null)
        {
            try
            {
                SafeLog($"StartMonitoring调用：{cachePath} -> {originalPath}, 模式:{mode}");

                if (!Directory.Exists(cachePath))
                {
                    var msg = $"缓存目录不存在：{cachePath}";
                    progress?.Report(msg);
                    SafeLog(msg);
                    return false;
                }

                if (!Directory.Exists(originalPath))
                {
                    var msg = $"原始目录不存在：{originalPath}";
                    progress?.Report(msg);
                    SafeLog(msg);
                    return false;
                }

                lock (_lockObject)
                {
                    // 如果已经在监控，先停止
                    if (_watchers.ContainsKey(cachePath))
                    {
                        SafeLog($"停止现有监控：{cachePath}");
                        StopMonitoring(cachePath, progress);
                    }

                    var msg = $"开始监控：{cachePath} -> {originalPath}";
                    progress?.Report(msg);
                    SafeLog(msg);

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

                    SafeLog($"开始监控：{cachePath} (模式：{mode}，延迟：{delaySeconds}秒)");
                    progress?.Report("文件监控启动成功");
                    return true;
                }
            }
            catch (Exception ex)
            {
                progress?.Report($"启动监控异常：{ex.Message}");
                SafeLog($"启动监控异常：{ex.Message}");
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

                    SafeLog($"停止监控：{cachePath}");
                    progress?.Report("文件监控停止成功");
                    return true;
                }
            }
            catch (Exception ex)
            {
                progress?.Report($"停止监控异常：{ex.Message}");
                SafeLog($"停止监控异常：{ex.Message}");
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
                SafeLog($"强制同步完成：{cachePath}，成功 {totalSyncCount}，失败 {totalErrorCount}");

                return totalErrorCount == 0;
            }
            catch (Exception ex)
            {
                progress?.Report($"强制同步异常：{ex.Message}");
                SafeLog($"强制同步异常：{ex.Message}");
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

        /// <summary>
        /// 获取当前队列项目
        /// </summary>
        public IEnumerable<SyncQueueItemViewModel> GetCurrentQueueItems()
        {
            lock (_lockObject)
            {
                return _queueItems.Values.ToList();
            }
        }

        /// <summary>
        /// 获取队列统计信息
        /// </summary>
        public SyncQueueStats GetQueueStats()
        {
            lock (_lockObject)
            {
                var items = _queueItems.Values.ToList();
                return new SyncQueueStats
                {
                    TotalCount = items.Count,
                    PendingCount = items.Count(x => x.Status == "等待中"),
                    ProcessingCount = items.Count(x => x.Status == "处理中"),
                    CompletedCount = items.Count(x => x.Status == "完成"),
                    FailedCount = items.Count(x => x.Status == "失败"),
                    TotalSizeMB = items.Sum(x => x.FileSize) / (1024.0 * 1024.0),
                    ProcessedSizeMB = items.Where(x => x.Status == "完成").Sum(x => x.FileSize) / (1024.0 * 1024.0)
                };
            }
        }

        /// <summary>
        /// 清理已完成的队列项目
        /// </summary>
        public void ClearCompletedItems()
        {
            lock (_lockObject)
            {
                var completedItems = _queueItems.Where(kv => kv.Value.Status == "完成").ToList();
                foreach (var item in completedItems)
                {
                    _queueItems.Remove(item.Key);
                    InvokeEventAsync(QueueItemRemoved, new SyncQueueEventArgs(item.Value, "Removed"), "QueueItemRemoved");
                }
            }
        }

        /// <summary>
        /// 清理失败的队列项目
        /// </summary>
        public void ClearFailedItems()
        {
            lock (_lockObject)
            {
                var failedItems = _queueItems.Where(kv => kv.Value.Status == "失败").ToList();
                foreach (var item in failedItems)
                {
                    _queueItems.Remove(item.Key);
                    InvokeEventAsync(QueueItemRemoved, new SyncQueueEventArgs(item.Value, "Removed"), "QueueItemRemoved");
                }
            }
        }

        private void OnFileChanged(string filePath, string cacheRoot, WatcherChangeTypes changeType)
        {
            // 事件去抖动：500ms内的重复事件视为同一操作
            var now = DateTime.Now;
            var fileKey = $"{filePath.ToLowerInvariant()}_{changeType}"; // 区分不同事件类型，避免删除和创建冲突

            if (_lastEventTime.TryGetValue(fileKey, out var lastTime))
            {
                if ((now - lastTime).TotalMilliseconds < EVENT_DEBOUNCE_MS)
                {
                    // 静默忽略重复事件，不记录日志避免干扰
                    return;
                }
            }
            _lastEventTime[fileKey] = now;

            // 对于新建或修改的文件，立即进行去重检查
            if (changeType == WatcherChangeTypes.Created || changeType == WatcherChangeTypes.Changed)
            {
                var tcs = new TaskCompletionSource<bool>();

                // 尝试添加到处理队列，如果已存在则跳过
                if (!_processingFiles.TryAdd(fileKey, tcs))
                {
                    var relativePath = Path.GetRelativePath(cacheRoot, filePath);
                    _logger.LogInfo($"文件已在处理队列中，跳过重复事件: {relativePath}", "FileSyncService");
                    return;
                }

                // 异步处理该文件
                _ = Task.Run(async () => await ProcessFileChangeAsync(filePath, cacheRoot, changeType, fileKey, tcs));
                return;
            }

            // 删除和重命名事件的处理（非创建/修改事件）
            if (changeType == WatcherChangeTypes.Deleted || changeType == WatcherChangeTypes.Renamed)
            {
                var relativePath = Path.GetRelativePath(cacheRoot, filePath);
                _logger.LogInfo($"检测到{changeType}事件: {relativePath}", "FileSyncService");

                // 异步处理删除/重命名事件
                _ = Task.Run(async () => await ProcessOtherFileChangeAsync(filePath, cacheRoot, changeType));
            }
        }

        /// <summary>
        /// 处理新建和修改文件的异步方法
        /// </summary>
        private async Task ProcessFileChangeAsync(string filePath, string cacheRoot, WatcherChangeTypes changeType, string fileKey, TaskCompletionSource<bool> tcs)
        {
            try
            {
                if (!_syncConfigs.TryGetValue(cacheRoot, out var config) || !config.Enabled)
                    return;

                // 过滤临时文件
                if (IsTemporaryFile(filePath))
                    return;

                _logger.LogInfo($"检测到文件变化，等待写入完成: {Path.GetFileName(filePath)}", "FileSyncService");

                // 检查文件或目录是否仍然存在
                if (!File.Exists(filePath) && !Directory.Exists(filePath))
                {
                    _logger.LogInfo($"文件或目录已被删除，跳过同步: {Path.GetFileName(filePath)}", "FileSyncService");
                    return;
                }

                // 如果是文件，等待写入完成；如果是目录，直接继续
                if (File.Exists(filePath))
                {
                    var writeComplete = await SafeFileOperations.IsFileWriteComplete(filePath);
                    if (!writeComplete)
                    {
                        _logger.LogWarning($"文件写入未完成，跳过同步: {Path.GetFileName(filePath)}", "FileSyncService");
                        return;
                    }
                }
                else if (Directory.Exists(filePath))
                {
                    _logger.LogInfo($"检测到目录变化: {Path.GetFileName(filePath)}", "FileSyncService");
                }

                var itemType = File.Exists(filePath) ? "文件" : "目录";
                _logger.LogInfo($"{itemType}准备就绪，开始同步: {Path.GetFileName(filePath)}", "FileSyncService");

                // 记录操作用于智能分析
                if (_analyzers.TryGetValue(cacheRoot, out var analyzer))
                {
                    var fileSize = 0L;
                    try
                    {
                        if (File.Exists(filePath))
                        {
                            var fileInfo = new FileInfo(filePath);
                            if (fileInfo.Exists)
                            {
                                fileSize = fileInfo.Length;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"获取文件大小用于分析失败: {Path.GetFileName(filePath)} - {ex.Message}", "FileSyncService");
                    }

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
                    CreatedAt = DateTime.Now,
                    OperationType = changeType == WatcherChangeTypes.Created ? "Created" : "Changed"
                };

                // 尝试获取文件大小
                try
                {
                    if (File.Exists(filePath))
                    {
                        var fileInfo = new FileInfo(filePath);
                        // 再次检查文件是否存在，因为它可能在检查后被删除
                        if (fileInfo.Exists)
                        {
                            queueItem.FileSize = fileInfo.Length;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"获取文件大小失败: {Path.GetFileName(filePath)} - {ex.Message}", "FileSyncService");
                }

                // 添加到队列项目字典
                var itemKey = $"{filePath}_{DateTime.Now.Ticks}";
                lock (_lockObject)
                {
                    _queueItems[itemKey] = queueItem;
                }

                // 安全触发队列项目添加事件（通过Channel）
                await _uiUpdateChannel.Writer.WriteAsync(new UIUpdateMessage
                {
                    Type = UIUpdateType.QueueItemAdded,
                    QueueItem = queueItem
                });

                // 启动异步处理任务，状态将在获得SemaphoreSlim许可后设置为"处理中"
                _ = Task.Run(() => ProcessSyncOperationWithTracking(operation, queueItem, itemKey, fileKey, tcs));
                SafeLog($"开始同步文件：{Path.GetFileName(filePath)}");

                // 注意：不在这里清理文件锁！文件锁会在 ProcessSyncOperationWithTracking 中清理
            }
            catch (Exception ex)
            {
                // 只有在异常情况下才清理文件锁
                if (_processingFiles.TryRemove(fileKey, out var removedTcs))
                {
                    removedTcs.SetResult(false);
                }
                SafeLog($"文件变化处理异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理删除和重命名事件的异步方法
        /// </summary>
        private async Task ProcessOtherFileChangeAsync(string filePath, string cacheRoot, WatcherChangeTypes changeType)
        {
            try
            {
                _logger.LogInfo($"开始处理{changeType}事件: {Path.GetFileName(filePath)}, 缓存根目录: {cacheRoot}", "FileSyncService");

                if (!_syncConfigs.TryGetValue(cacheRoot, out var config))
                {
                    _logger.LogWarning($"未找到缓存根目录配置: {cacheRoot}", "FileSyncService");
                    return;
                }

                if (!config.Enabled)
                {
                    _logger.LogWarning($"同步配置已禁用: {cacheRoot}", "FileSyncService");
                    return;
                }

                // 过滤临时文件
                if (IsTemporaryFile(filePath))
                    return;

                // 记录操作用于智能分析
                if (_analyzers.TryGetValue(cacheRoot, out var analyzer))
                {
                    analyzer.RecordOperation(filePath, changeType, 0);
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

                // 创建队列项并显示删除操作
                var queueItem = new SyncQueueItemViewModel
                {
                    FilePath = operation.FilePath,
                    Status = "等待中",
                    Progress = 0,
                    FileSize = 0, // 删除操作没有文件大小
                    CreatedAt = DateTime.Now,
                    OperationType = "Deleted"
                };

                // 添加到队列并触发UI更新
                _logger.LogInfo($"创建删除队列项: {Path.GetFileName(queueItem.FilePath)}", "FileSyncService");
                InvokeEventAsync(QueueItemAdded, new SyncQueueEventArgs(queueItem, "Added"), "QueueItemAdded");

                // 处理删除操作，传递队列项用于状态更新
                var itemKey = $"{operation.FilePath}_{DateTime.Now.Ticks}";
                var fileKey = $"{operation.FilePath.ToLowerInvariant()}_{changeType}";
                var tcs = new TaskCompletionSource<bool>();
                _ = Task.Run(() => ProcessSyncOperationWithTracking(operation, queueItem, itemKey, fileKey, tcs));
            }
            catch (Exception ex)
            {
                SafeLog($"文件删除/重命名事件处理异常: {ex.Message}");
            }
        }

        private void OnFileRenamed(string oldPath, string newPath, string cacheRoot)
        {
            OnFileChanged(oldPath, cacheRoot, WatcherChangeTypes.Deleted);
            OnFileChanged(newPath, cacheRoot, WatcherChangeTypes.Created);
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

            // 按时间排序处理
            var prioritizedOperations = operations
                .GroupBy(op => op.FilePath)
                .Select(g => g.OrderByDescending(op => op.Timestamp).First()) // 每个文件最新操作
                .Where(op => ShouldProcessOperation(op, now))
                .OrderBy(op => op.Timestamp)
                .ToList();

            // 将未处理的操作重新入队
            foreach (var op in operations.Except(prioritizedOperations))
            {
                _syncQueue.Enqueue(op);
            }

            // 智能批量处理
            if (prioritizedOperations.Count > 0)
            {
                SafeLog($"智能同步处理：{prioritizedOperations.Count} 个操作");

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

            // 简化为固定延迟
            var ageSeconds = (now - operation.Timestamp).TotalSeconds;
            return ageSeconds >= config.DelaySeconds;
        }


        private async void ProcessSyncOperationWithTracking(SyncOperation operation, SyncQueueItemViewModel queueItem, string itemKey, string fileKey, TaskCompletionSource<bool> tcs)
        {
            try
            {
                // 注意：不在这里设置"处理中"状态，而是在获得SemaphoreSlim许可后设置
                queueItem.Progress = 0;

                var startTime = DateTime.Now;

                // 安全地计算相对路径和目标路径
                string relativePath;
                string targetPath;
                try
                {
                    relativePath = Path.GetRelativePath(operation.SourceRoot, operation.FilePath);
                    targetPath = Path.Combine(operation.TargetRoot, relativePath);
                }
                catch (Exception ex)
                {
                    var errorMsg = $"路径计算失败: {ex.Message}";
                    queueItem.Status = "失败";
                    queueItem.ErrorMessage = errorMsg;
                    InvokeEventAsync(QueueItemUpdated, new SyncQueueEventArgs(queueItem, "Failed"), "QueueItemUpdated");
                    SafeLog($"路径计算失败: {Path.GetFileName(operation.FilePath)} - {ex.Message}");
                    return;
                }

                bool success = false;
                string message = "";

                switch (operation.ChangeType)
                {
                    case WatcherChangeTypes.Created:
                    case WatcherChangeTypes.Changed:
                        // 在尝试获取锁定前，再次确认文件或目录存在
                        if (!File.Exists(operation.FilePath) && !Directory.Exists(operation.FilePath))
                        {
                            success = false;
                            message = "文件或目录已被删除，跳过同步";
                            SafeLog($"源文件或目录已被删除，跳过同步: {Path.GetFileName(operation.FilePath)}");
                            break;
                        }

                        // 统一处理文件和目录：目录不需要文件锁定
                        if (Directory.Exists(operation.FilePath))
                        {
                            // 目录：直接同步，不需要锁定
                            SafeLog($"开始同步目录: {Path.GetFileName(operation.FilePath)}");
                            queueItem.Status = "处理中";
                            InvokeEventAsync(QueueItemUpdated, new SyncQueueEventArgs(queueItem, "Processing"), "QueueItemUpdated");
                            var syncResult = await SyncFileWithProgressAsync(operation.FilePath, targetPath, queueItem);
                            success = syncResult.Success;
                            message = syncResult.Message;
                            SafeLog($"目录同步完成: {Path.GetFileName(operation.FilePath)}");
                        }
                        else
                        {
                            // 文件：需要文件锁定
                            var lockResult = await TryAcquireFileLockWithRetryAsync(operation.FilePath, queueItem);
                            if (!lockResult.Success)
                            {
                                success = false;
                                message = lockResult.Message;
                                break;
                            }

                            using (var fileLock = lockResult.FileLock!)
                            {
                                // 在开始实际操作前再次确认文件存在（防止锁定获取成功后文件被删除）
                                if (!File.Exists(operation.FilePath))
                                {
                                    success = false;
                                    message = "文件在锁定获取后被删除";
                                    SafeLog($"文件在锁定获取后被删除: {Path.GetFileName(operation.FilePath)}");
                                    break;
                                }

                                SafeLog($"已获取文件锁定，开始同步: {Path.GetFileName(operation.FilePath)}");
                                var syncResult = await SyncFileWithProgressAsync(operation.FilePath, targetPath, queueItem);
                                success = syncResult.Success;
                                message = syncResult.Message;
                                SafeLog($"同步完成，释放文件锁定: {Path.GetFileName(operation.FilePath)}");
                            }
                        }
                        break;

                    case WatcherChangeTypes.Deleted:
                        SafeLog($"开始同步文件：{Path.GetFileName(operation.FilePath)}");
                        queueItem.Status = "处理中";
                        InvokeEventAsync(QueueItemUpdated, new SyncQueueEventArgs(queueItem, "Processing"), "QueueItemUpdated");
                        SafeLog($"正在删除: {Path.GetFileName(operation.FilePath)}");

                        var deleteResult = await DeleteFileAsync(targetPath);
                        success = deleteResult.Success;
                        message = deleteResult.Message;
                        queueItem.Progress = 100;
                        InvokeEventAsync(QueueItemUpdated, new SyncQueueEventArgs(queueItem, "Progress"), "QueueItemUpdated");
                        SafeLog($"删除完成: {Path.GetFileName(operation.FilePath)}");
                        break;
                }

                var duration = DateTime.Now - startTime;

                if (success)
                {
                    queueItem.Status = "完成";
                    queueItem.Progress = 100;
                    InvokeEventAsync(QueueItemUpdated, new SyncQueueEventArgs(queueItem, "Completed"), "QueueItemUpdated");

                    // 延迟移除完成的项目
                    _ = Task.Delay(5000).ContinueWith(_ =>
                    {
                        lock (_lockObject)
                        {
                            if (_queueItems.Remove(itemKey))
                            {
                                InvokeEventAsync(QueueItemRemoved, new SyncQueueEventArgs(queueItem, "Removed"), "QueueItemRemoved");
                            }
                        }
                    });

                    InvokeEventAsync(SyncCompleted, new SyncEventArgs
                    {
                        FilePath = operation.FilePath,
                        Success = true,
                        Message = message,
                        Duration = duration
                    }, "SyncCompleted");
                }
                else
                {
                    queueItem.Status = "失败";
                    queueItem.ErrorMessage = message;
                    InvokeEventAsync(QueueItemUpdated, new SyncQueueEventArgs(queueItem, "Failed"), "QueueItemUpdated");

                    InvokeEventAsync(SyncFailed, new SyncEventArgs
                    {
                        FilePath = operation.FilePath,
                        Success = false,
                        Message = message,
                        Duration = duration
                    }, "SyncFailed");
                }
            }
            catch (Exception ex)
            {
                queueItem.Status = "失败";
                queueItem.ErrorMessage = ex.Message;
                InvokeEventAsync(QueueItemUpdated, new SyncQueueEventArgs(queueItem, "Failed"), "QueueItemUpdated");

                InvokeEventAsync(SyncFailed, new SyncEventArgs
                {
                    FilePath = operation.FilePath,
                    Success = false,
                    Message = ex.Message,
                    Duration = TimeSpan.Zero
                }, "SyncFailed");
            }
            finally
            {
                // 清理文件处理锁
                if (_processingFiles.TryRemove(fileKey, out var removedTcs))
                {
                    removedTcs.SetResult(true);
                    _logger.LogInfo($"文件处理完成，清理锁: {Path.GetFileName(operation.FilePath)}", "FileSyncService");
                }
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
                    InvokeEventAsync(SyncCompleted, eventArgs, "SyncCompleted");
                    if (message.Contains("经过") && message.Contains("次尝试"))
                    {
                        SafeLog($"同步成功（重试后）：{Path.GetFileName(operation.FilePath)} - {message}");
                    }
                }
                else
                {
                    InvokeEventAsync(SyncFailed, eventArgs, "SyncFailed");
                    SafeLog($"同步失败：{operation.FilePath} - {message}");
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

                InvokeEventAsync(SyncFailed, eventArgs, "SyncFailed");
                SafeLog($"同步异常：{operation.FilePath} - {ex.Message}");
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
                        SafeLog(msg);
                        // 删除了不可靠的百分比解析，只保留日志记录
                    });

                    // 使用SemaphoreSlim限制FastCopy并发数量
                    await _fastCopyLimiter.WaitAsync();
                    try
                    {
                        // 在获取许可后设置为"处理中"状态
                        queueItem.Status = "处理中";
                        InvokeEventAsync(QueueItemUpdated, new SyncQueueEventArgs(queueItem, "Processing"), "QueueItemUpdated");

                        // 在获取许可后再次确认文件存在
                        if (!File.Exists(sourcePath))
                        {
                            _logger.LogWarning($"源文件在获取FastCopy许可后已被删除: {Path.GetFileName(sourcePath)}", "FileSyncService");
                            return new SafeFileOperations.FileOperationResult
                            {
                                Success = false,
                                Message = "源文件已被删除"
                            };
                        }

                        _logger.LogInfo($"获取FastCopy并发许可，开始复制: {Path.GetFileName(sourcePath)}", "FileSyncService");
                        return await SafeFileOperations.SafeCopyFileAsync(sourcePath, targetPath, true, retryConfig, progress);
                    }
                    finally
                    {
                        _fastCopyLimiter.Release();
                        _logger.LogInfo($"释放FastCopy并发许可: {Path.GetFileName(sourcePath)}", "FileSyncService");
                    }
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
                        InvokeEventAsync(QueueItemUpdated, new SyncQueueEventArgs(queueItem, "Progress"), "QueueItemUpdated");

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

                    var progress = new Progress<string>(msg => SafeLog(msg));
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

                    var progress = new Progress<string>(msg => SafeLog(msg));
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

        /// <summary>
        /// 安全的日志记录方法 - 通过Channel发送，不阻塞调用线程
        /// </summary>
        private void SafeLog(string message)
        {
            _ = _uiUpdateChannel.Writer.TryWrite(new UIUpdateMessage
            {
                Type = UIUpdateType.LogMessage,
                Message = message
            });
        }

        /// <summary>
        /// 更新统计信息
        /// </summary>
        private void UpdateStats(object? state)
        {
            var stats = new SyncStatsEventArgs
            {
                QueueCount = _syncQueue.Count,
                CompletedOperations = _completedOperations,
                FailedOperations = _failedOperations,
                BytesProcessed = _bytesProcessed,
                AverageProcessingTime = _processingTimes.Any() ? TimeSpan.FromMilliseconds(_processingTimes.Average(t => t.TotalMilliseconds)) : TimeSpan.Zero
            };

            // 通过Channel发送统计更新
            _ = _uiUpdateChannel.Writer.TryWrite(new UIUpdateMessage
            {
                Type = UIUpdateType.StatsUpdated,
                Stats = stats
            });
        }

        /// <summary>
        /// 异步处理UI更新消息
        /// </summary>
        private async Task ProcessUIUpdatesAsync(CancellationToken cancellationToken)
        {
            await foreach (var message in _uiUpdateChannel.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    switch (message.Type)
                    {
                        case UIUpdateType.QueueItemAdded:
                            if (message.QueueItem != null)
                                InvokeEventAsync(QueueItemAdded, new SyncQueueEventArgs(message.QueueItem, ""), "QueueItemAdded");
                            break;
                        case UIUpdateType.QueueItemUpdated:
                            if (message.QueueItem != null)
                                InvokeEventAsync(QueueItemUpdated, new SyncQueueEventArgs(message.QueueItem, ""), "QueueItemUpdated");
                            break;
                        case UIUpdateType.QueueItemRemoved:
                            if (message.QueueItem != null)
                                InvokeEventAsync(QueueItemRemoved, new SyncQueueEventArgs(message.QueueItem, ""), "QueueItemRemoved");
                            break;
                        case UIUpdateType.StatsUpdated:
                            InvokeEventAsync(StatsUpdated, message.Stats!, "StatsUpdated");
                            break;
                        case UIUpdateType.LogMessage:
                            InvokeLogMessageAsync(message.Message ?? string.Empty);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    // UI更新失败不应影响主流程
                    System.Diagnostics.Debug.WriteLine($"UI update failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 使用ParallelSyncEngine处理文件操作
        /// </summary>
        public async Task<bool> ProcessWithEngineAsync(string sourcePath, string targetPath)
        {
            try
            {
                // 直接使用FastCopy进行文件操作
                var success = await _fastCopyService.CopyWithVerifyAsync(sourcePath, targetPath);
                return success;
            }
            catch (Exception ex)
            {
                SafeLog($"引擎处理失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 提交IO操作到批处理器
        /// </summary>
        private async Task SubmitToBatchProcessor(string sourcePath, string targetPath)
        {
            // 简化方法 - 直接执行文件操作，不再使用复杂的批处理器
            // 这个方法现在主要用于UI更新，实际文件操作已简化
            await Task.CompletedTask; // 占位符
        }

        /// <summary>
        /// 计算操作优先级
        /// </summary>
        private int CalculateOperationPriority(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch
            {
                ".exe" or ".dll" => 0, // 最高优先级
                ".cs" or ".cpp" or ".h" => 1,
                ".txt" or ".md" or ".json" => 2,
                ".log" or ".tmp" => 4, // 最低优先级
                _ => 3 // 默认优先级
            };
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            // 取消所有异步操作
            _serviceCancellation?.Cancel();

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
            _periodicTimer?.Dispose();
            _intelligentTimer?.Dispose();
            _statsTimer?.Dispose();

            // 关闭Channel
            _uiUpdateChannel.Writer.TryComplete();

            // 释放资源
            _fastCopyLimiter?.Dispose();
            _logger?.Dispose();
            _serviceCancellation?.Dispose();

            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// 从配置文件读取FastCopy最大并发数
        /// </summary>
        private int GetFastCopyMaxConcurrency()
        {
            try
            {
                var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                if (File.Exists(configPath))
                {
                    var configContent = File.ReadAllText(configPath);
                    var config = JsonSerializer.Deserialize<JsonElement>(configContent);

                    if (config.TryGetProperty("FastCopy", out var fastCopyConfig) &&
                        fastCopyConfig.TryGetProperty("MaxConcurrency", out var maxConcurrencyElement))
                    {
                        return maxConcurrencyElement.GetInt32();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"读取FastCopy配置失败，使用默认值: {ex.Message}", "FileSyncService");
            }

            return 3; // 默认值
        }

        /// <summary>
        /// 文件锁定尝试结果
        /// </summary>
        public class FileLockResult
        {
            public bool Success { get; set; }
            public string Message { get; set; } = string.Empty;
            public SafeFileOperations.FileLockHandle? FileLock { get; set; }
        }

        /// <summary>
        /// 使用指数退避重试机制尝试获取文件锁定，最大等待时间为2分钟
        /// 注意：目录不需要文件锁定
        /// </summary>
        private async Task<FileLockResult> TryAcquireFileLockWithRetryAsync(string filePath, SyncQueueItemViewModel queueItem)
        {
            const int maxRetries = 8; // 最多重试8次
            const int baseDelayMs = 1000; // 基础等待时间1秒
            const int maxDelayMs = 120000; // 最大等待时间2分钟

            // 检查文件或目录是否仍然存在
            if (!File.Exists(filePath) && !Directory.Exists(filePath))
            {
                return new FileLockResult
                {
                    Success = false,
                    Message = "文件或目录已被删除，跳过同步"
                };
            }

            // 如果是目录，不需要文件锁定，直接返回成功
            if (Directory.Exists(filePath))
            {
                SafeLog($"目录不需要文件锁定，直接继续: {Path.GetFileName(filePath)}");
                return new FileLockResult
                {
                    Success = true,
                    Message = "目录不需要文件锁定",
                    FileLock = null // 目录不需要锁定句柄
                };
            }

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                // 再次检查文件是否仍然存在
                if (!File.Exists(filePath))
                {
                    return new FileLockResult
                    {
                        Success = false,
                        Message = "文件已被删除，跳过同步"
                    };
                }

                // 尝试获取文件锁定
                var fileLock = SafeFileOperations.AcquireReadOnlyLock(filePath);
                if (fileLock != null && fileLock.IsValid)
                {
                    SafeLog($"成功获取文件锁定 (尝试 {attempt}/{maxRetries}): {Path.GetFileName(filePath)}");
                    return new FileLockResult
                    {
                        Success = true,
                        Message = "成功获取文件锁定",
                        FileLock = fileLock
                    };
                }

                // 释放无效的锁定句柄
                fileLock?.Dispose();

                // 如果不是最后一次尝试，等待并重试
                if (attempt < maxRetries)
                {
                    // 指数退避：1s, 2s, 4s, 8s, 16s, 32s, 64s, 120s (最大2分钟)
                    var delayMs = Math.Min(baseDelayMs * (int)Math.Pow(2, attempt - 1), maxDelayMs);
                    SafeLog($"文件被占用，等待 {delayMs/1000} 秒后重试 (尝试 {attempt}/{maxRetries}): {Path.GetFileName(filePath)}");

                    // 更新UI状态
                    queueItem.Status = $"等待文件释放 ({attempt}/{maxRetries})";
                    InvokeEventAsync(QueueItemUpdated, new SyncQueueEventArgs(queueItem, "WaitingForLock"), "QueueItemUpdated");

                    await Task.Delay(delayMs);
                }
            }

            // 所有重试都失败了
            SafeLog($"经过 {maxRetries} 次尝试后仍无法获取文件锁定: {Path.GetFileName(filePath)}");
            return new FileLockResult
            {
                Success = false,
                Message = $"文件被占用时间过长，经过 {maxRetries} 次重试后放弃"
            };
        }

        /// <summary>
        /// 重试现有队列项（避免创建重复任务）
        /// </summary>
        public async Task RetryExistingQueueItem(SyncQueueItemViewModel queueItem)
        {
            try
            {
                // 找到对应的缓存路径和配置
                var config = _syncConfigs.Values.FirstOrDefault(c => queueItem.FilePath.StartsWith(c.CachePath, StringComparison.OrdinalIgnoreCase));
                if (config != null)
                {
                    var operation = new SyncOperation
                    {
                        FilePath = queueItem.FilePath,
                        SourceRoot = config.CachePath,
                        TargetRoot = config.OriginalPath,
                        ChangeType = WatcherChangeTypes.Changed,
                        Timestamp = DateTime.Now,
                        Mode = config.Mode
                    };

                    // 直接处理操作，不通过文件监视器
                    var fileKey = queueItem.FilePath;
                    var itemKey = $"{queueItem.FilePath}_{DateTime.Now.Ticks}";
                    var tcs = new TaskCompletionSource<bool>();

                    // 避免重复处理
                    if (_processingFiles.TryAdd(fileKey, tcs))
                    {
                        _ = Task.Run(() => ProcessSyncOperationWithTracking(operation, queueItem, itemKey, fileKey, tcs));
                        _logger.LogInfo($"重试现有队列项: {Path.GetFileName(queueItem.FilePath)}", "FileSyncService");
                    }
                    else
                    {
                        _logger.LogWarning($"文件正在处理中，跳过重试: {Path.GetFileName(queueItem.FilePath)}", "FileSyncService");
                    }
                }
                else
                {
                    _logger.LogWarning($"无法找到文件对应的同步配置: {queueItem.FilePath}", "FileSyncService");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"重试队列项失败: {ex.Message}", ex, "FileSyncService");
                queueItem.Status = "失败";
                queueItem.ErrorMessage = ex.Message;
            }
        }

        /// <summary>
        /// 手动触发文件同步（用于重试失败的文件）
        /// </summary>
        public void TriggerManualSync(string filePath)
        {
            try
            {
                // 找到对应的缓存路径和配置
                var config = _syncConfigs.Values.FirstOrDefault(c => filePath.StartsWith(c.CachePath, StringComparison.OrdinalIgnoreCase));
                if (config != null)
                {
                    // 手动触发文件变化事件
                    OnFileChanged(filePath, config.CachePath, WatcherChangeTypes.Changed);
                    _logger.LogInfo($"手动触发同步: {Path.GetFileName(filePath)}", "FileSyncService");
                }
                else
                {
                    _logger.LogWarning($"无法找到文件对应的同步配置: {filePath}", "FileSyncService");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"手动触发同步失败: {ex.Message}", ex, "FileSyncService");
                throw;
            }
        }

    }

    /// <summary>
    /// UI更新消息
    /// </summary>
    public class UIUpdateMessage
    {
        public UIUpdateType Type { get; set; }
        public SyncQueueItemViewModel? QueueItem { get; set; }
        public FileSyncService.SyncStatsEventArgs? Stats { get; set; }
        public string? Message { get; set; }
    }


    public enum UIUpdateType
    {
        QueueItemAdded,
        QueueItemUpdated,
        QueueItemRemoved,
        StatsUpdated,
        LogMessage
    }
}