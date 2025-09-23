using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace CacheMax.GUI.Services
{
    /// <summary>
    /// 单实例管理器 - 防止多个Release版本同时运行
    /// Debug版本允许多实例，Release版本强制单实例
    /// </summary>
    public class SingleInstanceManager : IDisposable
    {
        private readonly string _pipeName;
        private readonly string _mutexName;
        private NamedPipeServerStream? _pipeServer;
        private Mutex? _mutex;
        private readonly CancellationTokenSource _cancellation;
        private bool _disposed = false;

        public SingleInstanceManager(string applicationId)
        {
            // 使用固定的标识符，确保不同路径启动的CacheMax都被识别为同一应用
            var fixedAppId = "CacheMax_Global";
            _pipeName = $"CacheMax_Pipe_{fixedAppId}_{Environment.UserName}";
            _mutexName = $"CacheMax_Mutex_{fixedAppId}_{Environment.UserName}";
            _cancellation = new CancellationTokenSource();
        }

        /// <summary>
        /// 尝试启动为主实例
        /// </summary>
        /// <returns>如果是主实例返回true，否则返回false</returns>
        public bool TryStartAsPrimaryInstance()
        {
#if DEBUG
            // Debug版本总是允许启动多实例
            return true;
#else
            try
            {
                // 1. 首先尝试创建互斥量
                bool createdNew;
                _mutex = new Mutex(true, _mutexName, out createdNew);

                if (!createdNew)
                {
                    // 互斥量已存在，说明有其他实例
                    return false;
                }

                // 2. 创建命名管道服务器用于通信
                _pipeServer = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1, // 只允许一个连接
                    PipeTransmissionMode.Message,
                    PipeOptions.Asynchronous);

                // 3. 启动管道监听任务
                _ = Task.Run(() => ListenForMessages(_cancellation.Token));

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"创建单实例控制失败: {ex.Message}");
                return false;
            }
#endif
        }

        /// <summary>
        /// 通知已存在的实例并传递启动参数
        /// </summary>
        /// <param name="args">启动参数</param>
        /// <returns>是否成功通知</returns>
        public async Task<bool> NotifyExistingInstance(string[] args)
        {
#if DEBUG
            return false; // Debug版本不需要通知
#else
            try
            {
                using var client = new NamedPipeClientStream(
                    ".",
                    _pipeName,
                    PipeDirection.Out,
                    PipeOptions.None);

                // 尝试连接到已存在的实例
                await client.ConnectAsync(3000); // 3秒超时

                using var writer = new StreamWriter(client) { AutoFlush = true };

                // 发送激活命令
                await writer.WriteLineAsync("ACTIVATE");
                await writer.WriteLineAsync(string.Join("|", args)); // 使用|分隔参数
                await writer.WriteLineAsync("END"); // 结束标记

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"通知已存在实例失败: {ex.Message}");
                return false;
            }
#endif
        }

        /// <summary>
        /// 监听来自其他实例的消息
        /// </summary>
        private async Task ListenForMessages(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _pipeServer != null)
            {
                try
                {
                    // 等待客户端连接
                    await _pipeServer.WaitForConnectionAsync(cancellationToken);

                    using var reader = new StreamReader(_pipeServer);

                    // 读取命令
                    var command = await reader.ReadLineAsync();
                    if (command == "ACTIVATE")
                    {
                        // 读取参数
                        var arguments = await reader.ReadLineAsync();
                        var endMarker = await reader.ReadLineAsync();

                        if (endMarker == "END")
                        {
                            // 解析参数
                            var args = string.IsNullOrEmpty(arguments)
                                ? new string[0]
                                : arguments.Split('|');

                            // 在UI线程上激活窗口
                            await Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                ActivateMainWindow(args);
                            });
                        }
                    }

                    // 断开连接准备下一个连接
                    _pipeServer.Disconnect();
                }
                catch (OperationCanceledException)
                {
                    // 正常取消，退出循环
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"管道通信异常: {ex.Message}");

                    // 尝试重新创建管道服务器
                    try
                    {
                        _pipeServer?.Dispose();
                        _pipeServer = new NamedPipeServerStream(
                            _pipeName,
                            PipeDirection.InOut,
                            1,
                            PipeTransmissionMode.Message,
                            PipeOptions.Asynchronous);
                    }
                    catch (Exception recreateEx)
                    {
                        Debug.WriteLine($"重新创建管道失败: {recreateEx.Message}");
                        break; // 无法恢复，退出监听
                    }
                }
            }
        }

        /// <summary>
        /// 激活主窗口
        /// </summary>
        private void ActivateMainWindow(string[] args)
        {
            try
            {
                var mainWindow = Application.Current.MainWindow;
                if (mainWindow != null)
                {
                    // 如果窗口最小化，先恢复
                    if (mainWindow.WindowState == WindowState.Minimized)
                    {
                        mainWindow.WindowState = WindowState.Normal;
                    }

                    // 显示窗口
                    mainWindow.Show();

                    // 激活窗口
                    mainWindow.Activate();

                    // 确保窗口在最前面
                    mainWindow.Topmost = true;
                    mainWindow.Topmost = false;

                    // 让窗口闪烁提醒用户
                    mainWindow.FlashWindow();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"激活主窗口失败: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;

                try
                {
                    _cancellation.Cancel();
                }
                catch { }

                try
                {
                    _pipeServer?.Dispose();
                }
                catch { }

                try
                {
                    _mutex?.ReleaseMutex();
                    _mutex?.Dispose();
                }
                catch { }

                try
                {
                    _cancellation.Dispose();
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// 窗口闪烁扩展方法
    /// </summary>
    public static class WindowExtensions
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool FlashWindow(IntPtr hWnd, bool bInvert);

        public static void FlashWindow(this Window window)
        {
            try
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(window);
                FlashWindow(helper.Handle, true);
            }
            catch { }
        }
    }
}