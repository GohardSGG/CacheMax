using System;
using System.Threading.Tasks;
using System.Windows;
using CacheMax.GUI.Services;

namespace CacheMax.GUI
{
    public partial class App : Application
    {
        private SingleInstanceManager? _singleInstanceManager;

        protected override void OnStartup(StartupEventArgs e)
        {
            // 设置全局异常处理
            SetupGlobalExceptionHandling();

            // 创建单实例管理器并检查 - 必须在UI初始化之前进行
            _singleInstanceManager = new SingleInstanceManager("CacheMax_v3.0");

            // 检查是否为主实例（Release版本才检查，Debug版本总是允许）
            if (!_singleInstanceManager.TryStartAsPrimaryInstance())
            {
                // 已有实例在运行，通知现有实例并退出（不启动UI）
                HandleExistingInstanceAndExit(e.Args);
                return; // 直接返回，不调用base.OnStartup
            }

            // 只有主实例才启动UI
            base.OnStartup(e);
        }

        /// <summary>
        /// 设置全局异常处理
        /// </summary>
        private void SetupGlobalExceptionHandling()
        {
            // 应用程序域未处理异常
            AppDomain.CurrentDomain.UnhandledException += (sender, ex) =>
            {
                var exception = ex.ExceptionObject as Exception;
                MessageBox.Show(
                    $"应用程序发生未处理异常：\n\n{exception?.Message}\n\n程序将退出。",
                    "严重错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            };

            // UI线程未处理异常
            DispatcherUnhandledException += (sender, ex) =>
            {
                MessageBox.Show(
                    $"UI线程发生未处理异常：\n\n{ex.Exception.Message}\n\n程序将继续运行，但可能不稳定。",
                    "UI异常",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                ex.Handled = true; // 标记异常已处理，防止程序崩溃
            };
        }

        /// <summary>
        /// 处理已存在实例的情况并退出
        /// </summary>
        private void HandleExistingInstanceAndExit(string[] args)
        {
            try
            {
                // 尝试通知已存在的实例（同步方式，避免UI未初始化的问题）
                var task = _singleInstanceManager!.NotifyExistingInstance(args);
                task.Wait(5000); // 最多等待5秒

                var notified = task.IsCompletedSuccessfully && task.Result;

                var message = notified
                    ? "CacheMax 已在运行中！\n\n已激活现有窗口，请查看。"
                    : "CacheMax 已在运行中！\n\n请检查系统托盘或任务管理器。";

                // 使用Win32 API显示消息，避免创建窗口句柄和任务栏图标
                ShowMessageWithoutTaskbarIcon(message, "程序已启动", notified ? 0x40u : 0x30u); // 0x40=Info, 0x30=Warning
            }
            catch (Exception ex)
            {
                // 异常情况也使用相同方式显示消息
                ShowMessageWithoutTaskbarIcon(
                    $"检测到程序已在运行，但通信失败。\n\n错误详情：{ex.Message}\n\n请手动关闭已运行的实例后重试。",
                    "启动失败",
                    0x30u); // Warning
            }
            finally
            {
                // 直接退出应用程序，不启动UI
                Environment.Exit(0);
            }
        }

        /// <summary>
        /// 使用Win32 API显示消息框，避免在任务栏创建图标
        /// </summary>
        private void ShowMessageWithoutTaskbarIcon(string message, string title, uint type)
        {
            try
            {
                // 使用user32.dll的MessageBox，不会在任务栏留下痕迹
                MessageBoxW(IntPtr.Zero, message, title, type);
            }
            catch
            {
                // 如果Win32调用失败，直接退出（静默失败比留下任务栏图标更好）
            }
        }

        /// <summary>
        /// Win32 API MessageBox声明
        /// </summary>
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                // 清理单实例管理器
                _singleInstanceManager?.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"清理单实例管理器时发生异常: {ex.Message}");
            }

            base.OnExit(e);
        }
    }
}