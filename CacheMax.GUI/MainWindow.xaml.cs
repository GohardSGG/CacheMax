using System;
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

        public MainWindow()
        {
            InitializeComponent();

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
        }

        private async void AccelerateButton_Click(object sender, RoutedEventArgs e)
        {
            var cacheRoot = CacheRootTextBox.Text.Trim();

            if (string.IsNullOrEmpty(cacheRoot))
            {
                MessageBox.Show("请指定缓存根目录", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 查找所有未加速的文件夹
            var unacceleratedFolders = _acceleratedFolders.Where(f => f.Status == "未加速").ToList();

            if (unacceleratedFolders.Count == 0)
            {
                MessageBox.Show("没有需要加速的文件夹。请先添加路径。", "信息", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show($"检测到 {unacceleratedFolders.Count} 个未加速的文件夹。\n\n是否开始批量加速？",
                "确认批量加速", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                AccelerateButton.IsEnabled = false;
                UpdateStatus("开始批量加速...");

                foreach (var folder in unacceleratedFolders)
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

                            // 解析进度信息并更新
                            if (msg.Contains("步骤"))
                            {
                                if (msg.Contains("1/4")) folder.ProgressPercentage = 25;
                                else if (msg.Contains("2/4")) folder.ProgressPercentage = 50;
                                else if (msg.Contains("3/4")) folder.ProgressPercentage = 75;
                                else if (msg.Contains("4/4")) folder.ProgressPercentage = 100;
                            }
                            else if (msg.Contains("Robocopy") && msg.Contains("%"))
                            {
                                // 尝试解析Robocopy进度
                                var match = Regex.Match(msg, @"(\d+)%");
                                if (match.Success && int.TryParse(match.Groups[1].Value, out int percent))
                                {
                                    folder.ProgressPercentage = Math.Max(folder.ProgressPercentage, percent * 0.6); // Robocopy占60%
                                }
                            }
                        });

                        // 使用默认同步设置
                        var syncMode = SyncMode.Immediate;
                        var syncDelay = 3;

                        bool initSuccess = await _cacheManager.InitializeCacheAcceleration(
                            folder.MountPoint, cacheRoot, syncMode, syncDelay, progress);

                        if (initSuccess)
                        {
                            folder.Status = "已完成";
                            folder.ProgressPercentage = 100;

                            // 正确计算缓存路径（与CacheManagerService逻辑一致）
                            var folderName = Path.GetFileName(folder.MountPoint);
                            var driveLetter = Path.GetPathRoot(folder.MountPoint)?.Replace(":", "").Replace("\\", "") ?? "Unknown";
                            var driveSpecificCacheRoot = Path.Combine(cacheRoot, driveLetter);
                            var cachePath = Path.Combine(driveSpecificCacheRoot, folderName);

                            // 正确设置所有路径
                            folder.CachePath = cachePath;
                            folder.OriginalPath = folder.MountPoint + ".original"; // 这是关键修复！
                            folder.CacheSize = GetDirectorySize(cachePath);

                            // 现在保存完整正确的配置到文件
                            _config.AddAcceleratedFolder(folder);

                            AddLog($"✅ 加速完成：{folder.MountPoint}");
                            AddLog($"原始备份：{folder.OriginalPath}");
                            AddLog($"缓存路径：{cachePath}");
                        }
                        else
                        {
                            folder.Status = "失败";
                            folder.ProgressPercentage = 0;
                            AddLog($"❌ 加速失败：{folder.OriginalPath}");
                        }
                    }
                    catch (Exception ex)
                    {
                        folder.Status = "失败";
                        folder.ProgressPercentage = 0;
                        AddLog($"❌ 加速异常：{folder.OriginalPath} - {ex.Message}");
                    }
                }

                // 保存配置
                _config.SaveConfig();

                var successCount = unacceleratedFolders.Count(f => f.Status == "已完成");
                var failedCount = unacceleratedFolders.Count(f => f.Status == "失败");

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
            var selected = AcceleratedFoldersGrid.SelectedItem as AcceleratedFolder;
            if (selected == null)
            {
                MessageBox.Show("请选择要停止加速的文件夹", "信息",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show($"停止 {selected.MountPoint} 的加速？\n\n这将恢复原始文件夹。",
                "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                StopButton.IsEnabled = false;
                UpdateStatus($"正在停止 {selected.MountPoint} 的加速...");

                var progress = new Progress<string>(msg => AddLog(msg));

                // 询问是否删除缓存文件
                var deleteCache = MessageBox.Show($"是否删除缓存文件？\n\n缓存位置：{selected.CachePath}\n\n" +
                    "选择\"是\"将删除缓存文件（节省空间）\n选择\"否\"将保留缓存文件（便于重新加速）",
                    "删除缓存", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

                if (deleteCache == MessageBoxResult.Cancel)
                {
                    return;
                }

                bool deleteCacheFiles = deleteCache == MessageBoxResult.Yes;

                AddLog($"停止加速：{selected.MountPoint}");
                if (!await _cacheManager.StopCacheAcceleration(
                    selected.MountPoint,
                    selected.OriginalPath,
                    selected.CachePath,
                    deleteCacheFiles,
                    progress))
                {
                    throw new Exception("停止缓存加速失败");
                }

                // 更新配置
                _config.RemoveAcceleratedFolder(selected.MountPoint);
                _acceleratedFolders.Remove(selected);

                UpdateStatus($"成功停止 {selected.MountPoint} 的加速");
                AddLog($"加速已停止，文件夹已恢复");

                MessageBox.Show($"加速已停止！\n\n{selected.MountPoint} 已恢复为普通文件夹。",
                    "停止成功", MessageBoxButton.OK, MessageBoxImage.Information);
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

        private void UpdateUI()
        {
            var selected = AcceleratedFoldersGrid.SelectedItem as AcceleratedFolder;
            StopButton.IsEnabled = selected != null && selected.Status == "✅";
            DeleteButton.IsEnabled = selected != null;

            // 控制按钮状态
            ValidateButton.IsEnabled = selected != null;

            // 检查缓存完整性按钮：有已加速项目时启用
            var hasAcceleratedItems = _acceleratedFolders.Any(f => f.Status == "已加速" || f.Status == "已完成");
            CheckIntegrityButton.IsEnabled = hasAcceleratedItems;

            // 同步所有未同步项按钮：有未同步项目时启用
            var hasUnsyncedItems = _acceleratedFolders.Any(f => f.Status == "未同步");
            SyncAllButton.IsEnabled = hasUnsyncedItems;

            var runningCount = _acceleratedFolders.Count(f => f.Status == "已完成");
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
            var selected = AcceleratedFoldersGrid.SelectedItem as AcceleratedFolder;
            if (selected == null)
            {
                MessageBox.Show("请选择要删除的路径", "信息", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 检查是否已经加速，需要不同的处理方式
            bool isAccelerated = selected.Status == "已完成" || _cacheManager.IsAccelerated(selected.MountPoint);

            string message;
            if (isAccelerated)
            {
                message = $"路径 '{selected.OriginalPath}' 已被加速。\n\n" +
                         "删除将会：\n" +
                         "• 停止加速并恢复原始文件夹\n" +
                         "• 移除Junction链接\n" +
                         "• 清理缓存文件\n\n" +
                         "确定要继续吗？";
            }
            else
            {
                message = $"确定要删除路径 '{selected.OriginalPath}' 吗？";
            }

            var result = MessageBox.Show(message, "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    DeleteButton.IsEnabled = false;

                    if (isAccelerated)
                    {
                        // 执行完整的停止加速流程
                        AddLog($"正在停止加速：{selected.OriginalPath}");
                        UpdateStatus($"正在停止加速：{selected.MountPoint}");

                        selected.Status = "停止中";
                        selected.ProgressPercentage = 0;

                        var progress = new Progress<string>(msg => AddLog(msg));

                        bool stopSuccess = await _cacheManager.StopCacheAcceleration(
                            selected.MountPoint,
                            selected.OriginalPath,
                            selected.CachePath,
                            true, // 删除缓存文件
                            progress);

                        if (stopSuccess)
                        {
                            AddLog($"✅ 加速停止成功：{selected.OriginalPath}");
                            UpdateStatus($"成功停止加速：{selected.MountPoint}");
                        }
                        else
                        {
                            AddLog($"⚠️ 加速停止过程中出现问题：{selected.OriginalPath}");
                            UpdateStatus($"停止加速时出现问题：{selected.MountPoint}");

                            // 即使停止失败，也询问是否强制删除记录
                            var forceResult = MessageBox.Show(
                                "停止加速过程中出现问题，但可能部分操作已完成。\n\n是否强制删除此记录？\n\n注意：您可能需要手动清理残留的文件链接。",
                                "强制删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                            if (forceResult != MessageBoxResult.Yes)
                            {
                                selected.Status = "失败";
                                return;
                            }
                        }
                    }

                    // 从列表和配置中删除
                    _acceleratedFolders.Remove(selected);
                    _config.RemoveAcceleratedFolder(selected.MountPoint);
                    AddLog($"已删除路径记录：{selected.OriginalPath}");
                    UpdateStatus("删除完成");
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
                StatusText.Foreground = Brushes.Red;

                // 5秒后恢复状态栏
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                timer.Tick += (s, args) =>
                {
                    timer.Stop();
                    StatusText.Text = "Ready";
                    StatusText.Foreground = Brushes.Black;
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
            try
            {
                // 清理缓存管理器资源
                _cacheManager?.Dispose();
                AddLog("应用程序正在关闭，已清理所有资源");
            }
            catch (Exception ex)
            {
                AddLog($"关闭时清理资源出错：{ex.Message}");
            }
            base.OnClosing(e);
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


        private void NewPathTextBox_KeyDown(object sender, KeyEventArgs e)
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

                // 检查是否为禁止目录
                var appSettings = System.Configuration.ConfigurationManager.AppSettings;
                var forbiddenDirs = new[]
                {
                    @"C:\Windows",
                    @"C:\Program Files",
                    @"C:\Program Files (x86)",
                    @"C:\System Volume Information",
                    @"C:\$Recycle.Bin",
                    @"C:\Recovery",
                    @"C:\Boot",
                    @"C:\EFI",
                    @"C:\Users\All Users",
                    @"C:\Users\Default",
                    @"C:\Users\Public",
                    @"C:\ProgramData",
                    @"C:\Documents and Settings"
                };

                foreach (var forbiddenDir in forbiddenDirs)
                {
                    if (fullPath.StartsWith(forbiddenDir, StringComparison.OrdinalIgnoreCase))
                    {
                        errorMessage = $"禁止加速系统目录：{forbiddenDir}";
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

            if (item == null || item.Status != "未同步")
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
                button.IsEnabled = true;
                if (item.Status == "未同步")
                {
                    button.Content = "🔄 未同步";
                }
            }
        }

        private async Task CheckCacheIntegrityAsync(IProgress<string> progress)
        {
            progress?.Report("开始检查缓存完整性...");

            var acceleratedItems = _acceleratedFolders.Where(f =>
                f.Status == "已加速" ||
                f.Status == "已完成" ||
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
            Dispatcher.BeginInvoke(() =>
            {
                SyncAllButton.IsEnabled = unsyncedCount > 0;
            });
        }

        private async Task<bool> CheckSingleItemIntegrityAsync(string cachePath, string originalPath, IProgress<string> progress)
        {
            return await Task.Run(() =>
            {
                try
                {
                    // 使用RoboCopy的/L参数检查差异
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "robocopy",
                        Arguments = $"\"{cachePath}\" \"{originalPath}\" /L /S /E",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(startInfo);
                    if (process == null)
                    {
                        progress?.Report("无法启动RoboCopy进程");
                        // 记录到后台日志
                        AsyncLogger.Instance.LogInfo($"缓存完整性检查失败 - 无法启动RoboCopy进程。源路径: {originalPath}, 缓存路径: {cachePath}", "RoboCopy");
                        return false;
                    }

                    var output = process.StandardOutput.ReadToEnd();
                    var error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    // 记录完整输出到后台日志（不显示在前台）
                    AsyncLogger.Instance.LogInfo($"缓存完整性检查 - 源路径: {originalPath}, 缓存路径: {cachePath}", "RoboCopy");
                    AsyncLogger.Instance.LogInfo($"RoboCopy退出代码: {process.ExitCode}", "RoboCopy");
                    AsyncLogger.Instance.LogInfo($"RoboCopy完整输出 ({output.Length} 字符):", "RoboCopy");
                    AsyncLogger.Instance.LogInfo(output, "RoboCopy");

                    if (!string.IsNullOrEmpty(error))
                    {
                        AsyncLogger.Instance.LogInfo($"RoboCopy错误输出: {error}", "RoboCopy");
                    }

                    // 解析RoboCopy输出判断是否有差异
                    return ParseRoboCopyOutput(output, progress);
                }
                catch (Exception ex)
                {
                    progress?.Report($"检查差异时出错: {ex.Message}");
                    AsyncLogger.Instance.LogInfo($"缓存完整性检查异常 - 源路径: {originalPath}, 缓存路径: {cachePath}, 错误: {ex.Message}", "RoboCopy");
                    return false;
                }
            });
        }

        private bool ParseRoboCopyOutput(string output, IProgress<string> progress)
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
            Dispatcher.BeginInvoke(() =>
            {
                var remainingUnsynced = _acceleratedFolders.Count(f => f.Status == "未同步");
                SyncAllButton.IsEnabled = remainingUnsynced > 0;
            });
        }

        private async Task<bool> SyncSingleItemAsync(string cachePath, string originalPath, IProgress<string> progress)
        {
            return await Task.Run(() =>
            {
                try
                {
                    // 使用RoboCopy进行实际同步
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "robocopy",
                        Arguments = $"\"{cachePath}\" \"{originalPath}\" /S /E /PURGE",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(startInfo);
                    if (process == null)
                    {
                        progress?.Report("无法启动RoboCopy进程");
                        AsyncLogger.Instance.LogInfo($"同步失败 - 无法启动RoboCopy进程。源路径: {originalPath}, 缓存路径: {cachePath}", "RoboCopy");
                        return false;
                    }

                    var output = process.StandardOutput.ReadToEnd();
                    var error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    // 记录完整输出到后台日志（不显示在前台）
                    AsyncLogger.Instance.LogInfo($"同步操作 - 源路径: {originalPath}, 缓存路径: {cachePath}", "RoboCopy");
                    AsyncLogger.Instance.LogInfo($"RoboCopy退出代码: {process.ExitCode}", "RoboCopy");
                    AsyncLogger.Instance.LogInfo($"RoboCopy完整输出 ({output.Length} 字符):", "RoboCopy");
                    AsyncLogger.Instance.LogInfo(output, "RoboCopy");

                    if (!string.IsNullOrEmpty(error))
                    {
                        AsyncLogger.Instance.LogInfo($"RoboCopy错误输出: {error}", "RoboCopy");
                    }

                    // RoboCopy的退出代码: 0-3 表示成功，>=4 表示错误
                    bool success = process.ExitCode <= 3;

                    if (!success)
                    {
                        progress?.Report($"RoboCopy同步失败，退出代码: {process.ExitCode}");
                    }

                    return success;
                }
                catch (Exception ex)
                {
                    progress?.Report($"同步时出错: {ex.Message}");
                    AsyncLogger.Instance.LogInfo($"同步异常 - 源路径: {originalPath}, 缓存路径: {cachePath}, 错误: {ex.Message}", "RoboCopy");
                    return false;
                }
            });
        }
    }

}