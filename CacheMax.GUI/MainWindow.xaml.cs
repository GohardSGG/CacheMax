using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CacheMax.GUI.Services;
using Microsoft.Win32;

namespace CacheMax.GUI
{
    public partial class MainWindow : Window
    {
        private readonly FastCopyService _fastCopy;
        private readonly WinFspService _winFsp;
        private readonly ConfigService _config;
        private readonly ObservableCollection<AcceleratedFolderViewModel> _acceleratedFolders;

        public MainWindow()
        {
            InitializeComponent();

            _fastCopy = new FastCopyService();
            _winFsp = new WinFspService();
            _config = new ConfigService();
            _acceleratedFolders = new ObservableCollection<AcceleratedFolderViewModel>();

            AcceleratedFoldersGrid.ItemsSource = _acceleratedFolders;
            CacheRootTextBox.Text = _config.Config.DefaultCacheRoot;

            LoadExistingAccelerations();
            UpdateUI();
        }

        private void LoadExistingAccelerations()
        {
            _acceleratedFolders.Clear();
            foreach (var folder in _config.Config.AcceleratedFolders)
            {
                var vm = new AcceleratedFolderViewModel
                {
                    OriginalPath = folder.OriginalPath,
                    CachePath = folder.CachePath,
                    MountPoint = folder.MountPoint,
                    CreatedAt = folder.CreatedAt,
                    CacheSize = folder.CacheSize,
                    Status = _winFsp.IsRunning(folder.MountPoint) ? "✅" : "⭕"
                };
                _acceleratedFolders.Add(vm);
            }
        }

        private async void AccelerateButton_Click(object sender, RoutedEventArgs e)
        {
            var sourceFolder = SourceFolderTextBox.Text.Trim();
            var cacheRoot = CacheRootTextBox.Text.Trim();

            if (string.IsNullOrEmpty(sourceFolder) || !Directory.Exists(sourceFolder))
            {
                MessageBox.Show("Please select a valid source folder.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (string.IsNullOrEmpty(cacheRoot))
            {
                MessageBox.Show("Please specify a cache root directory.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Prepare paths
            var folderName = Path.GetFileName(sourceFolder);
            var originalPath = $"{sourceFolder}.original";
            var cachePath = Path.Combine(cacheRoot, folderName);

            // Check if already accelerated
            if (_acceleratedFolders.Any(f => f.MountPoint == sourceFolder))
            {
                MessageBox.Show("This folder is already accelerated.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                AccelerateButton.IsEnabled = false;
                UpdateStatus("Starting acceleration process...");

                // Progress reporter
                var progress = new Progress<string>(msg => AddLog(msg));

                // Step 1: Copy to cache
                AddLog($"Copying {sourceFolder} to {cachePath}...");
                if (!await _fastCopy.CopyWithVerifyAsync(sourceFolder, cachePath, progress))
                {
                    throw new Exception("FastCopy failed to copy files");
                }

                // Step 2: Rename original folder
                AddLog($"Renaming {sourceFolder} to {originalPath}...");
                if (Directory.Exists(originalPath))
                {
                    MessageBox.Show($"Backup folder already exists: {originalPath}\nPlease remove it first.", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                Directory.Move(sourceFolder, originalPath);

                // Step 3: Start WinFsp
                AddLog($"Starting WinFsp file system...");
                if (!_winFsp.StartFileSystem(originalPath, cachePath, sourceFolder, progress))
                {
                    // Rollback
                    Directory.Move(originalPath, sourceFolder);
                    throw new Exception("Failed to start WinFsp file system");
                }

                // Step 4: Save configuration
                var acceleratedFolder = new AcceleratedFolder
                {
                    OriginalPath = originalPath,
                    CachePath = cachePath,
                    MountPoint = sourceFolder,
                    CreatedAt = DateTime.Now,
                    CacheSize = GetDirectorySize(cachePath)
                };
                _config.AddAcceleratedFolder(acceleratedFolder);

                // Update UI
                var vm = new AcceleratedFolderViewModel
                {
                    OriginalPath = originalPath,
                    CachePath = cachePath,
                    MountPoint = sourceFolder,
                    CreatedAt = acceleratedFolder.CreatedAt,
                    CacheSize = acceleratedFolder.CacheSize,
                    Status = "✅"
                };
                _acceleratedFolders.Add(vm);

                UpdateStatus($"Successfully accelerated {sourceFolder}");
                AddLog($"Acceleration complete! {sourceFolder} is now accelerated.");

                // Clear input
                SourceFolderTextBox.Clear();
            }
            catch (Exception ex)
            {
                AddLog($"Error: {ex.Message}");
                UpdateStatus($"Failed to accelerate: {ex.Message}");
                MessageBox.Show($"Failed to accelerate folder:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                AccelerateButton.IsEnabled = true;
                UpdateUI();
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = AcceleratedFoldersGrid.SelectedItem as AcceleratedFolderViewModel;
            if (selected == null)
            {
                MessageBox.Show("Please select a folder to stop acceleration.", "Info",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show($"Stop acceleration for {selected.MountPoint}?\n\nThis will restore the original folder.",
                "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                StopButton.IsEnabled = false;
                UpdateStatus($"Stopping acceleration for {selected.MountPoint}...");

                var progress = new Progress<string>(msg => AddLog(msg));

                // Step 1: Stop WinFsp
                AddLog($"Stopping WinFsp file system...");
                if (!_winFsp.StopFileSystem(selected.MountPoint, progress))
                {
                    throw new Exception("Failed to stop WinFsp file system");
                }

                // Step 2: Remove mount point (might be empty directory)
                if (Directory.Exists(selected.MountPoint))
                {
                    try { Directory.Delete(selected.MountPoint); } catch { }
                }

                // Step 3: Restore original folder
                AddLog($"Restoring original folder...");
                if (Directory.Exists(selected.OriginalPath))
                {
                    Directory.Move(selected.OriginalPath, selected.MountPoint);
                }

                // Step 4: Optional - ask to delete cache
                var deleteCache = MessageBox.Show($"Delete cached files at {selected.CachePath}?",
                    "Delete Cache", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (deleteCache == MessageBoxResult.Yes && Directory.Exists(selected.CachePath))
                {
                    AddLog($"Deleting cache at {selected.CachePath}...");
                    Directory.Delete(selected.CachePath, true);
                }

                // Step 5: Update configuration
                _config.RemoveAcceleratedFolder(selected.MountPoint);
                _acceleratedFolders.Remove(selected);

                UpdateStatus($"Successfully stopped acceleration for {selected.MountPoint}");
                AddLog($"Acceleration stopped and folder restored.");
            }
            catch (Exception ex)
            {
                AddLog($"Error: {ex.Message}");
                UpdateStatus($"Failed to stop acceleration: {ex.Message}");
                MessageBox.Show($"Failed to stop acceleration:\n{ex.Message}", "Error",
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
                // Get cache root path
                var cacheRoot = CacheRootTextBox.Text?.Trim();
                if (string.IsNullOrEmpty(cacheRoot))
                {
                    MessageBox.Show("Please specify a cache root directory.", "Input Required",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!Directory.Exists(cacheRoot))
                {
                    var create = MessageBox.Show($"Cache directory '{cacheRoot}' does not exist. Create it?",
                        "Create Directory", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (create == MessageBoxResult.Yes)
                    {
                        Directory.CreateDirectory(cacheRoot);
                    }
                    else
                    {
                        return;
                    }
                }

                // Get available drive letter
                var drives = DriveInfo.GetDrives().Select(d => d.Name[0]).ToHashSet();
                char driveLetter = 'Z';
                for (char c = 'Z'; c >= 'D'; c--)
                {
                    if (!drives.Contains(c))
                    {
                        driveLetter = c;
                        break;
                    }
                }

                var mountPoint = $"{driveLetter}:";

                AddLog($"Testing mount of cache directory: {cacheRoot} -> {driveLetter}:");
                UpdateStatus($"Mounting {cacheRoot} as drive {driveLetter}...");

                // Start WinFsp to mount the cache directory
                var progress = new Progress<string>(msg => AddLog(msg));

                // For testing, we'll mount the cache directory as both source and cache
                // This will make it appear as a drive letter
                if (_winFsp.StartFileSystem(cacheRoot, cacheRoot, mountPoint, progress))
                {
                    AddLog($"Successfully mounted {cacheRoot} as drive {driveLetter}");
                    UpdateStatus($"Test mount successful: Drive {driveLetter} is now available");

                    // Add to grid for tracking
                    var vm = new AcceleratedFolderViewModel
                    {
                        OriginalPath = cacheRoot,
                        CachePath = cacheRoot,
                        MountPoint = mountPoint,
                        CreatedAt = DateTime.Now,
                        Status = "🧪"
                    };
                    _acceleratedFolders.Add(vm);

                    MessageBox.Show($"Cache directory mounted successfully as drive {driveLetter}:\nYou can now access it through Windows Explorer.",
                        "Test Mount Successful", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    AddLog("Failed to mount cache directory");
                    UpdateStatus("Test mount failed");
                    MessageBox.Show("Failed to mount cache directory. Check the log for details.",
                        "Mount Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                AddLog($"Error during test mount: {ex.Message}");
                UpdateStatus($"Test mount error: {ex.Message}");
                MessageBox.Show($"Error during test mount:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                UpdateUI();
            }
        }

        private void BrowseSourceButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select folder to accelerate"
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                SourceFolderTextBox.Text = dialog.SelectedPath;
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

        private void UpdateUI()
        {
            var selected = AcceleratedFoldersGrid.SelectedItem as AcceleratedFolderViewModel;
            StopButton.IsEnabled = selected != null && selected.Status == "✅";
            DeleteButton.IsEnabled = selected != null;

            var runningCount = _acceleratedFolders.Count(f => f.Status == "✅");
            RunningCountText.Text = $"Running: {runningCount}";
        }

        private void UpdateStatus(string message)
        {
            StatusText.Text = message;
        }

        private void AddLog(string message)
        {
            // Check if this is a cache stats message
            if (message.Contains("[CACHE STATS]"))
            {
                ParseCacheStats(message);
            }

            Dispatcher.Invoke(() =>
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

        private void ParseCacheStats(string logMessage)
        {
            // Parse cache statistics from log message
            // Format: [CACHE STATS] Hits: 1234, Misses: 56, Hit Rate: 95.7%, Read: 89 ops (125 MB), Write: 12 ops (8 MB)
            var match = Regex.Match(logMessage, @"\[CACHE STATS\] Hits: (\d+), Misses: (\d+), Hit Rate: ([\d.]+)%, Read: (\d+) ops \((\d+) MB\), Write: (\d+) ops \((\d+) MB\)");

            if (match.Success)
            {
                var hits = long.Parse(match.Groups[1].Value);
                var misses = long.Parse(match.Groups[2].Value);
                var hitRate = double.Parse(match.Groups[3].Value);
                var readOps = long.Parse(match.Groups[4].Value);
                var readMB = long.Parse(match.Groups[5].Value);
                var writeOps = long.Parse(match.Groups[6].Value);
                var writeMB = long.Parse(match.Groups[7].Value);

                // Update UI on main thread
                Dispatcher.Invoke(() =>
                {
                    CacheStatsText.Text = $"Cache: {hits}H/{misses}M";
                    HitRateText.Text = $"Hit Rate: {hitRate:F1}%";
                    OperationsText.Text = $"Ops: R:{readOps}({readMB}MB) W:{writeOps}({writeMB}MB)";

                    // Color coding based on hit rate
                    if (hitRate >= 80)
                        HitRateText.Foreground = System.Windows.Media.Brushes.Green;
                    else if (hitRate >= 50)
                        HitRateText.Foreground = System.Windows.Media.Brushes.Orange;
                    else
                        HitRateText.Foreground = System.Windows.Media.Brushes.Red;
                });
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = AcceleratedFoldersGrid.SelectedItem as AcceleratedFolderViewModel;
            if (selected == null)
                return;

            var result = MessageBox.Show(
                $"确定要删除加速条目吗？\n\n原始路径: {selected.OriginalPath}\n挂载点: {selected.MountPoint}\n\n注意：这只是删除记录，不会删除实际文件。",
                "确认删除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    // 如果正在运行，先停止
                    if (selected.Status == "✅")
                    {
                        AddLog($"正在停止 {selected.MountPoint} 的文件系统...");
                        if (_winFsp.IsRunning(selected.MountPoint))
                        {
                            _winFsp.StopFileSystem(selected.MountPoint);
                        }

                        // 如果原始目录被重命名了，恢复它
                        if (Directory.Exists(selected.OriginalPath) && !Directory.Exists(selected.MountPoint))
                        {
                            AddLog($"恢复原始目录: {selected.OriginalPath} -> {selected.MountPoint}");
                            Directory.Move(selected.OriginalPath, selected.MountPoint);
                        }
                    }

                    // 从配置中删除
                    _config.RemoveAcceleratedFolder(selected.MountPoint);

                    // 从UI中删除
                    _acceleratedFolders.Remove(selected);

                    AddLog($"已删除加速条目: {selected.MountPoint}");
                    UpdateStatus($"已删除加速条目: {selected.MountPoint}");
                    UpdateUI();
                }
                catch (Exception ex)
                {
                    AddLog($"删除条目时出错: {ex.Message}");
                    MessageBox.Show($"删除条目时出错:\n{ex.Message}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            _winFsp.StopAllFileSystems();
            base.OnClosing(e);
        }
    }

    public class AcceleratedFolderViewModel : INotifyPropertyChanged
    {
        private string _status = "⭕";

        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        public string OriginalPath { get; set; } = string.Empty;
        public string CachePath { get; set; } = string.Empty;
        public string MountPoint { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public long CacheSize { get; set; }

        public string CacheSizeFormatted
        {
            get
            {
                var sizes = new[] { "B", "KB", "MB", "GB", "TB" };
                var len = (double)CacheSize;
                var order = 0;
                while (len >= 1024 && order < sizes.Length - 1)
                {
                    order++;
                    len /= 1024;
                }
                return $"{len:0.##} {sizes[order]}";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}