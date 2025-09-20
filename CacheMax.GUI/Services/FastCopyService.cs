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
                progress?.Report($"Error: FastCopy.exe not found at {_fastCopyPath}");
                return false;
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
                progress?.Report($"Error: FastCopy.exe not found at {_fastCopyPath}");
                return false;
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
    }
}