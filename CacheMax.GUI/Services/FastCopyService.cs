using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace CacheMax.GUI.Services
{
    public class FastCopyService
    {
        private readonly string _fastCopyPath;

        public FastCopyService()
        {
            // FastCopy is installed in Program Files
            _fastCopyPath = @"C:\Program Files\FastCopy64\FastCopy.exe";
        }

        public async Task<bool> CopyWithVerifyAsync(string source, string destination, IProgress<string>? progress = null)
        {
            if (!File.Exists(_fastCopyPath))
            {
                progress?.Report($"FastCopy.exe not found, using built-in copy method");
                return await CopyWithBuiltinMethod(source, destination, progress);
            }

            var arguments = $"/cmd=diff /verify /estimate /log \"{source}\" /to=\"{destination}\"";

            progress?.Report($"Starting FastCopy: {source} -> {destination}");

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
                    progress?.Report(e.Data);
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    progress?.Report($"Error: {e.Data}");
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            var success = process.ExitCode == 0;
            progress?.Report(success ? "FastCopy completed successfully" : $"FastCopy failed with exit code {process.ExitCode}");

            return success;
        }

        public async Task<bool> SyncChangesAsync(string source, string destination, IProgress<string>? progress = null)
        {
            if (!File.Exists(_fastCopyPath))
            {
                progress?.Report($"FastCopy.exe not found, using built-in sync method");
                return await CopyWithBuiltinMethod(source, destination, progress);
            }

            // Use update command for syncing changes only
            var arguments = $"/cmd=update /verify \"{source}\" /to=\"{destination}\"";

            progress?.Report($"Syncing changes: {source} -> {destination}");

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

            return process.ExitCode == 0;
        }

        /// <summary>
        /// 内置复制方法（备用方案）
        /// </summary>
        private async Task<bool> CopyWithBuiltinMethod(string source, string destination, IProgress<string>? progress = null)
        {
            try
            {
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
                    progress?.Report($"源路径不存在：{source}");
                    return false;
                }
            }
            catch (Exception ex)
            {
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