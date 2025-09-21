using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Text.Json;

namespace CacheMax.GUI.Services
{
    public class FastCopyService
    {
        private readonly string _fastCopyPath;
        private readonly string _defaultArguments;
        private readonly AsyncLogger _logger;

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

            using var process = new Process { StartInfo = processStartInfo };

            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    _logger.LogDebug($"FastCopy输出: {e.Data}", "FastCopyService");
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    _logger.LogError($"FastCopy错误: {e.Data}", null, "FastCopyService");
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            var success = process.ExitCode == 0;
            if (success)
            {
                _logger.LogInfo($"FastCopy复制成功: {source} -> {destination}", "FastCopyService");
                progress?.Report($"复制完成: {Path.GetFileName(source)}");
            }
            else
            {
                _logger.LogError($"FastCopy复制失败: 退出码 {process.ExitCode}", null, "FastCopyService");
                progress?.Report($"复制失败: {Path.GetFileName(source)}");
            }

            return success;
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

            using var process = new Process { StartInfo = processStartInfo };
            process.Start();
            await process.WaitForExitAsync();

            var success = process.ExitCode == 0;
            if (success)
            {
                _logger.LogInfo($"FastCopy同步成功: {source} -> {destination}", "FastCopyService");
                progress?.Report($"同步完成: {Path.GetFileName(source)}");
            }
            else
            {
                _logger.LogError($"FastCopy同步失败: 退出码 {process.ExitCode}", null, "FastCopyService");
                progress?.Report($"同步失败: {Path.GetFileName(source)}");
            }

            return success;
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
    }
}