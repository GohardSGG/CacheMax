using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace CacheMax.GUI.Services
{
    /// <summary>
    /// 高性能流式文件复制服务 - 支持校验、进度监控、断点续传
    /// </summary>
    public class StreamCopyService
    {
        private readonly AsyncLogger _logger;

        public StreamCopyService(AsyncLogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 流式复制文件，支持多种校验方式
        /// </summary>
        public async Task<StreamCopyResult> CopyFileAsync(
            string sourcePath,
            string targetPath,
            StreamCopyOptions options,
            IProgress<StreamCopyProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var result = new StreamCopyResult { SourcePath = sourcePath, TargetPath = targetPath };
            var stopwatch = Stopwatch.StartNew();

            try
            {
                _logger.LogInfo($"开始流式复制: {sourcePath} -> {targetPath}", "StreamCopy");

                // 预检查
                var preCheckResult = await PreCopyValidation(sourcePath, targetPath, options);
                if (!preCheckResult.Success)
                {
                    result.ErrorMessage = preCheckResult.ErrorMessage;
                    return result;
                }

                // 确定最优缓冲区大小
                var fileSize = new FileInfo(sourcePath).Length;
                var bufferSize = CalculateOptimalBufferSize(fileSize, options);

                _logger.LogInfo($"文件大小: {fileSize:N0} 字节, 缓冲区: {bufferSize:N0} 字节", "StreamCopy");

                // 强制校验 - 任何情况下都必须校验
                if (options.VerificationMode == VerificationMode.None)
                {
                    _logger.LogWarning("检测到None校验模式，强制改为SHA256确保数据完整性", "StreamCopy");
                    options.VerificationMode = VerificationMode.SHA256;
                }

                // 为超大文件选择最适合的校验策略
                var effectiveVerificationMode = DetermineEffectiveVerificationMode(fileSize, options);
                var hashAlgorithm = CreateHashAlgorithm(effectiveVerificationMode);

                _logger.LogInfo($"强制完整性校验: {effectiveVerificationMode}, 策略: {options.HugeFileStrategy}", "StreamCopy");

                result = await PerformStreamCopyAsync(
                    sourcePath, targetPath, fileSize, bufferSize,
                    hashAlgorithm, effectiveVerificationMode, options, progress, cancellationToken);

                stopwatch.Stop();
                result.Duration = stopwatch.Elapsed;
                result.ThroughputMBps = (fileSize / 1024.0 / 1024.0) / stopwatch.Elapsed.TotalSeconds;

                _logger.LogPerformance($"流式复制完成", stopwatch.Elapsed,
                    $"文件: {Path.GetFileName(sourcePath)}, 吞吐量: {result.ThroughputMBps:F2} MB/s");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"流式复制失败: {sourcePath}", ex, "StreamCopy");
                result.ErrorMessage = ex.Message;
                result.Exception = ex;
                return result;
            }
        }

        /// <summary>
        /// 执行流式复制的核心逻辑
        /// </summary>
        private async Task<StreamCopyResult> PerformStreamCopyAsync(
            string sourcePath, string targetPath, long fileSize, int bufferSize,
            HashAlgorithm? hashAlgorithm, VerificationMode effectiveVerificationMode, StreamCopyOptions options,
            IProgress<StreamCopyProgress>? progress, CancellationToken cancellationToken)
        {
            var result = new StreamCopyResult { SourcePath = sourcePath, TargetPath = targetPath };

            // 创建目标目录
            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            // 打开文件流，优化参数用于大文件
            var sourceOptions = FileOptions.SequentialScan | FileOptions.RandomAccess;
            var targetOptions = FileOptions.WriteThrough | FileOptions.SequentialScan;

            if (options.UseDirectIO && fileSize > 100 * 1024 * 1024) // 100MB以上使用DirectIO
            {
                // 注意：真实应用中需要P/Invoke使用FILE_FLAG_NO_BUFFERING
                targetOptions |= FileOptions.WriteThrough;
            }

            using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, sourceOptions);
            using var targetStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, targetOptions);

            var buffer = new byte[bufferSize];
            long totalCopied = 0;
            var lastProgressReport = DateTime.Now;
            var lastSpeedMeasurement = DateTime.Now;
            long lastCopiedBytes = 0;
            var chunkCount = 0;

            _logger.LogInfo($"开始数据流传输，缓冲区大小: {bufferSize:N0} 字节", "StreamCopy");

            while (totalCopied < fileSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 读取数据块
                var bytesToRead = Math.Min(bufferSize, (int)(fileSize - totalCopied));
                var bytesRead = await sourceStream.ReadAsync(buffer, 0, bytesToRead, cancellationToken);

                if (bytesRead == 0)
                    break; // 文件结束

                // 写入数据块
                await targetStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                totalCopied += bytesRead;
                chunkCount++;

                // 增量校验（边复制边计算哈希）
                if (hashAlgorithm != null && options.VerificationMode != VerificationMode.None)
                {
                    // 对于大文件，使用TransformBlock避免内存占用
                    if (totalCopied < fileSize)
                    {
                        hashAlgorithm.TransformBlock(buffer, 0, bytesRead, null, 0);
                    }
                    else
                    {
                        hashAlgorithm.TransformFinalBlock(buffer, 0, bytesRead);
                    }
                }

                // 定期强制刷新到磁盘
                if (chunkCount % options.FlushInterval == 0)
                {
                    await targetStream.FlushAsync(cancellationToken);

                    // 大文件每1GB让出CPU时间
                    if (totalCopied % (1024 * 1024 * 1024) == 0)
                    {
                        await Task.Yield();
                        _logger.LogDebug($"已复制 {totalCopied / 1024 / 1024 / 1024} GB", "StreamCopy");
                    }
                }

                // 进度报告（限制频率，避免UI过载）
                var now = DateTime.Now;
                if (now - lastProgressReport >= options.ProgressReportInterval)
                {
                    var progressPercent = (double)totalCopied / fileSize * 100;
                    var elapsedSinceSpeed = (now - lastSpeedMeasurement).TotalSeconds;
                    var currentSpeed = elapsedSinceSpeed > 0 ?
                        (totalCopied - lastCopiedBytes) / elapsedSinceSpeed / 1024 / 1024 : 0;

                    var progressInfo = new StreamCopyProgress
                    {
                        TotalBytes = fileSize,
                        CopiedBytes = totalCopied,
                        ProgressPercent = progressPercent,
                        CurrentSpeedMBps = currentSpeed,
                        EstimatedTimeRemaining = currentSpeed > 0 ?
                            TimeSpan.FromSeconds((fileSize - totalCopied) / (currentSpeed * 1024 * 1024)) : null
                    };

                    progress?.Report(progressInfo);
                    lastProgressReport = now;
                    lastSpeedMeasurement = now;
                    lastCopiedBytes = totalCopied;
                }

                // 内存压力检测
                if (chunkCount % 100 == 0) // 每100个块检查一次
                {
                    var memoryUsage = GC.GetTotalMemory(false);
                    if (memoryUsage > options.MemoryPressureThreshold)
                    {
                        _logger.LogWarning($"内存压力检测: {memoryUsage:N0} 字节", "StreamCopy");
                        GC.Collect(1, GCCollectionMode.Optimized, false);
                    }
                }
            }

            // 最终刷新确保数据写入磁盘
            await targetStream.FlushAsync(cancellationToken);

            // 同步磁盘写入（Windows特有）
            if (options.ForceDiskSync)
            {
                // 注意：实际应用中需要P/Invoke调用FlushFileBuffers
                await targetStream.FlushAsync(cancellationToken);
            }

            result.Success = true;
            result.BytesCopied = totalCopied;

            // 获取源文件哈希（如果需要校验）
            if (hashAlgorithm != null && options.VerificationMode != VerificationMode.None)
            {
                result.SourceHash = Convert.ToHexString(hashAlgorithm.Hash ?? Array.Empty<byte>());
            }

            // 复制文件属性
            await CopyFileAttributesAsync(sourcePath, targetPath);

            // 强制验证文件完整性 - 任何文件都必须校验
            _logger.LogInfo($"开始强制完整性验证: {effectiveVerificationMode}", "StreamCopy");
            result.VerificationResult = await VerifyFileIntegrityAsync(sourcePath, targetPath, effectiveVerificationMode, result.SourceHash, options);

            if (!result.VerificationResult.IsValid)
            {
                _logger.LogError($"文件校验失败: {result.VerificationResult.ErrorMessage}", null, "StreamCopy");
                result.Success = false;
                result.ErrorMessage = $"校验失败: {result.VerificationResult.ErrorMessage}";
            }

            return result;
        }

        /// <summary>
        /// 为超大文件确定最有效的校验模式
        /// </summary>
        private VerificationMode DetermineEffectiveVerificationMode(long fileSize, StreamCopyOptions options)
        {
            const long HUGE_FILE_THRESHOLD = 10L * 1024 * 1024 * 1024; // 10GB

            // 对于超大文件，根据策略选择校验方式
            if (fileSize > HUGE_FILE_THRESHOLD)
            {
                _logger.LogInfo($"检测到超大文件: {fileSize / 1024.0 / 1024 / 1024:F1} GB, 应用特殊校验策略: {options.HugeFileStrategy}", "StreamCopy");

                return options.HugeFileStrategy switch
                {
                    HugeFileVerificationStrategy.FullHashWithProgress => options.VerificationMode, // 保持用户选择的模式
                    HugeFileVerificationStrategy.SegmentedHash => options.VerificationMode, // 分段使用相同算法
                    HugeFileVerificationStrategy.DualHash => VerificationMode.MD5, // 双重校验先用快速的
                    HugeFileVerificationStrategy.IncrementalHash => options.VerificationMode, // 增量校验
                    _ => options.VerificationMode
                };
            }

            return options.VerificationMode;
        }

        /// <summary>
        /// 文件完整性验证 - 增强版，支持超大文件特殊处理
        /// </summary>
        private async Task<VerificationResult> VerifyFileIntegrityAsync(
            string sourcePath, string targetPath, VerificationMode mode, string? sourceHash, StreamCopyOptions options)
        {
            var result = new VerificationResult { Mode = mode };
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var fileSize = new FileInfo(sourcePath).Length;
                const long HUGE_FILE_THRESHOLD = 10L * 1024 * 1024 * 1024; // 10GB

                // 对于超大文件，根据策略执行特殊校验
                if (fileSize > HUGE_FILE_THRESHOLD)
                {
                    _logger.LogInfo($"执行超大文件校验: {fileSize / 1024.0 / 1024 / 1024:F1} GB, 策略: {options.HugeFileStrategy}", "StreamCopy");

                    result = await PerformHugeFileVerificationAsync(sourcePath, targetPath, mode, sourceHash, options);
                }
                else
                {
                    // 标准文件校验
                    switch (mode)
                    {
                        case VerificationMode.Size:
                            result = await VerifyBySizeAsync(sourcePath, targetPath);
                            break;

                        case VerificationMode.SizeAndDate:
                            result = await VerifyBySizeAndDateAsync(sourcePath, targetPath);
                            break;

                        case VerificationMode.MD5:
                        case VerificationMode.SHA256:
                        case VerificationMode.SHA512:
                            result = await VerifyByHashAsync(sourcePath, targetPath, mode, sourceHash);
                            break;

                        case VerificationMode.ByteByByte:
                            result = await VerifyByteByByteAsync(sourcePath, targetPath);
                            break;
                    }
                }

                stopwatch.Stop();
                result.VerificationTime = stopwatch.Elapsed;

                var sizeInfo = fileSize > HUGE_FILE_THRESHOLD ? $" ({fileSize / 1024.0 / 1024 / 1024:F1} GB 超大文件)" : "";
                _logger.LogInfo($"文件校验完成: {mode}, 结果: {(result.IsValid ? "通过" : "失败")}, 耗时: {stopwatch.Elapsed.TotalSeconds:F2}s{sizeInfo}", "StreamCopy");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"文件校验异常: {mode}", ex, "StreamCopy");
                result.IsValid = false;
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        /// <summary>
        /// 执行超大文件的专门校验策略
        /// </summary>
        private async Task<VerificationResult> PerformHugeFileVerificationAsync(
            string sourcePath, string targetPath, VerificationMode mode, string? sourceHash, StreamCopyOptions options)
        {
            var result = new VerificationResult { Mode = mode };
            var fileSize = new FileInfo(sourcePath).Length;

            _logger.LogInfo($"开始超大文件校验: {options.HugeFileStrategy}, 文件大小: {fileSize / 1024.0 / 1024 / 1024:F1} GB", "StreamCopy");

            switch (options.HugeFileStrategy)
            {
                case HugeFileVerificationStrategy.FullHashWithProgress:
                    // 完整哈希校验，但提供详细进度反馈
                    result = await VerifyByHashWithProgressAsync(sourcePath, targetPath, mode, sourceHash);
                    break;

                case HugeFileVerificationStrategy.SegmentedHash:
                    // 分段校验：将大文件分成多个段，独立校验每个段
                    result = await VerifyBySegmentedHashAsync(sourcePath, targetPath, mode);
                    break;

                case HugeFileVerificationStrategy.DualHash:
                    // 双重校验：快速MD5 + 安全SHA256
                    result = await VerifyByDualHashAsync(sourcePath, targetPath, sourceHash);
                    break;

                case HugeFileVerificationStrategy.IncrementalHash:
                    // 增量校验：基于已有的复制时哈希
                    result = await VerifyByIncrementalHashAsync(sourcePath, targetPath, mode, sourceHash);
                    break;

                default:
                    // 默认使用完整哈希校验
                    result = await VerifyByHashWithProgressAsync(sourcePath, targetPath, mode, sourceHash);
                    break;
            }

            return result;
        }

        /// <summary>
        /// 带进度的完整哈希校验 - 适用于90GB+超大文件
        /// </summary>
        private async Task<VerificationResult> VerifyByHashWithProgressAsync(
            string sourcePath, string targetPath, VerificationMode mode, string? sourceHash)
        {
            var result = new VerificationResult { Mode = mode };

            try
            {
                _logger.LogInfo($"开始完整哈希校验: {mode}", "StreamCopy");

                // 并行计算两个文件的哈希
                var sourceTask = CalculateFileHashWithProgressAsync(sourcePath, mode, "源文件");
                var targetTask = CalculateFileHashWithProgressAsync(targetPath, mode, "目标文件");

                var sourceHashResult = await sourceTask;
                var targetHashResult = await targetTask;

                result.SourceHash = sourceHashResult;
                result.TargetHash = targetHashResult;
                result.IsValid = string.Equals(sourceHashResult, targetHashResult, StringComparison.OrdinalIgnoreCase);

                if (!result.IsValid)
                {
                    result.ErrorMessage = $"哈希不匹配: 源={sourceHashResult}, 目标={targetHashResult}";
                    _logger.LogError($"超大文件哈希不匹配: {result.ErrorMessage}", null, "StreamCopy");
                }
                else
                {
                    _logger.LogInfo($"超大文件哈希校验通过: {mode}", "StreamCopy");
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"超大文件哈希校验异常: {mode}", ex, "StreamCopy");
                result.IsValid = false;
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        /// <summary>
        /// 分段哈希校验 - 将超大文件分成多个段独立校验
        /// </summary>
        private async Task<VerificationResult> VerifyBySegmentedHashAsync(
            string sourcePath, string targetPath, VerificationMode mode)
        {
            var result = new VerificationResult { Mode = mode };
            const long SEGMENT_SIZE = 1024 * 1024 * 1024; // 1GB每段

            try
            {
                var fileSize = new FileInfo(sourcePath).Length;
                var segmentCount = (int)((fileSize + SEGMENT_SIZE - 1) / SEGMENT_SIZE);

                _logger.LogInfo($"分段校验: 文件大小 {fileSize / 1024.0 / 1024 / 1024:F1} GB, 分为 {segmentCount} 段", "StreamCopy");

                var allValid = true;
                var segmentResults = new List<string>();

                for (int segment = 0; segment < segmentCount; segment++)
                {
                    var segmentStart = segment * SEGMENT_SIZE;
                    var segmentLength = Math.Min(SEGMENT_SIZE, fileSize - segmentStart);

                    _logger.LogDebug($"校验段 {segment + 1}/{segmentCount}, 位置: {segmentStart:N0}, 长度: {segmentLength:N0}", "StreamCopy");

                    var sourceSegmentHash = await CalculateSegmentHashAsync(sourcePath, segmentStart, segmentLength, mode);
                    var targetSegmentHash = await CalculateSegmentHashAsync(targetPath, segmentStart, segmentLength, mode);

                    var segmentValid = string.Equals(sourceSegmentHash, targetSegmentHash, StringComparison.OrdinalIgnoreCase);
                    allValid &= segmentValid;

                    segmentResults.Add($"段{segment + 1}: {(segmentValid ? "通过" : "失败")}");

                    if (!segmentValid)
                    {
                        _logger.LogWarning($"段 {segment + 1} 校验失败: 源={sourceSegmentHash}, 目标={targetSegmentHash}", "StreamCopy");
                    }
                }

                result.IsValid = allValid;
                result.SourceHash = string.Join(";", segmentResults);
                result.TargetHash = $"分段校验结果: {(allValid ? "全部通过" : "存在失败段")}";

                if (!allValid)
                {
                    result.ErrorMessage = "一个或多个段校验失败";
                }

                _logger.LogInfo($"分段校验完成: {(allValid ? "全部通过" : "存在失败")}", "StreamCopy");
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError("分段校验异常", ex, "StreamCopy");
                result.IsValid = false;
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        /// <summary>
        /// 双重哈希校验 - MD5快速检查 + SHA256安全验证
        /// </summary>
        private async Task<VerificationResult> VerifyByDualHashAsync(string sourcePath, string targetPath, string? sourceHash)
        {
            var result = new VerificationResult { Mode = VerificationMode.SHA256 }; // 最终以SHA256为准

            try
            {
                _logger.LogInfo("开始双重哈希校验: MD5(快速) + SHA256(安全)", "StreamCopy");

                // 第一步：快速MD5校验
                var md5Result = await VerifyByHashWithProgressAsync(sourcePath, targetPath, VerificationMode.MD5, null);

                if (!md5Result.IsValid)
                {
                    result.IsValid = false;
                    result.ErrorMessage = $"MD5快速校验失败: {md5Result.ErrorMessage}";
                    _logger.LogWarning("MD5快速校验失败，跳过SHA256校验", "StreamCopy");
                    return result;
                }

                _logger.LogInfo("MD5快速校验通过，继续SHA256安全校验", "StreamCopy");

                // 第二步：安全SHA256校验
                var sha256Result = await VerifyByHashWithProgressAsync(sourcePath, targetPath, VerificationMode.SHA256, sourceHash);

                result.IsValid = sha256Result.IsValid;
                result.SourceHash = $"MD5:{md5Result.SourceHash};SHA256:{sha256Result.SourceHash}";
                result.TargetHash = $"MD5:{md5Result.TargetHash};SHA256:{sha256Result.TargetHash}";
                result.ErrorMessage = sha256Result.ErrorMessage;

                _logger.LogInfo($"双重校验完成: MD5=通过, SHA256={(sha256Result.IsValid ? "通过" : "失败")}", "StreamCopy");
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError("双重哈希校验异常", ex, "StreamCopy");
                result.IsValid = false;
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        /// <summary>
        /// 增量哈希校验 - 基于复制时已计算的哈希
        /// </summary>
        private async Task<VerificationResult> VerifyByIncrementalHashAsync(
            string sourcePath, string targetPath, VerificationMode mode, string? sourceHash)
        {
            var result = new VerificationResult { Mode = mode };

            try
            {
                if (string.IsNullOrEmpty(sourceHash))
                {
                    _logger.LogWarning("增量校验失败：缺少源文件哈希，回退到完整校验", "StreamCopy");
                    return await VerifyByHashWithProgressAsync(sourcePath, targetPath, mode, sourceHash);
                }

                _logger.LogInfo("执行增量校验：仅计算目标文件哈希", "StreamCopy");

                // 只需要计算目标文件哈希，源文件哈希已有
                var targetHash = await CalculateFileHashWithProgressAsync(targetPath, mode, "目标文件");

                result.SourceHash = sourceHash;
                result.TargetHash = targetHash;
                result.IsValid = string.Equals(sourceHash, targetHash, StringComparison.OrdinalIgnoreCase);

                if (!result.IsValid)
                {
                    result.ErrorMessage = $"增量校验失败: 源={sourceHash}, 目标={targetHash}";
                    _logger.LogError($"增量校验失败: {result.ErrorMessage}", null, "StreamCopy");
                }
                else
                {
                    _logger.LogInfo("增量校验通过", "StreamCopy");
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError("增量校验异常", ex, "StreamCopy");
                result.IsValid = false;
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        /// <summary>
        /// 带进度的文件哈希计算 - 适用于超大文件
        /// </summary>
        private async Task<string> CalculateFileHashWithProgressAsync(string filePath, VerificationMode mode, string description)
        {
            using var algorithm = CreateHashAlgorithm(mode);
            if (algorithm == null)
                throw new ArgumentException($"不支持的哈希模式: {mode}");

            const int bufferSize = 4 * 1024 * 1024; // 4MB缓冲区
            var fileInfo = new FileInfo(filePath);
            long totalBytes = fileInfo.Length;
            long processedBytes = 0;

            _logger.LogInfo($"开始计算{description}哈希: {mode}, 大小: {totalBytes / 1024.0 / 1024 / 1024:F1} GB", "StreamCopy");

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize);
            var buffer = new byte[bufferSize];
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                processedBytes += bytesRead;

                if (processedBytes < totalBytes)
                {
                    algorithm.TransformBlock(buffer, 0, bytesRead, null, 0);
                }
                else
                {
                    algorithm.TransformFinalBlock(buffer, 0, bytesRead);
                }

                // 每处理1GB报告一次进度
                if (processedBytes % (1024 * 1024 * 1024) == 0 || processedBytes == totalBytes)
                {
                    var progress = (double)processedBytes / totalBytes * 100;
                    _logger.LogDebug($"{description}哈希计算进度: {progress:F1}% ({processedBytes / 1024.0 / 1024 / 1024:F1}/{totalBytes / 1024.0 / 1024 / 1024:F1} GB)", "StreamCopy");
                }
            }

            var hashString = Convert.ToHexString(algorithm.Hash ?? Array.Empty<byte>());
            _logger.LogInfo($"{description}哈希计算完成: {mode} = {hashString}", "StreamCopy");
            return hashString;
        }

        /// <summary>
        /// 计算文件段的哈希值
        /// </summary>
        private async Task<string> CalculateSegmentHashAsync(string filePath, long offset, long length, VerificationMode mode)
        {
            using var algorithm = CreateHashAlgorithm(mode);
            if (algorithm == null)
                throw new ArgumentException($"不支持的哈希模式: {mode}");

            const int bufferSize = 1024 * 1024; // 1MB缓冲区
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize);

            stream.Seek(offset, SeekOrigin.Begin);

            var buffer = new byte[bufferSize];
            long remainingBytes = length;

            while (remainingBytes > 0)
            {
                var bytesToRead = (int)Math.Min(bufferSize, remainingBytes);
                var bytesRead = await stream.ReadAsync(buffer, 0, bytesToRead);

                if (bytesRead == 0)
                    break;

                remainingBytes -= bytesRead;

                if (remainingBytes > 0)
                {
                    algorithm.TransformBlock(buffer, 0, bytesRead, null, 0);
                }
                else
                {
                    algorithm.TransformFinalBlock(buffer, 0, bytesRead);
                }
            }

            return Convert.ToHexString(algorithm.Hash ?? Array.Empty<byte>());
        }

        /// <summary>
        /// 哈希校验
        /// </summary>
        private async Task<VerificationResult> VerifyByHashAsync(
            string sourcePath, string targetPath, VerificationMode mode, string? sourceHash)
        {
            var result = new VerificationResult { Mode = mode };

            // 如果复制时已经计算了源文件哈希，只需要计算目标文件哈希
            var targetHash = await CalculateFileHashAsync(targetPath, mode);

            if (string.IsNullOrEmpty(sourceHash))
            {
                // 重新计算源文件哈希
                sourceHash = await CalculateFileHashAsync(sourcePath, mode);
            }

            result.SourceHash = sourceHash;
            result.TargetHash = targetHash;
            result.IsValid = string.Equals(sourceHash, targetHash, StringComparison.OrdinalIgnoreCase);

            if (!result.IsValid)
            {
                result.ErrorMessage = $"哈希不匹配: 源={sourceHash}, 目标={targetHash}";
            }

            return result;
        }

        /// <summary>
        /// 字节级别校验（最严格但最慢）
        /// </summary>
        private async Task<VerificationResult> VerifyByteByByteAsync(string sourcePath, string targetPath)
        {
            var result = new VerificationResult { Mode = VerificationMode.ByteByByte };
            const int bufferSize = 1024 * 1024; // 1MB缓冲区

            using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize);
            using var targetStream = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize);

            if (sourceStream.Length != targetStream.Length)
            {
                result.ErrorMessage = $"文件大小不匹配: 源={sourceStream.Length}, 目标={targetStream.Length}";
                return result;
            }

            var sourceBuffer = new byte[bufferSize];
            var targetBuffer = new byte[bufferSize];
            long position = 0;

            while (position < sourceStream.Length)
            {
                var sourceBytesRead = await sourceStream.ReadAsync(sourceBuffer, 0, bufferSize);
                var targetBytesRead = await targetStream.ReadAsync(targetBuffer, 0, bufferSize);

                if (sourceBytesRead != targetBytesRead)
                {
                    result.ErrorMessage = $"读取字节数不匹配，位置: {position}";
                    return result;
                }

                for (int i = 0; i < sourceBytesRead; i++)
                {
                    if (sourceBuffer[i] != targetBuffer[i])
                    {
                        result.ErrorMessage = $"字节不匹配，位置: {position + i}";
                        return result;
                    }
                }

                position += sourceBytesRead;
            }

            result.IsValid = true;
            return result;
        }

        /// <summary>
        /// 计算文件哈希
        /// </summary>
        private async Task<string> CalculateFileHashAsync(string filePath, VerificationMode mode)
        {
            using var algorithm = CreateHashAlgorithm(mode);
            if (algorithm == null)
                throw new ArgumentException($"不支持的哈希模式: {mode}");

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
            var hashBytes = await algorithm.ComputeHashAsync(stream);
            return Convert.ToHexString(hashBytes);
        }

        /// <summary>
        /// 创建哈希算法实例
        /// </summary>
        private HashAlgorithm? CreateHashAlgorithm(VerificationMode mode)
        {
            return mode switch
            {
                VerificationMode.MD5 => MD5.Create(),
                VerificationMode.SHA256 => SHA256.Create(),
                VerificationMode.SHA512 => SHA512.Create(),
                _ => null
            };
        }

        /// <summary>
        /// 大小验证
        /// </summary>
        private async Task<VerificationResult> VerifyBySizeAsync(string sourcePath, string targetPath)
        {
            var result = new VerificationResult { Mode = VerificationMode.Size };

            var sourceInfo = new FileInfo(sourcePath);
            var targetInfo = new FileInfo(targetPath);

            result.IsValid = sourceInfo.Length == targetInfo.Length;

            if (!result.IsValid)
            {
                result.ErrorMessage = $"文件大小不匹配: 源={sourceInfo.Length}, 目标={targetInfo.Length}";
            }

            return result;
        }

        /// <summary>
        /// 大小和日期验证
        /// </summary>
        private async Task<VerificationResult> VerifyBySizeAndDateAsync(string sourcePath, string targetPath)
        {
            var result = new VerificationResult { Mode = VerificationMode.SizeAndDate };

            var sourceInfo = new FileInfo(sourcePath);
            var targetInfo = new FileInfo(targetPath);

            var sizeMatch = sourceInfo.Length == targetInfo.Length;
            var dateMatch = Math.Abs((sourceInfo.LastWriteTime - targetInfo.LastWriteTime).TotalSeconds) < 2; // 2秒容差

            result.IsValid = sizeMatch && dateMatch;

            if (!result.IsValid)
            {
                if (!sizeMatch)
                    result.ErrorMessage = $"文件大小不匹配: 源={sourceInfo.Length}, 目标={targetInfo.Length}";
                else
                    result.ErrorMessage = $"修改时间不匹配: 源={sourceInfo.LastWriteTime}, 目标={targetInfo.LastWriteTime}";
            }

            return result;
        }

        /// <summary>
        /// 复制文件属性
        /// </summary>
        private async Task CopyFileAttributesAsync(string sourcePath, string targetPath)
        {
            try
            {
                var sourceInfo = new FileInfo(sourcePath);
                var targetInfo = new FileInfo(targetPath);

                targetInfo.CreationTime = sourceInfo.CreationTime;
                targetInfo.LastWriteTime = sourceInfo.LastWriteTime;
                targetInfo.LastAccessTime = sourceInfo.LastAccessTime;
                targetInfo.Attributes = sourceInfo.Attributes;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"复制文件属性失败: {ex.Message}", "StreamCopy");
            }
        }

        /// <summary>
        /// 复制前验证
        /// </summary>
        private async Task<(bool Success, string ErrorMessage)> PreCopyValidation(
            string sourcePath, string targetPath, StreamCopyOptions options)
        {
            // 检查源文件
            if (!File.Exists(sourcePath))
                return (false, $"源文件不存在: {sourcePath}");

            // 检查磁盘空间
            var sourceInfo = new FileInfo(sourcePath);
            var targetDrive = Path.GetPathRoot(targetPath);

            if (!string.IsNullOrEmpty(targetDrive))
            {
                var driveInfo = new DriveInfo(targetDrive);
                if (driveInfo.AvailableFreeSpace < sourceInfo.Length * 1.1) // 10%余量
                {
                    return (false, $"目标磁盘空间不足: 需要 {sourceInfo.Length:N0} 字节, 可用 {driveInfo.AvailableFreeSpace:N0} 字节");
                }
            }

            // 检查文件是否被占用
            if (SafeFileOperations.IsFileInUse(sourcePath))
                return (false, $"源文件被占用: {sourcePath}");

            return (true, string.Empty);
        }

        /// <summary>
        /// 计算最优缓冲区大小
        /// </summary>
        private int CalculateOptimalBufferSize(long fileSize, StreamCopyOptions options)
        {
            var baseSize = fileSize switch
            {
                < 1024 * 1024 => 64 * 1024,           // 小于1MB: 64KB
                < 10 * 1024 * 1024 => 256 * 1024,     // 小于10MB: 256KB
                < 100 * 1024 * 1024 => 1024 * 1024,   // 小于100MB: 1MB
                < 1024 * 1024 * 1024 => 4 * 1024 * 1024, // 小于1GB: 4MB
                _ => 16 * 1024 * 1024                  // 大于1GB: 16MB
            };

            // 应用用户自定义的倍数
            var result = (int)(baseSize * options.BufferSizeMultiplier);

            // 限制在合理范围内
            return Math.Clamp(result, 64 * 1024, 64 * 1024 * 1024); // 64KB - 64MB
        }
    }
}