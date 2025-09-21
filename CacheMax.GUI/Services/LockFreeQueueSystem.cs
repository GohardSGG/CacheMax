using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace CacheMax.GUI.Services
{
    /// <summary>
    /// 无锁分级队列系统 - 支持优先级和公平调度
    /// </summary>
    public class LockFreeQueueSystem<T> where T : IQueueItem
    {
        private readonly ConcurrentDictionary<int, ConcurrentQueue<T>> _priorityQueues;
        private readonly SemaphoreSlim _signal;
        private readonly int[] _priorityWeights;
        private readonly int _maxPriority;
        private long _totalEnqueued;
        private long _totalDequeued;
        private readonly int[] _roundRobinCounters;
        private readonly object _fairnessLock = new();

        public LockFreeQueueSystem(int maxPriority = 5)
        {
            _maxPriority = maxPriority;
            _priorityQueues = new ConcurrentDictionary<int, ConcurrentQueue<T>>();
            _signal = new SemaphoreSlim(0);
            _roundRobinCounters = new int[maxPriority];

            // 初始化所有优先级队列
            for (int i = 0; i < maxPriority; i++)
            {
                _priorityQueues[i] = new ConcurrentQueue<T>();
            }

            // 设置优先级权重（高优先级获得更多处理机会）
            _priorityWeights = new int[maxPriority];
            for (int i = 0; i < maxPriority; i++)
            {
                _priorityWeights[i] = maxPriority - i; // 优先级0权重最高
            }
        }

        /// <summary>
        /// 入队操作（O(1)时间复杂度）
        /// </summary>
        public void Enqueue(T item)
        {
            int priority = Math.Clamp(item.Priority, 0, _maxPriority - 1);
            _priorityQueues[priority].Enqueue(item);
            Interlocked.Increment(ref _totalEnqueued);
            _signal.Release();
        }

        /// <summary>
        /// 批量入队
        /// </summary>
        public void EnqueueRange(IEnumerable<T> items)
        {
            foreach (var item in items)
            {
                Enqueue(item);
            }
        }

        /// <summary>
        /// 出队操作（使用加权轮询算法保证公平性）
        /// </summary>
        public async Task<T?> DequeueAsync(CancellationToken cancellationToken = default)
        {
            await _signal.WaitAsync(cancellationToken);

            // 加权轮询算法
            lock (_fairnessLock)
            {
                for (int round = 0; round < _maxPriority * 2; round++)
                {
                    for (int priority = 0; priority < _maxPriority; priority++)
                    {
                        // 检查该优先级是否还有处理配额
                        if (_roundRobinCounters[priority] < _priorityWeights[priority])
                        {
                            if (_priorityQueues[priority].TryDequeue(out var item))
                            {
                                _roundRobinCounters[priority]++;
                                Interlocked.Increment(ref _totalDequeued);

                                // 重置计数器（当所有优先级都达到配额）
                                if (_roundRobinCounters.All(c => c >= 1))
                                {
                                    for (int i = 0; i < _maxPriority; i++)
                                    {
                                        _roundRobinCounters[i] = 0;
                                    }
                                }

                                return item;
                            }
                        }
                    }
                }

                // 饥饿避免：如果加权轮询没有找到，从任意非空队列取
                for (int priority = 0; priority < _maxPriority; priority++)
                {
                    if (_priorityQueues[priority].TryDequeue(out var item))
                    {
                        Interlocked.Increment(ref _totalDequeued);
                        return item;
                    }
                }
            }

            return default(T);
        }

        /// <summary>
        /// 批量出队
        /// </summary>
        public async Task<List<T>> DequeueBatchAsync(int batchSize, CancellationToken cancellationToken = default)
        {
            var batch = new List<T>(batchSize);
            for (int i = 0; i < batchSize; i++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var item = await DequeueAsync(cancellationToken);
                if (item != null)
                {
                    batch.Add(item);
                }
                else
                {
                    break;
                }
            }
            return batch;
        }

        /// <summary>
        /// 尝试窥视队列头部（不移除）
        /// </summary>
        public bool TryPeek(int priority, out T? item)
        {
            item = default;
            if (priority >= 0 && priority < _maxPriority)
            {
                return _priorityQueues[priority].TryPeek(out item);
            }
            return false;
        }

        /// <summary>
        /// 获取队列统计信息
        /// </summary>
        public QueueStatistics GetStatistics()
        {
            var stats = new QueueStatistics
            {
                TotalEnqueued = _totalEnqueued,
                TotalDequeued = _totalDequeued,
                PriorityDistribution = new Dictionary<int, int>()
            };

            for (int i = 0; i < _maxPriority; i++)
            {
                stats.PriorityDistribution[i] = _priorityQueues[i].Count;
            }

            stats.TotalPending = stats.PriorityDistribution.Values.Sum();
            return stats;
        }

        /// <summary>
        /// 清空指定优先级队列
        /// </summary>
        public void Clear(int priority)
        {
            if (priority >= 0 && priority < _maxPriority)
            {
                while (_priorityQueues[priority].TryDequeue(out _)) { }
            }
        }

        /// <summary>
        /// 清空所有队列
        /// </summary>
        public void ClearAll()
        {
            for (int i = 0; i < _maxPriority; i++)
            {
                Clear(i);
            }
        }

        public int Count => _priorityQueues.Values.Sum(q => q.Count);
        public bool IsEmpty => Count == 0;
    }

    /// <summary>
    /// 队列项接口
    /// </summary>
    public interface IQueueItem
    {
        int Priority { get; }
    }

    /// <summary>
    /// 队列统计信息
    /// </summary>
    public class QueueStatistics
    {
        public long TotalEnqueued { get; set; }
        public long TotalDequeued { get; set; }
        public int TotalPending { get; set; }
        public Dictionary<int, int> PriorityDistribution { get; set; } = new();

        public double Throughput => TotalDequeued > 0 ? TotalDequeued / (double)TotalEnqueued : 0;
        public double AverageQueueDepth => PriorityDistribution.Values.Average();
    }

    /// <summary>
    /// 批量I/O处理器 - 优化磁盘I/O性能
    /// </summary>
    public class BatchIOProcessor
    {
        private readonly int _maxBatchSize;
        private readonly TimeSpan _batchTimeout;
        private readonly ConcurrentDictionary<string, List<IOOperation>> _pendingBatches;
        private readonly Timer _flushTimer;
        private readonly SemaphoreSlim _processingSemaphore;
        private long _totalBatches;
        private long _totalOperations;

        public BatchIOProcessor(int maxBatchSize = 100, int batchTimeoutMs = 100)
        {
            _maxBatchSize = maxBatchSize;
            _batchTimeout = TimeSpan.FromMilliseconds(batchTimeoutMs);
            _pendingBatches = new ConcurrentDictionary<string, List<IOOperation>>();
            _processingSemaphore = new SemaphoreSlim(Environment.ProcessorCount * 2);

            // 定期刷新批次
            _flushTimer = new Timer(FlushTimeoutBatches, null, _batchTimeout, _batchTimeout);
        }

        /// <summary>
        /// 提交I/O操作（自动批量化）
        /// </summary>
        public async Task<bool> SubmitOperationAsync(IOOperation operation)
        {
            // 根据目标路径分组（相同目录的操作批量处理）
            string batchKey = Path.GetDirectoryName(operation.TargetPath) ?? "root";

            var batch = _pendingBatches.AddOrUpdate(batchKey,
                key => new List<IOOperation> { operation },
                (key, list) =>
                {
                    lock (list)
                    {
                        list.Add(operation);
                        return list;
                    }
                });

            // 达到批量大小立即处理
            if (batch.Count >= _maxBatchSize)
            {
                await ProcessBatchAsync(batchKey);
            }

            Interlocked.Increment(ref _totalOperations);
            return true;
        }

        /// <summary>
        /// 处理批次
        /// </summary>
        private async Task ProcessBatchAsync(string batchKey)
        {
            if (!_pendingBatches.TryRemove(batchKey, out var batch)) return;

            await _processingSemaphore.WaitAsync();
            try
            {
                await ExecuteBatchIOAsync(batch);
                Interlocked.Increment(ref _totalBatches);
            }
            finally
            {
                _processingSemaphore.Release();
            }
        }

        /// <summary>
        /// 执行批量I/O（优化系统调用）
        /// </summary>
        private async Task ExecuteBatchIOAsync(List<IOOperation> batch)
        {
            // 按操作类型分组
            var readOps = batch.Where(op => op.Type == IOType.Read).ToList();
            var writeOps = batch.Where(op => op.Type == IOType.Write).ToList();
            var copyOps = batch.Where(op => op.Type == IOType.Copy).ToList();

            // 并行执行不同类型的操作
            var tasks = new List<Task>();

            if (readOps.Any())
            {
                tasks.Add(ExecuteParallelReadsAsync(readOps));
            }

            if (writeOps.Any())
            {
                tasks.Add(ExecuteSequentialWritesAsync(writeOps)); // 写操作顺序执行
            }

            if (copyOps.Any())
            {
                tasks.Add(ExecuteParallelCopiesAsync(copyOps));
            }

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// 并行读取（利用操作系统预读取）
        /// </summary>
        private async Task ExecuteParallelReadsAsync(List<IOOperation> reads)
        {
            var tasks = reads.Select(async op =>
            {
                try
                {
                    op.Data = await File.ReadAllBytesAsync(op.SourcePath);
                    op.Status = IOStatus.Completed;
                }
                catch (Exception ex)
                {
                    op.Status = IOStatus.Failed;
                    op.Error = ex;
                }
            });

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// 顺序写入（减少磁盘寻道）
        /// </summary>
        private async Task ExecuteSequentialWritesAsync(List<IOOperation> writes)
        {
            // 按文件路径排序，减少磁盘寻道
            var sortedWrites = writes.OrderBy(w => w.TargetPath).ToList();

            foreach (var op in sortedWrites)
            {
                try
                {
                    if (op.Data != null)
                    {
                        await File.WriteAllBytesAsync(op.TargetPath, op.Data);
                        op.Status = IOStatus.Completed;
                    }
                }
                catch (Exception ex)
                {
                    op.Status = IOStatus.Failed;
                    op.Error = ex;
                }
            }
        }

        /// <summary>
        /// 并行复制
        /// </summary>
        private async Task ExecuteParallelCopiesAsync(List<IOOperation> copies)
        {
            var tasks = copies.Select(async op =>
            {
                try
                {
                    await Task.Run(() => File.Copy(op.SourcePath, op.TargetPath, true));
                    op.Status = IOStatus.Completed;
                }
                catch (Exception ex)
                {
                    op.Status = IOStatus.Failed;
                    op.Error = ex;
                }
            });

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// 定时刷新超时批次
        /// </summary>
        private async void FlushTimeoutBatches(object? state)
        {
            var tasks = _pendingBatches.Keys.Select(key => ProcessBatchAsync(key));
            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// 获取处理统计
        /// </summary>
        public BatchIOStatistics GetStatistics()
        {
            return new BatchIOStatistics
            {
                TotalBatches = _totalBatches,
                TotalOperations = _totalOperations,
                AverageOperationsPerBatch = _totalBatches > 0 ? _totalOperations / (double)_totalBatches : 0,
                PendingBatches = _pendingBatches.Count
            };
        }

        public void Dispose()
        {
            _flushTimer?.Dispose();
            _processingSemaphore?.Dispose();
        }
    }

    /// <summary>
    /// I/O操作
    /// </summary>
    public class IOOperation : IQueueItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string SourcePath { get; set; } = string.Empty;
        public string TargetPath { get; set; } = string.Empty;
        public IOType Type { get; set; }
        public byte[]? Data { get; set; }
        public int Priority { get; set; }
        public IOStatus Status { get; set; } = IOStatus.Pending;
        public Exception? Error { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public enum IOType
    {
        Read,
        Write,
        Copy,
        Move,
        Delete
    }

    public enum IOStatus
    {
        Pending,
        Processing,
        Completed,
        Failed
    }

    public class BatchIOStatistics
    {
        public long TotalBatches { get; set; }
        public long TotalOperations { get; set; }
        public double AverageOperationsPerBatch { get; set; }
        public int PendingBatches { get; set; }
    }
}