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
            public long BytesCopied { get; set; }
            public double ThroughputMBps { get; set; }
            public bool IsVerified { get; set; }
            public VerificationMode? VerificationMode { get; set; }
        }

        /// <summary>
        /// 检查文件是否被占用
        /// </summary>
        public static bool IsFileInUse(string filePath)
        {
            if (!File.Exists(filePath))
                return false;

            try
            {
                using var handle = CreateFile(
                    filePath,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    IntPtr.Zero,
                    FileMode.Open,
                    FileAttributes.Normal,
                    IntPtr.Zero);

                return handle.IsInvalid;
            }
            catch
            {
                return true;
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

                    // 使用流式复制技术执行文件复制（解决90GB大文件卡死问题）
                    var logger = new AsyncLogger("Logs", "SafeFileOperations");
                    var streamCopyService = new StreamCopyService(logger);

                    // 配置流式复制选项 - 强制校验，支持大文件
                    var copyOptions = new StreamCopyOptions
                    {
                        VerificationMode = Services.VerificationMode.SHA256, // 强制校验
                        EnforceVerification = true, // 任何情况下都要校验
                        SupportResume = false, // 不支持断点续传
                        UseDirectIO = false, // 不使用直接IO
                        ForceDiskSync = true, // 强制磁盘同步
                        MaxRetries = 0, // 在SafeFileOperations层面处理重试
                        HugeFileStrategy = HugeFileVerificationStrategy.FullHashWithProgress // 90GB+文件使用带进度的完整校验
                    };

                    // 创建进度报告转换器
                    var streamProgress = progress != null ? new Progress<StreamCopyProgress>(p =>
                    {
                        progress.Report($"复制进度: {p.ProgressPercent:F1}% - {p.CurrentSpeedMBps:F1} MB/s - {p.CurrentFileName}");
                    }) : null;

                    // 执行流式复制
                    var copyResult = await streamCopyService.CopyFileAsync(
                        sourcePath,
                        targetPath,
                        copyOptions,
                        streamProgress,
                        cancellationToken);

                    if (!copyResult.Success)
                    {
                        result.LastException = copyResult.Exception;
                        result.Message = $"流式复制失败: {copyResult.ErrorMessage}";
                        if (attempt == retryConfig.MaxAttempts)
                        {
                            result.TotalWaitTime = DateTime.Now - startTime;
                            return result;
                        }
                        continue; // 重试
                    }

                    // 复制文件属性
                    var sourceInfo = new FileInfo(sourcePath);
                    var targetInfo = new FileInfo(targetPath);
                    targetInfo.CreationTime = sourceInfo.CreationTime;
                    targetInfo.LastWriteTime = sourceInfo.LastWriteTime;
                    targetInfo.Attributes = sourceInfo.Attributes;

                    result.Success = true;
                    result.BytesCopied = copyResult.BytesCopied;
                    result.ThroughputMBps = copyResult.ThroughputMBps;
                    result.IsVerified = copyResult.IsVerified;
                    result.VerificationMode = copyResult.VerificationResult?.Mode;
                    result.Message = $"文件复制成功 - {copyResult.Summary}";
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