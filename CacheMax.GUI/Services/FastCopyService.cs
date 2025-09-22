using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CacheMax.GUI.Services
{
    public class FastCopyService : IDisposable
    {
        private static readonly Lazy<FastCopyService> _instance = new Lazy<FastCopyService>(() => new FastCopyService());
        public static FastCopyService Instance => _instance.Value;

        private readonly string _fastCopyPath;
        private readonly string _defaultArguments;
        private readonly AsyncLogger _logger;
        private readonly ConcurrentDictionary<int, ProcessMonitorInfo> _runningProcesses = new();
        private readonly int _processTimeoutMinutes = 30; // 30分钟超时
        private readonly Timer _processMonitorTimer;

        /// <summary>
        /// 进程监控信息
        /// </summary>
        private class ProcessMonitorInfo
        {
            public Process Process { get; set; } = null!;
            public string SourcePath { get; set; } = string.Empty;
            public string DestinationPath { get; set; } = string.Empty;
            public DateTime StartTime { get; set; }
            public TaskCompletionSource<bool> CompletionSource { get; set; } = null!;
            public CancellationTokenSource CancellationTokenSource { get; set; } = null!;
        }

        private FastCopyService(AsyncLogger? logger = null)
        {
            _logger = logger ?? AsyncLogger.Instance;

            // 尝试从配置文件读取路径
            try
            {
                var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                if (File.Exists(configPath))
                {
                    var configContent = File.ReadAllText(configPath);
                    var config = JsonSerializer.Deserialize<JsonElement>(configContent);

                    if (config.TryGetProperty("FastCopy", out var fastCopyConfig))
                    {
                        _fastCopyPath = fastCopyConfig.GetProperty("ExecutablePath").GetString() ?? @"C:\Program Files\FastCopy64\fcp.exe";
                        _defaultArguments = fastCopyConfig.GetProperty("DefaultArguments").GetString() ?? "/cmd=force_copy /verify /auto_close /error_stop /no_ui";
                    }
                    else
                    {
                        _fastCopyPath = @"C:\Program Files\FastCopy64\fcp.exe";
                        _defaultArguments = "/cmd=force_copy /verify /auto_close /error_stop /no_ui";
                    }
                }
                else
                {
                    _fastCopyPath = @"C:\Program Files\FastCopy64\fcp.exe";
                    _defaultArguments = "/cmd=force_copy /verify /auto_close /error_stop /no_ui";
                }
            }
            catch
            {
                // 配置读取失败时使用默认值
                _fastCopyPath = @"C:\Program Files\FastCopy64\fcp.exe";
                _defaultArguments = "/cmd=force_copy /verify /auto_close /error_stop /no_ui";
            }

            _logger.LogInfo($"FastCopy服务初始化: {_fastCopyPath}", "FastCopyService");

            // 初始化进程监控定时器（每30秒检查一次）
            _processMonitorTimer = new Timer(MonitorProcesses, null,
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }


        /// <summary>
        /// 进程监控方法，检查超时和异常进程
        /// </summary>
        private void MonitorProcesses(object? state)
        {
            try
            {
                var processesToRemove = new List<int>();
                var currentTime = DateTime.Now;

                foreach (var kvp in _runningProcesses)
                {
                    var processId = kvp.Key;
                    var monitorInfo = kvp.Value;

                    try
                    {
                        // 检查进程是否还在运行
                        if (monitorInfo.Process.HasExited)
                        {
                            _logger.LogInfo($"FastCopy进程已退出 (PID: {processId}): {Path.GetFileName(monitorInfo.SourcePath)}", "FastCopyService");
                            monitorInfo.CompletionSource.TrySetResult(monitorInfo.Process.ExitCode == 0);
                            processesToRemove.Add(processId);
                            continue;
                        }

                        // 检查超时
                        var runTime = currentTime - monitorInfo.StartTime;
                        if (runTime.TotalMinutes > _processTimeoutMinutes)
                        {
                            _logger.LogWarning($"FastCopy进程超时，正在终止 (PID: {processId}, 运行时间: {runTime.TotalMinutes:F1}分钟): {Path.GetFileName(monitorInfo.SourcePath)}", "FastCopyService");

                            // 尝试优雅关闭
                            try
                            {
                                monitorInfo.Process.CloseMainWindow();
                                if (!monitorInfo.Process.WaitForExit(5000)) // 等待5秒
                                {
                                    // 强制终止
                                    monitorInfo.Process.Kill();
                                    _logger.LogWarning($"强制终止超时的FastCopy进程 (PID: {processId})", "FastCopyService");
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError($"终止超时进程时发生错误 (PID: {processId}): {ex.Message}", ex, "FastCopyService");
                            }

                            monitorInfo.CancellationTokenSource.Cancel();
                            monitorInfo.CompletionSource.TrySetResult(false);
                            processesToRemove.Add(processId);
                        }
                        else
                        {
                            // 记录进程状态（用于调试）
                            _logger.LogDebug($"FastCopy进程监控 (PID: {processId}): 运行时间 {runTime.TotalMinutes:F1}分钟, 内存: {monitorInfo.Process.WorkingSet64 / 1024 / 1024}MB", "FastCopyService");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"监控FastCopy进程时发生错误 (PID: {processId}): {ex.Message}", ex, "FastCopyService");
                        monitorInfo.CompletionSource.TrySetResult(false);
                        processesToRemove.Add(processId);
                    }
                }

                // 清理已完成的进程
                foreach (var processId in processesToRemove)
                {
                    _runningProcesses.TryRemove(processId, out _);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"进程监控器发生错误: {ex.Message}", ex, "FastCopyService");
            }
        }

        /// <summary>
        /// FastCopy操作结果
        /// </summary>
        public class FastCopyResult
        {
            public bool Success { get; set; }
            public int ExitCode { get; set; }
            public string ErrorMessage { get; set; } = string.Empty;
            public List<string> StandardOutput { get; set; } = new();
            public List<string> ErrorOutput { get; set; } = new();
        }

        /// <summary>
        /// 使用FastCopy复制文件或目录，自动校验（保持向后兼容）
        /// </summary>
        public async Task<bool> CopyWithVerifyAsync(string source, string destination, IProgress<string>? progress = null)
        {
            var result = await CopyWithVerifyDetailedAsync(source, destination, progress);
            return result.Success;
        }

        /// <summary>
        /// 使用FastCopy复制文件或目录，返回详细结果
        /// </summary>
        public async Task<FastCopyResult> CopyWithVerifyDetailedAsync(string source, string destination, IProgress<string>? progress = null)
        {
            if (!File.Exists(_fastCopyPath))
            {
                _logger.LogWarning($"FastCopy可执行文件未找到: {_fastCopyPath}，使用内置复制方法", "FastCopyService");
                progress?.Report($"FastCopy未找到，使用内置复制方法");
                var builtinResult = await CopyWithBuiltinMethod(source, destination, progress);
                return new FastCopyResult
                {
                    Success = builtinResult,
                    ExitCode = builtinResult ? 0 : -1,
                    ErrorMessage = builtinResult ? string.Empty : "内置复制方法失败"
                };
            }

            var targetDir = Path.GetDirectoryName(destination);
            var arguments = $"{_defaultArguments} /log \"{source}\" /to=\"{targetDir}\"";

            _logger.LogInfo($"开始FastCopy复制: {source} -> {destination}", "FastCopyService");
            progress?.Report($"正在复制: {Path.GetFileName(source)}");

            var processStartInfo = new ProcessStartInfo
            {
                FileName = _fastCopyPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            return await ExecuteWithMonitoringDetailedAsync(processStartInfo, source, destination, progress);
        }

        /// <summary>
        /// 同步目录变更，只复制新增或修改的文件
        /// </summary>
        public async Task<bool> SyncChangesAsync(string source, string destination, IProgress<string>? progress = null)
        {
            if (!File.Exists(_fastCopyPath))
            {
                _logger.LogWarning($"FastCopy可执行文件未找到: {_fastCopyPath}，使用内置同步方法", "FastCopyService");
                progress?.Report($"FastCopy未找到，使用内置同步方法");
                return await CopyWithBuiltinMethod(source, destination, progress);
            }

            // 使用update命令只同步变更
            var targetDir = Path.GetDirectoryName(destination);
            var arguments = $"/cmd=update /verify /log \"{source}\" /to=\"{targetDir}\"";

            _logger.LogInfo($"开始FastCopy同步: {source} -> {destination}", "FastCopyService");
            progress?.Report($"正在同步: {Path.GetFileName(source)}");

            var processStartInfo = new ProcessStartInfo
            {
                FileName = _fastCopyPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            return await ExecuteWithMonitoringAsync(processStartInfo, source, destination, progress);
        }

        /// <summary>
        /// 带监控的进程执行方法（返回详细结果）
        /// </summary>
        private async Task<FastCopyResult> ExecuteWithMonitoringDetailedAsync(ProcessStartInfo startInfo, string source, string destination, IProgress<string>? progress = null)
        {
            var cts = new CancellationTokenSource();
            var tcs = new TaskCompletionSource<bool>();
            var result = new FastCopyResult();

            using var process = new Process { StartInfo = startInfo };

            // 错误和输出缓冲
            var errorOutput = new List<string>();
            var standardOutput = new List<string>();

            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    standardOutput.Add(e.Data);
                    _logger.LogDebug($"FastCopy输出: {e.Data}", "FastCopyService");
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    errorOutput.Add(e.Data);
                    _logger.LogError($"FastCopy错误: {e.Data}", null, "FastCopyService");
                }
            };

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var processId = process.Id;
                _logger.LogInfo($"启动FastCopy进程 (PID: {processId}): {Path.GetFileName(source)}", "FastCopyService");

                // 添加到监控列表
                var monitorInfo = new ProcessMonitorInfo
                {
                    Process = process,
                    SourcePath = source,
                    DestinationPath = destination,
                    StartTime = DateTime.Now,
                    CompletionSource = tcs,
                    CancellationTokenSource = cts
                };

                _runningProcesses.TryAdd(processId, monitorInfo);

                // 等待进程完成或取消
                var processTask = process.WaitForExitAsync(cts.Token);
                await Task.WhenAny(processTask, tcs.Task);

                // 清理监控
                _runningProcesses.TryRemove(processId, out _);

                // 设置详细结果
                result.StandardOutput = standardOutput;
                result.ErrorOutput = errorOutput;

                if (process.HasExited)
                {
                    result.ExitCode = process.ExitCode;
                    result.Success = process.ExitCode == 0;

                    if (result.Success)
                    {
                        _logger.LogInfo($"FastCopy复制成功: {source} -> {destination}", "FastCopyService");
                        progress?.Report($"复制完成: {Path.GetFileName(source)}");
                        result.ErrorMessage = string.Empty;
                    }
                    else
                    {
                        var errorSummary = errorOutput.Count > 0 ? string.Join("; ", errorOutput.Take(3)) : "未知错误";
                        result.ErrorMessage = errorSummary;
                        _logger.LogError($"FastCopy复制失败: 退出码 {process.ExitCode}, 错误: {errorSummary}", null, "FastCopyService");
                        progress?.Report($"复制失败: {Path.GetFileName(source)} - {errorSummary}");
                    }
                }
                else
                {
                    result.ExitCode = -1;
                    result.Success = false;
                    result.ErrorMessage = "进程被终止或取消";
                    _logger.LogWarning($"FastCopy进程被终止或取消: {Path.GetFileName(source)}", "FastCopyService");
                    progress?.Report($"复制被中断: {Path.GetFileName(source)}");
                }

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ExitCode = -1;
                result.ErrorMessage = ex.Message;
                result.StandardOutput = standardOutput;
                result.ErrorOutput = errorOutput;

                _logger.LogError($"执行FastCopy时发生异常: {ex.Message}", ex, "FastCopyService");
                progress?.Report($"复制异常: {Path.GetFileName(source)} - {ex.Message}");

                // 清理监控
                if (process.Id > 0)
                {
                    _runningProcesses.TryRemove(process.Id, out _);
                }

                return result;
            }
        }

        /// <summary>
        /// 带监控的进程执行方法
        /// </summary>
        private async Task<bool> ExecuteWithMonitoringAsync(ProcessStartInfo startInfo, string source, string destination, IProgress<string>? progress = null)
        {
            var cts = new CancellationTokenSource();
            var tcs = new TaskCompletionSource<bool>();

            using var process = new Process { StartInfo = startInfo };

            // 错误和输出缓冲
            var errorOutput = new List<string>();
            var standardOutput = new List<string>();

            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    standardOutput.Add(e.Data);
                    _logger.LogDebug($"FastCopy输出: {e.Data}", "FastCopyService");
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    errorOutput.Add(e.Data);
                    _logger.LogError($"FastCopy错误: {e.Data}", null, "FastCopyService");
                }
            };

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var processId = process.Id;
                _logger.LogInfo($"启动FastCopy进程 (PID: {processId}): {Path.GetFileName(source)}", "FastCopyService");

                // 添加到监控列表
                var monitorInfo = new ProcessMonitorInfo
                {
                    Process = process,
                    SourcePath = source,
                    DestinationPath = destination,
                    StartTime = DateTime.Now,
                    CompletionSource = tcs,
                    CancellationTokenSource = cts
                };

                _runningProcesses.TryAdd(processId, monitorInfo);

                // 等待进程完成或取消
                var processTask = process.WaitForExitAsync(cts.Token);

                // 等待进程完成或监控器发出信号
                await Task.WhenAny(processTask, tcs.Task);

                // 清理监控
                _runningProcesses.TryRemove(processId, out _);

                var success = false;
                if (process.HasExited)
                {
                    success = process.ExitCode == 0;
                    if (success)
                    {
                        _logger.LogInfo($"FastCopy复制成功: {source} -> {destination}", "FastCopyService");
                        progress?.Report($"复制完成: {Path.GetFileName(source)}");
                    }
                    else
                    {
                        var errorSummary = errorOutput.Count > 0 ? string.Join("; ", errorOutput.Take(3)) : "未知错误";
                        _logger.LogError($"FastCopy复制失败: 退出码 {process.ExitCode}, 错误: {errorSummary}", null, "FastCopyService");
                        progress?.Report($"复制失败: {Path.GetFileName(source)} - {errorSummary}");
                    }
                }
                else
                {
                    // 进程被监控器终止或取消
                    _logger.LogWarning($"FastCopy进程被终止或取消: {Path.GetFileName(source)}", "FastCopyService");
                    progress?.Report($"复制被中断: {Path.GetFileName(source)}");
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError($"执行FastCopy时发生异常: {ex.Message}", ex, "FastCopyService");
                progress?.Report($"复制异常: {Path.GetFileName(source)} - {ex.Message}");

                // 清理监控
                if (process.Id > 0)
                {
                    _runningProcesses.TryRemove(process.Id, out _);
                }

                return false;
            }
        }

        /// <summary>
        /// 内置复制方法（备用方案）
        /// </summary>
        private async Task<bool> CopyWithBuiltinMethod(string source, string destination, IProgress<string>? progress = null)
        {
            try
            {
                _logger.LogInfo($"使用内置复制方法: {source} -> {destination}", "FastCopyService");

                if (File.Exists(source))
                {
                    return await CopyFileAsync(source, destination, progress);
                }
                else if (Directory.Exists(source))
                {
                    return await CopyDirectoryAsync(source, destination, progress);
                }
                else
                {
                    _logger.LogError($"源路径不存在: {source}", null, "FastCopyService");
                    progress?.Report($"源路径不存在：{source}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"内置复制方法失败: {ex.Message}", ex, "FastCopyService");
                progress?.Report($"内置复制方法失败：{ex.Message}");
                return false;
            }
        }

        private async Task<bool> CopyFileAsync(string sourceFile, string destinationFile, IProgress<string>? progress = null)
        {
            try
            {
                // 确保目标目录存在
                var targetDir = Path.GetDirectoryName(destinationFile);
                if (targetDir != null && !Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                await Task.Run(() =>
                {
                    File.Copy(sourceFile, destinationFile, true);

                    // 保持文件属性
                    var sourceInfo = new FileInfo(sourceFile);
                    var targetInfo = new FileInfo(destinationFile);
                    targetInfo.CreationTime = sourceInfo.CreationTime;
                    targetInfo.LastWriteTime = sourceInfo.LastWriteTime;
                    targetInfo.Attributes = sourceInfo.Attributes;
                });

                progress?.Report($"复制完成：{Path.GetFileName(sourceFile)}");
                return true;
            }
            catch (Exception ex)
            {
                progress?.Report($"复制失败：{Path.GetFileName(sourceFile)} - {ex.Message}");
                return false;
            }
        }

        private async Task<bool> CopyDirectoryAsync(string sourceDir, string destinationDir, IProgress<string>? progress = null)
        {
            try
            {
                await Task.Run(() => CopyDirectoryRecursive(sourceDir, destinationDir, progress));
                return true;
            }
            catch (Exception ex)
            {
                progress?.Report($"目录复制失败：{ex.Message}");
                return false;
            }
        }

        private void CopyDirectoryRecursive(string sourceDir, string destinationDir, IProgress<string>? progress)
        {
            // 创建目标目录
            if (!Directory.Exists(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }

            // 复制所有文件
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var fileName = Path.GetFileName(file);
                var destFile = Path.Combine(destinationDir, fileName);

                File.Copy(file, destFile, true);

                // 保持文件属性
                var sourceInfo = new FileInfo(file);
                var destInfo = new FileInfo(destFile);
                destInfo.CreationTime = sourceInfo.CreationTime;
                destInfo.LastWriteTime = sourceInfo.LastWriteTime;
                destInfo.Attributes = sourceInfo.Attributes;

                progress?.Report($"复制文件：{fileName}");
            }

            // 递归复制子目录
            foreach (var subDir in Directory.GetDirectories(sourceDir))
            {
                var dirName = Path.GetFileName(subDir);
                var destSubDir = Path.Combine(destinationDir, dirName);
                CopyDirectoryRecursive(subDir, destSubDir, progress);
            }
        }

        /// <summary>
        /// 获取当前运行的FastCopy进程状态
        /// </summary>
        public (int Count, List<string> ProcessInfo) GetRunningProcessStatus()
        {
            var processInfo = new List<string>();
            foreach (var kvp in _runningProcesses)
            {
                var processId = kvp.Key;
                var monitorInfo = kvp.Value;
                var runTime = DateTime.Now - monitorInfo.StartTime;
                processInfo.Add($"PID: {processId}, 文件: {Path.GetFileName(monitorInfo.SourcePath)}, 运行时间: {runTime.TotalMinutes:F1}分钟");
            }
            return (_runningProcesses.Count, processInfo);
        }

        /// <summary>
        /// 使用FastCopy复制整个目录（支持自定义参数）
        /// </summary>
        public async Task<bool> CopyDirectoryAsync(string sourceDir, string destinationDir, string[]? customOptions = null, IProgress<string>? progress = null)
        {
            if (!File.Exists(_fastCopyPath))
            {
                _logger.LogWarning($"FastCopy可执行文件未找到: {_fastCopyPath}，使用内置复制方法", "FastCopyService");
                progress?.Report($"FastCopy未找到，使用内置复制方法");
                return await CopyWithBuiltinMethod(sourceDir, destinationDir, progress);
            }

            try
            {
                // 构建FastCopy命令行参数
                var argumentsList = new List<string>();

                // 添加自定义选项
                if (customOptions != null && customOptions.Length > 0)
                {
                    argumentsList.AddRange(customOptions);
                }
                else
                {
                    // 默认选项
                    argumentsList.Add("/cmd=force_copy");
                    argumentsList.Add("/verify");
                    argumentsList.Add("/auto_close");
                    argumentsList.Add("/error_stop");
                    argumentsList.Add("/no_ui");
                }

                // 添加日志和路径
                argumentsList.Add("/log");
                argumentsList.Add($"\"{sourceDir}\"");
                argumentsList.Add($"/to=\"{destinationDir}\"");

                var arguments = string.Join(" ", argumentsList);

                _logger.LogInfo($"开始FastCopy目录复制: {sourceDir} -> {destinationDir}", "FastCopyService");
                _logger.LogDebug($"FastCopy参数: {arguments}", "FastCopyService");
                progress?.Report($"正在复制目录: {Path.GetFileName(sourceDir)}");

                var processStartInfo = new ProcessStartInfo
                {
                    FileName = _fastCopyPath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                var success = await ExecuteWithMonitoringAsync(processStartInfo, sourceDir, destinationDir, progress);

                if (success)
                {
                    _logger.LogInfo($"FastCopy目录复制成功: {sourceDir} -> {destinationDir}", "FastCopyService");
                    progress?.Report($"目录复制完成: {Path.GetFileName(sourceDir)}");
                }
                else
                {
                    _logger.LogError($"FastCopy目录复制失败: {sourceDir} -> {destinationDir}", null, "FastCopyService");
                    progress?.Report($"目录复制失败: {Path.GetFileName(sourceDir)}");
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError($"FastCopy目录复制异常: {ex.Message}", ex, "FastCopyService");
                progress?.Report($"目录复制异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 强制终止所有运行的FastCopy进程
        /// </summary>
        public async Task ForceKillAllProcessesAsync()
        {
            var processesToKill = _runningProcesses.Values.ToList();

            foreach (var monitorInfo in processesToKill)
            {
                try
                {
                    if (!monitorInfo.Process.HasExited)
                    {
                        _logger.LogWarning($"强制终止FastCopy进程 (PID: {monitorInfo.Process.Id}): {Path.GetFileName(monitorInfo.SourcePath)}", "FastCopyService");
                        monitorInfo.Process.Kill();
                        monitorInfo.CompletionSource.TrySetResult(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"强制终止进程时发生错误: {ex.Message}", ex, "FastCopyService");
                }
            }

            _runningProcesses.Clear();
        }

        public void Dispose()
        {
            // 停止监控定时器
            _processMonitorTimer?.Dispose();

            // 终止所有运行的进程
            Task.Run(ForceKillAllProcessesAsync).Wait(5000);

            GC.SuppressFinalize(this);
        }
    }
}