using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace CacheMax.GUI
{
    public partial class ProgressWindow : Window
    {
        private CancellationTokenSource _cancellationTokenSource;
        private bool _isCompleted = false;

        public CancellationToken CancellationToken => _cancellationTokenSource.Token;

        public ProgressWindow()
        {
            InitializeComponent();
            _cancellationTokenSource = new CancellationTokenSource();
        }

        /// <summary>
        /// 更新当前操作
        /// </summary>
        public void UpdateCurrentOperation(string operation)
        {
            Dispatcher.Invoke(() =>
            {
                CurrentOperationText.Text = operation;
            });
        }

        /// <summary>
        /// 添加日志消息
        /// </summary>
        public void AddLogMessage(string message)
        {
            Dispatcher.Invoke(() =>
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss");
                var logEntry = $"[{timestamp}] {message}\n";

                LogTextBlock.Text += logEntry;

                // 自动滚动到底部
                LogScrollViewer.ScrollToEnd();
            });
        }

        /// <summary>
        /// 设置进度条进度（0-100）
        /// </summary>
        public void SetProgress(double percentage)
        {
            Dispatcher.Invoke(() =>
            {
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = percentage;
            });
        }

        /// <summary>
        /// 设置为不确定进度
        /// </summary>
        public void SetIndeterminate()
        {
            Dispatcher.Invoke(() =>
            {
                ProgressBar.IsIndeterminate = true;
            });
        }

        /// <summary>
        /// 操作完成
        /// </summary>
        public void OperationCompleted(bool success)
        {
            Dispatcher.Invoke(() =>
            {
                _isCompleted = true;

                if (success)
                {
                    CurrentOperationText.Text = "✅ 缓存初始化完成！";
                    ProgressBar.IsIndeterminate = false;
                    ProgressBar.Value = 100;
                    AddLogMessage("🎉 缓存初始化成功完成！");
                }
                else
                {
                    CurrentOperationText.Text = "❌ 缓存初始化失败";
                    ProgressBar.IsIndeterminate = false;
                    AddLogMessage("❌ 缓存初始化失败，请检查日志");
                }

                CancelButton.IsEnabled = false;
                CloseButton.IsEnabled = true;
                CloseButton.Focus();
            });
        }

        /// <summary>
        /// 取消按钮点击
        /// </summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isCompleted)
            {
                var result = MessageBox.Show("确定要取消缓存初始化吗？\n\n这将中断当前操作，可能导致不完整的缓存。",
                    "确认取消", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    _cancellationTokenSource.Cancel();
                    AddLogMessage("⚠️ 用户取消操作");
                    CurrentOperationText.Text = "正在取消操作...";
                    CancelButton.IsEnabled = false;
                }
            }
        }

        /// <summary>
        /// 关闭按钮点击
        /// </summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = _isCompleted;
            Close();
        }

        /// <summary>
        /// 窗口关闭事件
        /// </summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_isCompleted && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                var result = MessageBox.Show("缓存初始化正在进行中，确定要关闭吗？",
                    "确认关闭", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result == MessageBoxResult.No)
                {
                    e.Cancel = true;
                    return;
                }

                _cancellationTokenSource.Cancel();
            }

            base.OnClosing(e);
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            _cancellationTokenSource?.Dispose();
            base.OnClosed(e);
        }
    }
}