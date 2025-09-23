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

            // 处理单实例逻辑
            HandleSingleInstance(e);
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
        /// 处理单实例逻辑
        /// </summary>
        private void HandleSingleInstance(StartupEventArgs e)
        {
            // 创建单实例管理器
            _singleInstanceManager = new SingleInstanceManager("CacheMax_v3.0");

            // 检查是否为主实例（Release版本才检查，Debug版本总是允许）
            if (!_singleInstanceManager.TryStartAsPrimaryInstance())
            {
                // 已有实例在运行，通知现有实例并退出
                HandleExistingInstance(e.Args);
                return;
            }

            // 正常启动应用程序
            base.OnStartup(e);
        }

        /// <summary>
        /// 处理已存在实例的情况
        /// </summary>
        private void HandleExistingInstance(string[] args)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    // 尝试通知已存在的实例
                    var notified = await _singleInstanceManager!.NotifyExistingInstance(args);

                    // 在UI线程显示消息并退出
                    Dispatcher.Invoke(() =>
                    {
                        var message = notified
                            ? "CacheMax 已在运行中！\n\n已激活现有窗口，请查看。"
                            : "CacheMax 已在运行中！\n\n请检查系统托盘或任务管理器。";

                        MessageBox.Show(
                            message,
                            "程序已启动",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);

                        Shutdown();
                    });
                }
                catch (Exception ex)
                {
                    // 异常情况下也要退出
                    Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show(
                            $"检测到程序已在运行，但通信失败。\n\n错误详情：{ex.Message}\n\n请手动关闭已运行的实例后重试。",
                            "启动失败",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);

                        Shutdown();
                    });
                }
            });
        }

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