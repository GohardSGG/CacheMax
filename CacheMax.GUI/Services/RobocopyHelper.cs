using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CacheMax.GUI.Services
{
    /// <summary>
    /// Robocopy辅助类
    /// 封装所有现有的Robocopy输出解析逻辑，保持原有行为不变
    /// </summary>
    public static class RobocopyHelper
    {
        /// <summary>
        /// Robocopy统计信息
        /// </summary>
        public class RobocopyStatistics
        {
            public long TotalDirs { get; set; }
            public long CopiedDirs { get; set; }
            public long TotalFiles { get; set; }
            public long CopiedFiles { get; set; }
            public long TotalBytes { get; set; }
            public long CopiedBytes { get; set; }
            public long FailedDirs { get; set; }
            public long FailedFiles { get; set; }
        }

        /// <summary>
        /// 检查Robocopy是否完全成功：对比总数和复制列是否完全一致
        /// 完全保留 CacheManagerService.IsRobocopyCompletelySuccessful 的逻辑
        /// </summary>
        public static bool IsCompletelySuccessful(List<string> outputLines)
        {
            bool allRowsMatch = true;
            int checkedRows = 0;

            foreach (var line in outputLines)
            {
                // 匹配目录、文件、字节这三行的统计
                if (line.Contains("目录:") || line.Contains("文件:") || line.Contains("字节:"))
                {
                    var numbers = Regex.Matches(line, @"\d+");
                    if (numbers.Count >= 2)
                    {
                        // 对比第1列（总数）和第2列（复制）是否相等
                        if (long.TryParse(numbers[0].Value, out long totalCount) &&
                            long.TryParse(numbers[1].Value, out long copiedCount))
                        {
                            checkedRows++;
                            bool rowMatches = (totalCount == copiedCount);

                            string rowType = line.Contains("目录:") ? "目录" :
                                           line.Contains("文件:") ? "文件" : "字节";

                            if (!rowMatches)
                            {
                                allRowsMatch = false;
                                // 注意：这里原来有LogMessage调用，现在返回调用者处理
                            }
                        }
                    }
                }
            }

            return allRowsMatch && checkedRows >= 2; // 至少检查到2行（文件和字节）
        }

        /// <summary>
        /// 检查是否有显著的数据传输
        /// 完全保留 CacheManagerService.CheckForSignificantDataTransfer 的逻辑
        /// </summary>
        public static bool HasSignificantDataTransfer(List<string> outputLines)
        {
            foreach (var line in outputLines)
            {
                // 检查字节传输统计：字节: 2970355320 2970355320 0 0 0 0
                if (line.Contains("字节:"))
                {
                    var numbers = Regex.Matches(line, @"\d+");
                    if (numbers.Count >= 2)
                    {
                        // 第二个数字是实际复制的字节数
                        if (long.TryParse(numbers[1].Value, out long copiedBytes))
                        {
                            return copiedBytes > 0;
                        }
                    }
                }

                // 检查文件复制统计：文件: 289 289 0 0 0 0
                if (line.Contains("文件:"))
                {
                    var numbers = Regex.Matches(line, @"\d+");
                    if (numbers.Count >= 2)
                    {
                        // 第二个数字是复制的文件数
                        if (int.TryParse(numbers[1].Value, out int copiedFiles))
                        {
                            return copiedFiles > 0;
                        }
                    }
                }
            }

            return false; // 如果找不到统计行，默认认为没有传输
        }

        /// <summary>
        /// CacheManagerService场景：智能成功判断
        /// 完全保留原有的复杂判断逻辑
        /// </summary>
        public static bool IsSuccessForInitialCopy(int exitCode, List<string> outputLines)
        {
            bool isCompletelySuccessful = IsCompletelySuccessful(outputLines);
            bool hasSignificantDataTransfer = HasSignificantDataTransfer(outputLines);
            bool isOfficialSuccess = exitCode < 8;

            // 判断最终成功状态 - 完全保留原逻辑
            if (isOfficialSuccess)
            {
                // 退出码 < 8，官方认为成功
                return true;
            }
            else if (isCompletelySuccessful && hasSignificantDataTransfer)
            {
                // 虽然退出码 >= 8，但总数和复制列完全一致，视为成功
                return true;
            }
            else
            {
                // 真正的失败
                return false;
            }
        }

        /// <summary>
        /// MainWindow.SyncSingleItemAsync场景：简单的退出码判断
        /// 完全保留原有逻辑
        /// </summary>
        public static bool IsSuccessForSync(int exitCode)
        {
            // RoboCopy的退出代码: 0-3 表示成功，>=4 表示错误
            return exitCode <= 3;
        }

        /// <summary>
        /// MainWindow.CheckSingleItemIntegrityAsync场景：检查是否有差异
        /// 完全保留 ParseRoboCopyOutput 的逻辑
        /// </summary>
        public static bool HasChanges(List<string> outputLines)
        {
            try
            {
                // 查找统计行：目录: 总数 复制 跳过 不匹配 失败 其他
                // 和：文件: 总数 复制 跳过 不匹配 失败 其他
                int fileChanges = 0;
                int dirChanges = 0;

                for (int i = 0; i < outputLines.Count; i++)
                {
                    var trimmedLine = outputLines[i].Trim();

                    // 查找文件统计行：文件:         3         2         1         0         0         0
                    if (trimmedLine.StartsWith("文件:"))
                    {
                        var parts = trimmedLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                        if (parts.Length >= 3)
                        {
                            // parts[0]="文件:", parts[1]="总数", parts[2]="复制数"
                            if (int.TryParse(parts[2], out int copyCount))
                            {
                                fileChanges = copyCount;
                            }
                        }
                    }

                    // 查找目录统计行：目录:         2         1         1         0         0         0
                    if (trimmedLine.StartsWith("目录:"))
                    {
                        var parts = trimmedLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                        if (parts.Length >= 3)
                        {
                            // parts[0]="目录:", parts[1]="总数", parts[2]="复制数"
                            if (int.TryParse(parts[2], out int copyCount))
                            {
                                dirChanges = copyCount;
                            }
                        }
                    }

                    // 另外检查是否有"新文件"行，这也表示有差异
                    if (trimmedLine.Contains("新文件"))
                    {
                        return true;
                    }

                    // 检查是否有"新目录"行
                    if (trimmedLine.Contains("新目录"))
                    {
                        return true;
                    }
                }

                // 检查是否有需要同步的变更
                bool hasChanges = fileChanges > 0 || dirChanges > 0;
                return hasChanges;
            }
            catch (Exception)
            {
                // 解析失败，保守地认为有变化
                return true;
            }
        }

        /// <summary>
        /// 解析Robocopy输出，提取统计信息
        /// </summary>
        public static RobocopyStatistics? ParseStatistics(List<string> outputLines)
        {
            var stats = new RobocopyStatistics();
            bool foundStats = false;

            foreach (var line in outputLines)
            {
                // 匹配目录行：目录: 总数 复制 跳过 不匹配 失败 其他
                if (line.Contains("目录:"))
                {
                    var numbers = Regex.Matches(line, @"\d+");
                    if (numbers.Count >= 6)
                    {
                        if (long.TryParse(numbers[0].Value, out long total) &&
                            long.TryParse(numbers[1].Value, out long copied) &&
                            long.TryParse(numbers[4].Value, out long failed))
                        {
                            stats.TotalDirs = total;
                            stats.CopiedDirs = copied;
                            stats.FailedDirs = failed;
                            foundStats = true;
                        }
                    }
                }

                // 匹配文件行：文件: 总数 复制 跳过 不匹配 失败 其他
                if (line.Contains("文件:"))
                {
                    var numbers = Regex.Matches(line, @"\d+");
                    if (numbers.Count >= 6)
                    {
                        if (long.TryParse(numbers[0].Value, out long total) &&
                            long.TryParse(numbers[1].Value, out long copied) &&
                            long.TryParse(numbers[4].Value, out long failed))
                        {
                            stats.TotalFiles = total;
                            stats.CopiedFiles = copied;
                            stats.FailedFiles = failed;
                            foundStats = true;
                        }
                    }
                }

                // 匹配字节行：字节: 总数 复制 跳过 不匹配 失败 其他
                if (line.Contains("字节:"))
                {
                    var numbers = Regex.Matches(line, @"\d+");
                    if (numbers.Count >= 2)
                    {
                        if (long.TryParse(numbers[0].Value, out long total) &&
                            long.TryParse(numbers[1].Value, out long copied))
                        {
                            stats.TotalBytes = total;
                            stats.CopiedBytes = copied;
                            foundStats = true;
                        }
                    }
                }
            }

            return foundStats ? stats : null;
        }

        /// <summary>
        /// 获取用户友好的执行结果描述
        /// </summary>
        public static string GetResultDescription(int exitCode, RobocopyStatistics? stats = null)
        {
            // Robocopy退出码含义
            var meanings = new List<string>();

            if ((exitCode & 1) != 0) meanings.Add("文件被复制");
            if ((exitCode & 2) != 0) meanings.Add("检测到额外文件/目录");
            if ((exitCode & 4) != 0) meanings.Add("有不匹配文件/目录");
            if ((exitCode & 8) != 0) meanings.Add("部分文件无法复制");
            if ((exitCode & 16) != 0) meanings.Add("严重错误：文件访问失败");

            var description = $"退出码: {exitCode}";
            if (meanings.Any())
            {
                description += $" ({string.Join(", ", meanings)})";
            }

            if (stats != null)
            {
                description += $" - 文件: {stats.CopiedFiles}/{stats.TotalFiles}, 字节: {FormatBytes(stats.CopiedBytes)}/{FormatBytes(stats.TotalBytes)}";
            }

            return description;
        }

        /// <summary>
        /// Robocopy进度输出过滤器
        /// 判断输出行是否包含有用的进度信息
        /// </summary>
        public static bool IsProgressLine(string line, bool showProgress = true)
        {
            if (string.IsNullOrEmpty(line)) return false;

            // 捕捉所有有用的进度信息
            return line.Contains("Files :") ||
                   line.Contains("Dirs :") ||
                   line.Contains("Bytes :") ||
                   line.Contains("Times :") ||
                   line.Contains("Speed :") ||
                   line.Contains("ETA:") ||
                   line.Contains("%") ||
                   (line.Contains("New File") && showProgress);
        }

        /// <summary>
        /// 创建Robocopy进度过滤器
        /// </summary>
        public static Func<string, bool> CreateProgressFilter(bool showProgress = true)
        {
            return line => IsProgressLine(line, showProgress);
        }

        /// <summary>
        /// 创建进度友好的Robocopy参数
        /// 解决GUI应用中进度显示问题
        /// </summary>
        public static List<string> CreateProgressFriendlyArguments(bool enableDetailedProgress = true)
        {
            var args = new List<string>();

            if (enableDetailedProgress)
            {
                // 关键：不使用 /NP (这会完全禁用进度)
                // 关键：不使用 /NFL (这会禁用文件名，我们需要文件名来判断进度)

                args.Add("/BYTES");    // 以字节显示大小，更精确的进度计算
                args.Add("/ETA");      // 显示预计完成时间
                args.Add("/FP");       // 显示完整路径名称，便于识别当前文件

                // 减少噪音但保留关键信息
                args.Add("/NDL");      // 不记录目录名，减少输出噪音
                args.Add("/NC");       // 不记录文件类，减少输出噪音

                // 关键：添加这个参数强制输出到标准输出
                args.Add("/TEE");      // 同时输出到控制台和日志，确保实时输出
            }
            else
            {
                // 禁用所有进度信息（用于后台静默操作）
                args.Add("/NP");       // 不显示进度
                args.Add("/NFL");      // 不记录文件名
                args.Add("/NDL");      // 不记录目录名
                args.Add("/NC");       // 不记录文件类
                args.Add("/NJS");      // 不显示作业摘要
                args.Add("/NJH");      // 不显示作业头
            }

            return args;
        }

        /// <summary>
        /// 格式化字节大小
        /// </summary>
        private static string FormatBytes(long bytes)
        {
            var sizes = new[] { "B", "KB", "MB", "GB", "TB" };
            var len = (double)bytes;
            var order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }
    }
}