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

        public FastCopyService(AsyncLogger? logger = null)
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

            // 启动时清理可能存在的遗留FastCopy进程
            CleanupOrphanedFastCopyProcesses();

            // 初始化进程监控定时器（每30秒检查一次）
            _processMonitorTimer = new Timer(MonitorProcesses, null,
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        /// <summary>
        /// 清理遗留的FastCopy进程（启动时调用）
        /// 注意：不会清理其他应用启动的FastCopy进程，只清理我们自己可能遗留的
        /// </summary>
        private void CleanupOrphanedFastCopyProcesses()
        {
            try
            {
                // 更安全的方法：只记录发现的进程数量，不进行清理
                // 因为无法可靠地区分是否是我们启动的进程
                var fastCopyProcessName = Path.GetFileNameWithoutExtension(_fastCopyPath);
                var allFastCopyProcesses = Process.GetProcessesByName(fastCopyProcessName);

                if (allFastCopyProcesses.Length > 0)
                {
                    _logger.LogInfo($"检测到系统中有 {allFastCopyProcesses.Length} 个FastCopy进程正在运行", "FastCopyService");

                    // 只记录，不清理，避免误杀其他应用的进程
                    foreach (var process in allFastCopyProcesses)
                    {
                        try
                        {
                            _logger.LogDebug($"FastCopy进程: PID={process.Id}, 启动时间={process.StartTime:yyyy-MM-dd HH:mm:ss}", "FastCopyService");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug($"无法获取FastCopy进程信息 (PID: {process.Id}): {ex.Message}", "FastCopyService");
                        }
                        finally
                        {
                            process.Dispose();
                        }
                    }
                }
                else
                {
                    _logger.LogInfo("系统中没有发现FastCopy进程", "FastCopyService");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"检查FastCopy进程时发生异常: {ex.Message}", ex, "FastCopyService");
            }
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
        /// 使用FastCopy复制文件或目录，自动校验
        /// </summary>
        public async Task<bool> CopyWithVerifyAsync(string source, string destination, IProgress<string>? progress = null)
        {
            if (!File.Exists(_fastCopyPath))
            {
                _logger.LogWarning($"FastCopy可执行文件未找到: {_fastCopyPath}，使用内置复制方法", "FastCopyService");
                progress?.Report($"FastCopy未找到，使用内置复制方法");
                return await CopyWithBuiltinMethod(source, destination, progress);
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

            return await ExecuteWithMonitoringAsync(processStartInfo, source, destination, progress);
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