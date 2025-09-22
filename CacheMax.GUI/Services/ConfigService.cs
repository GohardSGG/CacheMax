using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace CacheMax.GUI.Services
{
    public class AcceleratedFolder : INotifyPropertyChanged
    {
        public string OriginalPath { get; set; } = string.Empty;
        public string CachePath { get; set; } = string.Empty;
        public string MountPoint { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public long CacheSize { get; set; }

        private string _status = "未加速";
        public string Status
        {
            get => _status;
            set
            {
                _status = value;
                OnPropertyChanged(nameof(Status));
            }
        }

        private double _progressPercentage = 0.0;
        public double ProgressPercentage
        {
            get => _progressPercentage;
            set
            {
                _progressPercentage = value;
                OnPropertyChanged(nameof(ProgressPercentage));
                OnPropertyChanged(nameof(ProgressText));
            }
        }

        [JsonIgnore]
        public string ProgressText => $"{ProgressPercentage:F0}%";

        [JsonIgnore]
        public string CacheSizeFormatted => FormatBytes(CacheSize);

        private static string FormatBytes(long bytes)
        {
            if (bytes == 0) return "0 B";

            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int suffixIndex = 0;

            while (size >= 1024 && suffixIndex < suffixes.Length - 1)
            {
                size /= 1024;
                suffixIndex++;
            }

            return $"{size:F1} {suffixes[suffixIndex]}";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class AppConfig
    {
        public string DefaultCacheRoot { get; set; } = @"S:\Cache";
        public List<AcceleratedFolder> AcceleratedFolders { get; set; } = new();
        public bool AutoStartWithWindows { get; set; }
        public bool MinimizeToTray { get; set; }
    }

    public class ConfigService
    {
        private readonly string _configPath;
        private AppConfig _config = new();

        public AppConfig Config => _config;

        public ConfigService()
        {
            // 将配置文件保存在exe所在目录
            var exeDirectory = AppDomain.CurrentDomain.BaseDirectory;
            _configPath = Path.Combine(exeDirectory, "accelerated_folders.json");

            Console.WriteLine($"配置文件路径：{_configPath}");
            LoadConfig();
        }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    Console.WriteLine($"找到配置文件，正在加载：{_configPath}");
                    var json = File.ReadAllText(_configPath);
                    _config = JsonConvert.DeserializeObject<AppConfig>(json) ?? new AppConfig();
                    Console.WriteLine($"成功加载配置，包含 {_config.AcceleratedFolders.Count} 个加速项目");

                    // 输出每个加速项目的详细信息
                    foreach (var folder in _config.AcceleratedFolders)
                    {
                        Console.WriteLine($"  - 挂载点: {folder.MountPoint}");
                        Console.WriteLine($"    原始路径: {folder.OriginalPath}");
                        Console.WriteLine($"    缓存路径: {folder.CachePath}");
                    }
                }
                else
                {
                    Console.WriteLine($"配置文件不存在，创建新配置：{_configPath}");
                    _config = new AppConfig();
                    SaveConfig();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载配置文件失败：{ex.Message}");
                _config = new AppConfig();
            }
        }

        public void SaveConfig()
        {
            try
            {
                Console.WriteLine($"正在保存配置到：{_configPath}");
                Console.WriteLine($"配置包含 {_config.AcceleratedFolders.Count} 个加速项目");

                var directory = Path.GetDirectoryName(_configPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                    Console.WriteLine($"创建目录：{directory}");
                }

                var json = JsonConvert.SerializeObject(_config, Formatting.Indented);
                File.WriteAllText(_configPath, json);
                Console.WriteLine("配置文件保存成功");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"保存配置文件失败：{ex.Message}");
            }
        }

        public void AddAcceleratedFolder(AcceleratedFolder folder)
        {
            // 检查是否已存在相同的MountPoint，如果存在则更新，否则添加
            var existingFolder = _config.AcceleratedFolders.FirstOrDefault(f => f.MountPoint == folder.MountPoint);
            if (existingFolder != null)
            {
                // 更新现有项目
                existingFolder.OriginalPath = folder.OriginalPath;
                existingFolder.CachePath = folder.CachePath;
                existingFolder.CacheSize = folder.CacheSize;
                existingFolder.Status = folder.Status;
                existingFolder.ProgressPercentage = folder.ProgressPercentage;
                Console.WriteLine($"更新现有加速项目：{folder.MountPoint}");
            }
            else
            {
                // 添加新项目
                _config.AcceleratedFolders.Add(folder);
                Console.WriteLine($"添加新加速项目：{folder.MountPoint}");
            }
            SaveConfig();
        }

        public void RemoveAcceleratedFolder(string mountPoint)
        {
            _config.AcceleratedFolders.RemoveAll(f => f.MountPoint == mountPoint);
            SaveConfig();
        }

        public AcceleratedFolder? GetAcceleratedFolder(string mountPoint)
        {
            return _config.AcceleratedFolders.Find(f => f.MountPoint == mountPoint);
        }
    }
}