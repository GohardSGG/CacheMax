using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CacheMax.GUI.Services
{
    public class PerformanceMonitoringService : IDisposable
    {
        private readonly Dictionary<string, PerformanceTracker> _trackers = new();
        private readonly Timer _aggregationTimer;
        private readonly object _lockObject = new object();
        private bool _disposed = false;

        public event EventHandler<PerformanceStatsEventArgs>? StatsUpdated;
        public event EventHandler<string>? LogMessage;

        public class PerformanceTracker
        {
            private readonly string _cachePath;
            private readonly string _mountPoint;
            private readonly FileSystemWatcher _watcher;
            private readonly ConcurrentQueue<FileOperation> _recentOperations = new();
            private readonly Dictionary<string, FileAccessStats> _fileStats = new();

            private long _totalReadOperations = 0;
            private long _totalWriteOperations = 0;
            private long _totalBytesRead = 0;
            private long _totalBytesWritten = 0;
            private DateTime _startTime = DateTime.Now;

            public string CachePath => _cachePath;
            public string MountPoint => _mountPoint;

            public class FileOperation
            {
                public string FilePath { get; set; } = string.Empty;
                public OperationType Type { get; set; }
                public long FileSize { get; set; }
                public DateTime Timestamp { get; set; } = DateTime.Now;
                public TimeSpan Duration { get; set; }
            }

            public class FileAccessStats
            {
                public string FilePath { get; set; } = string.Empty;
                public int AccessCount { get; set; }
                public DateTime LastAccess { get; set; }
                public DateTime FirstAccess { get; set; }
                public long TotalSize { get; set; }
                public double AverageAccessInterval { get; set; }

                public bool IsHotFile => AccessCount >= 5 &&
                    (DateTime.Now - FirstAccess).TotalMinutes > 0 &&
                    AccessCount / (DateTime.Now - FirstAccess).TotalMinutes > 0.5;
            }

            public enum OperationType
            {
                Read,
                Write,
                Create,
                Delete,
                Access
            }

            public PerformanceTracker(string cachePath, string mountPoint)
            {
                _cachePath = cachePath;
                _mountPoint = mountPoint;

                _watcher = new FileSystemWatcher(cachePath)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                 NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.LastAccess,
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true
                };

                _watcher.Created += OnFileCreated;
                _watcher.Changed += OnFileChanged;
                _watcher.Deleted += OnFileDeleted;
                _watcher.Renamed += OnFileRenamed;
            }

            private void OnFileCreated(object sender, FileSystemEventArgs e)
            {
                RecordOperation(e.FullPath, OperationType.Create);
            }

            private void OnFileChanged(object sender, FileSystemEventArgs e)
            {
                RecordOperation(e.FullPath, OperationType.Write);
            }

            private void OnFileDeleted(object sender, FileSystemEventArgs e)
            {
                RecordOperation(e.FullPath, OperationType.Delete);
            }

            private void OnFileRenamed(object sender, RenamedEventArgs e)
            {
                RecordOperation(e.OldFullPath, OperationType.Delete);
                RecordOperation(e.FullPath, OperationType.Create);
            }

            private void RecordOperation(string filePath, OperationType operationType)
            {
                var startTime = DateTime.Now;

                try
                {
                    long fileSize = 0;
                    if (File.Exists(filePath) && operationType != OperationType.Delete)
                    {
                        try
                        {
                            fileSize = new FileInfo(filePath).Length;
                        }
                        catch { }
                    }

                    var operation = new FileOperation
                    {
                        FilePath = filePath,
                        Type = operationType,
                        FileSize = fileSize,
                        Timestamp = startTime,
                        Duration = DateTime.Now - startTime
                    };

                    _recentOperations.Enqueue(operation);

                    // 清理旧记录（保留最近1000条）
                    while (_recentOperations.Count > 1000)
                    {
                        _recentOperations.TryDequeue(out _);
                    }

                    // 更新统计
                    UpdateStatistics(operation);
                    UpdateFileAccessStats(filePath, operationType, fileSize);
                }
                catch (Exception ex)
                {
                    // 记录错误但不影响正常操作
                    System.Diagnostics.Debug.WriteLine($"记录文件操作时出错: {ex.Message}");
                }
            }

            private void UpdateStatistics(FileOperation operation)
            {
                switch (operation.Type)
                {
                    case OperationType.Read:
                    case OperationType.Access:
                        Interlocked.Increment(ref _totalReadOperations);
                        Interlocked.Add(ref _totalBytesRead, operation.FileSize);
                        break;
                    case OperationType.Write:
                    case OperationType.Create:
                        Interlocked.Increment(ref _totalWriteOperations);
                        Interlocked.Add(ref _totalBytesWritten, operation.FileSize);
                        break;
                }
            }

            private void UpdateFileAccessStats(string filePath, OperationType operationType, long fileSize)
            {
                lock (_fileStats)
                {
                    if (!_fileStats.TryGetValue(filePath, out var stats))
                    {
                        stats = new FileAccessStats
                        {
                            FilePath = filePath,
                            FirstAccess = DateTime.Now,
                            LastAccess = DateTime.Now,
                            AccessCount = 1,
                            TotalSize = fileSize
                        };
                        _fileStats[filePath] = stats;
                    }
                    else
                    {
                        var interval = (DateTime.Now - stats.LastAccess).TotalMinutes;
                        stats.AverageAccessInterval = (stats.AverageAccessInterval * stats.AccessCount + interval) / (stats.AccessCount + 1);
                        stats.AccessCount++;
                        stats.LastAccess = DateTime.Now;
                        stats.TotalSize = Math.Max(stats.TotalSize, fileSize);
                    }
                }
            }

            public PerformanceSnapshot GetCurrentStats()
            {
                var uptime = DateTime.Now - _startTime;
                var recentOps = _recentOperations.ToArray();

                // 计算最近5分钟的活动
                var recentWindow = DateTime.Now.AddMinutes(-5);
                var recentActivity = recentOps.Where(op => op.Timestamp >= recentWindow).ToArray();

                List<FileAccessStats> hotFiles;
                lock (_fileStats)
                {
                    hotFiles = _fileStats.Values
                        .Where(s => s.IsHotFile)
                        .OrderByDescending(s => s.AccessCount)
                        .Take(10)
                        .ToList();
                }

                return new PerformanceSnapshot
                {
                    MountPoint = _mountPoint,
                    CachePath = _cachePath,
                    Uptime = uptime,
                    TotalReadOps = _totalReadOperations,
                    TotalWriteOps = _totalWriteOperations,
                    TotalBytesRead = _totalBytesRead,
                    TotalBytesWritten = _totalBytesWritten,
                    RecentActivity = recentActivity.Length,
                    AverageResponseTime = recentActivity.Any() ?
                        TimeSpan.FromMilliseconds(recentActivity.Average(op => op.Duration.TotalMilliseconds)) :
                        TimeSpan.Zero,
                    ReadThroughput = uptime.TotalSeconds > 0 ? _totalBytesRead / uptime.TotalSeconds : 0,
                    WriteThroughput = uptime.TotalSeconds > 0 ? _totalBytesWritten / uptime.TotalSeconds : 0,
                    HotFiles = hotFiles,
                    Timestamp = DateTime.Now
                };
            }

            public void Dispose()
            {
                _watcher?.Dispose();
            }
        }

        public class PerformanceSnapshot
        {
            public string MountPoint { get; set; } = string.Empty;
            public string CachePath { get; set; } = string.Empty;
            public TimeSpan Uptime { get; set; }
            public long TotalReadOps { get; set; }
            public long TotalWriteOps { get; set; }
            public long TotalBytesRead { get; set; }
            public long TotalBytesWritten { get; set; }
            public int RecentActivity { get; set; }
            public TimeSpan AverageResponseTime { get; set; }
            public double ReadThroughput { get; set; }  // bytes/second
            public double WriteThroughput { get; set; } // bytes/second
            public List<PerformanceTracker.FileAccessStats> HotFiles { get; set; } = new();
            public DateTime Timestamp { get; set; }

            public double ReadThroughputMBps => ReadThroughput / (1024 * 1024);
            public double WriteThroughputMBps => WriteThroughput / (1024 * 1024);
        }

        public class PerformanceStatsEventArgs : EventArgs
        {
            public PerformanceSnapshot Snapshot { get; set; } = new();
        }

        public PerformanceMonitoringService()
        {
            // 每30秒聚合一次统计数据
            _aggregationTimer = new Timer(AggregateStats, null,
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        public bool StartMonitoring(string mountPoint, string cachePath)
        {
            try
            {
                lock (_lockObject)
                {
                    if (_trackers.ContainsKey(mountPoint))
                    {
                        LogMessage?.Invoke(this, $"已在监控 {mountPoint}");
                        return true;
                    }

                    if (!Directory.Exists(cachePath))
                    {
                        LogMessage?.Invoke(this, $"缓存目录不存在: {cachePath}");
                        return false;
                    }

                    var tracker = new PerformanceTracker(cachePath, mountPoint);
                    _trackers[mountPoint] = tracker;

                    LogMessage?.Invoke(this, $"开始性能监控: {mountPoint} -> {cachePath}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"启动性能监控失败: {ex.Message}");
                return false;
            }
        }

        public bool StopMonitoring(string mountPoint)
        {
            try
            {
                lock (_lockObject)
                {
                    if (_trackers.TryGetValue(mountPoint, out var tracker))
                    {
                        tracker.Dispose();
                        _trackers.Remove(mountPoint);
                        LogMessage?.Invoke(this, $"停止性能监控: {mountPoint}");
                        return true;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"停止性能监控失败: {ex.Message}");
                return false;
            }
        }

        public PerformanceSnapshot? GetCurrentStats(string mountPoint)
        {
            lock (_lockObject)
            {
                return _trackers.TryGetValue(mountPoint, out var tracker) ?
                    tracker.GetCurrentStats() : null;
            }
        }

        public List<PerformanceSnapshot> GetAllStats()
        {
            lock (_lockObject)
            {
                return _trackers.Values.Select(t => t.GetCurrentStats()).ToList();
            }
        }

        private void AggregateStats(object? state)
        {
            try
            {
                lock (_lockObject)
                {
                    foreach (var tracker in _trackers.Values)
                    {
                        var snapshot = tracker.GetCurrentStats();
                        StatsUpdated?.Invoke(this, new PerformanceStatsEventArgs { Snapshot = snapshot });
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"聚合统计数据时出错: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _aggregationTimer?.Dispose();

            lock (_lockObject)
            {
                foreach (var tracker in _trackers.Values)
                {
                    tracker.Dispose();
                }
                _trackers.Clear();
            }

            GC.SuppressFinalize(this);
        }
    }
}