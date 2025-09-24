using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace CacheMax.GUI.Services
{
    /// <summary>
    /// Robocopy进度分析器
    /// 解决Robocopy在GUI应用中进度显示问题的专门工具
    /// </summary>
    public static class RobocopyProgressAnalyzer
    {
        /// <summary>
        /// Robocopy进度信息
        /// </summary>
        public class ProgressInfo
        {
            public double PercentComplete { get; set; }
            public long FilesCompleted { get; set; }
            public long TotalFiles { get; set; }
            public long BytesCompleted { get; set; }
            public long TotalBytes { get; set; }
            public string CurrentFile { get; set; } = string.Empty;
            public string TransferSpeed { get; set; } = string.Empty;
            public string EstimatedTimeRemaining { get; set; } = string.Empty;
            public bool IsValid { get; set; }
        }

        /// <summary>
        /// 分析Robocopy输出行，提取进度信息
        ///
        /// Robocopy进度显示问题的根本原因：
        /// 1. Robocopy的进度输出使用了控制台特殊字符（回车符\r）来覆盖同一行
        /// 2. 在GUI应用的重定向输出中，这些控制字符不能正常工作
        /// 3. /NP参数会完全禁用进度，但我们需要其他信息
        /// 4. Robocopy的实时进度依赖于控制台窗口的光标控制
        /// </summary>
        public static ProgressInfo AnalyzeOutputLine(string line)
        {
            var progress = new ProgressInfo();

            if (string.IsNullOrEmpty(line))
                return progress;

            // 清理控制字符
            var cleanLine = CleanControlCharacters(line);

            // 尝试解析不同格式的进度信息
            if (TryParsePercentProgress(cleanLine, progress) ||
                TryParseFileCountProgress(cleanLine, progress) ||
                TryParseCurrentFileInfo(cleanLine, progress) ||
                TryParseSpeedInfo(cleanLine, progress) ||
                TryParseETAInfo(cleanLine, progress))
            {
                progress.IsValid = true;
            }

            return progress;
        }

        /// <summary>
        /// 清理控制字符，这是导致进度显示问题的主要原因
        /// </summary>
        private static string CleanControlCharacters(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            // 移除回车符、换行符、制表符等控制字符
            var cleaned = input.Replace('\r', ' ')
                              .Replace('\n', ' ')
                              .Replace('\t', ' ')
                              .Trim();

            // 移除多余的空格
            return Regex.Replace(cleaned, @"\s+", " ");
        }

        /// <summary>
        /// 尝试解析百分比进度 (如: "25.3%")
        /// </summary>
        private static bool TryParsePercentProgress(string line, ProgressInfo progress)
        {
            var percentMatch = Regex.Match(line, @"(\d+\.?\d*)%");
            if (percentMatch.Success)
            {
                if (double.TryParse(percentMatch.Groups[1].Value, out double percent))
                {
                    progress.PercentComplete = percent;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 尝试解析文件计数进度 (如: "Files: 150/500")
        /// </summary>
        private static bool TryParseFileCountProgress(string line, ProgressInfo progress)
        {
            // 匹配 "Files: 150/500" 或 "Files: 150 of 500" 格式
            var fileCountMatch = Regex.Match(line, @"Files?\s*:?\s*(\d+)[\/\s](?:of\s*)?(\d+)", RegexOptions.IgnoreCase);
            if (fileCountMatch.Success)
            {
                if (long.TryParse(fileCountMatch.Groups[1].Value, out long completed) &&
                    long.TryParse(fileCountMatch.Groups[2].Value, out long total))
                {
                    progress.FilesCompleted = completed;
                    progress.TotalFiles = total;
                    if (total > 0)
                    {
                        progress.PercentComplete = (double)completed / total * 100.0;
                    }
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 尝试解析当前文件信息
        /// </summary>
        private static bool TryParseCurrentFileInfo(string line, ProgressInfo progress)
        {
            // 匹配 "New File" 或文件路径模式
            if (line.Contains("New File") || line.Contains("100%"))
            {
                // 提取文件路径
                var pathMatch = Regex.Match(line, @"[A-Za-z]:\\[^\t\n\r]+");
                if (pathMatch.Success)
                {
                    progress.CurrentFile = pathMatch.Value;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 尝试解析传输速度信息 (如: "Speed: 1.2 MB/s")
        /// </summary>
        private static bool TryParseSpeedInfo(string line, ProgressInfo progress)
        {
            var speedMatch = Regex.Match(line, @"Speed\s*:?\s*([\d\.,]+\s*[KMGT]?B/s)", RegexOptions.IgnoreCase);
            if (speedMatch.Success)
            {
                progress.TransferSpeed = speedMatch.Groups[1].Value.Trim();
                return true;
            }

            // 也匹配纯数字+单位的速度格式
            var speedMatch2 = Regex.Match(line, @"([\d\.,]+\s*[KMGT]?B/s)");
            if (speedMatch2.Success)
            {
                progress.TransferSpeed = speedMatch2.Groups[1].Value.Trim();
                return true;
            }

            return false;
        }

        /// <summary>
        /// 尝试解析预计剩余时间 (如: "ETA: 00:05:30")
        /// </summary>
        private static bool TryParseETAInfo(string line, ProgressInfo progress)
        {
            var etaMatch = Regex.Match(line, @"ETA\s*:?\s*(\d{1,2}:\d{2}:\d{2})", RegexOptions.IgnoreCase);
            if (etaMatch.Success)
            {
                progress.EstimatedTimeRemaining = etaMatch.Groups[1].Value;
                return true;
            }

            // 也匹配其他时间格式
            var timeMatch = Regex.Match(line, @"(\d{1,2}:\d{2}:\d{2})");
            if (timeMatch.Success && line.ToLower().Contains("remaining"))
            {
                progress.EstimatedTimeRemaining = timeMatch.Groups[1].Value;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 为Robocopy创建优化的参数，减少进度显示问题
        ///
        /// 关键策略：
        /// 1. 不使用 /NP（不禁用进度），但使用其他参数减少噪音
        /// 2. 使用 /BYTES 显示字节而不是文件数，更精确
        /// 3. 使用 /ETA 显示预计时间
        /// 4. 避免使用会导致输出混乱的参数
        /// </summary>
        public static List<string> CreateProgressOptimizedArguments(bool enableProgress = true)
        {
            var args = new List<string>();

            if (enableProgress)
            {
                // 启用有用的进度信息
                args.Add("/BYTES");    // 以字节显示大小，更精确
                args.Add("/ETA");      // 显示预计完成时间
                args.Add("/FP");       // 显示完整路径名称

                // 减少噪音但保留关键信息
                args.Add("/NDL");      // 不记录目录名
                args.Add("/NC");       // 不记录文件类

                // 注意：不添加 /NP，因为这会完全禁用进度
                // 注意：不添加 /NFL，因为我们需要文件名来显示当前文件
            }
            else
            {
                // 完全禁用进度（用于后台操作）
                args.Add("/NP");       // 不显示进度
                args.Add("/NFL");      // 不记录文件名
                args.Add("/NDL");      // 不记录目录名
                args.Add("/NC");       // 不记录文件类
            }

            return args;
        }

        /// <summary>
        /// 替代方案：使用文件系统信息估算进度
        /// 当Robocopy进度信息不可靠时，可以使用这种方法
        /// </summary>
        public static ProgressInfo EstimateProgressFromFileSystem(string sourcePath, string destinationPath)
        {
            var progress = new ProgressInfo();

            try
            {
                // 获取源目录信息
                var sourceInfo = new System.IO.DirectoryInfo(sourcePath);
                if (!sourceInfo.Exists) return progress;

                var sourceFiles = sourceInfo.GetFiles("*", System.IO.SearchOption.AllDirectories);
                var sourceTotalSize = sourceFiles.Sum(f => f.Length);
                var sourceTotalCount = sourceFiles.Length;

                // 获取目标目录信息
                var destInfo = new System.IO.DirectoryInfo(destinationPath);
                if (!destInfo.Exists) return progress;

                var destFiles = destInfo.GetFiles("*", System.IO.SearchOption.AllDirectories);
                var destTotalSize = destFiles.Sum(f => f.Length);
                var destTotalCount = destFiles.Length;

                // 计算进度
                progress.TotalFiles = sourceTotalCount;
                progress.FilesCompleted = destTotalCount;
                progress.TotalBytes = sourceTotalSize;
                progress.BytesCompleted = destTotalSize;

                if (sourceTotalSize > 0)
                {
                    progress.PercentComplete = (double)destTotalSize / sourceTotalSize * 100.0;
                }

                progress.IsValid = true;
            }
            catch
            {
                // 文件系统访问失败，返回无效进度
            }

            return progress;
        }

        /// <summary>
        /// 检测Robocopy进度是否可靠
        /// 如果检测到进度总是显示0%，建议使用文件系统估算
        /// </summary>
        public static bool IsProgressReliable(List<ProgressInfo> recentProgress)
        {
            if (recentProgress.Count < 5) return true; // 样本太少，假设可靠

            var validProgressCount = recentProgress.Count(p => p.IsValid && p.PercentComplete > 0);
            var totalCount = recentProgress.Count;

            // 如果超过80%的进度都是0或无效，认为不可靠
            return (double)validProgressCount / totalCount > 0.2;
        }
    }
}