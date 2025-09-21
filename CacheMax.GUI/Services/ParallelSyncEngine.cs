using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.IO;
using System.Collections.Generic;
using System.Buffers;
using System.Diagnostics;
using System.Linq;

namespace CacheMax.GUI.Services
{
    /// <summary>
    /// 工业级并行同步引擎 - 支持成千上万文件的高性能处理
    /// 特性：8个专用线程池、无锁队列、智能调度、错误隔离
    /// </summary>
    public class ParallelSyncEngine : IDisposable
    {
        #region 硬件配置
        private readonly int _cpuCount = Environment.ProcessorCount;
        private readonly long _totalMemory = GC.GetTotalMemory(false);
        private readonly bool _isHighEndMachine;

        // 动态线程池大小（基于硬件）
        private int _detectionThreads;
        private int _smallFileThreads;
        private int _largeFileThreads;
        private int _directoryThreads;
        private int _verificationThreads;
        private int _compressionThreads;
        private int _encryptionThreads;
        private int _indexingThreads;
        #endregion

        #region 8个专用线程池
        private ThreadPoolExecutor _fileDetectionPool;
        private ThreadPoolExecutor _smallFilePool;
        private ThreadPoolExecutor _largeFilePool;
        private ThreadPoolExecutor _directoryPool;
        private ThreadPoolExecutor _verificationPool;
        private ThreadPoolExecutor _compressionPool;
        private ThreadPoolExecutor _encryptionPool;
        private ThreadPoolExecutor _indexingPool;
        #endregion

        #region 无锁通信Channel系统
        private Channel<FileOperation> _detectionChannel;
        private Channel<FileOperation> _tinyFileChannel;      // < 1MB
        private Channel<FileOperation> _smallFileChannel;     // 1-10MB
        private Channel<FileOperation> _mediumFileChannel;    // 10-100MB
        private Channel<FileOperation> _largeFileChannel;     // 100MB-1GB
        private Channel<FileOperation> _hugeFileChannel;      // > 1GB
        private Channel<DirectoryOperation> _directoryChannel;
        private Channel<VerificationOperation> _verificationChannel;
        private Channel<TelemetryData> _telemetryChannel;
        private Channel<ErrorEvent> _errorChannel;
        #endregion

        #region 性能监控和遥测
        private TelemetryCollector _telemetry;
        private PerformanceCounters _perfCounters;
        private Timer _adaptiveTimer;
        private AdaptiveConcurrencyController _concurrencyController;
        #endregion

        #region 取消和生命周期管理
        private readonly CancellationTokenSource _engineCancellation;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _activeOperations;
        private volatile bool _isRunning;
        private volatile bool _disposed;
        #endregion

        public ParallelSyncEngine()
        {
            // 检测硬件配置
            _isHighEndMachine = _cpuCount >= 8 && _totalMemory >= 8L * 1024 * 1024 * 1024; // 8核8GB+

            // 生命周期管理（必须先初始化）
            _engineCancellation = new CancellationTokenSource();
            _activeOperations = new ConcurrentDictionary<string, TaskCompletionSource<bool>>();

            // 快速初始化，延迟启动重型组件
            _ = Task.Run(InitializeAsync);
        }

        /// <summary>
        /// 异步初始化重型组件，避免阻塞UI线程
        /// </summary>
        private async Task InitializeAsync()
        {
            try
            {
                // 根据硬件动态配置线程数
                ConfigureThreadPools();

                // 初始化Channel系统（大容量，支持高并发）
                InitializeChannels();

                // 初始化线程池
                InitializeThreadPools();

                // 初始化监控系统
                _telemetry = new TelemetryCollector(_telemetryChannel.Writer);
                _perfCounters = new PerformanceCounters();
                _concurrencyController = new AdaptiveConcurrencyController(_cpuCount, _telemetry);

                // 延迟启动自适应调优定时器（避免启动时CPU峰值）
                await Task.Delay(10000); // 启动10秒后再开始优化
                _adaptiveTimer = new Timer(AdaptiveOptimization, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

                StartProcessingPipelines();

                _isRunning = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ParallelSyncEngine初始化失败: {ex.Message}");
            }
        }

        private void ConfigureThreadPools()
        {
            if (_isHighEndMachine)
            {
                // 高端机器：激进配置
                _detectionThreads = _cpuCount * 2;     // 大量并行扫描
                _smallFileThreads = _cpuCount * 8;     // 海量小文件并发
                _largeFileThreads = _cpuCount * 2;     // 大文件流水线
                _directoryThreads = _cpuCount;         // 目录操作
                _verificationThreads = _cpuCount;      // 校验
                _compressionThreads = _cpuCount;       // 压缩
                _encryptionThreads = _cpuCount / 2;    // 加密（CPU密集）
                _indexingThreads = _cpuCount / 2;      // 索引
            }
            else
            {
                // 标准机器：平衡配置
                _detectionThreads = _cpuCount;
                _smallFileThreads = _cpuCount * 4;
                _largeFileThreads = _cpuCount;
                _directoryThreads = _cpuCount / 2;
                _verificationThreads = _cpuCount / 2;
                _compressionThreads = _cpuCount / 2;
                _encryptionThreads = _cpuCount / 4;
                _indexingThreads = _cpuCount / 4;
            }
        }

        private void InitializeChannels()
        {
            var options = new BoundedChannelOptions(100000) // 支持10万并发操作
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            };

            _detectionChannel = Channel.CreateBounded<FileOperation>(options);
            _tinyFileChannel = Channel.CreateBounded<FileOperation>(options);
            _smallFileChannel = Channel.CreateBounded<FileOperation>(options);
            _mediumFileChannel = Channel.CreateBounded<FileOperation>(options);
            _largeFileChannel = Channel.CreateBounded<FileOperation>(options);
            _hugeFileChannel = Channel.CreateBounded<FileOperation>(options);
            _directoryChannel = Channel.CreateBounded<DirectoryOperation>(options);
            _verificationChannel = Channel.CreateBounded<VerificationOperation>(options);
            _telemetryChannel = Channel.CreateBounded<TelemetryData>(options);
            _errorChannel = Channel.CreateBounded<ErrorEvent>(options);
        }

        private void InitializeThreadPools()
        {
            _fileDetectionPool = new ThreadPoolExecutor("FileDetection", _detectionThreads, _engineCancellation.Token);
            _smallFilePool = new ThreadPoolExecutor("SmallFile", _smallFileThreads, _engineCancellation.Token);
            _largeFilePool = new ThreadPoolExecutor("LargeFile", _largeFileThreads, _engineCancellation.Token);
            _directoryPool = new ThreadPoolExecutor("Directory", _directoryThreads, _engineCancellation.Token);
            _verificationPool = new ThreadPoolExecutor("Verification", _verificationThreads, _engineCancellation.Token);
            _compressionPool = new ThreadPoolExecutor("Compression", _compressionThreads, _engineCancellation.Token);
            _encryptionPool = new ThreadPoolExecutor("Encryption", _encryptionThreads, _engineCancellation.Token);
            _indexingPool = new ThreadPoolExecutor("Indexing", _indexingThreads, _engineCancellation.Token);
        }

        private void StartProcessingPipelines()
        {
            _isRunning = true;

            // 启动所有处理流水线
            _ = Task.Run(() => FileDetectionPipeline(_engineCancellation.Token));
            _ = Task.Run(() => SmallFileProcessingPipeline(_engineCancellation.Token));
            _ = Task.Run(() => LargeFileProcessingPipeline(_engineCancellation.Token));
            _ = Task.Run(() => DirectoryProcessingPipeline(_engineCancellation.Token));
            _ = Task.Run(() => VerificationPipeline(_engineCancellation.Token));
            _ = Task.Run(() => TelemetryProcessingPipeline(_engineCancellation.Token));
            _ = Task.Run(() => ErrorProcessingPipeline(_engineCancellation.Token));

            // 启动分发器
            _ = Task.Run(() => FileDistributorPipeline(_engineCancellation.Token));
        }

        /// <summary>
        /// 主入口：提交文件操作（支持海量并发）
        /// </summary>
        public async Task<bool> SubmitFileOperationAsync(string sourcePath, string targetPath, FileOperationType type, CancellationToken cancellationToken = default)
        {
            if (_disposed) return false;

            var operation = new FileOperation
            {
                Id = Guid.NewGuid().ToString(),
                SourcePath = sourcePath,
                TargetPath = targetPath,
                Type = type,
                SubmittedAt = DateTime.UtcNow,
                Priority = CalculatePriority(sourcePath, type)
            };

            // 预分析文件大小（如果存在）
            if (File.Exists(sourcePath))
            {
                try
                {
                    operation.FileSize = new FileInfo(sourcePath).Length;
                }
                catch
                {
                    operation.FileSize = 0;
                }
            }

            // 提交到检测流水线
            await _detectionChannel.Writer.WriteAsync(operation, cancellationToken);

            // 注册活跃操作
            var tcs = new TaskCompletionSource<bool>();
            _activeOperations[operation.Id] = tcs;

            _telemetry.RecordOperation("OperationSubmitted", operation.Type.ToString());

            return true;
        }

        /// <summary>
        /// 智能文件分发流水线
        /// </summary>
        private async Task FileDistributorPipeline(CancellationToken cancellationToken)
        {
            await foreach (var operation in _detectionChannel.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    // 根据文件大小智能分发到不同的处理通道
                    var channel = operation.FileSize switch
                    {
                        < 1_000_000 => _tinyFileChannel,           // < 1MB
                        < 10_000_000 => _smallFileChannel,         // 1-10MB
                        < 100_000_000 => _mediumFileChannel,       // 10-100MB
                        < 1_000_000_000 => _largeFileChannel,      // 100MB-1GB
                        _ => _hugeFileChannel                       // > 1GB
                    };

                    await channel.Writer.WriteAsync(operation, cancellationToken);

                    _telemetry.RecordOperation("FileDistributed", $"Size{GetSizeCategory(operation.FileSize)}");
                }
                catch (Exception ex)
                {
                    await ReportError(operation, ex, "FileDistributor");
                }
            }
        }

        /// <summary>
        /// 文件检测流水线（并行扫描、分析、去重）
        /// </summary>
        private async Task FileDetectionPipeline(CancellationToken cancellationToken)
        {
            var semaphore = new SemaphoreSlim(_detectionThreads);

            await foreach (var operation in _detectionChannel.Reader.ReadAllAsync(cancellationToken))
            {
                _ = Task.Run(async () =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    try
                    {
                        await _fileDetectionPool.ExecuteAsync(async () =>
                        {
                            // 执行文件检测逻辑
                            await ProcessFileDetection(operation, cancellationToken);
                        });
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, cancellationToken);
            }
        }

        /// <summary>
        /// 小文件高并发处理流水线
        /// </summary>
        private async Task SmallFileProcessingPipeline(CancellationToken cancellationToken)
        {
            // 合并处理多个通道
            var channels = new[] { _tinyFileChannel, _smallFileChannel, _mediumFileChannel };

            await Task.Run(async () =>
            {
                await Parallel.ForEachAsync(channels,
                    new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = cancellationToken },
                    async (channel, ct) =>
                    {
                        await foreach (var operation in channel.Reader.ReadAllAsync(ct))
                        {
                            await _smallFilePool.ExecuteAsync(async () =>
                            {
                                await ProcessSmallFile(operation, ct);
                            });
                        }
                    });
            }, cancellationToken);
        }

        /// <summary>
        /// 大文件流式处理流水线
        /// </summary>
        private async Task LargeFileProcessingPipeline(CancellationToken cancellationToken)
        {
            var channels = new[] { _largeFileChannel, _hugeFileChannel };

            await Task.Run(async () =>
            {
                await Parallel.ForEachAsync(channels,
                    new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellationToken },
                    async (channel, ct) =>
                    {
                        await foreach (var operation in channel.Reader.ReadAllAsync(ct))
                        {
                            await _largeFilePool.ExecuteAsync(async () =>
                            {
                                await ProcessLargeFile(operation, ct);
                            });
                        }
                    });
            }, cancellationToken);
        }

        /// <summary>
        /// 目录处理流水线
        /// </summary>
        private async Task DirectoryProcessingPipeline(CancellationToken cancellationToken)
        {
            await foreach (var operation in _directoryChannel.Reader.ReadAllAsync(cancellationToken))
            {
                await _directoryPool.ExecuteAsync(async () =>
                {
                    await ProcessDirectory(operation, cancellationToken);
                });
            }
        }

        /// <summary>
        /// 验证流水线
        /// </summary>
        private async Task VerificationPipeline(CancellationToken cancellationToken)
        {
            await foreach (var operation in _verificationChannel.Reader.ReadAllAsync(cancellationToken))
            {
                await _verificationPool.ExecuteAsync(async () =>
                {
                    await ProcessVerification(operation, cancellationToken);
                });
            }
        }

        /// <summary>
        /// 遥测处理流水线
        /// </summary>
        private async Task TelemetryProcessingPipeline(CancellationToken cancellationToken)
        {
            await foreach (var telemetry in _telemetryChannel.Reader.ReadAllAsync(cancellationToken))
            {
                _perfCounters.Update(telemetry);
            }
        }

        /// <summary>
        /// 错误处理流水线
        /// </summary>
        private async Task ErrorProcessingPipeline(CancellationToken cancellationToken)
        {
            await foreach (var error in _errorChannel.Reader.ReadAllAsync(cancellationToken))
            {
                await ProcessError(error, cancellationToken);
            }
        }

        /// <summary>
        /// 自适应性能优化
        /// </summary>
        private void AdaptiveOptimization(object? state)
        {
            if (_disposed) return;

            try
            {
                // 动态调整并发度
                var newConcurrency = _concurrencyController.CalculateOptimalConcurrency();

                // 调整线程池大小
                _smallFilePool.AdjustConcurrency(newConcurrency.SmallFileThreads);
                _largeFilePool.AdjustConcurrency(newConcurrency.LargeFileThreads);

                // 触发GC如果内存压力过高
                if (_perfCounters.MemoryPressure > 0.8)
                {
                    GC.Collect(2, GCCollectionMode.Optimized);
                }

                _telemetry.RecordMetric("AdaptiveOptimization", newConcurrency.TotalThreads);
            }
            catch (Exception ex)
            {
                // 优化失败不应影响主流程
                Debug.WriteLine($"Adaptive optimization failed: {ex.Message}");
            }
        }

        #region 核心处理方法

        private async Task ProcessFileDetection(FileOperation operation, CancellationToken cancellationToken)
        {
            // 实现文件检测逻辑
            await Task.Delay(1, cancellationToken); // 占位符
        }

        private async Task ProcessSmallFile(FileOperation operation, CancellationToken cancellationToken)
        {
            // 实现小文件快速处理
            await Task.Delay(1, cancellationToken); // 占位符
        }

        private async Task ProcessLargeFile(FileOperation operation, CancellationToken cancellationToken)
        {
            // 实现大文件分块流式处理
            await Task.Delay(1, cancellationToken); // 占位符
        }

        private async Task ProcessDirectory(DirectoryOperation operation, CancellationToken cancellationToken)
        {
            // 实现目录处理
            await Task.Delay(1, cancellationToken); // 占位符
        }

        private async Task ProcessVerification(VerificationOperation operation, CancellationToken cancellationToken)
        {
            // 实现文件完整性验证
            await Task.Delay(1, cancellationToken); // 占位符
        }

        private async Task ProcessError(ErrorEvent error, CancellationToken cancellationToken)
        {
            // 实现错误处理和重试逻辑
            await Task.Delay(1, cancellationToken); // 占位符
        }

        #endregion

        #region 辅助方法

        private int CalculatePriority(string filePath, FileOperationType type)
        {
            var priority = 1000;

            // 类型优先级
            priority += type switch
            {
                FileOperationType.Copy => 100,
                FileOperationType.Move => 200,
                FileOperationType.Delete => 50,
                _ => 0
            };

            // 扩展名优先级
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            priority += ext switch
            {
                ".tmp" => -100,
                ".log" => -50,
                ".exe" => 300,
                ".dll" => 250,
                _ => 0
            };

            return priority;
        }

        private string GetSizeCategory(long size)
        {
            return size switch
            {
                < 1_000_000 => "Tiny",
                < 10_000_000 => "Small",
                < 100_000_000 => "Medium",
                < 1_000_000_000 => "Large",
                _ => "Huge"
            };
        }

        private async Task ReportError(FileOperation operation, Exception exception, string component)
        {
            var error = new ErrorEvent
            {
                OperationId = operation.Id,
                Component = component,
                Exception = exception,
                Timestamp = DateTime.UtcNow,
                Operation = operation
            };

            await _errorChannel.Writer.WriteAsync(error);
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _engineCancellation?.Cancel();
                _adaptiveTimer?.Dispose();

                // 关闭所有Channel写入器
                _detectionChannel.Writer.Complete();
                _tinyFileChannel.Writer.Complete();
                _smallFileChannel.Writer.Complete();
                _mediumFileChannel.Writer.Complete();
                _largeFileChannel.Writer.Complete();
                _hugeFileChannel.Writer.Complete();
                _directoryChannel.Writer.Complete();
                _verificationChannel.Writer.Complete();
                _telemetryChannel.Writer.Complete();
                _errorChannel.Writer.Complete();

                // 等待所有活跃操作完成
                var completionTasks = _activeOperations.Values.Select(tcs => tcs.Task).ToArray();
                Task.WaitAll(completionTasks, TimeSpan.FromSeconds(30));

                // 释放线程池资源
                _fileDetectionPool?.Dispose();
                _smallFilePool?.Dispose();
                _largeFilePool?.Dispose();
                _directoryPool?.Dispose();
                _verificationPool?.Dispose();
                _compressionPool?.Dispose();
                _encryptionPool?.Dispose();
                _indexingPool?.Dispose();

                _engineCancellation?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during ParallelSyncEngine disposal: {ex.Message}");
            }
        }

        #endregion
    }

    #region 数据结构

    public class FileOperation
    {
        public string Id { get; set; } = string.Empty;
        public string SourcePath { get; set; } = string.Empty;
        public string TargetPath { get; set; } = string.Empty;
        public FileOperationType Type { get; set; }
        public long FileSize { get; set; }
        public int Priority { get; set; }
        public DateTime SubmittedAt { get; set; }
        public int RetryCount { get; set; }
    }

    public class DirectoryOperation
    {
        public string Id { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public DirectoryOperationType Type { get; set; }
        public DateTime SubmittedAt { get; set; }
    }

    public class VerificationOperation
    {
        public string Id { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string ExpectedHash { get; set; } = string.Empty;
        public DateTime SubmittedAt { get; set; }
    }

    public class ErrorEvent
    {
        public string OperationId { get; set; } = string.Empty;
        public string Component { get; set; } = string.Empty;
        public Exception Exception { get; set; } = new Exception();
        public DateTime Timestamp { get; set; }
        public FileOperation Operation { get; set; } = new FileOperation();
    }

    public enum FileOperationType
    {
        Copy,
        Move,
        Delete,
        Verify
    }

    public enum DirectoryOperationType
    {
        Create,
        Delete,
        Move
    }

    #endregion

    #region 支持类

    /// <summary>
    /// 专用线程池执行器
    /// </summary>
    public class ThreadPoolExecutor : IDisposable
    {
        private readonly string _name;
        private readonly SemaphoreSlim _semaphore;
        private readonly CancellationToken _cancellationToken;
        private readonly ConcurrentQueue<Func<Task>> _taskQueue;
        private readonly Task[] _workers;
        private volatile int _maxConcurrency;
        private volatile bool _disposed;
        private long _executedTasks;
        private long _failedTasks;

        public ThreadPoolExecutor(string name, int concurrency, CancellationToken cancellationToken)
        {
            _name = name;
            _maxConcurrency = Math.Max(1, concurrency);
            _semaphore = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
            _cancellationToken = cancellationToken;
            _taskQueue = new ConcurrentQueue<Func<Task>>();
            _workers = new Task[_maxConcurrency];

            // 启动工作线程
            for (int i = 0; i < _maxConcurrency; i++)
            {
                int workerId = i;
                _workers[i] = Task.Run(() => WorkerLoop(workerId), cancellationToken);
            }
        }

        public async Task ExecuteAsync(Func<Task> taskFactory)
        {
            if (_disposed) return;

            _taskQueue.Enqueue(taskFactory);
            await Task.Yield(); // 让出线程，让工作线程处理
        }

        private async Task WorkerLoop(int workerId)
        {
            while (!_cancellationToken.IsCancellationRequested && !_disposed)
            {
                if (_taskQueue.TryDequeue(out var taskFactory))
                {
                    await _semaphore.WaitAsync(_cancellationToken);
                    try
                    {
                        await taskFactory();
                        Interlocked.Increment(ref _executedTasks);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _failedTasks);
                        Debug.WriteLine($"[{_name}-Worker{workerId}] Task failed: {ex.Message}");
                    }
                    finally
                    {
                        _semaphore.Release();
                    }
                }
                else
                {
                    await Task.Delay(10, _cancellationToken);
                }
            }
        }

        public void AdjustConcurrency(int newConcurrency)
        {
            if (newConcurrency == _maxConcurrency) return;

            int diff = newConcurrency - _maxConcurrency;
            if (diff > 0)
            {
                // 增加并发度
                _semaphore.Release(diff);
            }
            else
            {
                // 减少并发度
                for (int i = 0; i < Math.Abs(diff); i++)
                {
                    _ = _semaphore.WaitAsync();
                }
            }
            _maxConcurrency = newConcurrency;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Task.WaitAll(_workers, TimeSpan.FromSeconds(5));
            _semaphore?.Dispose();
        }
    }

    /// <summary>
    /// 遥测数据收集器
    /// </summary>
    public class TelemetryCollector
    {
        private readonly ChannelWriter<TelemetryData> _writer;
        private readonly ConcurrentDictionary<string, long> _metrics;
        private readonly ConcurrentDictionary<string, long> _operations;
        private readonly Stopwatch _uptime;

        public TelemetryCollector(ChannelWriter<TelemetryData> writer)
        {
            _writer = writer;
            _metrics = new ConcurrentDictionary<string, long>();
            _operations = new ConcurrentDictionary<string, long>();
            _uptime = Stopwatch.StartNew();
        }

        public void RecordMetric(string name, double value)
        {
            var data = new TelemetryData
            {
                Type = TelemetryType.Metric,
                Name = name,
                Value = value,
                Timestamp = DateTime.UtcNow
            };
            _ = _writer.TryWrite(data);
        }

        public void RecordOperation(string operation, string details)
        {
            _operations.AddOrUpdate(operation, 1, (key, oldValue) => oldValue + 1);
            var data = new TelemetryData
            {
                Type = TelemetryType.Operation,
                Name = operation,
                Details = details,
                Timestamp = DateTime.UtcNow
            };
            _ = _writer.TryWrite(data);
        }

        public long GetOperationCount(string operation)
        {
            return _operations.TryGetValue(operation, out var count) ? count : 0;
        }

        public TimeSpan Uptime => _uptime.Elapsed;
    }

    /// <summary>
    /// 性能计数器
    /// </summary>
    public class PerformanceCounters
    {
        private long _totalFiles;
        private long _processedFiles;
        private long _failedFiles;
        private long _bytesProcessed;
        private double _avgProcessingTime;
        private readonly Queue<double> _recentProcessingTimes;
        private readonly object _lock = new();

        public PerformanceCounters()
        {
            _recentProcessingTimes = new Queue<double>(100);
        }

        public double MemoryPressure => GC.GetTotalMemory(false) / (double)(GC.GetTotalMemory(true) * 2);
        public long TotalFiles => _totalFiles;
        public long ProcessedFiles => _processedFiles;
        public long FailedFiles => _failedFiles;
        public double SuccessRate => _totalFiles > 0 ? (double)_processedFiles / _totalFiles : 0;
        public double AvgProcessingTime => _avgProcessingTime;

        public void Update(TelemetryData telemetry)
        {
            lock (_lock)
            {
                if (telemetry.Type == TelemetryType.Operation)
                {
                    if (telemetry.Name.Contains("Processed"))
                    {
                        Interlocked.Increment(ref _processedFiles);
                    }
                    else if (telemetry.Name.Contains("Failed"))
                    {
                        Interlocked.Increment(ref _failedFiles);
                    }
                    Interlocked.Increment(ref _totalFiles);
                }
                else if (telemetry.Type == TelemetryType.Metric && telemetry.Name == "ProcessingTime")
                {
                    _recentProcessingTimes.Enqueue(telemetry.Value);
                    if (_recentProcessingTimes.Count > 100)
                    {
                        _recentProcessingTimes.Dequeue();
                    }
                    _avgProcessingTime = _recentProcessingTimes.Average();
                }
            }
        }
    }

    /// <summary>
    /// 自适应并发控制器
    /// </summary>
    public class AdaptiveConcurrencyController
    {
        private readonly int _baseCpuCount;
        private readonly TelemetryCollector _telemetry;
        private readonly PerformanceCounter? _cpuCounter;
        private readonly PerformanceCounter? _memoryCounter;
        private double _lastCpuUsage;
        private double _lastMemoryUsage;

        public AdaptiveConcurrencyController(int cpuCount, TelemetryCollector telemetry)
        {
            _baseCpuCount = cpuCount;
            _telemetry = telemetry;

            try
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _memoryCounter = new PerformanceCounter("Memory", "Available MBytes");
            }
            catch
            {
                // 性能计数器可能不可用
            }
        }

        public (int TotalThreads, int SmallFileThreads, int LargeFileThreads) CalculateOptimalConcurrency()
        {
            try
            {
                _lastCpuUsage = _cpuCounter?.NextValue() ?? 50;
                _lastMemoryUsage = _memoryCounter?.NextValue() ?? 1000;

                // 基于CPU使用率动态调整
                double cpuFactor = _lastCpuUsage switch
                {
                    < 30 => 1.5,  // CPU空闲，增加并发
                    < 50 => 1.2,  // 中等负载
                    < 70 => 1.0,  // 正常负载
                    < 85 => 0.8,  // 高负载，减少并发
                    _ => 0.6      // 极高负载，大幅减少
                };

                // 基于可用内存动态调整
                double memoryFactor = _lastMemoryUsage switch
                {
                    > 8000 => 1.2,  // 充足内存
                    > 4000 => 1.0,  // 正常内存
                    > 2000 => 0.8,  // 内存紧张
                    _ => 0.6        // 内存不足
                };

                double combinedFactor = (cpuFactor + memoryFactor) / 2;

                int totalThreads = (int)(_baseCpuCount * 10 * combinedFactor);
                int smallFileThreads = (int)(_baseCpuCount * 4 * combinedFactor);
                int largeFileThreads = (int)(_baseCpuCount * 2 * combinedFactor);

                _telemetry.RecordMetric("CPU_Usage", _lastCpuUsage);
                _telemetry.RecordMetric("Memory_Available_MB", _lastMemoryUsage);
                _telemetry.RecordMetric("Concurrency_Factor", combinedFactor);

                return (totalThreads, smallFileThreads, largeFileThreads);
            }
            catch
            {
                // 失败时返回默认值
                return (_baseCpuCount * 8, _baseCpuCount * 4, _baseCpuCount * 2);
            }
        }
    }

    /// <summary>
    /// 遥测数据
    /// </summary>
    public class TelemetryData
    {
        public TelemetryType Type { get; set; }
        public string Name { get; set; } = string.Empty;
        public double Value { get; set; }
        public string Details { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
    }

    public enum TelemetryType
    {
        Metric,
        Operation,
        Event,
        Error
    }

    #endregion
}