using System;

namespace CacheMax.GUI.Services
{
    /// <summary>
    /// 流式复制配置选项
    /// </summary>
    public class StreamCopyOptions
    {
        /// <summary>
        /// 校验模式 - 强制必须校验，任何情况下都不能为None
        /// </summary>
        public VerificationMode VerificationMode { get; set; } = VerificationMode.SHA256;

        /// <summary>
        /// 缓冲区大小倍数（基于文件大小的动态倍数）
        /// </summary>
        public double BufferSizeMultiplier { get; set; } = 1.0;

        /// <summary>
        /// 进度报告间隔
        /// </summary>
        public TimeSpan ProgressReportInterval { get; set; } = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// 刷新间隔（每N个块强制刷新到磁盘）
        /// </summary>
        public int FlushInterval { get; set; } = 10;

        /// <summary>
        /// 使用直接IO（绕过系统缓存）
        /// </summary>
        public bool UseDirectIO { get; set; } = false;

        /// <summary>
        /// 强制磁盘同步
        /// </summary>
        public bool ForceDiskSync { get; set; } = true;

        /// <summary>
        /// 内存压力阈值（字节）
        /// </summary>
        public long MemoryPressureThreshold { get; set; } = 512 * 1024 * 1024; // 512MB

        /// <summary>
        /// 支持断点续传
        /// </summary>
        public bool SupportResume { get; set; } = false;

        /// <summary>
        /// 最大重试次数
        /// </summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// 重试延迟
        /// </summary>
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>
        /// 强制校验模式 - 确保任何文件都必须进行完整性验证
        /// </summary>
        public bool EnforceVerification { get; set; } = true;

        /// <summary>
        /// 超大文件（>10GB）的特殊校验策略
        /// </summary>
        public HugeFileVerificationStrategy HugeFileStrategy { get; set; } = HugeFileVerificationStrategy.FullHashWithProgress;
    }

    /// <summary>
    /// 超大文件校验策略
    /// </summary>
    public enum HugeFileVerificationStrategy
    {
        /// <summary>
        /// 完整哈希校验，带进度显示（推荐）
        /// </summary>
        FullHashWithProgress,

        /// <summary>
        /// 分段哈希校验 - 将大文件分成多个段独立校验
        /// </summary>
        SegmentedHash,

        /// <summary>
        /// 双重校验 - 快速MD5 + 安全SHA256
        /// </summary>
        DualHash,

        /// <summary>
        /// 增量校验 - 复制和校验完全并行
        /// </summary>
        IncrementalHash
    }

    /// <summary>
    /// 校验模式
    /// </summary>
    public enum VerificationMode
    {
        /// <summary>
        /// 不校验
        /// </summary>
        None,

        /// <summary>
        /// 仅检查文件大小
        /// </summary>
        Size,

        /// <summary>
        /// 检查大小和修改时间
        /// </summary>
        SizeAndDate,

        /// <summary>
        /// MD5哈希校验（快速但安全性较低）
        /// </summary>
        MD5,

        /// <summary>
        /// SHA256哈希校验（推荐，安全性和性能平衡）
        /// </summary>
        SHA256,

        /// <summary>
        /// SHA512哈希校验（最安全但较慢）
        /// </summary>
        SHA512,

        /// <summary>
        /// 字节级别比较（最严格但最慢，仅用于关键文件）
        /// </summary>
        ByteByByte
    }

    /// <summary>
    /// 流式复制进度信息
    /// </summary>
    public class StreamCopyProgress
    {
        /// <summary>
        /// 总字节数
        /// </summary>
        public long TotalBytes { get; set; }

        /// <summary>
        /// 已复制字节数
        /// </summary>
        public long CopiedBytes { get; set; }

        /// <summary>
        /// 进度百分比
        /// </summary>
        public double ProgressPercent { get; set; }

        /// <summary>
        /// 当前复制速度（MB/s）
        /// </summary>
        public double CurrentSpeedMBps { get; set; }

        /// <summary>
        /// 预估剩余时间
        /// </summary>
        public TimeSpan? EstimatedTimeRemaining { get; set; }

        /// <summary>
        /// 当前处理的文件名
        /// </summary>
        public string? CurrentFileName { get; set; }

        /// <summary>
        /// 额外状态信息
        /// </summary>
        public string? StatusMessage { get; set; }

        public override string ToString()
        {
            var speed = CurrentSpeedMBps > 0 ? $"{CurrentSpeedMBps:F1} MB/s" : "计算中...";
            var eta = EstimatedTimeRemaining?.ToString(@"hh\:mm\:ss") ?? "未知";
            return $"{ProgressPercent:F1}% ({FormatBytes(CopiedBytes)}/{FormatBytes(TotalBytes)}) - {speed} - 剩余: {eta}";
        }

        private static string FormatBytes(long bytes)
        {
            const long KB = 1024;
            const long MB = KB * 1024;
            const long GB = MB * 1024;
            const long TB = GB * 1024;

            return bytes switch
            {
                >= TB => $"{bytes / (double)TB:F1} TB",
                >= GB => $"{bytes / (double)GB:F1} GB",
                >= MB => $"{bytes / (double)MB:F1} MB",
                >= KB => $"{bytes / (double)KB:F1} KB",
                _ => $"{bytes} B"
            };
        }
    }

    /// <summary>
    /// 流式复制结果
    /// </summary>
    public class StreamCopyResult
    {
        /// <summary>
        /// 操作是否成功
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 源文件路径
        /// </summary>
        public string SourcePath { get; set; } = string.Empty;

        /// <summary>
        /// 目标文件路径
        /// </summary>
        public string TargetPath { get; set; } = string.Empty;

        /// <summary>
        /// 复制的字节数
        /// </summary>
        public long BytesCopied { get; set; }

        /// <summary>
        /// 操作耗时
        /// </summary>
        public TimeSpan Duration { get; set; }

        /// <summary>
        /// 平均吞吐量（MB/s）
        /// </summary>
        public double ThroughputMBps { get; set; }

        /// <summary>
        /// 源文件哈希值
        /// </summary>
        public string? SourceHash { get; set; }

        /// <summary>
        /// 目标文件哈希值
        /// </summary>
        public string? TargetHash { get; set; }

        /// <summary>
        /// 校验结果
        /// </summary>
        public VerificationResult? VerificationResult { get; set; }

        /// <summary>
        /// 错误消息
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// 异常详情
        /// </summary>
        public Exception? Exception { get; set; }

        /// <summary>
        /// 重试次数
        /// </summary>
        public int RetryCount { get; set; }

        /// <summary>
        /// 是否通过校验
        /// </summary>
        public bool IsVerified => VerificationResult?.IsValid == true;

        /// <summary>
        /// 操作摘要
        /// </summary>
        public string Summary
        {
            get
            {
                if (!Success)
                    return $"失败: {ErrorMessage}";

                var throughput = ThroughputMBps > 0 ? $"{ThroughputMBps:F1} MB/s" : "未知";
                var verification = VerificationResult?.Mode != VerificationMode.None ?
                    $", 校验: {(IsVerified ? "通过" : "失败")}" : "";

                return $"成功复制 {FormatBytes(BytesCopied)}, 耗时: {Duration:hh\\:mm\\:ss}, 速度: {throughput}{verification}";
            }
        }

        private static string FormatBytes(long bytes)
        {
            const long KB = 1024;
            const long MB = KB * 1024;
            const long GB = MB * 1024;
            const long TB = GB * 1024;

            return bytes switch
            {
                >= TB => $"{bytes / (double)TB:F1} TB",
                >= GB => $"{bytes / (double)GB:F1} GB",
                >= MB => $"{bytes / (double)MB:F1} MB",
                >= KB => $"{bytes / (double)KB:F1} KB",
                _ => $"{bytes} B"
            };
        }
    }

    /// <summary>
    /// 文件校验结果
    /// </summary>
    public class VerificationResult
    {
        /// <summary>
        /// 校验模式
        /// </summary>
        public VerificationMode Mode { get; set; }

        /// <summary>
        /// 校验是否通过
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// 源文件哈希
        /// </summary>
        public string? SourceHash { get; set; }

        /// <summary>
        /// 目标文件哈希
        /// </summary>
        public string? TargetHash { get; set; }

        /// <summary>
        /// 校验耗时
        /// </summary>
        public TimeSpan VerificationTime { get; set; }

        /// <summary>
        /// 错误消息
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// 校验详情
        /// </summary>
        public string Details
        {
            get
            {
                var result = IsValid ? "通过" : "失败";
                var time = VerificationTime.TotalSeconds > 0 ? $", 耗时: {VerificationTime.TotalSeconds:F2}s" : "";

                if (!IsValid && !string.IsNullOrEmpty(ErrorMessage))
                    return $"{Mode}校验{result}: {ErrorMessage}{time}";

                return $"{Mode}校验{result}{time}";
            }
        }
    }

    /// <summary>
    /// 文件完整性校验选项
    /// </summary>
    public class FileIntegrityOptions
    {
        /// <summary>
        /// 主校验模式
        /// </summary>
        public VerificationMode PrimaryMode { get; set; } = VerificationMode.SHA256;

        /// <summary>
        /// 备用校验模式（双重验证）
        /// </summary>
        public VerificationMode? SecondaryMode { get; set; }

        /// <summary>
        /// 对于小文件(<1MB)使用更快的校验
        /// </summary>
        public VerificationMode SmallFileMode { get; set; } = VerificationMode.MD5;

        /// <summary>
        /// 小文件阈值
        /// </summary>
        public long SmallFileThreshold { get; set; } = 1024 * 1024; // 1MB

        /// <summary>
        /// 对于超大文件(>10GB)使用采样校验
        /// </summary>
        public bool UseSamplingForHugeFiles { get; set; } = true;

        /// <summary>
        /// 超大文件阈值
        /// </summary>
        public long HugeFileThreshold { get; set; } = 10L * 1024 * 1024 * 1024; // 10GB

        /// <summary>
        /// 采样间隔（字节）
        /// </summary>
        public long SamplingInterval { get; set; } = 100 * 1024 * 1024; // 每100MB采样

        /// <summary>
        /// 采样块大小
        /// </summary>
        public int SamplingBlockSize { get; set; } = 64 * 1024; // 64KB
    }
}