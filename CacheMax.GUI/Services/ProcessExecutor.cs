using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CacheMax.GUI.Services
{
    /// <summary>
    /// 轻量级进程执行工具类
    /// 只负责统一处理进程执行的共同部分，不做业务逻辑判断
    /// </summary>
    public static class ProcessExecutor
    {
        /// <summary>
        /// 进程执行结果
        /// </summary>
        public class ProcessResult
        {
            public int ExitCode { get; set; }
            public List<string> StandardOutput { get; set; } = new();
            public List<string> ErrorOutput { get; set; } = new();
            public bool ProcessStarted { get; set; }
            public Exception? Exception { get; set; }
            public TimeSpan ExecutionTime { get; set; }
            public bool TimedOut { get; set; }
        }

        /// <summary>
        /// 执行命令行程序，统一处理所有边界情况
        /// </summary>
        /// <param name="fileName">可执行文件名</param>
        /// <param name="arguments">命令行参数</param>
        /// <param name="timeoutSeconds">超时时间（秒），默认5分钟</param>
        /// <param name="workingDirectory">工作目录</param>
        /// <param name="progress">进度报告</param>
        /// <param name="outputFilter">输出过滤器，用于实时进度报告</param>
        /// <returns>进程执行结果</returns>
        public static async Task<ProcessResult> ExecuteAsync(
            string fileName,
            string arguments,
            int timeoutSeconds = 300,
            string? workingDirectory = null,
            IProgress<string>? progress = null,
            Func<string, bool>? outputFilter = null)
        {
            var result = new ProcessResult();
            var startTime = DateTime.Now;

            try
            {
                var processStartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                if (!string.IsNullOrEmpty(workingDirectory))
                {
                    processStartInfo.WorkingDirectory = workingDirectory;
                }

                using var process = new Process { StartInfo = processStartInfo };

                // 统一处理进程创建失败的情况
                if (!process.Start())
                {
                    result.ProcessStarted = false;
                    return result;
                }

                result.ProcessStarted = true;

                // 设置超时保护
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

                // 异步读取输出，防止缓冲区溢出
                var outputTask = ReadStreamAsync(process.StandardOutput, result.StandardOutput, cts.Token, progress, outputFilter);
                var errorTask = ReadStreamAsync(process.StandardError, result.ErrorOutput, cts.Token);

                try
                {
                    // 等待进程结束或超时
                    await process.WaitForExitAsync(cts.Token);

                    // 等待输出读取完成
                    await Task.WhenAll(outputTask, errorTask);

                    result.ExitCode = process.ExitCode;
                    result.ExecutionTime = DateTime.Now - startTime;
                }
                catch (OperationCanceledException)
                {
                    // 超时处理
                    result.TimedOut = true;
                    try
                    {
                        process.Kill(true); // 强制终止进程及其子进程
                        progress?.Report($"进程执行超时({timeoutSeconds}秒)，已强制终止");
                    }
                    catch (Exception killEx)
                    {
                        progress?.Report($"终止超时进程失败: {killEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                result.Exception = ex;
                result.ExecutionTime = DateTime.Now - startTime;
            }

            return result;
        }

        /// <summary>
        /// 异步读取流内容，防止缓冲区溢出
        /// </summary>
        private static async Task ReadStreamAsync(
            System.IO.StreamReader reader,
            List<string> output,
            CancellationToken cancellationToken,
            IProgress<string>? progress = null,
            Func<string, bool>? outputFilter = null)
        {
            const int MaxOutputLines = 10000; // 最多保留10000行输出

            try
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null && !cancellationToken.IsCancellationRequested)
                {
                    output.Add(line);

                    // 实时进度报告
                    if (progress != null && outputFilter != null && outputFilter(line))
                    {
                        // 对于Robocopy，使用专门的进度分析器
                        if (line.Contains("robocopy") || line.Contains("Files :") || line.Contains("%"))
                        {
                            var progressInfo = RobocopyProgressAnalyzer.AnalyzeOutputLine(line);
                            if (progressInfo.IsValid)
                            {
                                var progressText = $"进度: {progressInfo.PercentComplete:F1}%";
                                if (progressInfo.FilesCompleted > 0)
                                {
                                    progressText += $" ({progressInfo.FilesCompleted}/{progressInfo.TotalFiles} 文件)";
                                }
                                if (!string.IsNullOrEmpty(progressInfo.TransferSpeed))
                                {
                                    progressText += $" - {progressInfo.TransferSpeed}";
                                }
                                progress.Report(progressText);
                            }
                            else
                            {
                                progress.Report(line.Trim());
                            }
                        }
                        else
                        {
                            progress.Report(line.Trim());
                        }
                    }

                    // 防止输出过多占用内存
                    if (output.Count > MaxOutputLines)
                    {
                        output.RemoveAt(0); // 移除最旧的行
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 取消操作是正常的
            }
            catch (Exception)
            {
                // 读取失败，但不影响主流程
            }
        }

        /// <summary>
        /// 同步执行版本，用于简单场景
        /// </summary>
        public static ProcessResult ExecuteSync(
            string fileName,
            string arguments,
            int timeoutSeconds = 300,
            string? workingDirectory = null)
        {
            return ExecuteAsync(fileName, arguments, timeoutSeconds, workingDirectory).GetAwaiter().GetResult();
        }
    }
}