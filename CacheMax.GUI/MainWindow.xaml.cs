using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Input;
using CacheMax.GUI.Services;
using CacheMax.GUI.ViewModels;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using WinForms = System.Windows.Forms;
using System.Drawing;

namespace CacheMax.GUI
{
    public partial class MainWindow : Window
    {
        private readonly FastCopyService _fastCopy;
        private readonly CacheManagerService _cacheManager;
        private readonly ConfigService _config;
        private readonly ObservableCollection<AcceleratedFolder> _acceleratedFolders;
        private readonly ObservableCollection<SyncQueueItemViewModel> _syncQueueItems;
        private readonly ObservableCollection<SyncQueueItemViewModel> _completedItems;
        private WinForms.NotifyIcon? _notifyIcon;

        public MainWindow()
        {
            InitializeComponent();

#if DEBUG
            // Debug版本在标题栏添加"调试"标识
            this.Title = "CacheMax - File System Accelerator [调试]";
#endif

            _fastCopy = FastCopyService.Instance;
            _cacheManager = new CacheManagerService();
            _config = new ConfigService();

            // 订阅缓存管理器事件
            _cacheManager.LogMessage += (sender, message) => AddLog(message);
            _cacheManager.StatsUpdated += OnCacheStatsUpdated;

            // 初始化集合
            _acceleratedFolders = new ObservableCollection<AcceleratedFolder>();
            _syncQueueItems = new ObservableCollection<SyncQueueItemViewModel>();
            _completedItems = new ObservableCollection<SyncQueueItemViewModel>();

            // 绑定数据源
            AcceleratedFoldersGrid.ItemsSource = _acceleratedFolders;
            SyncQueueGrid.ItemsSource = _syncQueueItems;
            CompletedQueueGrid.ItemsSource = _completedItems;

            // 订阅同步队列事件
            _cacheManager.FileSyncService.QueueItemAdded += OnQueueItemAdded;
            _cacheManager.FileSyncService.QueueItemUpdated += OnQueueItemUpdated;
            _cacheManager.FileSyncService.QueueItemRemoved += OnQueueItemRemoved;
            _cacheManager.FileSyncService.SyncFailed += OnSyncFailed;
            CacheRootTextBox.Text = _config.Config.DefaultCacheRoot;

            LoadExistingAccelerations();
            UpdateUI();
        }

        private void LoadExistingAccelerations()
        {
            _acceleratedFolders.Clear();

            // 恢复加速状态到错误恢复服务
            _cacheManager.RestoreAccelerationStates(_config.Config.AcceleratedFolders);

            foreach (var folder in _config.Config.AcceleratedFolders)
            {
                // RestoreAccelerationStates已经正确设置了状态，这里只需要设置进度条
                if (folder.Status == "已加速")
                {
                    folder.ProgressPercentage = 100.0;
                }
                else
                {
                    folder.ProgressPercentage = 0.0;
                }

                _acceleratedFolders.Add(folder);
            }

            // 初始化系统托盘
            InitializeSystemTray();
        }

        private async void AccelerateButton_Click(object sender, RoutedEventArgs e)
        {
            var cacheRoot = CacheRootTextBox.Text.Trim();

            if (string.IsNullOrEmpty(cacheRoot))
            {
                MessageBox.Show("请指定缓存根目录", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 获取选中的项目或所有未加速的文件夹
            var selectedItems = AcceleratedFoldersGrid.SelectedItems.Cast<AcceleratedFolder>().ToList();
            var targetFolders = selectedItems.Any()
                ? selectedItems.Where(f => f.Status == "未加速").ToList()
                : _acceleratedFolders.Where(f => f.Status == "未加速").ToList();

            if (targetFolders.Count == 0)
            {
                var message = selectedItems.Any()
                    ? "选中的项目中没有需要加速的文件夹。"
                    : "没有需要加速的文件夹。请先添加路径。";
                MessageBox.Show(message, "信息", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirmMessage = selectedItems.Any()
                ? $"将对选中的 {targetFolders.Count} 个文件夹进行加速。\n\n是否开始加速？"
                : $"检测到 {targetFolders.Count} 个未加速的文件夹。\n\n是否开始批量加速？";

            var result = MessageBox.Show(confirmMessage, "确认加速", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                AccelerateButton.IsEnabled = false;
                UpdateStatus("开始批量加速...");

                foreach (var folder in targetFolders)
                {
                    try
                    {
                        // 更新状态为初始化中
                        folder.Status = "初始化中";
                        folder.ProgressPercentage = 0;

                        AddLog($"开始加速：{folder.OriginalPath}");

                        // 进度报告器 - 更新表格中的进度
                        var progress = new Progress<string>(msg =>
                        {
                            AddLog(msg);

                            // 只解析可靠的步骤进度信息
                            if (msg.Contains("步骤"))
                            {
                                if (msg.Contains("1/4")) folder.ProgressPercentage = 25;
                                else if (msg.Contains("2/4")) folder.ProgressPercentage = 50;
                                else if (msg.Contains("3/4")) folder.ProgressPercentage = 75;
                                else if (msg.Contains("4/4")) folder.ProgressPercentage = 100;
                            }
                            // 删除了不可靠的Robocopy百分比解析
                        });

                        // 使用默认同步设置
                        var syncMode = SyncMode.Immediate;
                        var syncDelay = 3;

                        bool initSuccess = await _cacheManager.InitializeCacheAcceleration(
                            folder.MountPoint, cacheRoot, syncMode, syncDelay, progress);

                        if (initSuccess)
                        {
                            folder.Status = "已加速";
                            folder.ProgressPercentage = 100;

                            // 正确计算缓存路径（与CacheManagerService逻辑完全一致）
                            var driveLetter = Path.GetPathRoot(folder.MountPoint)?.Replace(":", "").Replace("\\", "") ?? "Unknown";
                            var driveSpecificCacheRoot = Path.Combine(cacheRoot, driveLetter);
                            var pathWithoutDrive = folder.MountPoint.Substring(Path.GetPathRoot(folder.MountPoint)?.Length ?? 0);
                            var cachePath = Path.Combine(driveSpecificCacheRoot, pathWithoutDrive);

                            // 正确设置所有路径
                            folder.CachePath = cachePath;
                            folder.OriginalPath = folder.MountPoint + ".original"; // 这是关键修复！
                            folder.CacheSize = GetDirectorySize(cachePath);

                            // 现在保存完整正确的配置到文件
                            _config.AddAcceleratedFolder(folder);
                            _config.SaveConfig(); // 立即保存配置

                            AddLog($"✅ 加速完成：{folder.MountPoint}");
                            AddLog($"原始备份：{folder.OriginalPath}");
                            AddLog($"缓存路径：{cachePath}");

                            // 立即更新界面显示
                            UpdateUI();
                        }
                        else
                        {
                            folder.Status = "失败";
                            folder.ProgressPercentage = 0;
                            AddLog($"❌ 加速失败：{folder.OriginalPath}");

                            // 立即更新界面显示失败状态
                            UpdateUI();
                        }
                    }
                    catch (Exception ex)
                    {
                        folder.Status = "失败";
                        folder.ProgressPercentage = 0;
                        AddLog($"❌ 加速异常：{folder.OriginalPath} - {ex.Message}");

                        // 立即更新界面显示异常状态
                        UpdateUI();
                    }
                }

                // 保存配置
                _config.SaveConfig();

                var successCount = targetFolders.Count(f => f.Status == "已加速");
                var failedCount = targetFolders.Count(f => f.Status == "失败");

                UpdateStatus($"批量加速完成：成功 {successCount} 个，失败 {failedCount} 个");

                MessageBox.Show(
                    $"批量加速完成！\n\n" +
                    $"✅ 成功：{successCount} 个文件夹\n" +
                    $"❌ 失败：{failedCount} 个文件夹\n\n" +
                    $"成功加速的文件夹现在已启用高速缓存！",
                    "批量加速完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AddLog($"批量加速错误：{ex.Message}");
                UpdateStatus($"批量加速失败：{ex.Message}");
                MessageBox.Show($"批量加速失败：\n{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                AccelerateButton.IsEnabled = true;
                UpdateUI();
            }
        }

        private async void StopButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = AcceleratedFoldersGrid.SelectedItems.Cast<AcceleratedFolder>().ToList();
            var targetItems = selectedItems.Any()
                ? selectedItems.Where(f => f.Status == "已加速").ToList()
                : _acceleratedFolders.Where(f => f.Status == "已加速").ToList();

            if (targetItems.Count == 0)
            {
                var message = selectedItems.Any()
                    ? "选中的项目中没有可以暂停的加速项目。"
                    : "没有可以暂停的加速项目。";
                MessageBox.Show(message, "信息", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 重要警告：文件句柄风险
            var warningMessage = targetItems.Count == 1
                ? $"⚠️ 警告：即将暂停 {targetItems[0].MountPoint} 的加速\n\n" +
                  "暂停操作将：\n" +
                  "• 移除目录连接点\n" +
                  "• 恢复原始文件夹\n" +
                  "• 保留所有配置和缓存文件\n\n" +
                  "⚠️ 重要：如果有程序正在使用该目录中的文件，突然中断可能导致数据丢失！\n" +
                  "请确保没有程序正在访问该目录。\n\n" +
                  "确定要继续吗？"
                : $"⚠️ 警告：即将暂停 {targetItems.Count} 个项目的加速\n\n" +
                  "暂停操作将：\n" +
                  "• 移除目录连接点\n" +
                  "• 恢复原始文件夹\n" +
                  "• 保留所有配置和缓存文件\n\n" +
                  "⚠️ 重要：如果有程序正在使用这些目录中的文件，突然中断可能导致数据丢失！\n" +
                  "请确保没有程序正在访问这些目录。\n\n" +
                  "确定要继续吗？";

            var result = MessageBox.Show(warningMessage, "暂停确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                StopButton.IsEnabled = false;
                UpdateStatus($"正在暂停 {targetItems.Count} 个项目的加速...");

                var progress = new Progress<string>(msg => AddLog(msg));
                int successCount = 0;
                int failCount = 0;

                foreach (var item in targetItems)
                {
                    try
                    {
                        AddLog($"暂停加速：{item.MountPoint}");
                        item.Status = "暂停中";
                        item.ProgressPercentage = 0;

                        // 使用新的暂停方法（不删除配置，不删除缓存）
                        if (await _cacheManager.PauseCacheAcceleration(
                            item.MountPoint,
                            item.OriginalPath,
                            item.CachePath,
                            progress))
                        {
                            // 暂停成功，状态变为未加速（这样可以重新开始加速）
                            item.Status = "未加速";
                            item.ProgressPercentage = 0;
                            successCount++;
                            AddLog($"✅ 暂停成功：{item.MountPoint}");
                        }
                        else
                        {
                            item.Status = "暂停失败";
                            failCount++;
                            AddLog($"❌ 暂停失败：{item.MountPoint}");
                        }
                    }
                    catch (Exception ex)
                    {
                        item.Status = "暂停失败";
                        failCount++;
                        AddLog($"❌ 暂停异常：{item.MountPoint} - {ex.Message}");
                    }
                }

                UpdateStatus($"暂停完成：成功 {successCount} 个，失败 {failCount} 个");

                var message = failCount == 0
                    ? $"暂停完成！\n\n成功暂停 {successCount} 个项目。\n这些项目已恢复为普通文件夹，可以重新加速。"
                    : $"暂停完成！\n\n成功：{successCount} 个\n失败：{failCount} 个\n\n暂停成功的项目已恢复为普通文件夹，可以重新加速。";

                MessageBox.Show(message, "暂停完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AddLog($"错误：{ex.Message}");
                UpdateStatus($"停止加速失败：{ex.Message}");
                MessageBox.Show($"停止加速失败：\n{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                StopButton.IsEnabled = true;
                UpdateUI();
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            LoadExistingAccelerations();
            UpdateUI();
            UpdateStatus("Refreshed acceleration status");
        }

        private async void TestMountButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 获取缓存根路径
                var cacheRoot = CacheRootTextBox.Text?.Trim();
                if (string.IsNullOrEmpty(cacheRoot))
                {
                    MessageBox.Show("请指定缓存根目录", "输入必填",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!Directory.Exists(cacheRoot))
                {
                    var create = MessageBox.Show($"缓存目录 '{cacheRoot}' 不存在，是否创建？",
                        "创建目录", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (create == MessageBoxResult.Yes)
                    {
                        Directory.CreateDirectory(cacheRoot);
                        AddLog($"已创建缓存目录：{cacheRoot}");
                    }
                    else
                    {
                        return;
                    }
                }

                // 测试缓存目录访问性能
                AddLog($"测试缓存目录性能：{cacheRoot}");
                UpdateStatus($"正在测试缓存目录性能...");

                // 执行简单的读写性能测试
                await Task.Run(() =>
                {
                    try
                    {
                        var testFile = Path.Combine(cacheRoot, "performance_test.tmp");
                        var testData = new byte[1024 * 1024]; // 1MB 测试数据
                        new Random().NextBytes(testData);

                        // 写入测试
                        var startTime = DateTime.Now;
                        File.WriteAllBytes(testFile, testData);
                        var writeTime = (DateTime.Now - startTime).TotalMilliseconds;

                        // 读取测试
                        startTime = DateTime.Now;
                        var readData = File.ReadAllBytes(testFile);
                        var readTime = (DateTime.Now - startTime).TotalMilliseconds;

                        // 清理测试文件
                        File.Delete(testFile);

                        var writeSpeed = 1024.0 / writeTime * 1000; // MB/s
                        var readSpeed = 1024.0 / readTime * 1000; // MB/s

                        AddLog($"缓存目录性能测试结果：");
                        AddLog($"  写入速度：{writeSpeed:F1} MB/s");
                        AddLog($"  读取速度：{readSpeed:F1} MB/s");

                        if (readSpeed > 800)
                        {
                            AddLog("✅ 缓存目录性能优秀！");
                        }
                        else if (readSpeed > 300)
                        {
                            AddLog("⚠️ 缓存目录性能良好");
                        }
                        else
                        {
                            AddLog("❌ 缓存目录性能较低，请考虑使用更快的存储");
                        }
                    }
                    catch (Exception ex)
                    {
                        AddLog($"性能测试失败：{ex.Message}");
                    }
                });

                UpdateStatus("缓存目录测试完成");
                MessageBox.Show($"缓存目录可用性测试完成！\n\n目录：{cacheRoot}\n\n请查看日志了解详细性能信息。",
                    "测试完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AddLog($"测试缓存目录时出错：{ex.Message}");
                UpdateStatus($"缓存目录测试错误：{ex.Message}");
                MessageBox.Show($"测试缓存目录时出错：\n{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                UpdateUI();
            }
        }


        private void BrowseCacheButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select cache root directory"
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                CacheRootTextBox.Text = dialog.SelectedPath;
                _config.Config.DefaultCacheRoot = dialog.SelectedPath;
                _config.SaveConfig();
            }
        }

        private void AcceleratedFoldersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateUI();
        }


        private async void CleanCacheButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = AcceleratedFoldersGrid.SelectedItem as AcceleratedFolder;
            if (selected == null)
            {
                MessageBox.Show("请选择要清理缓存的文件夹", "信息",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var inputDialog = new InputDialog("清理缓存", "请输入要释放的空间大小(MB):", "100");
            if (inputDialog.ShowDialog() != true)
            {
                return;
            }

            if (!int.TryParse(inputDialog.InputText, out var targetMB) || targetMB <= 0)
            {
                MessageBox.Show("请输入有效的数字", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                UpdateStatus($"正在清理缓存：{selected.CachePath}");

                var progress = new Progress<string>(msg => AddLog(msg));
                var targetBytes = targetMB * 1024L * 1024L;

                if (await _cacheManager.CleanupCache(selected.CachePath, targetBytes, progress))
                {
                    AddLog($"缓存清理完成：{selected.CachePath}");
                    UpdateStatus("缓存清理完成");

                    // 更新缓存大小显示
                    selected.CacheSize = GetDirectorySize(selected.CachePath);

                    MessageBox.Show($"缓存清理完成！\n已释放约 {targetMB} MB 空间", "成功",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    AddLog($"缓存清理失败：{selected.CachePath}");
                    UpdateStatus("缓存清理失败");
                    MessageBox.Show("缓存清理失败，请查看日志了解详情", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                AddLog($"清理缓存异常：{ex.Message}");
                UpdateStatus($"清理缓存异常：{ex.Message}");
                MessageBox.Show($"清理缓存时发生异常：\n{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                UpdateUI();
            }
        }

        private void ValidateButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = AcceleratedFoldersGrid.SelectedItem as AcceleratedFolder;
            if (selected == null)
            {
                MessageBox.Show("请选择要检查链接状态的文件夹", "信息",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                ValidateButton.IsEnabled = false;
                UpdateStatus($"正在检查链接状态：{selected.MountPoint}");

                var progress = new Progress<string>(msg => AddLog(msg));

                if (_cacheManager.ValidateAcceleration(selected.MountPoint, selected.OriginalPath, selected.CachePath, progress))
                {
                    AddLog($"链接状态检查成功：{selected.MountPoint}");
                    UpdateStatus("链接状态正常");
                    MessageBox.Show("链接状态检查成功！所有目录和Junction配置正常。", "检查成功",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    AddLog($"链接状态检查失败：{selected.MountPoint}");
                    UpdateStatus("链接状态异常");
                    MessageBox.Show("链接状态检查发现问题，请查看日志了解详情", "检查失败",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                AddLog($"链接状态检查异常：{ex.Message}");
                UpdateStatus($"检查异常：{ex.Message}");
                MessageBox.Show($"检查链接状态时发生异常：\n{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ValidateButton.IsEnabled = true;
                UpdateUI();
            }
        }

        private async void CheckIntegrityButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                CheckIntegrityButton.IsEnabled = false;
                UpdateStatus("正在检查缓存完整性...");

                var progress = new Progress<string>(msg => AddLog(msg));
                await CheckCacheIntegrityAsync(progress);

                UpdateStatus("缓存完整性检查完成");
                UpdateUI();
            }
            catch (Exception ex)
            {
                AddLog($"检查缓存完整性异常：{ex.Message}");
                UpdateStatus($"检查异常：{ex.Message}");
                MessageBox.Show($"检查缓存完整性时发生异常：\n{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                CheckIntegrityButton.IsEnabled = true;
            }
        }

        private async void SyncAllButton_Click(object sender, RoutedEventArgs e)
        {
            var unsyncedItems = _acceleratedFolders.Where(f => f.Status == "未同步").ToList();
            if (!unsyncedItems.Any())
            {
                MessageBox.Show("没有未同步的项目", "信息",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show($"发现 {unsyncedItems.Count} 个未同步项目，确定要全部同步吗？\n\n这将用缓存覆盖原始目录中的差异文件。", "确认同步",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                SyncAllButton.IsEnabled = false;
                UpdateStatus("正在同步所有未同步项目...");

                var progress = new Progress<string>(msg => AddLog(msg));
                await SyncAllUnsyncedAsync(progress);

                UpdateStatus("批量同步完成");
                UpdateUI();
            }
            catch (Exception ex)
            {
                AddLog($"批量同步异常：{ex.Message}");
                UpdateStatus($"同步异常：{ex.Message}");
                MessageBox.Show($"批量同步时发生异常：\n{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SyncAllButton.IsEnabled = true;
            }
        }

        private void AcceleratedFoldersGrid_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // 检查是否点击了空白区域
            var element = e.OriginalSource as FrameworkElement;

            // 如果点击的不是DataGridRow或其子元素，则取消选择
            if (element != null)
            {
                var row = element.FindParent<DataGridRow>();
                if (row == null)
                {
                    AcceleratedFoldersGrid.UnselectAll();
                }
            }
        }

        private void UpdateUI()
        {
            var selectedItems = AcceleratedFoldersGrid.SelectedItems.Cast<AcceleratedFolder>().ToList();
            var hasSelection = selectedItems.Any();

            // 暂停按钮：有选中的可暂停项目 OR 无选择但有可暂停的项目
            var hasStoppableItems = hasSelection
                ? selectedItems.Any(f => f.Status == "已加速")
                : _acceleratedFolders.Any(f => f.Status == "已加速");
            StopButton.IsEnabled = hasStoppableItems;

            // 移除按钮：有选中项目 OR 无选择但有项目可移除
            var hasDeletableItems = hasSelection || _acceleratedFolders.Any();
            DeleteButton.IsEnabled = hasDeletableItems;

            // 控制按钮状态
            ValidateButton.IsEnabled = hasSelection;

            // 检查缓存完整性按钮：有已加速项目时启用
            var hasAcceleratedItems = _acceleratedFolders.Any(f => f.Status == "已加速");
            CheckIntegrityButton.IsEnabled = hasAcceleratedItems;

            // 同步所有未同步项按钮：有未同步项目时启用
            var hasUnsyncedItems = _acceleratedFolders.Any(f => f.Status == "未同步");
            SyncAllButton.IsEnabled = hasUnsyncedItems;

            var runningCount = _acceleratedFolders.Count(f => f.Status == "已加速");
            RunningCountText.Text = $"Running: {runningCount}";
        }



        private void UpdateStatus(string message)
        {
            StatusText.Text = message;
        }

        private void AddLog(string message)
        {

            // 同时写入到文件日志（统一日志输出）
            AsyncLogger.Instance.LogInfo(message, "MainWindow");

            Dispatcher.BeginInvoke(() =>
            {
                LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
                LogScrollViewer.ScrollToEnd();
            });
        }

        private long GetDirectorySize(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                    return 0;

                var dirInfo = new DirectoryInfo(path);
                return dirInfo.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
            }
            catch
            {
                return 0;
            }
        }

        private void OnCacheStatsUpdated(object? sender, CacheManagerService.CacheStatsEventArgs e)
        {
            // 在UI线程更新缓存统计信息
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    // 更新选中项的缓存大小
                    var existingItem = _acceleratedFolders.FirstOrDefault(f => f.CachePath == e.CachePath);
                    if (existingItem != null)
                    {
                        existingItem.CacheSize = e.TotalCacheSize;
                    }

                    // 如果有UI控件用于显示缓存统计，可以在这里更新
                    if (CacheStatsText != null)
                    {
                        CacheStatsText.Text = $"文件: {e.FileCount}, 大小: {FormatBytes(e.TotalCacheSize)}";
                    }

                    if (e.SyncQueueCount > 0)
                    {
                        var oldestText = e.OldestPendingSync?.ToString("HH:mm:ss") ?? "";
                        AddLog($"同步队列：{e.SyncQueueCount} 个文件待同步，最旧：{oldestText}");
                    }
                }
                catch (Exception ex)
                {
                    AddLog($"更新缓存统计时出错：{ex.Message}");
                }
            });
        }


        private static string FormatBytes(long bytes)
        {
            var sizes = new[] { "B", "KB", "MB", "GB", "TB" };
            var len = (double)bytes;
            var order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }


        private async void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = AcceleratedFoldersGrid.SelectedItems.Cast<AcceleratedFolder>().ToList();
            var targetItems = selectedItems.Any()
                ? selectedItems
                : _acceleratedFolders.ToList();

            if (targetItems.Count == 0)
            {
                MessageBox.Show("没有可以删除的项目。", "信息", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 检查是否有已加速的项目
            var acceleratedItems = targetItems.Where(f => f.Status == "已加速" || _cacheManager.IsAccelerated(f.MountPoint)).ToList();
            var normalItems = targetItems.Except(acceleratedItems).ToList();

            string message;
            if (targetItems.Count == 1)
            {
                var item = targetItems[0];
                var isAccelerated = acceleratedItems.Contains(item);
                if (isAccelerated)
                {
                    message = $"路径 '{item.OriginalPath}' 已被加速。\n\n" +
                             "删除将会：\n" +
                             "• 停止加速并恢复原始文件夹\n" +
                             "• 移除Junction链接\n" +
                             "• 清理缓存文件\n\n" +
                             "确定要继续吗？";
                }
                else
                {
                    message = $"确定要删除路径 '{item.OriginalPath}' 吗？";
                }
            }
            else
            {
                message = $"将删除选中的 {targetItems.Count} 个项目";
                if (acceleratedItems.Any())
                {
                    message += $"\n\n其中 {acceleratedItems.Count} 个已加速项目将被停止并恢复";
                }
                if (normalItems.Any())
                {
                    message += $"\n{normalItems.Count} 个未加速项目将被直接移除";
                }
                message += "\n\n确定要继续吗？";
            }

            // 使用自定义对话框询问是否删除缓存文件
            var dialog = new RemoveAccelerationDialog(message, targetItems.Count);
            var result = dialog.ShowDialog();

            if (result == true)
            {
                bool deleteCacheFiles = dialog.DeleteCacheFiles;

                try
                {
                    DeleteButton.IsEnabled = false;
                    UpdateStatus($"正在移除 {targetItems.Count} 个项目...");

                    var progress = new Progress<string>(msg => AddLog(msg));
                    int successCount = 0;
                    int failCount = 0;

                    foreach (var item in targetItems)
                    {
                        try
                        {
                            var isAccelerated = acceleratedItems.Contains(item);

                            if (isAccelerated)
                            {
                                // 执行完整的停止加速流程
                                AddLog($"正在停止加速：{item.OriginalPath}");
                                item.Status = "停止中";
                                item.ProgressPercentage = 0;

                                bool stopSuccess = await _cacheManager.StopCacheAcceleration(
                                    item.MountPoint,
                                    item.OriginalPath,
                                    item.CachePath,
                                    deleteCacheFiles, // 根据用户选择决定是否删除缓存文件
                                    progress);

                                if (!stopSuccess)
                                {
                                    AddLog($"⚠️ 停止加速失败：{item.OriginalPath}");
                                    failCount++;
                                    continue;
                                }
                                AddLog($"✅ 加速停止成功：{item.OriginalPath}");
                            }

                            // 从列表和配置中删除
                            _acceleratedFolders.Remove(item);
                            _config.RemoveAcceleratedFolder(item.MountPoint);
                            successCount++;
                            AddLog($"✅ 移除成功：{item.OriginalPath}");
                        }
                        catch (Exception ex)
                        {
                            failCount++;
                            AddLog($"❌ 移除失败：{item.OriginalPath} - {ex.Message}");
                        }
                    }

                    UpdateStatus($"移除完成：成功 {successCount} 个，失败 {failCount} 个");

                    var resultMessage = failCount == 0
                        ? $"移除完成！\n\n成功移除 {successCount} 个项目。"
                        : $"移除完成！\n\n成功：{successCount} 个\n失败：{failCount} 个";

                    MessageBox.Show(resultMessage, "移除完成", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    AddLog($"删除路径时出错：{ex.Message}");
                    UpdateStatus($"删除失败：{ex.Message}");
                    MessageBox.Show($"删除路径时出错：\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    DeleteButton.IsEnabled = true;
                    UpdateUI();
                }
            }
        }

        private void OnQueueItemAdded(object? sender, FileSyncService.SyncQueueEventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                _syncQueueItems.Add(e.Item);
                UpdateQueueStats();
            });
        }

        private void OnQueueItemUpdated(object? sender, FileSyncService.SyncQueueEventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                // 如果文件完成了，移动到已完成选项卡
                if (e.Item.Status == "完成" && e.Item.Progress >= 100)
                {
                    var itemToMove = _syncQueueItems.FirstOrDefault(x => x.FilePath == e.Item.FilePath && x.CreatedAt == e.Item.CreatedAt);
                    if (itemToMove != null)
                    {
                        // 设置完成时间
                        itemToMove.CompletedAt = DateTime.Now;

                        // 移动到已完成列表
                        _completedItems.Insert(0, itemToMove);  // 插入到顶部
                        _syncQueueItems.Remove(itemToMove);

                        // 限制已完成列表大小（最多保留100个）
                        while (_completedItems.Count > 100)
                        {
                            _completedItems.RemoveAt(_completedItems.Count - 1);
                        }
                    }
                }

                // 项目已经在集合中，属性变更会自动更新UI
                UpdateQueueStats();
            });
        }

        private void OnQueueItemRemoved(object? sender, FileSyncService.SyncQueueEventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                var itemToRemove = _syncQueueItems.FirstOrDefault(x => x.FilePath == e.Item.FilePath && x.CreatedAt == e.Item.CreatedAt);
                if (itemToRemove != null)
                {
                    _syncQueueItems.Remove(itemToRemove);
                }
                UpdateQueueStats();
            });
        }

        private void OnSyncFailed(object? sender, FileSyncService.SyncEventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                // 在状态栏显示错误信息
                StatusText.Text = $"同步失败: {Path.GetFileName(e.FilePath)} - {e.Message}";
                StatusText.Foreground = System.Windows.Media.Brushes.Red;

                // 5秒后恢复状态栏
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                timer.Tick += (s, args) =>
                {
                    timer.Stop();
                    StatusText.Text = "Ready";
                    StatusText.Foreground = System.Windows.Media.Brushes.Black;
                };
                timer.Start();

                // 记录到日志
                AddLog($"❌ 同步失败: {Path.GetFileName(e.FilePath)} - {e.Message}");
            });
        }

        private void UpdateQueueStats()
        {
            var stats = _cacheManager.FileSyncService.GetQueueStats();

            QueueCountText.Text = stats.TotalCount.ToString();
            PendingCountText.Text = stats.PendingCount.ToString();
            ProcessingCountText.Text = stats.ProcessingCount.ToString();
            FailedCountText.Text = stats.FailedCount.ToString();
        }

        private void ClearCompletedButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("确定要清理所有已完成的文件记录吗？", "确认清理",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                var count = _completedItems.Count;
                _completedItems.Clear();
                AddLog($"已清理 {count} 个已完成文件记录");
            }
        }

        private void ClearFailedButton_Click(object sender, RoutedEventArgs e)
        {
            var failedItems = _syncQueueItems.Where(x => x.Status == "失败").ToList();

            if (failedItems.Count == 0)
            {
                MessageBox.Show("没有失败的文件需要清理", "信息",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show($"确定要清理 {failedItems.Count} 个失败的文件记录吗？", "确认清理",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                foreach (var item in failedItems)
                {
                    _syncQueueItems.Remove(item);
                }

                AddLog($"已清理 {failedItems.Count} 个失败的文件记录");
                UpdateQueueStats();
            }
        }

        private async void RetryFailedButton_Click(object sender, RoutedEventArgs e)
        {
            var failedItems = _syncQueueItems.Where(x => x.Status == "失败").ToList();

            if (failedItems.Count == 0)
            {
                MessageBox.Show("没有失败的文件需要重试", "信息",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show($"确定要重试 {failedItems.Count} 个失败的文件吗？", "确认重试",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                int successCount = 0;
                int failedCount = 0;

                foreach (var item in failedItems)
                {
                    try
                    {
                        if (File.Exists(item.FilePath))
                        {
                            // 文件存在，可以重试 - 先重置状态
                            item.Status = "等待中";
                            item.Progress = 0;
                            item.ErrorMessage = null;

                            // 直接重新处理现有队列项，不创建新的队列项
                            await _cacheManager.FileSyncService.RetryExistingQueueItem(item);
                            successCount++;
                        }
                        else
                        {
                            // 文件不存在，保持失败状态
                            item.ErrorMessage = "文件不存在";
                            AddLog($"重试失败，文件不存在: {Path.GetFileName(item.FilePath)}");
                            failedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        // 重试异常，保持失败状态
                        item.ErrorMessage = ex.Message;
                        AddLog($"重试失败: {Path.GetFileName(item.FilePath)} - {ex.Message}");
                        failedCount++;
                    }
                }

                // 根据实际成功数量记录日志
                if (successCount > 0)
                {
                    AddLog($"已重新排队 {successCount} 个失败的文件");
                }
                if (failedCount > 0)
                {
                    AddLog($"{failedCount} 个文件重试失败");
                }

                UpdateQueueStats();
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // 点击关闭按钮时最小化到托盘，而不是退出程序
            e.Cancel = true;
            MinimizeToTray();
        }

        protected override void OnStateChanged(EventArgs e)
        {
            // 最小化按钮应该最小化到任务栏，不是托盘
            // 只有关闭按钮才缩小到托盘
            base.OnStateChanged(e);
        }

        // 新的UI事件处理程序

        private void AddPathButton_Click(object sender, RoutedEventArgs e)
        {
            var pathText = NewPathTextBox.Text.Trim();

            // 路径格式验证
            if (!ValidatePath(pathText, out string errorMessage))
            {
                MessageBox.Show(errorMessage, "路径验证失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 重复检查
            if (_acceleratedFolders.Any(f => f.OriginalPath.Equals(pathText, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("此路径已存在于列表中", "重复路径", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 创建新的AcceleratedFolder对象
            var newFolder = new AcceleratedFolder
            {
                OriginalPath = "", // 将在加速成功后设置为 pathText + ".original"
                CachePath = "", // 将在加速时设置
                MountPoint = pathText,
                CreatedAt = DateTime.Now,
                CacheSize = 0,
                Status = "未加速",
                ProgressPercentage = 0
            };

            _acceleratedFolders.Add(newFolder);
            // 不立即保存配置，等加速成功后再保存完整的配置

            NewPathTextBox.Clear();
            AddLog($"已添加路径：{pathText}");
        }


        private void NewPathTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                AddPathButton_Click(sender, e);
            }
        }

        private bool ValidatePath(string path, out string errorMessage)
        {
            errorMessage = "";

            if (string.IsNullOrWhiteSpace(path))
            {
                errorMessage = "路径不能为空";
                return false;
            }

            // 检查是否为绝对路径
            if (!Path.IsPathRooted(path))
            {
                errorMessage = "请输入绝对路径（例如：C:\\MyFolder）";
                return false;
            }

            // 检查路径格式
            try
            {
                var fullPath = Path.GetFullPath(path);

                // 检查是否为目录
                if (!Directory.Exists(fullPath))
                {
                    errorMessage = "指定的目录不存在";
                    return false;
                }

                // 检查是否为禁止目录（从appsettings.json读取）
                var forbiddenDirs = LoadForbiddenDirectories();

                foreach (var forbiddenDir in forbiddenDirs)
                {
                    if (IsForbiddenPath(fullPath, forbiddenDir))
                    {
                        errorMessage = $"禁止加速的目录：{forbiddenDir}";
                        return false;
                    }
                }

                // 检查权限（尝试访问目录）
                try
                {
                    Directory.GetFiles(fullPath, "*", SearchOption.TopDirectoryOnly);
                }
                catch (UnauthorizedAccessException)
                {
                    errorMessage = "没有访问此目录的权限";
                    return false;
                }
                catch (Exception ex)
                {
                    errorMessage = $"访问目录时出错：{ex.Message}";
                    return false;
                }
            }
            catch (Exception ex)
            {
                errorMessage = $"路径格式无效：{ex.Message}";
                return false;
            }

            return true;
        }

        private async void UnsyncedStatus_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var item = button?.DataContext as AcceleratedFolder;

            if (button == null || item == null || item.Status != "未同步")
                return;

            var result = MessageBox.Show($"确定要同步项目 '{item.MountPoint}' 吗？\n\n这将用缓存覆盖原始目录中的差异文件。", "确认同步",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                button.IsEnabled = false;
                button.Content = "🔄 同步中...";

                UpdateStatus($"正在同步: {item.MountPoint}");
                var progress = new Progress<string>(msg => AddLog(msg));

                var success = await SyncSingleItemAsync(item.CachePath, item.OriginalPath, progress);

                if (success)
                {
                    item.Status = "已加速";
                    UpdateStatus($"同步成功: {item.MountPoint}");
                    AddLog($"单项目同步成功: {item.MountPoint}");
                }
                else
                {
                    item.Status = "同步失败";
                    UpdateStatus($"同步失败: {item.MountPoint}");
                    AddLog($"单项目同步失败: {item.MountPoint}");
                }

                UpdateUI();
            }
            catch (Exception ex)
            {
                item.Status = "同步失败";
                AddLog($"同步 {item.MountPoint} 时出错: {ex.Message}");
                UpdateStatus($"同步异常: {ex.Message}");
                MessageBox.Show($"同步时发生异常：\n{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (button != null)
                {
                    button.IsEnabled = true;
                    if (item.Status == "未同步")
                    {
                        button.Content = "🔄 未同步";
                    }
                }
            }
        }

        private async Task CheckCacheIntegrityAsync(IProgress<string> progress)
        {
            progress?.Report("开始检查缓存完整性...");

            var acceleratedItems = _acceleratedFolders.Where(f =>
                f.Status == "已加速" ||
                f.Status == "未同步" ||
                f.Status == "同步失败").ToList();
            if (!acceleratedItems.Any())
            {
                progress?.Report("没有已加速的项目需要检查");
                return;
            }

            int checkedCount = 0;
            int unsyncedCount = 0;

            foreach (var item in acceleratedItems)
            {
                progress?.Report($"检查项目: {item.MountPoint}");

                try
                {
                    var hasUnsyncedChanges = await CheckSingleItemIntegrityAsync(item.CachePath, item.OriginalPath, progress);

                    if (hasUnsyncedChanges)
                    {
                        item.Status = "未同步";
                        unsyncedCount++;
                        progress?.Report($"发现未同步项目: {item.MountPoint}");
                    }
                    else
                    {
                        // 没有差异，保持原状态不变
                        progress?.Report($"项目无需同步: {item.MountPoint}");
                    }

                    checkedCount++;
                }
                catch (Exception ex)
                {
                    progress?.Report($"检查项目 {item.MountPoint} 时出错: {ex.Message}");
                    item.Status = "检查失败";
                }
            }

            progress?.Report($"完整性检查完成。检查了 {checkedCount} 个项目，发现 {unsyncedCount} 个未同步项目");

            // 更新SyncAllButton状态
            _ = Dispatcher.BeginInvoke(() =>
            {
                SyncAllButton.IsEnabled = unsyncedCount > 0;
            });
        }

        private async Task<bool> CheckSingleItemIntegrityAsync(string cachePath, string originalPath, IProgress<string>? progress)
        {
            try
            {
                // 使用统一的进程执行器，/L参数表示List-only模式检查差异
                var result = await ProcessExecutor.ExecuteAsync(
                    "robocopy",
                    $"\"{cachePath}\" \"{originalPath}\" /L /S /E",
                    timeoutSeconds: 300);

                // 统一的进程启动失败处理
                if (!result.ProcessStarted)
                {
                    progress?.Report("无法启动RoboCopy进程");
                    AsyncLogger.Instance.LogInfo($"缓存完整性检查失败 - 无法启动RoboCopy进程。源路径: {originalPath}, 缓存路径: {cachePath}", "RoboCopy");
                    return false;
                }

                // 记录完整输出到后台日志（不显示在前台）
                AsyncLogger.Instance.LogInfo($"缓存完整性检查 - 源路径: {originalPath}, 缓存路径: {cachePath}", "RoboCopy");
                AsyncLogger.Instance.LogInfo($"RoboCopy退出代码: {result.ExitCode}", "RoboCopy");
                AsyncLogger.Instance.LogInfo($"RoboCopy完整输出 ({result.StandardOutput.Count} 行):", "RoboCopy");
                foreach (var line in result.StandardOutput)
                {
                    AsyncLogger.Instance.LogInfo(line, "RoboCopy");
                }

                if (result.ErrorOutput.Count > 0)
                {
                    AsyncLogger.Instance.LogInfo($"RoboCopy错误输出:", "RoboCopy");
                    foreach (var line in result.ErrorOutput)
                    {
                        AsyncLogger.Instance.LogInfo(line, "RoboCopy");
                    }
                }

                // 使用RobocopyHelper的HasChanges方法 - 保持原有的ParseRoboCopyOutput逻辑
                return RobocopyHelper.HasChanges(result.StandardOutput);
            }
            catch (Exception ex)
            {
                progress?.Report($"检查差异时出错: {ex.Message}");
                AsyncLogger.Instance.LogInfo($"缓存完整性检查异常 - 源路径: {originalPath}, 缓存路径: {cachePath}, 错误: {ex.Message}", "RoboCopy");
                return false;
            }
        }

        private bool ParseRoboCopyOutput(string output, IProgress<string>? progress)
        {
            try
            {
                // 查找统计行：目录: 总数 复制 跳过 不匹配 失败 其他
                // 和：文件: 总数 复制 跳过 不匹配 失败 其他
                var lines = output.Split('\n');
                int fileChanges = 0;
                int dirChanges = 0;

                for (int i = 0; i < lines.Length; i++)
                {
                    var trimmedLine = lines[i].Trim();

                    // 查找文件统计行：文件:         3         2         1         0         0         0
                    if (trimmedLine.StartsWith("文件:"))
                    {
                        var parts = trimmedLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                        if (parts.Length >= 3)
                        {
                            // parts[0]="文件:", parts[1]="总数", parts[2]="复制数"
                            if (int.TryParse(parts[2], out int copyCount))
                            {
                                fileChanges = copyCount;
                            }
                        }
                    }

                    // 查找目录统计行：目录:         2         1         1         0         0         0
                    if (trimmedLine.StartsWith("目录:"))
                    {
                        var parts = trimmedLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                        if (parts.Length >= 3)
                        {
                            // parts[0]="目录:", parts[1]="总数", parts[2]="复制数"
                            if (int.TryParse(parts[2], out int copyCount))
                            {
                                dirChanges = copyCount;
                            }
                        }
                    }

                    // 另外检查是否有"新文件"行，这也表示有差异
                    if (trimmedLine.Contains("新文件"))
                    {
                        return true;
                    }

                    // 检查是否有"新目录"行
                    if (trimmedLine.Contains("新目录"))
                    {
                        return true;
                    }
                }

                // 检查是否有需要同步的变更
                bool hasChanges = fileChanges > 0 || dirChanges > 0;

                if (hasChanges)
                {
                    progress?.Report($"发现差异：{fileChanges} 个文件，{dirChanges} 个目录需要同步");
                }

                return hasChanges;
            }
            catch (Exception ex)
            {
                progress?.Report($"解析RoboCopy输出时出错: {ex.Message}");
                AsyncLogger.Instance.LogInfo($"解析RoboCopy输出异常: {ex.Message}", "RoboCopy");
                return false;
            }
        }

        private async Task SyncAllUnsyncedAsync(IProgress<string> progress)
        {
            var unsyncedItems = _acceleratedFolders.Where(f => f.Status == "未同步").ToList();

            progress?.Report($"开始同步 {unsyncedItems.Count} 个未同步项目...");

            int successCount = 0;
            int failureCount = 0;

            foreach (var item in unsyncedItems)
            {
                progress?.Report($"同步项目: {item.MountPoint}");

                try
                {
                    var success = await SyncSingleItemAsync(item.CachePath, item.OriginalPath, progress);

                    if (success)
                    {
                        item.Status = "已加速";
                        successCount++;
                        progress?.Report($"同步成功: {item.MountPoint}");
                    }
                    else
                    {
                        item.Status = "同步失败";
                        failureCount++;
                        progress?.Report($"同步失败: {item.MountPoint}");
                    }
                }
                catch (Exception ex)
                {
                    item.Status = "同步失败";
                    failureCount++;
                    progress?.Report($"同步 {item.MountPoint} 时出错: {ex.Message}");
                }
            }

            progress?.Report($"批量同步完成。成功: {successCount}，失败: {failureCount}");

            // 更新SyncAllButton状态
            _ = Dispatcher.BeginInvoke(() =>
            {
                var remainingUnsynced = _acceleratedFolders.Count(f => f.Status == "未同步");
                SyncAllButton.IsEnabled = remainingUnsynced > 0;
            });
        }

        private async Task<bool> SyncSingleItemAsync(string cachePath, string originalPath, IProgress<string>? progress)
        {
            try
            {
                // 使用统一的进程执行器
                var result = await ProcessExecutor.ExecuteAsync(
                    "robocopy",
                    $"\"{cachePath}\" \"{originalPath}\" /S /E /PURGE",
                    timeoutSeconds: 600); // 同步操作可能需要更长时间

                // 统一的进程启动失败处理
                if (!result.ProcessStarted)
                {
                    progress?.Report("无法启动RoboCopy进程");
                    AsyncLogger.Instance.LogInfo($"同步失败 - 无法启动RoboCopy进程。源路径: {originalPath}, 缓存路径: {cachePath}", "RoboCopy");
                    return false;
                }

                // 记录完整输出到后台日志（不显示在前台）
                AsyncLogger.Instance.LogInfo($"同步操作 - 源路径: {originalPath}, 缓存路径: {cachePath}", "RoboCopy");
                AsyncLogger.Instance.LogInfo($"RoboCopy退出代码: {result.ExitCode}", "RoboCopy");
                AsyncLogger.Instance.LogInfo($"RoboCopy完整输出 ({result.StandardOutput.Count} 行):", "RoboCopy");
                foreach (var line in result.StandardOutput)
                {
                    AsyncLogger.Instance.LogInfo(line, "RoboCopy");
                }

                if (result.ErrorOutput.Count > 0)
                {
                    AsyncLogger.Instance.LogInfo($"RoboCopy错误输出:", "RoboCopy");
                    foreach (var line in result.ErrorOutput)
                    {
                        AsyncLogger.Instance.LogInfo(line, "RoboCopy");
                    }
                }

                // 使用RobocopyHelper进行成功判断 - 保持原有逻辑
                bool success = RobocopyHelper.IsSuccessForSync(result.ExitCode);

                if (!success)
                {
                    var description = RobocopyHelper.GetResultDescription(result.ExitCode);
                    progress?.Report($"RoboCopy同步失败: {description}");
                }

                return success;
            }
            catch (Exception ex)
            {
                progress?.Report($"同步时出错: {ex.Message}");
                AsyncLogger.Instance.LogInfo($"同步异常 - 源路径: {originalPath}, 缓存路径: {cachePath}, 错误: {ex.Message}", "RoboCopy");
                return false;
            }
        }

        private List<string> LoadForbiddenDirectories()
        {
            try
            {
                var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                if (File.Exists(configPath))
                {
                    var json = File.ReadAllText(configPath);
                    var config = JObject.Parse(json);
                    return config["ForbiddenDirectories"]?.ToObject<List<string>>() ?? new List<string>();
                }
            }
            catch (Exception ex)
            {
                AsyncLogger.Instance.LogError($"加载禁止目录配置失败: {ex.Message}", ex, "Config");
            }

            // 如果配置文件不存在或读取失败，返回默认的禁止目录列表
            return new List<string>
            {
                "C:\\Windows\\*",
                "C:\\Program Files",
                "C:\\Program Files (x86)",
                "C:\\System Volume Information",
                "C:\\$Recycle.Bin",
                "C:\\Recovery",
                "C:\\Boot",
                "C:\\EFI",
                "C:\\Users\\All Users",
                "C:\\Users\\Default",
                "C:\\Users\\Public",
                "C:\\ProgramData",
                "C:\\Documents and Settings"
            };
        }

        private bool IsForbiddenPath(string targetPath, string forbiddenPattern)
        {
            // 标准化路径，确保使用统一的路径分隔符
            var normalizedTarget = Path.GetFullPath(targetPath).TrimEnd('\\');
            var normalizedPattern = Path.GetFullPath(forbiddenPattern.TrimEnd('*')).TrimEnd('\\');

            // 如果模式以*结尾，表示通配符模式，禁止该目录及所有子目录
            if (forbiddenPattern.EndsWith("*"))
            {
                return normalizedTarget.StartsWith(normalizedPattern, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                // 精确匹配模式，只禁止完全相同的路径
                return string.Equals(normalizedTarget, normalizedPattern, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// 初始化系统托盘
        /// </summary>
        private void InitializeSystemTray()
        {
            try
            {
                _notifyIcon = new WinForms.NotifyIcon
                {
#if DEBUG
                    Text = "CacheMax - 缓存加速工具 [调试]",
#else
                    Text = "CacheMax - 缓存加速工具",
#endif
                    Visible = true,  // 程序启动时就显示托盘图标
                    Icon = LoadIconFromResource()  // 使用自定义图标
                };

                // 双击托盘图标时显示窗口
                _notifyIcon.DoubleClick += NotifyIcon_DoubleClick;

                // 创建右键菜单
                var contextMenu = new WinForms.ContextMenuStrip();
                contextMenu.Items.Add("显示主窗口", null, (s, e) => ShowMainWindow());
                contextMenu.Items.Add(new WinForms.ToolStripSeparator());
                contextMenu.Items.Add("退出程序", null, (s, e) => ExitApplication());

                _notifyIcon.ContextMenuStrip = contextMenu;
            }
            catch (Exception ex)
            {
                AddLog($"初始化系统托盘失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 从资源加载图标
        /// </summary>
        private System.Drawing.Icon LoadIconFromResource()
        {
            try
            {
                // 尝试从资源流加载图标
                var resourceUri = new Uri("pack://application:,,,/CacheMax.ico");
                var streamInfo = System.Windows.Application.GetResourceStream(resourceUri);
                if (streamInfo != null)
                {
                    return new System.Drawing.Icon(streamInfo.Stream);
                }
            }
            catch (Exception ex)
            {
                AddLog($"加载图标资源失败: {ex.Message}");
            }

            // 回退到默认图标
            return SystemIcons.Application;
        }

        /// <summary>
        /// 托盘图标双击事件
        /// </summary>
        private void NotifyIcon_DoubleClick(object? sender, EventArgs e)
        {
            ShowMainWindow();
        }

        /// <summary>
        /// 显示主窗口
        /// </summary>
        private void ShowMainWindow()
        {
            try
            {
                this.Show();
                this.WindowState = WindowState.Normal;
                this.Activate();
                this.Topmost = true;  // 暂时置顶
                this.Topmost = false; // 立即取消置顶
                this.Focus();

                // 保持托盘图标可见，这样用户可以再次使用托盘功能
                // 不隐藏托盘图标，避免图标消失的问题
            }
            catch (Exception ex)
            {
                AddLog($"显示主窗口失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 最小化到托盘
        /// </summary>
        private void MinimizeToTray()
        {
            try
            {
                this.Hide();
                // 托盘图标已经在启动时显示，无需再次设置Visible
                // 移除烦人的气球提示，用户已经知道程序最小化到托盘了
            }
            catch (Exception ex)
            {
                AddLog($"最小化到托盘失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 彻底退出应用程序
        /// </summary>
        private void ExitApplication()
        {
            try
            {
                // 清理缓存管理器资源
                _cacheManager?.Dispose();
                AddLog("应用程序正在关闭，已清理所有资源");

                // 清理系统托盘
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                    _notifyIcon = null;
                }

                System.Windows.Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                AddLog($"退出应用程序失败: {ex.Message}");
                // 强制退出
                Environment.Exit(0);
            }
        }

    }

    // 扩展方法用于查找父元素
    public static class VisualTreeHelperExtensions
    {
        public static T? FindParent<T>(this DependencyObject child) where T : DependencyObject
        {
            var parent = VisualTreeHelper.GetParent(child);

            if (parent == null) return null;

            if (parent is T parentT)
                return parentT;

            return FindParent<T>(parent);
        }
    }
}