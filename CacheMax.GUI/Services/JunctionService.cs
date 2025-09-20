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
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint FILE_SHARE_DELETE = 0x00000004;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
        private const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
        private const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;
        private const uint FSCTL_DELETE_REPARSE_POINT = 0x000900AC;
        private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
        private const ushort MAXIMUM_REPARSE_DATA_BUFFER_SIZE = 16384;

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetFileAttributes(string lpFileName);

        [StructLayout(LayoutKind.Sequential)]
        private struct REPARSE_DATA_BUFFER
        {
            public uint ReparseTag;
            public ushort ReparseDataLength;
            public ushort Reserved;
            public ushort SubstituteNameOffset;
            public ushort SubstituteNameLength;
            public ushort PrintNameOffset;
            public ushort PrintNameLength;
        }

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

                // 创建Junction目录
                Directory.CreateDirectory(junctionPath);

                // 打开Junction目录句柄
                IntPtr handle = CreateFile(
                    junctionPath,
                    GENERIC_READ | GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
                    IntPtr.Zero);

                if (handle == new IntPtr(-1))
                {
                    progress?.Report($"错误：无法打开Junction目录：{Marshal.GetLastWin32Error()}");
                    Directory.Delete(junctionPath);
                    return false;
                }

                try
                {
                    // 准备目标路径（必须是\\?\开头的格式）
                    string nativeTarget = @"\??\" + targetPath;
                    byte[] targetBytes = Encoding.Unicode.GetBytes(nativeTarget);

                    // 计算缓冲区大小
                    int bufferSize = Marshal.SizeOf<REPARSE_DATA_BUFFER>() + targetBytes.Length + targetBytes.Length + 12;
                    IntPtr buffer = Marshal.AllocHGlobal(bufferSize);

                    try
                    {
                        // 构建REPARSE_DATA_BUFFER
                        var reparseData = new REPARSE_DATA_BUFFER
                        {
                            ReparseTag = IO_REPARSE_TAG_MOUNT_POINT,
                            ReparseDataLength = (ushort)(targetBytes.Length + targetBytes.Length + 12),
                            Reserved = 0,
                            SubstituteNameOffset = 0,
                            SubstituteNameLength = (ushort)targetBytes.Length,
                            PrintNameOffset = (ushort)(targetBytes.Length + 2),
                            PrintNameLength = (ushort)targetBytes.Length
                        };

                        // 写入缓冲区
                        Marshal.StructureToPtr(reparseData, buffer, false);
                        IntPtr dataStart = new IntPtr(buffer.ToInt64() + Marshal.SizeOf<REPARSE_DATA_BUFFER>());

                        // 写入SubstituteName
                        Marshal.Copy(targetBytes, 0, dataStart, targetBytes.Length);

                        // 写入分隔符
                        Marshal.WriteInt16(dataStart, targetBytes.Length, 0);

                        // 写入PrintName
                        Marshal.Copy(targetBytes, 0, new IntPtr(dataStart.ToInt64() + targetBytes.Length + 2), targetBytes.Length);

                        // 写入结束符
                        Marshal.WriteInt16(dataStart, targetBytes.Length + targetBytes.Length + 2, 0);

                        // 调用DeviceIoControl设置重解析点
                        bool result = DeviceIoControl(
                            handle,
                            FSCTL_SET_REPARSE_POINT,
                            buffer,
                            (uint)bufferSize,
                            IntPtr.Zero,
                            0,
                            out _,
                            IntPtr.Zero);

                        if (!result)
                        {
                            int error = Marshal.GetLastWin32Error();
                            progress?.Report($"错误：设置重解析点失败：{error}");
                            return false;
                        }

                        progress?.Report("Junction创建成功");
                        return true;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
            catch (Exception ex)
            {
                progress?.Report($"创建Junction异常：{ex.Message}");

                // 清理失败的Junction目录
                try
                {
                    if (Directory.Exists(junctionPath))
                    {
                        Directory.Delete(junctionPath);
                    }
                }
                catch { }

                return false;
            }
        }

        /// <summary>
        /// 删除Junction
        /// </summary>
        public bool RemoveJunction(string junctionPath, IProgress<string>? progress = null)
        {
            try
            {
                progress?.Report($"删除Junction：{junctionPath}");

                if (!IsJunction(junctionPath))
                {
                    progress?.Report($"路径不是Junction：{junctionPath}");
                    return false;
                }

                // 打开Junction目录句柄
                IntPtr handle = CreateFile(
                    junctionPath,
                    GENERIC_READ | GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
                    IntPtr.Zero);

                if (handle == new IntPtr(-1))
                {
                    progress?.Report($"错误：无法打开Junction目录：{Marshal.GetLastWin32Error()}");
                    return false;
                }

                try
                {
                    // 准备删除重解析点的数据
                    var deleteData = new REPARSE_DATA_BUFFER
                    {
                        ReparseTag = IO_REPARSE_TAG_MOUNT_POINT,
                        ReparseDataLength = 0,
                        Reserved = 0
                    };

                    IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<REPARSE_DATA_BUFFER>());
                    try
                    {
                        Marshal.StructureToPtr(deleteData, buffer, false);

                        // 删除重解析点
                        bool result = DeviceIoControl(
                            handle,
                            FSCTL_DELETE_REPARSE_POINT,
                            buffer,
                            (uint)Marshal.SizeOf<REPARSE_DATA_BUFFER>(),
                            IntPtr.Zero,
                            0,
                            out _,
                            IntPtr.Zero);

                        if (!result)
                        {
                            int error = Marshal.GetLastWin32Error();
                            progress?.Report($"错误：删除重解析点失败：{error}");
                            return false;
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
                finally
                {
                    CloseHandle(handle);
                }

                // 删除目录
                Directory.Delete(junctionPath);
                progress?.Report("Junction删除成功");
                return true;
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
                uint attributes = GetFileAttributes(path);
                if (attributes == 0xFFFFFFFF) // INVALID_FILE_ATTRIBUTES
                    return false;

                // 检查是否有重解析点属性
                return (attributes & 0x400) != 0; // FILE_ATTRIBUTE_REPARSE_POINT
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
            if (!IsJunction(junctionPath))
                return null;

            try
            {
                IntPtr handle = CreateFile(
                    junctionPath,
                    GENERIC_READ,
                    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
                    IntPtr.Zero);

                if (handle == new IntPtr(-1))
                    return null;

                try
                {
                    IntPtr buffer = Marshal.AllocHGlobal(MAXIMUM_REPARSE_DATA_BUFFER_SIZE);
                    try
                    {
                        bool result = DeviceIoControl(
                            handle,
                            FSCTL_GET_REPARSE_POINT,
                            IntPtr.Zero,
                            0,
                            buffer,
                            MAXIMUM_REPARSE_DATA_BUFFER_SIZE,
                            out uint bytesReturned,
                            IntPtr.Zero);

                        if (!result)
                            return null;

                        var reparseData = Marshal.PtrToStructure<REPARSE_DATA_BUFFER>(buffer);
                        if (reparseData.ReparseTag != IO_REPARSE_TAG_MOUNT_POINT)
                            return null;

                        IntPtr dataStart = new IntPtr(buffer.ToInt64() + Marshal.SizeOf<REPARSE_DATA_BUFFER>());
                        IntPtr printNameStart = new IntPtr(dataStart.ToInt64() + reparseData.PrintNameOffset);

                        string targetPath = Marshal.PtrToStringUni(printNameStart, reparseData.PrintNameLength / 2);

                        // 移除\\?\前缀
                        if (targetPath?.StartsWith(@"\??\") == true)
                        {
                            targetPath = targetPath.Substring(4);
                        }

                        return targetPath;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 验证Junction完整性
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
                if (actualTarget == null)
                {
                    progress?.Report($"无法读取Junction目标：{junctionPath}");
                    return false;
                }

                string normalizedExpected = Path.GetFullPath(expectedTarget);
                string normalizedActual = Path.GetFullPath(actualTarget);

                if (!string.Equals(normalizedExpected, normalizedActual, StringComparison.OrdinalIgnoreCase))
                {
                    progress?.Report($"Junction目标不匹配：期望 {normalizedExpected}，实际 {normalizedActual}");
                    return false;
                }

                if (!Directory.Exists(actualTarget))
                {
                    progress?.Report($"Junction目标目录不存在：{actualTarget}");
                    return false;
                }

                progress?.Report("Junction验证成功");
                return true;
            }
            catch (Exception ex)
            {
                progress?.Report($"验证Junction异常：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 安全重命名目录（避免冲突）
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
                progress?.Report($"重命名目录失败：{ex.Message}");
                return false;
            }
        }
    }
}