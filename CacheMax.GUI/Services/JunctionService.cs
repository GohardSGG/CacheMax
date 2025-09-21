using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CacheMax.GUI.Services
{
    /// <summary>
    /// 目录连接点(Junction)管理服务
    /// 优势：不需要管理员权限，兼容性更好，支持跨驱动器
    /// </summary>
    public class JunctionService
    {
        private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetFileAttributes(string lpFileName);

        /// <summary>
        /// 创建目录连接点
        /// </summary>
        public bool CreateDirectoryJunction(string junctionPath, string targetPath, IProgress<string>? progress = null)
        {
            try
            {
                // 标准化路径
                junctionPath = Path.GetFullPath(junctionPath);
                targetPath = Path.GetFullPath(targetPath);

                progress?.Report($"创建Junction：{junctionPath} -> {targetPath}");

                // 检查目标路径是否存在
                if (!Directory.Exists(targetPath))
                {
                    progress?.Report($"错误：目标路径不存在：{targetPath}");
                    return false;
                }

                // 检查Junction路径是否已存在
                if (Directory.Exists(junctionPath))
                {
                    progress?.Report($"错误：Junction路径已存在：{junctionPath}");
                    return false;
                }

                // 使用cmd命令创建Junction（最可靠的方法）
                var processInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = System.Diagnostics.Process.Start(processInfo))
                {
                    if (process != null)
                    {
                        process.WaitForExit();

                        if (process.ExitCode == 0)
                        {
                            progress?.Report("Junction创建成功");
                            return true;
                        }
                        else
                        {
                            string error = process.StandardError.ReadToEnd();
                            string output = process.StandardOutput.ReadToEnd();
                            progress?.Report($"Junction创建失败：{error} {output}");
                            return false;
                        }
                    }
                }

                progress?.Report("启动mklink进程失败");
                return false;
            }
            catch (Exception ex)
            {
                progress?.Report($"创建Junction异常：{ex.Message}");

                // 清理可能残留的目录
                try
                {
                    if (Directory.Exists(junctionPath) && !IsJunction(junctionPath))
                    {
                        Directory.Delete(junctionPath);
                    }
                }
                catch { }

                return false;
            }
        }

        /// <summary>
        /// 删除目录连接点
        /// </summary>
        public bool RemoveJunction(string junctionPath, IProgress<string>? progress = null)
        {
            try
            {
                progress?.Report($"删除Junction：{junctionPath}");

                if (!Directory.Exists(junctionPath))
                {
                    progress?.Report($"Junction路径不存在：{junctionPath}");
                    return true; // 已经不存在了，算成功
                }

                if (!IsJunction(junctionPath))
                {
                    progress?.Report($"路径不是Junction：{junctionPath}");
                    return false;
                }

                // 使用cmd命令删除Junction
                var processInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c rmdir \"{junctionPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = System.Diagnostics.Process.Start(processInfo))
                {
                    if (process != null)
                    {
                        process.WaitForExit();

                        if (process.ExitCode == 0)
                        {
                            progress?.Report("Junction删除成功");
                            return true;
                        }
                        else
                        {
                            string error = process.StandardError.ReadToEnd();
                            string output = process.StandardOutput.ReadToEnd();
                            progress?.Report($"Junction删除失败：{error} {output}");
                            return false;
                        }
                    }
                }

                progress?.Report("启动rmdir进程失败");
                return false;
            }
            catch (Exception ex)
            {
                progress?.Report($"删除Junction异常：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 检查路径是否为Junction
        /// </summary>
        public bool IsJunction(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                    return false;

                uint attributes = GetFileAttributes(path);
                return (attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 获取Junction的目标路径
        /// </summary>
        public string? GetJunctionTarget(string junctionPath)
        {
            try
            {
                if (!IsJunction(junctionPath))
                    return null;

                // 使用fsutil命令获取Junction目标（更可靠）
                var processInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "fsutil.exe",
                    Arguments = $"reparsepoint query \"{junctionPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = System.Diagnostics.Process.Start(processInfo))
                {
                    if (process != null)
                    {
                        process.WaitForExit();

                        if (process.ExitCode == 0)
                        {
                            string output = process.StandardOutput.ReadToEnd();
                            // 解析fsutil输出来获取目标路径
                            // 格式：Print Name:        S:\Cache\Test
                            foreach (string line in output.Split('\n'))
                            {
                                if (line.Contains("Print Name:") && line.Contains(":"))
                                {
                                    var parts = line.Split(new[] { "Print Name:" }, StringSplitOptions.None);
                                    if (parts.Length > 1)
                                    {
                                        return parts[1].Trim();
                                    }
                                }
                                // 备用：Substitute Name也包含目标路径
                                else if (line.Contains("Substitute Name:") && line.Contains("\\??\\"))
                                {
                                    var parts = line.Split(new[] { "Substitute Name:" }, StringSplitOptions.None);
                                    if (parts.Length > 1)
                                    {
                                        string path = parts[1].Trim();
                                        if (path.StartsWith("\\??\\"))
                                        {
                                            return path.Substring(4); // 移除 \\??\\ 前缀
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // 如果fsutil失败，尝试dir命令作为备用
                processInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c dir \"{junctionPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = System.Diagnostics.Process.Start(processInfo))
                {
                    if (process != null)
                    {
                        process.WaitForExit();

                        if (process.ExitCode == 0)
                        {
                            string output = process.StandardOutput.ReadToEnd();
                            // 查找形如 "<JUNCTION>     filename [target]" 的行
                            foreach (string line in output.Split('\n'))
                            {
                                if (line.Contains("<JUNCTION>") && line.Contains("[") && line.Contains("]"))
                                {
                                    int start = line.LastIndexOf('[') + 1;
                                    int end = line.LastIndexOf(']');
                                    if (start > 0 && end > start)
                                    {
                                        return line.Substring(start, end - start);
                                    }
                                }
                            }
                        }
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 验证Junction是否指向正确的目标
        /// </summary>
        public bool ValidateJunction(string junctionPath, string expectedTarget, IProgress<string>? progress = null)
        {
            try
            {
                if (!IsJunction(junctionPath))
                {
                    progress?.Report($"路径不是Junction：{junctionPath}");
                    return false;
                }

                string? actualTarget = GetJunctionTarget(junctionPath);
                string normalizedExpected = Path.GetFullPath(expectedTarget);
                string? normalizedActual = actualTarget != null ? Path.GetFullPath(actualTarget) : null;

                bool isValid = string.Equals(normalizedExpected, normalizedActual, StringComparison.OrdinalIgnoreCase);

                if (isValid)
                {
                    progress?.Report($"Junction验证成功：{junctionPath} -> {actualTarget}");
                }
                else
                {
                    progress?.Report($"Junction验证失败：{junctionPath}");
                    progress?.Report($"  期望目标：{normalizedExpected}");
                    progress?.Report($"  实际目标：{normalizedActual ?? "无法读取"}");
                }

                return isValid;
            }
            catch (Exception ex)
            {
                progress?.Report($"验证Junction异常：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 安全重命名目录
        /// </summary>
        public bool SafeRenameDirectory(string sourcePath, string targetPath, IProgress<string>? progress = null)
        {
            try
            {
                if (!Directory.Exists(sourcePath))
                {
                    progress?.Report($"源目录不存在：{sourcePath}");
                    return false;
                }

                if (Directory.Exists(targetPath))
                {
                    progress?.Report($"目标目录已存在：{targetPath}");
                    return false;
                }

                Directory.Move(sourcePath, targetPath);
                progress?.Report($"目录重命名成功：{sourcePath} -> {targetPath}");
                return true;
            }
            catch (Exception ex)
            {
                progress?.Report($"目录重命名失败：{ex.Message}");
                return false;
            }
        }
    }
}