using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace CacheMax.GUI.Services
{
    public class WinFspService
    {
        private readonly Dictionary<string, Process> _runningProcesses = new();
        private readonly string _passthroughExePath;

        public WinFspService()
        {
            // passthrough-mod.exe is in the FileSystem Release folder
            _passthroughExePath = @"C:\Code\CacheMax\CacheMax.FileSystem\Release\passthrough-mod.exe";
        }

        public bool StartFileSystem(string sourcePath, string cachePath, string mountPoint, IProgress<string>? progress = null)
        {
            try
            {
                if (!File.Exists(_passthroughExePath))
                {
                    progress?.Report($"Error: passthrough-mod.exe not found at {_passthroughExePath}");
                    return false;
                }

                // Check if already running for this mount point
                if (_runningProcesses.ContainsKey(mountPoint))
                {
                    progress?.Report($"File system already running for {mountPoint}");
                    return false;
                }

                var arguments = $"-p \"{sourcePath}\" -c \"{cachePath}\" -m \"{mountPoint}\"";

                progress?.Report($"Starting WinFsp file system: {mountPoint}");
                progress?.Report($"Command: passthrough-mod.exe {arguments}");

                var processStartInfo = new ProcessStartInfo
                {
                    FileName = _passthroughExePath,
                    Arguments = arguments,
                    UseShellExecute = true,  // 改为true以获得管理员权限
                    Verb = "runas",  // 请求管理员权限
                    RedirectStandardOutput = false,  // UseShellExecute=true时不能重定向
                    RedirectStandardError = false,
                    CreateNoWindow = false,  // 显示窗口以便看到错误
                    WindowStyle = ProcessWindowStyle.Minimized
                };

                var process = new Process { StartInfo = processStartInfo };

                process.Start();

                // Give it a moment to start
                Task.Delay(1000).Wait();

                if (process.HasExited)
                {
                    progress?.Report($"WinFsp process failed to start (exit code: {process.ExitCode})");
                    return false;
                }

                _runningProcesses[mountPoint] = process;
                progress?.Report($"WinFsp file system started successfully for {mountPoint}");
                return true;
            }
            catch (Exception ex)
            {
                progress?.Report($"Exception starting WinFsp: {ex.Message}");
                return false;
            }
        }

        public bool StopFileSystem(string mountPoint, IProgress<string>? progress = null)
        {
            try
            {
                if (!_runningProcesses.TryGetValue(mountPoint, out var process))
                {
                    progress?.Report($"No running file system found for {mountPoint}");
                    return false;
                }

                progress?.Report($"Stopping WinFsp file system for {mountPoint}");

                if (!process.HasExited)
                {
                    // Try graceful shutdown first
                    process.CloseMainWindow();
                    if (!process.WaitForExit(5000))
                    {
                        // Force kill if graceful shutdown fails
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                }

                process.Dispose();
                _runningProcesses.Remove(mountPoint);

                progress?.Report($"WinFsp file system stopped for {mountPoint}");
                return true;
            }
            catch (Exception ex)
            {
                progress?.Report($"Exception stopping WinFsp: {ex.Message}");
                return false;
            }
        }

        public void StopAllFileSystems()
        {
            foreach (var mountPoint in new List<string>(_runningProcesses.Keys))
            {
                StopFileSystem(mountPoint);
            }
        }

        public bool IsRunning(string mountPoint)
        {
            return _runningProcesses.ContainsKey(mountPoint) &&
                   !_runningProcesses[mountPoint].HasExited;
        }

        public IEnumerable<string> GetRunningMountPoints()
        {
            var result = new List<string>();
            foreach (var kvp in _runningProcesses)
            {
                if (!kvp.Value.HasExited)
                    result.Add(kvp.Key);
            }
            return result;
        }
    }
}