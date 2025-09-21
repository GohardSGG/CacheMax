using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CacheMax.GUI.Services
{
    /// <summary>
    /// 安全的文件操作类，支持文件锁检测和智能重试机制
    /// 使用流式复制技术解决大文件(90GB+)卡死问题
    /// </summary>
    public static class SafeFileOperations
    {
        private const int ERROR_SHARING_VIOLATION = 32;
        private const int ERROR_LOCK_VIOLATION = 33;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            FileAccess dwDesiredAccess,
            FileShare dwShareMode,
            IntPtr lpSecurityAttributes,
            FileMode dwCreationDisposition,
            FileAttributes dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        /// <summary>
        /// 文件锁定句柄，用于在处理过程中锁定文件
        /// </summary>
        public class FileLockHandle : IDisposable
        {
            private readonly SafeFileHandle _handle;
            private readonly string _filePath;
            private bool _disposed = false;

            internal FileLockHandle(SafeFileHandle handle, string filePath)
            {
                _handle = handle;
                _filePath = filePath;
            }

            public string FilePath => _filePath;
            public bool IsValid => _handle != null && !_handle.IsInvalid && !_handle.IsClosed;

            public void Dispose()
            {
                if (!_disposed)
                {
                    _handle?.Dispose();
                    _disposed = true;
                }
                GC.SuppressFinalize(this);
            }
        }

        /// <summary>
        /// 重试配置
        /// </summary>
        public class RetryConfig
        {
            public int MaxAttempts { get; set; } = 5;
            public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMilliseconds(100);
            public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(10);
            public double BackoffMultiplier { get; set; } = 2.0;
            public TimeSpan FileCheckInterval { get; set; } = TimeSpan.FromMilliseconds(500);
        }

        /// <summary>
        /// 文件操作结果
        /// </summary>
        public class FileOperationResult
        {
            public bool Success { get; set; }
            public string Message { get; set; } = string.Empty;
            public int AttemptCount { get; set; }
            public TimeSpan TotalWaitTime { get; set; }
            public Exception? LastException { get; set; }
        }

        /// <summary>
        /// 获取文件的只读共享锁定，防止其他进程写入
        /// 使用FILE_SHARE_READ模式，允许其他进程读取但禁止写入
        /// </summary>
        public static FileLockHandle? AcquireReadOnlyLock(string filePath)
        {
            if (!File.Exists(filePath))
                return null;

            try
            {
                var handle = CreateFile(
                    filePath,
                    FileAccess.Read,
                    FileShare.Read, // 只允许其他进程读取
                    IntPtr.Zero,
                    FileMode.Open,
                    FileAttributes.Normal,
                    IntPtr.Zero);

                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    return null;
                }

                return new FileLockHandle(handle, filePath);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 检查文件是否被占用（修复版：只检测写入占用，允许读取共享）
        /// FastCopy只需要读取源文件，所以只要能读取就可以复制
        /// </summary>
        public static bool IsFileInUse(string filePath)
        {
            if (!File.Exists(filePath))
                return false;

            try
            {
                // 修复：只检测是否能以读取方式打开文件
                // FastCopy不需要写入源文件，所以只要能读取就足够了
                using var handle = CreateFile(
                    filePath,
                    FileAccess.Read,  // 只需要读取权限
                    FileShare.ReadWrite, // 允许其他进程读写
                    IntPtr.Zero,
                    FileMode.Open,
                    FileAttributes.Normal,
                    IntPtr.Zero);

                return handle.IsInvalid;
            }
            catch
            {
                // 如果连读取都失败，说明真的被严格锁定
                return true;
            }
        }

        /// <summary>
        /// 检查文件写入是否完成（改进版：更宽松的检测逻辑）
        /// </summary>
        public static async Task<bool> IsFileWriteComplete(string filePath, int maxRetries = 6)
        {
            if (!File.Exists(filePath))
                return false;

            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    // 再次检查文件是否存在，可能在循环过程中被删除
                    if (!File.Exists(filePath))
                        return false;

                    // 1. 首先检查文件大小稳定性（主要判断标准）
                    var fileInfo1 = new FileInfo(filePath);
                    if (!fileInfo1.Exists) return false;
                    var size1 = fileInfo1.Length;

                    await Task.Delay(1000); // 缩短等待时间到1秒

                    // 再次检查文件是否存在
                    if (!File.Exists(filePath))
                        return false;

                    var fileInfo2 = new FileInfo(filePath);
                    if (!fileInfo2.Exists) return false;
                    var size2 = fileInfo2.Length;

                    if (size1 == size2 && size1 > 0)
                    {
                        // 2. 只有在大小稳定的情况下，才尝试检测写入锁
                        // 使用更宽松的检测：只检查是否能读取文件
                        try
                        {
                            using (var readTest = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                // 能读取就说明文件基本完整
                                return true;
                            }
                        }
                        catch (IOException)
                        {
                            // 如果无法读取，可能仍在写入，继续等待
                            continue;
                        }
                        catch (UnauthorizedAccessException)
                        {
                            // 权限问题，但大小稳定，认为写入完成
                            return true;
                        }
                    }
                }
                catch (FileNotFoundException)
                {
                    // 文件被删除
                    return false;
                }
                catch (Exception)
                {
                    // 其他异常，等待重试
                }

                // 递增延迟等待
                await Task.Delay(500 * (i + 1));
            }

            // 超时后，如果文件存在且大于0字节，假设写入完成
            // 这是为了避免误判完成的大文件
            try
            {
                var finalInfo = new FileInfo(filePath);
                return finalInfo.Exists && finalInfo.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 等待文件变为可用状态
        /// </summary>
        public static async Task<bool> WaitForFileAvailable(string filePath, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var startTime = DateTime.Now;
            var checkInterval = TimeSpan.FromMilliseconds(100);

            while (DateTime.Now - startTime < timeout)
            {
                if (cancellationToken.IsCancellationRequested)
                    return false;

                if (!IsFileInUse(filePath))
                    return true;

                try
                {
                    await Task.Delay(checkInterval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// 安全复制文件，支持重试和文件锁检测
        /// 使用流式复制技术，支持90GB+大文件，带强制校验
        /// </summary>
        public static async Task<FileOperationResult> SafeCopyFileAsync(
            string sourcePath,
            string targetPath,
            bool overwrite = true,
            RetryConfig? retryConfig = null,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            retryConfig ??= new RetryConfig();
            var result = new FileOperationResult();
            var startTime = DateTime.Now;

            for (int attempt = 1; attempt <= retryConfig.MaxAttempts; attempt++)
            {
                result.AttemptCount = attempt;

                try
                {
                    // 检查取消请求
                    cancellationToken.ThrowIfCancellationRequested();

                    // 检查源文件是否存在
                    if (!File.Exists(sourcePath))
                    {
                        result.Message = $"源文件不存在：{sourcePath}";
                        return result;
                    }

                    // 检查源文件是否被占用
                    if (IsFileInUse(sourcePath))
                    {
                        progress?.Report($"源文件被占用，等待释放：{Path.GetFileName(sourcePath)} (尝试 {attempt}/{retryConfig.MaxAttempts})");

                        var waitTimeout = TimeSpan.FromMilliseconds(Math.Min(
                            retryConfig.InitialDelay.TotalMilliseconds * Math.Pow(retryConfig.BackoffMultiplier, attempt - 1),
                            retryConfig.MaxDelay.TotalMilliseconds));

                        if (!await WaitForFileAvailable(sourcePath, waitTimeout, cancellationToken))
                        {
                            if (attempt == retryConfig.MaxAttempts)
                            {
                                result.Message = $"源文件持续被占用：{Path.GetFileName(sourcePath)}";
                                result.TotalWaitTime = DateTime.Now - startTime;
                                return result;
                            }
                            continue;
                        }
                    }

                    // 检查目标文件是否被占用
                    if (File.Exists(targetPath) && IsFileInUse(targetPath))
                    {
                        progress?.Report($"目标文件被占用，等待释放：{Path.GetFileName(targetPath)} (尝试 {attempt}/{retryConfig.MaxAttempts})");

                        var waitTimeout = TimeSpan.FromMilliseconds(Math.Min(
                            retryConfig.InitialDelay.TotalMilliseconds * Math.Pow(retryConfig.BackoffMultiplier, attempt - 1),
                            retryConfig.MaxDelay.TotalMilliseconds));

                        if (!await WaitForFileAvailable(targetPath, waitTimeout, cancellationToken))
                        {
                            if (attempt == retryConfig.MaxAttempts)
                            {
                                result.Message = $"目标文件持续被占用：{Path.GetFileName(targetPath)}";
                                result.TotalWaitTime = DateTime.Now - startTime;
                                return result;
                            }
                            continue;
                        }
                    }

                    // 确保目标目录存在
                    var targetDir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                    }

                    // 使用FastCopy进行高效文件复制，支持90GB+大文件
                    var fastCopyService = new FastCopyService();

                    // 创建进度报告转换器
                    var fastCopyProgress = progress != null ? new Progress<string>(msg =>
                    {
                        progress.Report(msg);
                    }) : null;

                    // 执行FastCopy复制
                    var copySuccess = await fastCopyService.CopyWithVerifyAsync(
                        sourcePath,
                        targetPath,
                        fastCopyProgress);

                    if (!copySuccess)
                    {
                        result.Message = $"FastCopy复制失败";
                        if (attempt == retryConfig.MaxAttempts)
                        {
                            result.TotalWaitTime = DateTime.Now - startTime;
                            return result;
                        }
                        continue; // 重试
                    }

                    result.Success = true;
                    result.Message = $"文件复制成功";
                    result.TotalWaitTime = DateTime.Now - startTime;

                    if (attempt > 1)
                    {
                        progress?.Report($"文件复制成功（经过 {attempt} 次尝试）：{Path.GetFileName(sourcePath)}");
                    }

                    return result;
                }
                catch (UnauthorizedAccessException ex)
                {
                    result.LastException = ex;
                    if (attempt == retryConfig.MaxAttempts)
                    {
                        result.Message = $"权限不足：{ex.Message}";
                        break;
                    }
                    progress?.Report($"权限不足，重试中：{Path.GetFileName(sourcePath)} (尝试 {attempt}/{retryConfig.MaxAttempts})");
                }
                catch (IOException ex) when (IsFileLockException(ex))
                {
                    result.LastException = ex;
                    if (attempt == retryConfig.MaxAttempts)
                    {
                        result.Message = $"文件被占用：{ex.Message}";
                        break;
                    }

                    progress?.Report($"文件被占用，重试中：{Path.GetFileName(sourcePath)} (尝试 {attempt}/{retryConfig.MaxAttempts})");

                    var delay = TimeSpan.FromMilliseconds(Math.Min(
                        retryConfig.InitialDelay.TotalMilliseconds * Math.Pow(retryConfig.BackoffMultiplier, attempt - 1),
                        retryConfig.MaxDelay.TotalMilliseconds));

                    try
                    {
                        await Task.Delay(delay, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        result.Message = "操作被取消";
                        result.TotalWaitTime = DateTime.Now - startTime;
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    result.LastException = ex;
                    result.Message = $"文件操作异常：{ex.Message}";
                    break;
                }
            }

            result.TotalWaitTime = DateTime.Now - startTime;
            return result;
        }

        /// <summary>
        /// 安全删除文件，支持重试
        /// </summary>
        public static async Task<FileOperationResult> SafeDeleteFileAsync(
            string filePath,
            RetryConfig? retryConfig = null,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            retryConfig ??= new RetryConfig();
            var result = new FileOperationResult();
            var startTime = DateTime.Now;

            if (!File.Exists(filePath))
            {
                result.Success = true;
                result.Message = "文件不存在（已删除）";
                return result;
            }

            for (int attempt = 1; attempt <= retryConfig.MaxAttempts; attempt++)
            {
                result.AttemptCount = attempt;

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // 检查文件是否被占用
                    if (IsFileInUse(filePath))
                    {
                        progress?.Report($"文件被占用，等待释放：{Path.GetFileName(filePath)} (尝试 {attempt}/{retryConfig.MaxAttempts})");

                        var waitTimeout = TimeSpan.FromMilliseconds(Math.Min(
                            retryConfig.InitialDelay.TotalMilliseconds * Math.Pow(retryConfig.BackoffMultiplier, attempt - 1),
                            retryConfig.MaxDelay.TotalMilliseconds));

                        if (!await WaitForFileAvailable(filePath, waitTimeout, cancellationToken))
                        {
                            if (attempt == retryConfig.MaxAttempts)
                            {
                                result.Message = $"文件持续被占用，无法删除：{Path.GetFileName(filePath)}";
                                result.TotalWaitTime = DateTime.Now - startTime;
                                return result;
                            }
                            continue;
                        }
                    }

                    // 尝试删除文件
                    File.Delete(filePath);

                    result.Success = true;
                    result.Message = "文件删除成功";
                    result.TotalWaitTime = DateTime.Now - startTime;

                    if (attempt > 1)
                    {
                        progress?.Report($"文件删除成功（经过 {attempt} 次尝试）：{Path.GetFileName(filePath)}");
                    }

                    return result;
                }
                catch (UnauthorizedAccessException ex)
                {
                    result.LastException = ex;
                    result.Message = $"权限不足：{ex.Message}";
                    break;
                }
                catch (IOException ex) when (IsFileLockException(ex))
                {
                    result.LastException = ex;
                    if (attempt == retryConfig.MaxAttempts)
                    {
                        result.Message = $"文件被占用：{ex.Message}";
                        break;
                    }

                    var delay = TimeSpan.FromMilliseconds(Math.Min(
                        retryConfig.InitialDelay.TotalMilliseconds * Math.Pow(retryConfig.BackoffMultiplier, attempt - 1),
                        retryConfig.MaxDelay.TotalMilliseconds));

                    try
                    {
                        await Task.Delay(delay, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        result.Message = "操作被取消";
                        result.TotalWaitTime = DateTime.Now - startTime;
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    result.LastException = ex;
                    result.Message = $"文件删除异常：{ex.Message}";
                    break;
                }
            }

            result.TotalWaitTime = DateTime.Now - startTime;
            return result;
        }

        /// <summary>
        /// 检查是否为文件锁定相关异常
        /// </summary>
        private static bool IsFileLockException(IOException ex)
        {
            var hresult = Marshal.GetHRForException(ex);
            return hresult == ERROR_SHARING_VIOLATION || hresult == ERROR_LOCK_VIOLATION ||
                   ex.Message.Contains("being used by another process") ||
                   ex.Message.Contains("because it is being used") ||
                   ex.Message.Contains("另一个进程正在使用");
        }

        /// <summary>
        /// 获取文件的占用进程信息（仅用于调试）
        /// </summary>
        public static string GetFileLockInfo(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return "文件不存在";

                if (!IsFileInUse(filePath))
                    return "文件未被占用";

                return "文件被占用（无法获取详细进程信息）";
            }
            catch (Exception ex)
            {
                return $"检查失败：{ex.Message}";
            }
        }
    }
}