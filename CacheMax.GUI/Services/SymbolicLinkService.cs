using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace CacheMax.GUI.Services
{
    public class SymbolicLinkService
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreateSymbolicLink(
            string lpSymlinkFileName,
            string lpTargetFileName,
            SymbolicLinkFlag dwFlags);

        [Flags]
        private enum SymbolicLinkFlag
        {
            File = 0,
            Directory = 1
        }

        /// <summary>
        /// 检查当前是否以管理员权限运行
        /// </summary>
        public bool IsRunningAsAdministrator()
        {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        /// <summary>
        /// 创建目录符号链接
        /// </summary>
        public bool CreateDirectorySymbolicLink(string linkPath, string targetPath, IProgress<string>? progress = null)
        {
            try
            {
                // 检查管理员权限
                if (!IsRunningAsAdministrator())
                {
                    progress?.Report("错误：创建符号链接需要管理员权限");
                    return false;
                }

                // 检查目标路径是否存在
                if (!Directory.Exists(targetPath))
                {
                    progress?.Report($"错误：目标路径不存在：{targetPath}");
                    return false;
                }

                // 检查链接路径是否已存在
                if (Directory.Exists(linkPath) || File.Exists(linkPath))
                {
                    progress?.Report($"错误：链接路径已存在：{linkPath}");
                    return false;
                }

                progress?.Report($"创建符号链接：{linkPath} -> {targetPath}");

                // 创建符号链接
                bool result = CreateSymbolicLink(linkPath, targetPath, SymbolicLinkFlag.Directory);

                if (!result)
                {
                    var error = Marshal.GetLastWin32Error();
                    progress?.Report($"创建符号链接失败，错误代码：{error}");
                    return false;
                }

                progress?.Report("符号链接创建成功");
                return true;
            }
            catch (Exception ex)
            {
                progress?.Report($"创建符号链接异常：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 删除符号链接
        /// </summary>
        public bool RemoveSymbolicLink(string linkPath, IProgress<string>? progress = null)
        {
            try
            {
                if (!IsSymbolicLink(linkPath))
                {
                    progress?.Report($"路径不是符号链接：{linkPath}");
                    return false;
                }

                progress?.Report($"删除符号链接：{linkPath}");

                // 删除符号链接（作为目录删除）
                Directory.Delete(linkPath);

                progress?.Report("符号链接删除成功");
                return true;
            }
            catch (Exception ex)
            {
                progress?.Report($"删除符号链接异常：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 检查路径是否为符号链接
        /// </summary>
        public bool IsSymbolicLink(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                    return false;

                var dirInfo = new DirectoryInfo(path);
                return dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 获取符号链接的目标路径
        /// </summary>
        public string? GetSymbolicLinkTarget(string linkPath)
        {
            try
            {
                if (!IsSymbolicLink(linkPath))
                    return null;

                var dirInfo = new DirectoryInfo(linkPath);
                return dirInfo.ResolveLinkTarget(true)?.FullName;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 验证符号链接是否正常工作
        /// </summary>
        public bool ValidateSymbolicLink(string linkPath, string expectedTarget, IProgress<string>? progress = null)
        {
            try
            {
                if (!IsSymbolicLink(linkPath))
                {
                    progress?.Report($"路径不是符号链接：{linkPath}");
                    return false;
                }

                var actualTarget = GetSymbolicLinkTarget(linkPath);
                if (actualTarget == null)
                {
                    progress?.Report($"无法获取符号链接目标：{linkPath}");
                    return false;
                }

                // 规范化路径进行比较
                var normalizedActual = Path.GetFullPath(actualTarget);
                var normalizedExpected = Path.GetFullPath(expectedTarget);

                if (!string.Equals(normalizedActual, normalizedExpected, StringComparison.OrdinalIgnoreCase))
                {
                    progress?.Report($"符号链接目标不匹配。期望：{normalizedExpected}，实际：{normalizedActual}");
                    return false;
                }

                progress?.Report("符号链接验证成功");
                return true;
            }
            catch (Exception ex)
            {
                progress?.Report($"验证符号链接异常：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 安全地重命名目录（处理只读文件等情况）
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

                progress?.Report($"重命名目录：{sourcePath} -> {targetPath}");

                // 尝试直接重命名
                Directory.Move(sourcePath, targetPath);

                progress?.Report("目录重命名成功");
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                progress?.Report("权限不足，尝试修改文件属性...");
                return SafeRenameWithAttributeFixing(sourcePath, targetPath, progress);
            }
            catch (Exception ex)
            {
                progress?.Report($"重命名目录异常：{ex.Message}");
                return false;
            }
        }

        private bool SafeRenameWithAttributeFixing(string sourcePath, string targetPath, IProgress<string>? progress)
        {
            try
            {
                // 递归移除只读属性
                RemoveReadOnlyAttributes(sourcePath, progress);

                // 再次尝试重命名
                Directory.Move(sourcePath, targetPath);

                progress?.Report("目录重命名成功（已修复属性）");
                return true;
            }
            catch (Exception ex)
            {
                progress?.Report($"修复属性后重命名仍失败：{ex.Message}");
                return false;
            }
        }

        private void RemoveReadOnlyAttributes(string path, IProgress<string>? progress)
        {
            try
            {
                var dirInfo = new DirectoryInfo(path);

                // 移除目录的只读属性
                if (dirInfo.Attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    dirInfo.Attributes &= ~FileAttributes.ReadOnly;
                }

                // 递归处理所有文件
                foreach (var file in dirInfo.GetFiles("*", SearchOption.AllDirectories))
                {
                    if (file.Attributes.HasFlag(FileAttributes.ReadOnly))
                    {
                        file.Attributes &= ~FileAttributes.ReadOnly;
                    }
                }

                // 递归处理所有子目录
                foreach (var subDir in dirInfo.GetDirectories("*", SearchOption.AllDirectories))
                {
                    if (subDir.Attributes.HasFlag(FileAttributes.ReadOnly))
                    {
                        subDir.Attributes &= ~FileAttributes.ReadOnly;
                    }
                }
            }
            catch (Exception ex)
            {
                progress?.Report($"修复文件属性时出错：{ex.Message}");
            }
        }
    }
}