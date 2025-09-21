using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CacheMax.GUI.Services
{
    /// <summary>
    /// 完全异步的日志系统 - 高性能、非阻塞、线程安全
    /// 单例模式确保所有日志写入同一个文件
    /// </summary>
    public class AsyncLogger : IDisposable
    {
        private static readonly Lazy<AsyncLogger> _instance = new(() => new AsyncLogger());
        public static AsyncLogger Instance => _instance.Value;

        private Channel<LogEntry> _logChannel;
        private CancellationTokenSource _cancellationTokenSource;
        private Task _writerTask;
        private readonly string _logDirectory;
        private readonly string _logFileNamePrefix;
        private readonly long _maxLogFileSize;
        private readonly int _maxLogFiles;
        private string _currentLogFile;
        private FileStream? _currentFileStream;
        private StreamWriter? _currentWriter;
        private readonly object _fileOperationLock = new();
        private volatile bool _disposed;

        private AsyncLogger()
        {
            // 单例模式：使用固定配置
            _logDirectory = "Logs";
            _logFileNamePrefix = "CacheMax";
            _maxLogFileSize = 50 * 1024 * 1024; // 50MB
            _maxLogFiles = 5;

            Initialize();
        }

        public AsyncLogger(
            string logDirectory = "Logs",
            string logFileNamePrefix = "CacheMax",
            long maxLogFileSize = 50 * 1024 * 1024, // 50MB
            int maxLogFiles = 10)
        {
            _logDirectory = logDirectory;
            _logFileNamePrefix = logFileNamePrefix;
            _maxLogFileSize = maxLogFileSize;
            _maxLogFiles = maxLogFiles;

            Initialize();
        }

        private void Initialize()
        {
            _cancellationTokenSource = new CancellationTokenSource();

            // 创建无界Channel确保不会因为日志队列满而阻塞
            var channelOptions = new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            };
            _logChannel = Channel.CreateUnbounded<LogEntry>(channelOptions);

            // 确保日志目录存在
            Directory.CreateDirectory(_logDirectory);
            _currentLogFile = GetNextLogFileName();

            // 启动后台写入任务
            _writerTask = Task.Run(WriteLogsAsync, _cancellationTokenSource.Token);
        }

        /// <summary>
        /// 记录信息日志（完全非阻塞）
        /// </summary>
        public void LogInfo(string message, string? component = null)
        {
            LogInternal(LogLevel.Info, message, component, null);
        }

        /// <summary>
        /// 记录警告日志（完全非阻塞）
        /// </summary>
        public void LogWarning(string message, string? component = null)
        {
            LogInternal(LogLevel.Warning, message, component, null);
        }

        /// <summary>
        /// 记录错误日志（完全非阻塞）
        /// </summary>
        public void LogError(string message, Exception? exception = null, string? component = null)
        {
            LogInternal(LogLevel.Error, message, component, exception);
        }

        /// <summary>
        /// 记录调试日志（完全非阻塞）
        /// </summary>
        public void LogDebug(string message, string? component = null)
        {
            LogInternal(LogLevel.Debug, message, component, null);
        }

        /// <summary>
        /// 记录性能数据（完全非阻塞）
        /// </summary>
        public void LogPerformance(string operation, TimeSpan duration, string? details = null)
        {
            var message = $"性能监控: {operation} 耗时: {duration.TotalMilliseconds:F2}ms";
            if (!string.IsNullOrEmpty(details))
                message += $" - {details}";

            LogInternal(LogLevel.Performance, message, "Performance", null);
        }

        /// <summary>
        /// 内部日志方法
        /// </summary>
        private void LogInternal(LogLevel level, string message, string? component, Exception? exception)
        {
            if (_disposed) return;

            var logEntry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Level = level,
                Message = message,
                Component = component ?? "Unknown",
                ThreadId = Thread.CurrentThread.ManagedThreadId,
                Exception = exception
            };

            // 非阻塞写入，如果Channel已满则丢弃（但UnboundedChannel不会满）
            if (!_logChannel.Writer.TryWrite(logEntry))
            {
                // 这种情况在UnboundedChannel中不应该发生，但为了安全起见
                Console.WriteLine($"[AsyncLogger] Failed to queue log: {message}");
            }
        }

        /// <summary>
        /// 后台日志写入任务
        /// </summary>
        private async Task WriteLogsAsync()
        {
            try
            {
                await foreach (var logEntry in _logChannel.Reader.ReadAllAsync(_cancellationTokenSource.Token))
                {
                    await WriteLogEntryAsync(logEntry);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常关闭
            }
            catch (Exception ex)
            {
                // 日志系统自身出错，输出到控制台
                Console.WriteLine($"[AsyncLogger] WriteLogsAsync error: {ex}");
            }
            finally
            {
                await CloseCurrentFileAsync();
            }
        }

        /// <summary>
        /// 写入单个日志条目
        /// </summary>
        private async Task WriteLogEntryAsync(LogEntry entry)
        {
            try
            {
                await EnsureCurrentFileAsync();

                if (_currentWriter != null)
                {
                    var logLine = FormatLogEntry(entry);
                    await _currentWriter.WriteLineAsync(logLine);
                    await _currentWriter.FlushAsync();

                    // 检查文件大小，必要时轮转
                    if (_currentFileStream?.Length > _maxLogFileSize)
                    {
                        await RotateLogFileAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AsyncLogger] WriteLogEntryAsync error: {ex}");
            }
        }

        /// <summary>
        /// 格式化日志条目
        /// </summary>
        private static string FormatLogEntry(LogEntry entry)
        {
            var levelStr = entry.Level switch
            {
                LogLevel.Debug => "DEBUG",
                LogLevel.Info => "INFO ",
                LogLevel.Warning => "WARN ",
                LogLevel.Error => "ERROR",
                LogLevel.Performance => "PERF ",
                _ => "UNKNOWN"
            };

            var logLine = $"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{levelStr}] [T{entry.ThreadId:D3}] [{entry.Component}] {entry.Message}";

            if (entry.Exception != null)
            {
                logLine += $"\n异常详情: {entry.Exception}";
            }

            return logLine;
        }

        /// <summary>
        /// 确保当前日志文件打开
        /// </summary>
        private async Task EnsureCurrentFileAsync()
        {
            if (_currentWriter == null || _currentFileStream == null)
            {
                lock (_fileOperationLock)
                {
                    if (_currentWriter == null || _currentFileStream == null)
                    {
                        _currentFileStream = new FileStream(_currentLogFile, FileMode.Append, FileAccess.Write, FileShare.Read, 8192, FileOptions.SequentialScan);
                        _currentWriter = new StreamWriter(_currentFileStream, System.Text.Encoding.UTF8, 8192, false);
                    }
                }
            }
        }

        /// <summary>
        /// 轮转日志文件
        /// </summary>
        private async Task RotateLogFileAsync()
        {
            await CloseCurrentFileAsync();
            CleanOldLogFiles();
            _currentLogFile = GetNextLogFileName();
            await EnsureCurrentFileAsync();
        }

        /// <summary>
        /// 关闭当前日志文件
        /// </summary>
        private async Task CloseCurrentFileAsync()
        {
            if (_currentWriter != null)
            {
                await _currentWriter.FlushAsync();
                await _currentWriter.DisposeAsync();
                _currentWriter = null;
            }

            if (_currentFileStream != null)
            {
                await _currentFileStream.FlushAsync();
                await _currentFileStream.DisposeAsync();
                _currentFileStream = null;
            }
        }

        /// <summary>
        /// 生成下一个日志文件名
        /// </summary>
        private string GetNextLogFileName()
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            return Path.Combine(_logDirectory, $"{_logFileNamePrefix}_{timestamp}.log");
        }

        /// <summary>
        /// 清理旧日志文件
        /// </summary>
        private void CleanOldLogFiles()
        {
            try
            {
                var logFiles = Directory.GetFiles(_logDirectory, $"{_logFileNamePrefix}_*.log");
                if (logFiles.Length >= _maxLogFiles)
                {
                    Array.Sort(logFiles);
                    for (int i = 0; i < logFiles.Length - _maxLogFiles + 1; i++)
                    {
                        try
                        {
                            File.Delete(logFiles[i]);
                        }
                        catch
                        {
                            // 忽略删除失败
                        }
                    }
                }
            }
            catch
            {
                // 忽略清理失败
            }
        }

        /// <summary>
        /// 立即刷新所有待写入的日志
        /// </summary>
        public async Task FlushAsync()
        {
            if (_disposed) return;

            // 等待所有待处理的日志写入完成
            _logChannel.Writer.Complete();
            await _writerTask;

            // 重新打开Channel
            var channelOptions = new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            };
            var newChannel = Channel.CreateUnbounded<LogEntry>(channelOptions);
            // 注意：这里简化处理，实际应用中可能需要更复杂的重新初始化逻辑
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _logChannel.Writer.Complete();
                _cancellationTokenSource.Cancel();

                // 等待写入任务完成，但设置超时避免无限等待
                if (!_writerTask.Wait(5000))
                {
                    Console.WriteLine("[AsyncLogger] WriteTask did not complete within timeout");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AsyncLogger] Dispose error: {ex}");
            }
            finally
            {
                _cancellationTokenSource.Dispose();
                CloseCurrentFileAsync().Wait(1000);
            }
        }
    }

    /// <summary>
    /// 日志条目
    /// </summary>
    public class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public LogLevel Level { get; set; }
        public string Message { get; set; } = string.Empty;
        public string Component { get; set; } = string.Empty;
        public int ThreadId { get; set; }
        public Exception? Exception { get; set; }
    }

    /// <summary>
    /// 日志级别
    /// </summary>
    public enum LogLevel
    {
        Debug,
        Info,
        Warning,
        Error,
        Performance
    }
}