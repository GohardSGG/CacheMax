using System;
using System.Collections.Generic;
using System.Linq;

namespace CacheMax.GUI.Services
{
    /// <summary>
    /// FastCopy辅助类
    /// 保持现有的简单成功判断，但增强错误信息提取
    /// </summary>
    public static class FastCopyHelper
    {
        /// <summary>
        /// FastCopy成功判断 - 保持原有的简单逻辑
        /// </summary>
        public static bool IsSuccess(int exitCode)
        {
            return exitCode == 0;
        }

        /// <summary>
        /// 从FastCopy错误输出中提取有意义的错误信息
        /// </summary>
        public static string GetErrorMessage(List<string> errorOutput, List<string> standardOutput)
        {
            if (errorOutput.Count == 0 && standardOutput.Count == 0)
            {
                return "未知错误";
            }

            var errorMessages = new List<string>();

            // 从错误输出中提取关键信息
            foreach (var line in errorOutput)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                // FastCopy常见错误模式
                if (trimmed.Contains("Access denied") || trimmed.Contains("拒绝访问"))
                {
                    errorMessages.Add("访问权限不足");
                }
                else if (trimmed.Contains("No space left") || trimmed.Contains("磁盘空间不足"))
                {
                    errorMessages.Add("磁盘空间不足");
                }
                else if (trimmed.Contains("Path not found") || trimmed.Contains("找不到路径"))
                {
                    errorMessages.Add("路径不存在");
                }
                else if (trimmed.Contains("File in use") || trimmed.Contains("文件正在使用"))
                {
                    errorMessages.Add("文件被占用");
                }
                else if (trimmed.Contains("Network") || trimmed.Contains("网络"))
                {
                    errorMessages.Add("网络错误");
                }
                else if (trimmed.Length > 10) // 忽略太短的错误行
                {
                    errorMessages.Add(trimmed);
                }
            }

            // 从标准输出中提取FastCopy特有的错误信息
            foreach (var line in standardOutput)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                // FastCopy在标准输出中也可能输出错误信息
                if (trimmed.Contains("Error:") || trimmed.Contains("错误:"))
                {
                    errorMessages.Add(trimmed);
                }
                else if (trimmed.Contains("Failed:") || trimmed.Contains("失败:"))
                {
                    errorMessages.Add(trimmed);
                }
            }

            // 返回最有用的错误信息
            if (errorMessages.Count == 0)
            {
                // 如果没有特定错误，返回最后几行输出
                var lastLines = errorOutput.Concat(standardOutput)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .TakeLast(3);

                return string.Join("; ", lastLines).Trim();
            }

            // 去重并返回前3个最重要的错误
            return string.Join("; ", errorMessages.Distinct().Take(3));
        }

        /// <summary>
        /// 从FastCopy输出中提取传输统计信息（如果有的话）
        /// </summary>
        public static string? GetTransferStatistics(List<string> standardOutput)
        {
            foreach (var line in standardOutput)
            {
                var trimmed = line.Trim();

                // 查找传输速度信息
                if (trimmed.Contains("MB/s") || trimmed.Contains("KB/s") || trimmed.Contains("GB/s"))
                {
                    return trimmed;
                }

                // 查找完成百分比
                if (trimmed.Contains("%") && trimmed.Contains("complete"))
                {
                    return trimmed;
                }

                // 查找文件数量统计
                if (trimmed.Contains("files") && (trimmed.Contains("copied") || trimmed.Contains("processed")))
                {
                    return trimmed;
                }
            }

            return null;
        }

        /// <summary>
        /// 检查FastCopy是否因为超时被终止
        /// </summary>
        public static bool WasTerminatedByTimeout(List<string> errorOutput, bool timedOut)
        {
            if (timedOut) return true;

            // 检查输出中是否有超时相关的信息
            foreach (var line in errorOutput)
            {
                if (line.Contains("timeout") || line.Contains("超时") ||
                    line.Contains("terminated") || line.Contains("终止"))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 获取用户友好的结果描述
        /// </summary>
        public static string GetResultDescription(int exitCode, List<string> errorOutput, List<string> standardOutput, bool timedOut)
        {
            if (timedOut)
            {
                return "操作超时";
            }

            if (IsSuccess(exitCode))
            {
                var stats = GetTransferStatistics(standardOutput);
                return stats != null ? $"操作成功 - {stats}" : "操作成功";
            }

            var errorMsg = GetErrorMessage(errorOutput, standardOutput);
            return $"操作失败 (退出码: {exitCode}) - {errorMsg}";
        }

        /// <summary>
        /// 验证FastCopy可执行文件是否存在且可用
        /// </summary>
        public static bool ValidateExecutable(string fastCopyPath)
        {
            try
            {
                return System.IO.File.Exists(fastCopyPath);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 构建常用的FastCopy参数
        /// </summary>
        public static class Arguments
        {
            /// <summary>
            /// 强制复制模式的默认参数
            /// </summary>
            public static string ForceCopy => "/cmd=force_copy /verify /auto_close /error_stop /no_ui";

            /// <summary>
            /// 更新模式的默认参数
            /// </summary>
            public static string Update => "/cmd=update /verify /auto_close /error_stop /no_ui";

            /// <summary>
            /// 差异检查模式的参数
            /// </summary>
            public static string DiffOnly => "/cmd=diff_only /verify /auto_close /no_ui";

            /// <summary>
            /// 添加日志输出
            /// </summary>
            public static string WithLog(string baseArgs) => $"{baseArgs} /log";

            /// <summary>
            /// 添加源路径和目标路径
            /// </summary>
            public static string WithPaths(string baseArgs, string source, string targetDir)
            {
                return $"{baseArgs} \"{source}\" /to=\"{targetDir}\"";
            }
        }
    }
}